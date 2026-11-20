extern alias WorkerAssembly;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Services;
using WorkerAssembly::Minicloud.Worker.DevelopmentWorkspaces;

namespace Minicloud.Tests;

public sealed partial class DevelopmentWorkspaceApiTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new($"Data Source=workspace-api-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
    private WorkspaceApiFactory factory = default!;
    private string token = default!;
    private string otherToken = default!;
    private const string Root = "/api/development-workspaces";
    private static CreateDevelopmentWorkspaceRequest Request() => new("org", "API fixture", "image", "standard", "plan", 20, "create");

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new WorkspaceApiFactory(connection);
        factory.CreateClient().Dispose();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Organizations.AddRange(new Organization { Id = "org", Name = "Org", Slug = "org" },
            new Organization { Id = "other", Name = "Other", Slug = "other" });
        db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org", Name = "Synthetic",
            ProviderType = CloudProvider.Hetzner, CredentialPath = "sensitive/provider", CredentialGeneration = "synthetic" });
        db.ProviderPlans.Add(new ProviderPlan { Id = "plan", OrganizationId = "org", ProviderAccountId = "account",
            Name = "Standard", NormalizedName = "standard", Region = "nbg1", Sku = "cx-test" });
        var tokens = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
        token = AddKey(db, tokens, "org", ApiKeyScopes.All);
        otherToken = AddKey(db, tokens, "other", ApiKeyScopes.All);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SpoofedActorAndOrganizationHeadersNeverAuthorizePublicRequests()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Organization-Id", "org");
        client.DefaultRequestHeaders.Add("X-Actor-Id", "admin");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Root}?organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Root}/selections?organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Root, Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"{Root}/fixture/idle-timeout?organizationId=org",
            new UpdateDevelopmentWorkspaceIdleTimeoutRequest(90))).StatusCode);
        await AssertNoCompute();
    }

    [Fact]
    public async Task OtherOrganizationCannotReadOrMutateAnyWorkspaceVmOrTask()
    {
        using var owner = Client(token);
        var workspace = await Create(owner);
        var vm = workspace.Vms!.Single();
        using var other = Client(otherToken);
        var prefix = $"{Root}/{workspace.Id}";
        foreach (var url in new[] { $"{Root}?organizationId=org", $"{prefix}?organizationId=org", $"{prefix}/vms?organizationId=org",
            $"{Root}/selections?organizationId=org", $"{Root}/selections?organizationId=org&workspaceId={workspace.Id}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(url)).StatusCode);
        foreach (var action in new[] { "start", "stop", "stop-all", "cancel-idle-stop" })
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"{prefix}/{action}?organizationId=org", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Root, Request())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{prefix}?organizationId=other")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"{prefix}/idle-timeout?organizationId=org",
            new UpdateDevelopmentWorkspaceIdleTimeoutRequest(90))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{prefix}/idle-timeout?organizationId=other",
            new UpdateDevelopmentWorkspaceIdleTimeoutRequest(90))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await other.DeleteAsync($"{prefix}?organizationId=org&confirmPermanentDeletion=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.DeleteAsync($"{prefix}?organizationId=other&confirmPermanentDeletion=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{prefix}/vms/{vm.Id}/discard?organizationId=org",
            new DevelopmentWorkspaceDiscardRequest(0, true, true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"{prefix}/vms/{vm.Id}/tasks/task/lease?organizationId=org",
            new DevelopmentWorkspaceGenerationRequest(0))).StatusCode);
        await AssertNoCompute();
    }

    [Fact]
    public async Task DetailReportsAllowlistedStartupStageOfCurrentOperationEvenWhenBlocked()
    {
        using var owner = Client(token);
        var workspace = await Create(owner);
        var detail = $"{Root}/{workspace.Id}?organizationId=org";
        Assert.Null(Assert.Single((await owner.GetFromJsonAsync<DevelopmentWorkspaceResponse>(detail))!.Vms!).StartupStage);
        using var started = await owner.PostAsync($"{Root}/{workspace.Id}/start?organizationId=org", null);
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        var pending = Assert.Single((await owner.GetFromJsonAsync<DevelopmentWorkspaceResponse>(detail))!.Vms!);
        Assert.Equal("preparing_vm", pending.StartupStage);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var operation = await db.DevelopmentWorkspaceOperations.SingleAsync();
        var machine = await db.DevelopmentWorkspaceVms.SingleAsync();
        operation.Step = "session";
        operation.ProviderEvidenceJson = """{"private":"synthetic-private-evidence"}""";
        machine.Phase = "blocked";
        machine.ErrorCode = "workspace_reconciliation_required";
        await db.SaveChangesAsync();

        var raw = await owner.GetStringAsync(detail);
        Assert.DoesNotContain("synthetic-private-evidence", raw);
        Assert.DoesNotContain(operation.Id, raw);
        var blocked = Assert.Single((await owner.GetFromJsonAsync<DevelopmentWorkspaceResponse>(detail))!.Vms!);
        Assert.Equal("blocked", blocked.Phase);
        Assert.Equal("checking_readiness", blocked.StartupStage);

        machine.Generation++;
        await db.SaveChangesAsync();
        Assert.Null(Assert.Single((await owner.GetFromJsonAsync<DevelopmentWorkspaceResponse>(detail))!.Vms!).StartupStage);
    }

    [Fact]
    public async Task FolderSizesAndDeletionRequireWriteAccessExactConfirmationAndHideFilesystemIdentity()
    {
        using var owner = Client(token);
        var workspace = await Create(owner);
        var vmId = workspace.Vms!.Single().Id;
        string readToken;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            readToken = AddKey(db, scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>(), "org", [ApiKeyScopes.AppsRead]);
            var machine = await db.DevelopmentWorkspaceVms.SingleAsync();
            machine.Phase = "ready"; machine.DesiredState = "running";
            machine.RuntimeCapabilities = "directory-delete-v1,disk-usage-v1,resources-v1";
            machine.ResourceSampleGeneration = machine.Generation; machine.ResourceSampleReceivedAt = DateTimeOffset.UtcNow;
            machine.ResourceSampleJson = DevelopmentWorkspaceService.NormalizeResourceSample(JsonSerializer.SerializeToElement(new
            {
                resources = new
                {
                    schemaVersion = 1, vmId, generation = machine.Generation, observedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    cpu = new { logicalCount = 2, utilizationPercent = (double?)null, windowSeconds = (double?)null },
                    memory = new { totalBytes = 4096L, availableBytes = 1024L }, filesystems = Array.Empty<object>(),
                    unavailable = new Dictionary<string, string> { ["cpu.utilizationPercent"] = "warming_up" }
                }
            }), vmId, machine.Generation, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        var prefix = $"{Root}/{workspace.Id}/vms/{vmId}";
        var generation = workspace.Vms!.Single().Generation;
        using var reader = Client(readToken);
        var resources = Assert.Single((await reader.GetFromJsonAsync<DevelopmentWorkspaceResponse>(
            $"{Root}/{workspace.Id}?organizationId=org"))!.Vms!).Resources!;
        Assert.Equal("current", resources.State);
        Assert.Null(resources.Cpu!.UtilizationPercent);
        Assert.Equal("warming_up", resources.Unavailable["cpu.utilizationPercent"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{prefix}/disk-usage/scans?organizationId=org",
            new DevelopmentWorkspaceDiskUsageScanRequest(generation, "scan-key-0001"))).StatusCode);
        using var other = Client(otherToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{prefix}/disk-usage/scans?organizationId=org",
            new DevelopmentWorkspaceDiskUsageScanRequest(generation, "scan-key-0001"))).StatusCode);

        var started = await owner.PostAsJsonAsync($"{prefix}/disk-usage/scans?organizationId=org",
            new DevelopmentWorkspaceDiskUsageScanRequest(generation, "scan-key-0001"));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var job = (await started.Content.ReadFromJsonAsync<DevelopmentWorkspaceFilesystemJobResponse>())!;
        Assert.Equal(("queued", "/workspace"), (job.State, job.Path));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{prefix}/filesystem-jobs/{job.Id}?organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{prefix}/filesystem-jobs/missing?organizationId=org")).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DevelopmentWorkspaceService>();
            Assert.Equal(job.Id, (await service.ClaimFilesystemJobAsync(1, default))!.Id);
            await service.RecordFilesystemJobAsync(job.Id, JsonSerializer.SerializeToElement(new
            {
                state = "completed", result = new
                {
                    schemaVersion = 1, mode = "scan", policyVersion = 1, path = Array.Empty<string>(),
                    target = new { dev = 64769, ino = 731_001, uid = 2000 }, complete = true, totalBytes = 5000, filesHereBytes = 0,
                    omittedEntries = 0, omittedBytes = 0, errors = new { },
                    entries = new[] { new { name = "artifacts", bytes = (long?)5000, state = "complete", errors = Array.Empty<string>(),
                        selectable = true, deletable = false, uid = 2000, dev = 64769, ino = 731_002, storage = (string?)null },
                        // A scratch bind mount is never selectable, even if a runtime claimed otherwise.
                        new { name = "build", bytes = (long?)null, state = "excluded", errors = new[] { "separate_mount" },
                        selectable = true, deletable = true, uid = 2000, dev = 64770, ino = 731_003, storage = (string?)"scratch" } }
                }
            }), null, default);
        }
        var scanned = await owner.GetAsync($"{prefix}/filesystem-jobs/{job.Id}?organizationId=org");
        var raw = await scanned.Content.ReadAsStringAsync();
        Assert.True(scanned.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("731002", raw);
        Assert.DoesNotContain("64769", raw);
        var entries = JsonSerializer.Deserialize<DevelopmentWorkspaceFilesystemJobResponse>(raw,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.DiskUsage!.Entries;
        var entry = entries[0];
        Assert.Equal(("/workspace/artifacts", false, null), (entry.Path, entry.CanDelete, entry.Storage));
        var scratch = entries[1];
        Assert.Equal(("/workspace/build", false, false, null, "scratch", "scratch"),
            (scratch.Path, scratch.CanOpen, scratch.CanDelete, scratch.TargetToken, scratch.ProtectedReason, scratch.Storage));

        var deletion = new DevelopmentWorkspaceDirectoryDeletionRequest(generation, entry.TargetToken!, "/workspace/artifacts", true, "delete-key-01");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{prefix}/directory-deletions?organizationId=org", deletion)).StatusCode);
        var refused = await owner.PostAsJsonAsync($"{prefix}/directory-deletions?organizationId=org", deletion);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("workspace_folder_protected", await refused.Content.ReadAsStringAsync());
        var stale = await owner.PostAsJsonAsync($"{prefix}/disk-usage/scans?organizationId=org",
            new DevelopmentWorkspaceDiskUsageScanRequest(generation + 1, "scan-key-0002", entry.TargetToken));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task DiscoveryNeedsOnlyReadPermissionAndReturnsPublicMetadataWithoutAllocating()
    {
        string readToken, noReadToken;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            readToken = AddKey(db, scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>(), "org", [ApiKeyScopes.AppsRead]);
            noReadToken = AddKey(db, scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>(), "org", [ApiKeyScopes.PlansRead]);
            await db.SaveChangesAsync();
        }
        factory.Policy.Images["image"].DisplayName = "Development tools";
        using var noRead = Client(noReadToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRead.GetAsync($"{Root}/selections?organizationId=org")).StatusCode);
        using var client = Client(readToken);
        for (var i = 0; i < 2; i++)
        {
            var response = await client.GetAsync($"{Root}/selections?organizationId=org");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var choices = (await response.Content.ReadFromJsonAsync<WorkspaceSelections>())!;
            Assert.True(choices.Available);
            Assert.Null(choices.UnavailableReason);
            Assert.Equal(new WorkspaceSelectionOption("image", "Development tools", "standard", "plan",
                "Standard", "Synthetic", "nbg1", "cx-test", 4, 8, .1m, false), Assert.Single(choices.Options));
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("sensitive", raw);
            Assert.DoesNotContain("credential", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("manifest", raw, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Root, Request())).StatusCode);
        await AssertNoCompute();
        await using var verify = factory.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Empty(await context.DevelopmentWorkspaces.ToListAsync());
        Assert.Null((await context.ProviderPlans.SingleAsync()).ProvisioningLockedAt);
    }

    [Fact]
    public async Task CreationRequiresExplicitConnectApprovalAndNeverFallsBackToLegacyImage()
    {
        using var client = Client(token);
        const string discovery = $"{Root}/selections?organizationId=org";
        factory.Policy.Images["legacy-image"] = new()
        {
            DisplayName = "Muni T3 with Connect", Enabled = true, RuntimeQualified = true,
            QualifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ProviderAccountId = "account",
            AllowedRegions = ["nbg1"], ProviderImageId = "legacy-snapshot", ManifestSha256 = new('b', 64)
        };
        var choices = (await client.GetFromJsonAsync<WorkspaceSelections>(discovery))!;
        Assert.Equal("image", Assert.Single(choices.Options).ImageId);

        var staleRequest = Request() with { ImageId = "legacy-image" };
        using var rejected = await client.PostAsJsonAsync(Root, staleRequest);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var error = (await rejected.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;
        Assert.Equal("workspace_image_invalid", error.Code);
        Assert.Contains("T3 Connect", error.Message);

        var workspace = await Create(client);
        var scoped = (await client.GetFromJsonAsync<WorkspaceSelections>($"{discovery}&workspaceId={workspace.Id}"))!;
        Assert.Contains(scoped.Options, option => option.ImageId == "legacy-image");

        factory.Policy.Images["image"].AllowedRegions = ["other-region"];
        var empty = (await client.GetFromJsonAsync<WorkspaceSelections>(discovery))!;
        Assert.True(empty.Available);
        Assert.Empty(empty.Options);
        using var noCompatiblePlan = await client.PostAsJsonAsync(Root, Request() with { IdempotencyKey = "second" });
        Assert.Equal(HttpStatusCode.BadRequest, noCompatiblePlan.StatusCode);
        Assert.Equal("workspace_plan_invalid",
            (await noCompatiblePlan.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);

        await AssertNoCompute();
        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Equal(workspace.Id, (await db.DevelopmentWorkspaces.SingleAsync()).Id);
    }

    [Fact]
    public async Task DiscoveryHidesForeignWorkspaceAndDistinguishesEmptyAndDisabledConfiguration()
    {
        using var owner = Client(token);
        var workspace = await Create(owner);
        var scoped = await owner.GetFromJsonAsync<WorkspaceSelections>($"{Root}/selections?organizationId=org&workspaceId={workspace.Id}");
        Assert.Single(scoped!.Options);
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.GetAsync($"{Root}/selections?organizationId=org&workspaceId=missing")).StatusCode);
        using var other = Client(otherToken);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.GetAsync($"{Root}/selections?organizationId=other&workspaceId={workspace.Id}")).StatusCode);
        var empty = (await other.GetFromJsonAsync<WorkspaceSelections>($"{Root}/selections?organizationId=other"))!;
        Assert.True(empty.Available);
        Assert.Null(empty.UnavailableReason);
        Assert.Empty(empty.Options);
        factory.Policy.Enabled = false;
        var disabled = (await owner.GetFromJsonAsync<WorkspaceSelections>($"{Root}/selections?organizationId=org"))!;
        Assert.False(disabled.Available);
        Assert.Contains("disabled", disabled.UnavailableReason);
        Assert.Empty(disabled.Options);
        factory.Policy.Enabled = true;
        factory.Policy.PricingUpdatedAt = DateTimeOffset.UtcNow.AddDays(-100);
        var invalid = (await owner.GetFromJsonAsync<WorkspaceSelections>($"{Root}/selections?organizationId=org"))!;
        Assert.False(invalid.Available);
        Assert.Contains("pricing", invalid.UnavailableReason);
        await AssertNoCompute();
    }

    [Fact]
    public async Task DiscoveryReadFailureIsSanitizedUnavailableNotAnEmptyCatalog()
    {
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().Database.ExecuteSqlRawAsync("DROP TABLE provider_plans");
        using var client = Client(token);
        var response = await client.GetAsync($"{Root}/selections?organizationId=org");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
        Assert.Equal("workspace_selections_unavailable", error.Error.Code);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("provider_plans", raw);
        Assert.DoesNotContain("SQLite", raw);
        await AssertNoCompute();
    }

    [Fact]
    public async Task CreateAndResizeRejectSelectionWhosePolicyChangedSinceDiscovery()
    {
        using var client = Client(token);
        var workspace = await Create(client);
        var choices = (await client.GetFromJsonAsync<WorkspaceSelections>($"{Root}/selections?organizationId=org"))!;
        var choice = Assert.Single(choices.Options);
        factory.Policy.Images[choice.ImageId].RuntimeQualified = false;
        var create = await client.PostAsJsonAsync(Root, Request() with { Name = "Second", IdempotencyKey = "second" });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal("workspace_image_invalid", (await create.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        var resize = await client.PostAsJsonAsync($"{Root}/{workspace.Id}/resize?organizationId=org",
            new DevelopmentWorkspaceResizeRequest(0, choice.ImageId, choice.Profile, choice.PlanId));
        Assert.Equal(HttpStatusCode.BadRequest, resize.StatusCode);
        Assert.Equal("workspace_image_invalid", (await resize.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        var empty = (await client.GetFromJsonAsync<WorkspaceSelections>($"{Root}/selections?organizationId=org"))!;
        Assert.True(empty.Available);
        Assert.Empty(empty.Options);
        await AssertNoCompute();
    }

    [Fact]
    public async Task PermanentDeleteRequiresConfirmationAndReleasesNameOnlyAfterWorkerCompletion()
    {
        using var client = Client(token);
        var workspace = await Create(client);
        var prefix = $"{Root}/{workspace.Id}";
        var url = $"{prefix}?organizationId=org";
        var confirmed = $"{url}&confirmPermanentDeletion=true";
        using var stopped = await client.PostAsync($"{prefix}/stop?organizationId=org", null);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.Equal("stopped", (await stopped.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.Phase);

        foreach (var unconfirmed in new[] { url, $"{url}&confirmPermanentDeletion=false" })
        {
            using var refused = await client.DeleteAsync(unconfirmed);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("workspace_delete_confirmation_required",
                (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        }
        await AssertNoCompute();

        // Model a retained disk from a previous run without starting a real VM.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var retained = await db.DevelopmentWorkspaces.SingleAsync();
            retained.ProviderVolumeId = "201";
            retained.VolumeStatus = "retained";
            await db.SaveChangesAsync();
        }
        factory.Cloud.WorkspaceId = workspace.Id;
        using var queued = await client.DeleteAsync(confirmed);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal("deleting", (await queued.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.Phase);
        var replacementRequest = Request() with { IdempotencyKey = "replacement" };
        using var tooSoon = await client.PostAsJsonAsync(Root, replacementRequest);
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        Assert.Equal("workspace_reservation_conflict", (await tooSoon.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        Assert.Equal(0, factory.Cloud.Deletions);
        foreach (var mutation in new[] {
            client.PostAsync($"{prefix}/start?organizationId=org", null),
            client.PutAsJsonAsync($"{prefix}/idle-timeout?organizationId=org", new UpdateDevelopmentWorkspaceIdleTimeoutRequest(90)) })
        {
            using var locked = await mutation;
            Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
            Assert.Equal("workspace_permanent_deletion", (await locked.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        }

        for (var hop = 0; hop < 12; hop++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            if (!await scope.ServiceProvider.GetRequiredService<DevelopmentWorkspaceProcessor>().ProcessNextAsync(default))
                break;
        }

        using var deleted = await client.DeleteAsync(confirmed);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal("deleted", (await deleted.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.Phase);
        Assert.Equal(1, factory.Cloud.Deletions);
        Assert.Empty((await client.GetFromJsonAsync<DevelopmentWorkspaceResponse[]>($"{Root}?organizationId=org"))!);
        using var replacement = await client.PostAsJsonAsync(Root, replacementRequest);
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        Assert.NotEqual(workspace.Id, (await replacement.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.Id);
    }

    [Fact]
    public async Task CreateStartsPrimaryOnlyWhenExplicitlyRequestedAndRetryDoesNotQueueAnotherStart()
    {
        using var client = Client(token);
        var stopped = await Create(client);
        Assert.Equal("stopped", stopped.Vms!.Single().DesiredState);
        var body = Request() with { Name = "Started", IdempotencyKey = "create-started", StartPrimaryVm = true };
        var responses = new List<DevelopmentWorkspaceResponse>();
        for (var i = 0; i < 2; i++)
        {
            using var result = await client.PostAsJsonAsync(Root, body);
            Assert.Equal(HttpStatusCode.Created, result.StatusCode);
            responses.Add((await result.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!);
        }
        Assert.Equal(responses[0].Id, responses[1].Id);
        Assert.All(responses, x => Assert.Equal(("running", 1L), (x.Vms!.Single().DesiredState, x.Vms!.Single().Generation)));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var operation = Assert.Single(await db.DevelopmentWorkspaceOperations.ToListAsync());
        Assert.Equal(("start", responses[0].Id), (operation.Kind, operation.WorkspaceId));
    }

    [Fact]
    public async Task RepeatedGetAndVmListingNeverAllocateAndResponsesRedactRuntimeCredentials()
    {
        using var client = Client(token);
        var workspace = await Create(client);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Root}?organizationId=org")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Root}/{workspace.Id}?organizationId=org")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Root}/{workspace.Id}/vms?organizationId=org")).StatusCode);
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var vm = await db.DevelopmentWorkspaceVms.SingleAsync();
            vm.EnrollmentTokenDigest = "sensitive-enrollment"; vm.RuntimeTokenHash = "sensitive-runtime";
            vm.RepositoryCredentialRef = "sensitive-repository"; vm.TaskSecretReferencesJson = "sensitive-task";
            await db.SaveChangesAsync();
        }
        var raw = await client.GetStringAsync($"{Root}/{workspace.Id}?organizationId=org");
        Assert.DoesNotContain("sensitive-", raw);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        await AssertNoCompute();
    }

    [Fact]
    public async Task ResourceHistoryIsReaderScopedCamelCaseAndReadsOnlyStoredSamples()
    {
        using var owner = Client(token);
        using var other = Client(otherToken);
        var workspace = await Create(owner);
        var sibling = await owner.PostAsJsonAsync(Root, Request() with { Name = "Sibling", IdempotencyKey = "sibling" });
        var siblingVm = (await sibling.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.Vms!.Single();
        var vm = workspace.Vms!.Single();
        var url = $"{Root}/{workspace.Id}/vms/{vm.Id}/resource-history";

        var empty = await owner.GetAsync($"{url}?organizationId=org");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using (var json = JsonDocument.Parse(await empty.Content.ReadAsStringAsync()))
        {
            Assert.Equal(vm.Id, json.RootElement.GetProperty("vmId").GetString());
            Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("generation").ValueKind);
            Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("windowStart").ValueKind);
            Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("windowEnd").ValueKind);
            Assert.Empty(json.RootElement.GetProperty("samples").EnumerateArray());
        }

        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var generation = (await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id)).Generation;
            db.DevelopmentWorkspaceVmResourceSamples.Add(new()
            {
                WorkspaceId = workspace.Id, VmId = vm.Id, Generation = generation,
                Minute = new DateTimeOffset(observedAt.UtcTicks - observedAt.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero),
                ObservedAt = observedAt, MemoryTotalBytes = 8, MemoryAvailableBytes = 3,
                ExpiresAt = observedAt.AddHours(2)
            });
            await db.SaveChangesAsync();
        }
        using (var json = JsonDocument.Parse(await owner.GetStringAsync($"{url}?organizationId=org")))
        {
            var sample = Assert.Single(json.RootElement.GetProperty("samples").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, sample.GetProperty("cpuUtilizationPercent").ValueKind);
            Assert.Equal(5, sample.GetProperty("memoryUsedBytes").GetInt64());
            Assert.Equal(8, sample.GetProperty("memoryTotalBytes").GetInt64());
            Assert.Equal(JsonValueKind.Null, sample.GetProperty("workspaceDiskUsedBytes").ValueKind);
            Assert.Equal(JsonValueKind.Null, sample.GetProperty("workspaceDiskTotalBytes").ValueKind);
            Assert.Equal(observedAt, sample.GetProperty("observedAt").GetDateTimeOffset());
            Assert.True(sample.TryGetProperty("minute", out _));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{url}?organizationId=org")).StatusCode);
        var crossOrg = await other.GetAsync($"{url}?organizationId=other");
        Assert.Equal(HttpStatusCode.NotFound, crossOrg.StatusCode);
        Assert.DoesNotContain("samples", await crossOrg.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.GetAsync($"{Root}/{workspace.Id}/vms/{siblingVm.Id}/resource-history?organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.GetAsync($"{Root}/{workspace.Id}/vms/missing/resource-history?organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.GetAsync($"{Root}/missing/vms/{vm.Id}/resource-history?organizationId=org")).StatusCode);
        await AssertNoCompute();
    }

    [Fact]
    public async Task DisabledMutationsAndStaleGenerationFailClosed()
    {
        using var client = Client(token);
        var workspace = await Create(client);
        var vm = workspace.Vms!.Single();
        var stale = await client.PostAsJsonAsync($"{Root}/{workspace.Id}/vms/{vm.Id}/start?organizationId=org",
            new DevelopmentWorkspaceGenerationRequest(999));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("workspace_generation_stale", (await stale.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        factory.Policy.Enabled = false;
        var disabled = await client.PostAsync($"{Root}/{workspace.Id}/start?organizationId=org", null);
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Equal("workspaces_disabled", (await disabled.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        await AssertNoCompute();
    }

    [Fact]
    public async Task RuntimeEnrollmentConsumesOnlyTheGenerationBoundOneTimeCredential()
    {
        var (request, enrollmentToken) = await Enrollment();
        using var runtime = Client(enrollmentToken);
        var accepted = await runtime.PostAsJsonAsync($"{Root}/runtime/enroll", request);
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await runtime.PostAsJsonAsync($"{Root}/runtime/enroll", request)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var machine = await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().DevelopmentWorkspaceVms.SingleAsync();
        Assert.NotNull(machine.EnrolledAt);
        Assert.Null(machine.EnrollmentTokenDigest);
        Assert.Null(machine.EnrollmentTokenExpiresAt);
        Assert.False(machine.GatewayReady);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("kind")]
    [InlineData("manifest")]
    [InlineData("receipt")]
    public async Task ForgedRuntimeEvidenceCannotConsumeTheLegitimateEnrollment(string forged)
    {
        var (request, enrollmentToken) = await Enrollment();
        var invalid = forged switch
        {
            "generation" => request with { Generation = request.Generation + 1 },
            "kind" => request with { Kind = "ephemeral" },
            "manifest" => request with { ImageManifestSha256 = new('c', 64) },
            _ => request with { BootstrapReceipt = JsonSerializer.SerializeToElement(new { type = "UserSuppliedProof" }) }
        };
        using var runtime = Client(enrollmentToken);
        Assert.Equal(HttpStatusCode.Conflict, (await runtime.PostAsJsonAsync($"{Root}/runtime/enroll", invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await runtime.PostAsJsonAsync($"{Root}/runtime/enroll", request)).StatusCode);
    }

    private async Task<(Minicloud.Api.Controllers.DevelopmentWorkspaceEnrollmentRequest Request, string Token)> Enrollment()
    {
        using var owner = Client(token);
        var workspace = await Create(owner);
        var started = await owner.PostAsync($"{Root}/{workspace.Id}/start?organizationId=org", null);
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        await using var scope = factory.Services.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<DevelopmentWorkspaceService>();
        var operation = await lifecycle.ClaimOperationAsync("enrollment-fixture", default);
        Assert.NotNull(operation);
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var machine = await db.DevelopmentWorkspaceVms.SingleAsync();
        var credential = new string('b', 64);
        machine.ProviderServerId = "301"; machine.SshHostKeyGeneration = machine.Generation;
        machine.EnrollmentTokenDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
        machine.EnrollmentTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        var receipt = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, type = "BootstrapReady", vmId = machine.Id,
            generation = machine.Generation, kind = machine.Kind, operationId = operation.Id
        });
        return (new(1, machine.Id, machine.Generation, machine.Kind, operation.Id, new('a', 64), receipt), credential);
    }

    private async Task AssertNoCompute()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
        Assert.Empty(await db.DevelopmentWorkspaceVms.Where(x => x.ComputeReserved).ToListAsync());
        Assert.Empty(await db.ServerInstances.ToListAsync());
    }

    private static async Task<DevelopmentWorkspaceResponse> Create(HttpClient client)
    {
        var result = await client.PostAsJsonAsync(Root, Request());
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
        return (await result.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!;
    }
    private HttpClient Client(string value)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value);
        return client;
    }
    private static string AddKey(MinicloudDbContext db, IApiKeyTokenService tokens, string org, IEnumerable<string> scopes)
    {
        var value = tokens.Generate();
        db.ApiKeys.Add(new ApiKey { Id = Guid.NewGuid().ToString("N"), OrganizationId = org, Name = "Fixture",
            KeyPrefix = value.Prefix, KeyHash = tokens.Hash(value.Token), ScopesJson = JsonSerializer.Serialize(scopes),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        return value.Token;
    }
    public async Task DisposeAsync() { await factory.DisposeAsync(); await connection.DisposeAsync(); }

    private sealed class WorkspaceApiFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        public VolumeTransport Cloud { get; } = new();
        public DevelopmentWorkspaceOptions Policy { get; } = new()
        {
            Enabled = true, VolumeMonthlyRatePerGb = .1m, PricingUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Profiles = new()
            {
                ["standard"] = new() { CpuCount = 4, MemoryGiB = 8, ComputeHourlyRate = .1m,
                    AllowedPlanIds = ["plan"], AllowedRegions = ["nbg1"], AllowedSkus = ["cx-test"] }
            },
            Images = new()
            {
                ["image"] = new() { Enabled = true, RuntimeQualified = true, QualifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    ProviderAccountId = "account", AllowedRegions = ["nbg1"], ProviderImageId = "123", ManifestSha256 = new('a', 64),
                    T3ConnectForNewWorkspaces = true }
            }
        };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Minicloud"] = connection.ConnectionString
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options => options.UseSqlite(connection.ConnectionString));
                services.AddSingleton<IOptions<DevelopmentWorkspaceOptions>>(Options.Create(Policy));
                services.AddDevelopmentWorkspaceWorker(new ConfigurationBuilder().Build());
                // Drive the real processor explicitly so tests control the queue and share SQLite safely.
                services.Remove(services.Single(x => x.ServiceType == typeof(IHostedService)
                    && x.ImplementationType == typeof(DevelopmentWorkspaceWorker)));
                services.RemoveAll<ProviderAccountClientFactory>();
                services.AddSingleton<ProviderAccountClientFactory>(new VolumeProviderFactory(Cloud));
            });
        }
    }

    private sealed class VolumeProviderFactory(VolumeTransport cloud)
        : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, NullLoggerFactory.Instance)
    {
        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId,
            CancellationToken ct, bool allowDisabled = true)
        {
            Assert.Equal("org", organizationId);
            Assert.Equal("account", providerAccountId);
            return Task.FromResult<IVpsProviderClient>(new HetznerClient(new HttpClient(cloud, disposeHandler: false),
                new HetznerOptions { ApiToken = "synthetic", BaseUrl = "https://provider.test/v1/" }));
        }
    }

    private sealed class VolumeTransport : HttpMessageHandler
    {
        public string? WorkspaceId { get; set; }
        public int Deletions { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var volume = new
            {
                id = 201, name = "fixture-volume", size = 20, format = "ext4", server = (long?)null,
                location = new { name = "nbg1" }, linux_device = "/dev/disk/by-id/fixture",
                labels = new Dictionary<string, string>
                {
                    ["managed-by"] = "minicloud-workspaces", ["organization"] = "org",
                    ["workspace"] = WorkspaceId!, ["kind"] = "primary"
                }
            };
            if (request.RequestUri!.AbsolutePath == "/v1/volumes")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = JsonContent.Create(new { volumes = Deletions == 0 ? new[] { volume } : [] }) });
            }
            Assert.Equal("/v1/volumes/201", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal(0, Deletions++);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Deletions > 0
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new { volume }) });
        }
    }
}
