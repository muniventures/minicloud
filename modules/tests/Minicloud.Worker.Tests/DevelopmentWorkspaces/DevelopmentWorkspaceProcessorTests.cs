using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.DevelopmentWorkspaces;
using Minicloud.Worker.Pipeline;

namespace Minicloud.Worker.Tests.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessorTests : IAsyncLifetime
{
    private readonly DbContextOptions<MinicloudDbContext>? postgresOptions;

    public DevelopmentWorkspaceProcessorTests() { }

    internal DevelopmentWorkspaceProcessorTests(DbContextOptions<MinicloudDbContext> postgresOptions) =>
        this.postgresOptions = postgresOptions;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly FrozenClock clock = new();
    private readonly FakeCloud cloud = new();
    private readonly FakeSecrets secrets = new();
    private readonly FakeRuntime runtime = new();
    private readonly FakeTunnels tunnels = new();
    private readonly GitHubHandler github = new();
    private readonly DiagnosticLogger diagnostics = new();
    private readonly List<string> shellAuthorizations = [];
    private long t3SnapshotSequence = 7;
    private readonly DevelopmentWorkspaceWorkerOptions workerOptions = new()
    {
        ApiBaseUrl = "https://control.example.test", ManagementAddresses = ["198.51.100.1/32"],
        RelayBindAddress = "127.0.0.1", GatewayDrainSeconds = 75
    };
    private MinicloudDbContext db = default!;
    private DevelopmentWorkspaceService lifecycle = default!;
    private ServiceProvider services = default!;
    private WebApplication t3 = default!;
    private DevelopmentWorkspaceOptions policy = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = Context();
        if (postgresOptions is null) await db.Database.EnsureCreatedAsync();
        else await db.Database.MigrateAsync("20261119160815_DevelopmentWorkspaceDeletion");
        db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
        db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org", Name = "Synthetic",
            ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic", CredentialGeneration = "fixture" });
        db.ProviderPlans.Add(new ProviderPlan { Id = "plan", OrganizationId = "org", ProviderAccountId = "account",
            Name = "Standard", NormalizedName = "standard", Region = "nbg1", Sku = "standard-test" });
        await db.SaveChangesAsync();
        policy = new()
        {
            Enabled = true, MaxActiveVms = 5, MaxVmsPerWorkspace = 3, MaxVmsPerOrganization = 5,
            VolumeMonthlyRatePerGb = .1m, PricingUpdatedAt = clock.GetUtcNow().AddMinutes(-1),
            Profiles = new()
            {
                ["standard"] = new() { CpuCount = 4, MemoryGiB = 8, ComputeHourlyRate = .1m,
                    AllowedPlanIds = ["plan"], AllowedRegions = ["nbg1"], AllowedSkus = ["standard-test"] }
            },
            Images = new()
            {
                ["image"] = new() { Enabled = true, RuntimeQualified = true, QualifiedAt = clock.GetUtcNow().AddMinutes(-1),
                    ProviderAccountId = "account", AllowedRegions = ["nbg1"], ProviderImageId = "image-qualified",
                    ManifestSha256 = new('a', 64), T3ConnectForNewWorkspaces = true }
            },
            Repositories = [new() { OrganizationId = "org", Repository = "https://github.com/fixture-owner/project.git",
                AllowedBaseRefs = ["refs/heads/main"], AllowPush = true }]
        };
        var participant = MaintenanceParticipant.CreateLocal("worker", clock);
        lifecycle = new(db, new IdGenerator(), clock, Options.Create(policy), participant);
        services = new ServiceCollection()
            .AddSingleton(db).AddSingleton(lifecycle)
            .AddSingleton<ProviderAccountClientFactory>(new FakeFactory(cloud))
            .AddSingleton<ISecretStore>(secrets).AddSingleton<IWorkspaceRuntimeTransport>(runtime)
            .AddSingleton<IWorkspaceTunnelManager>(tunnels).AddSingleton<IIdGenerator, IdGenerator>()
            .AddSingleton<TimeProvider>(clock).AddSingleton(participant)
            .AddSingleton<ILogger<DevelopmentWorkspaceProcessor>>(diagnostics)
            .AddSingleton<IOptions<DevelopmentWorkspaceWorkerOptions>>(Options.Create(workerOptions))
            .AddSingleton<IOptions<DevelopmentWorkspaceOptions>>(Options.Create(policy))
            .AddSingleton<IOptions<DeploymentPipelineOptions>>(Options.Create(new DeploymentPipelineOptions
                { SshPublicKey = "ssh-ed25519 AAAAsynthetic-management-key" }))
            .AddSingleton(new WorkspaceGitAccessService(db, secrets, new(new HttpClient(github), clock), clock, Options.Create(policy)))
            .BuildServiceProvider();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        t3 = builder.Build();
        t3.UseWebSockets();
        t3.MapGet("/.well-known/t3/environment", () => Results.Json(new
            { serverVersion = t3Version, orchestrationProtocolVersion = 2, environmentId = "fixture-environment" }));
        t3.MapGet("/api/auth/session", () => Results.Json(new
            { authenticated = true, expiresAt = clock.GetUtcNow().AddMinutes(30).ToString("O") }));
        t3.MapPost("/api/auth/websocket-ticket", () => Results.Json(new { ticket = "fixture-ticket" }));
        t3.MapGet("/api/orchestration/shell", (HttpContext context) =>
        {
            shellAuthorizations.Add(context.Request.Headers.Authorization.ToString());
            return Results.Json(new { schemaVersion = 1, snapshotSequence = t3SnapshotSequence,
                threads = Array.Empty<object>(), archivedThreads = Array.Empty<object>() });
        });
        t3.MapGet("/", () => Results.Ok());
        t3.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest || context.Request.Query["wsTicket"] != "fixture-ticket")
            { context.Response.StatusCode = 401; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            tunnels.WebSocketConnections++;
            var message = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[128]), context.RequestAborted);
            if (message.MessageType == WebSocketMessageType.Close)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
        });
        await t3.StartAsync();
        var address = t3.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        tunnels.Port = new Uri(address).Port;
    }

    private string t3Version = "0.1.9";

    private MinicloudDbContext Context() => new(postgresOptions ??
        new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
    private DevelopmentWorkspaceProcessor Processor() => ActivatorUtilities.CreateInstance<DevelopmentWorkspaceProcessor>(services);

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ConnectPublicationRequiresTrustedAdmissionEvenWithSpoofedHttp200AndCachedReference(bool admitted, bool cached)
    {
        var (workspace, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        runtime.ConnectAdmitted = admitted;
        if (cached)
            db.DevelopmentWorkspaceGatewayRoutes.Add(new()
            {
                Id = "cached-connect", OrganizationId = "org", WorkspaceId = workspace.Id,
                VmId = vm.Id, Generation = vm.Generation, Service = "connect", LoopbackPort = tunnels.Port,
                RegisteredAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddMinutes(30),
                T3CredentialReference = secrets.Add("connect", new string('c', 64))
            });
        await db.SaveChangesAsync();
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{tunnels.Port}/")).StatusCode);

        Assert.True(await Processor().ProcessNextAsync(default));

        var route = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleOrDefaultAsync(x => x.Service == "connect");
        Assert.Equal(1, runtime.ConnectAdmissionChecks);
        Assert.Equal(admitted && !cached ? 1 : 0, runtime.ConnectCredentialReads);
        Assert.Equal(admitted, tunnels.Requests.Contains((774, "connect")));
        if (admitted) Assert.NotNull(route);
        else Assert.True(route is null || route.RevokedAt is not null);
        Assert.Null((await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync(x => x.Service == "t3")).RevokedAt);
        Assert.True((await CurrentVm(vm.Id)).GatewayReady);
        Assert.Equal("ready", (await CurrentVm(vm.Id)).Phase);
    }

    [Theory]
    [InlineData("0.1.9", "0.1.9", 2, false, true)]
    [InlineData("0.1.11", "0.1.11", 2, false, true)]
    [InlineData("0.1.11", "0.1.9", 2, false, false)]
    [InlineData("0.1.11", "0.1.11", 3, false, false)]
    [InlineData("0.1.11", "0.1.11", 2, true, false)]
    public async Task T3ReadinessRequiresTheReservedImageVersionAndProtocol(string registeredVersion,
        string reportedVersion, int protocol, bool changedManifest, bool ready)
    {
        var (_, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        var image = policy.Images[vm.ImageId];
        image.T3Version = registeredVersion;
        image.T3EnvironmentProtocol = protocol;
        if (changedManifest) image.ManifestSha256 = new('b', 64);
        t3Version = reportedVersion;

        Assert.True(await Processor().ProcessNextAsync(default));

        Assert.Equal(ready, (await CurrentVm(vm.Id)).GatewayReady);
        Assert.Equal(ready, await db.DevelopmentWorkspaceGatewayRoutes.AnyAsync(
            route => route.VmId == vm.Id && route.Service == "t3" && route.RevokedAt == null));
    }

    [Fact]
    public async Task ConnectCredentialExportRejectionAfterAdmissionDoesNotHideOrdinaryT3()
    {
        var (_, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        runtime.FailConnectExport = true;
        await db.SaveChangesAsync();

        Assert.True(await Processor().ProcessNextAsync(default));

        Assert.Equal(1, runtime.ConnectAdmissionChecks);
        Assert.Equal(1, runtime.ConnectCredentialReads);
        Assert.False(await db.DevelopmentWorkspaceGatewayRoutes.AnyAsync(x => x.Service == "connect"));
        Assert.Null((await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync(x => x.Service == "t3")).RevokedAt);
        Assert.True((await CurrentVm(vm.Id)).GatewayReady);
        Assert.Equal("ready", (await CurrentVm(vm.Id)).Phase);
    }

    [Fact]
    public async Task CachedConnectRouteIsRevokedWhenTrustedAdmissionFailsDespiteHttp200()
    {
        var (workspace, vm, operation) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        Assert.True(await Processor().ProcessNextAsync(default));
        Assert.Equal(1, runtime.ConnectCredentialReads);
        var reference = (await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking()
            .SingleAsync(x => x.Service == "connect")).T3CredentialReference;
        runtime.ConnectAdmitted = false;
        tunnels.Requests.Clear();

        var refresh = typeof(DevelopmentWorkspaceProcessor).GetMethod("RefreshRuntimeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)refresh.Invoke(Processor(), [workspace, vm, operation.Id, CancellationToken.None, false, true])!;

        var route = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync(x => x.Service == "connect");
        Assert.NotNull(route.RevokedAt);
        Assert.Equal(reference, route.T3CredentialReference);
        Assert.Equal(2, runtime.ConnectAdmissionChecks);
        Assert.Equal(1, runtime.ConnectCredentialReads);
        Assert.DoesNotContain((774, "connect"), tunnels.Requests);
        Assert.True((await CurrentVm(vm.Id)).GatewayReady);
        Assert.Null((await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync(x => x.Service == "t3")).RevokedAt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task BootstrapConfigStopsOnlyToolingAndExplicitlyRegisteredUserWork(bool ephemeral, bool preview)
    {
        var (_, vm, _) = await Seed("bootstrap", ephemeral);
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        vm.EnrollmentTokenExpiresAt = clock.GetUtcNow().AddMinutes(30);
        if (preview)
            workerOptions.PreviewServices["web"] = new()
                { Port = 8080, SupervisedUnit = "minicloud-preview-static.service" };
        await db.SaveChangesAsync();

        await Hop("bootstrap");

        Assert.NotNull(runtime.BootstrapConfiguration);
        var config = runtime.BootstrapConfiguration.Value;
        var stops = config.GetProperty("stopUnits").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(preview
            ? new[] { "minicloud-t3.service", "minicloud-preview-static.service" }
            : ["minicloud-t3.service"], stops);
        Assert.Equal(!ephemeral, config.TryGetProperty("volume", out _));
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal(0, cloud.Detaches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedHeadRecoverySurvivesCheckpointAndRequiresFreshRemoteProofBeforeBootstrap(bool remoteChanged)
    {
        var (vm, operation) = await SeedRecovery("git-recovery-pending");
        vm.BaseCommit = "b89a48cd27b1b7a423c5c9749316017ad3192eeb";
        var recovery = new DevelopmentWorkspaceGitRecoverySource(vm.Repository!, vm.BaseCommit,
            $"refs/heads/minicloud/{vm.Id}/g1", 1, "prior-stop", new('a', 64));
        operation.ProviderEvidenceJson = JsonSerializer.Serialize(new { gitRecovery = recovery }, Json);
        await db.SaveChangesAsync();

        await Hop("bootstrap-secret");

        var checkpoint = await CurrentOperation(operation.Id);
        var retained = JsonSerializer.Deserialize<DevelopmentWorkspaceGitRecoverySource>(
            JsonDocument.Parse(checkpoint.ProviderEvidenceJson!).RootElement.GetProperty("gitRecovery"), Json);
        Assert.Equal(recovery, retained);
        var tracked = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == operation.Id);
        tracked.Step = "bootstrap";
        vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        vm.EnrollmentTokenExpiresAt = clock.GetUtcNow().AddMinutes(30);
        runtime.RecoverySourceChanged = remoteChanged;
        await db.SaveChangesAsync();

        await Hop("bootstrap");

        Assert.Equal(recovery, Assert.Single(runtime.VerifiedRecoverySources));
        if (remoteChanged)
        {
            Assert.Null(runtime.BootstrapConfiguration);
            Assert.Equal("workspace_git_recovery_source_changed", (await CurrentOperation(operation.Id)).ErrorCode);
        }
        else
            Assert.Equal(recovery.Commit, runtime.BootstrapConfiguration!.Value
                .GetProperty("repositories")[0].GetProperty("baseSha").GetString());
        Assert.Equal("blocked", (await CurrentVm(vm.Id)).Phase);
        Assert.Equal(0, cloud.Deletes);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("commit")]
    [InlineData("ref")]
    [InlineData("generation")]
    [InlineData("branch")]
    public async Task RetargetedRecoveryEvidenceCannotReachBootstrap(string mismatch)
    {
        var (vm, operation) = await SeedRecovery("bootstrap");
        vm.BaseCommit = new('c', 40);
        if (mismatch == "branch") vm.WorkBranch = "main";
        var recovery = new DevelopmentWorkspaceGitRecoverySource(vm.Repository!, vm.BaseCommit,
            $"refs/heads/minicloud/{vm.Id}/g1", 1, "prior-stop", new('a', 64));
        recovery = mismatch switch
        {
            "repository" => recovery with { Repository = "https://github.com/fixture-owner/another.git" },
            "commit" => recovery with { Commit = new('d', 40) },
            "ref" => recovery with { RemoteRef = "refs/heads/main" },
            "generation" => recovery with { Generation = 0 },
            _ => recovery
        };
        operation.ProviderEvidenceJson = JsonSerializer.Serialize(new { gitRecovery = recovery }, Json);
        await db.SaveChangesAsync();

        await Hop("bootstrap");

        Assert.Empty(runtime.VerifiedRecoverySources);
        Assert.Null(runtime.BootstrapConfiguration);
        Assert.Equal("blocked", (await CurrentVm(vm.Id)).Phase);
        Assert.Equal(0, cloud.Deletes);
    }

    [Fact]
    public async Task RecoveryStartCannotAdvanceWhenItsPinnedSourceEvidenceIsMissing()
    {
        var (vm, operation) = await SeedRecovery("git-recovery-pending");
        await db.SaveChangesAsync();

        await Hop("git-recovery-pending");

        Assert.Equal("blocked", (await CurrentVm(vm.Id)).Phase);
        Assert.Null(runtime.BootstrapConfiguration);
        Assert.Empty(runtime.VerifiedRecoverySources);
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal("pending", (await CurrentOperation(operation.Id)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconciliationLogsSafeCauseWithoutExceptionOrProviderBody(bool providerFailure)
    {
        var (_, vm, operation) = await Seed("allocate");
        cloud.BeforeCreate = () => throw (providerFailure
            ? new HttpRequestException("secret-bootstrap-provider-body", null, HttpStatusCode.ServiceUnavailable)
            : new KeyNotFoundException("secret-bootstrap-provider-body"));

        await Hop();

        var entry = Assert.Single(diagnostics.Entries);
        Assert.Null(entry.Exception);
        Assert.Contains(operation.Id, entry.Message);
        Assert.Contains("allocate", entry.Message);
        Assert.Contains(providerFailure ? nameof(HttpRequestException) : nameof(KeyNotFoundException), entry.Message);
        if (providerFailure) Assert.Contains("503", entry.Message);
        Assert.DoesNotContain("secret-bootstrap-provider-body", entry.Message);
        Assert.Equal("blocked", (await CurrentVm(vm.Id)).Phase);
        Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        await Hop();
        Assert.Equal(1, cloud.Creates);
    }

    [Fact]
    public async Task LostAllocationResponsePersistsIntentAndReconcilesExactLabelsWithoutSecondCreate()
    {
        var (workspace, vm, operation) = await Seed("allocate");
        cloud.LoseCreateResponse = true;
        cloud.BeforeCreate = async () =>
        {
            await using var verify = Context();
            var durable = await verify.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == operation.Id);
            Assert.True(JsonDocument.Parse(durable.ProviderEvidenceJson!).RootElement.GetProperty("allocationAttempted").GetBoolean());
            Assert.Equal("allocate", durable.Step);
            Assert.NotNull(durable.LeaseToken);
        };
        await Hop();
        Assert.Equal(1, cloud.Creates);
        Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        Assert.Null((await CurrentVm(vm.Id)).ProviderServerId);
        Assert.Equal("pending", (await CurrentOperation(operation.Id)).Status);
        await Hop("server-ready");
        var recovered = await CurrentVm(vm.Id);
        Assert.Equal("server-owned", recovered.ProviderServerId);
        Assert.Equal(1, cloud.Creates);
        Assert.Equal(2, cloud.DiscoveryLabels.Count);
        foreach (var labels in cloud.DiscoveryLabels)
        {
            Assert.Equal(workspace.OrganizationId, labels["organization"]);
            Assert.Equal(workspace.Id, labels["workspace"]);
            Assert.Equal(vm.Id, labels["vm"]);
            Assert.Equal("1", labels["generation"]);
            Assert.Equal(operation.Id, labels["operation"]);
            Assert.Equal("primary", labels["kind"]);
        }
        Assert.Equal(cloud.DiscoveryLabels[0].OrderBy(x => x.Key), cloud.DiscoveryLabels[1].OrderBy(x => x.Key));
    }

    [Fact]
    public async Task AmbiguousAllocationNeverAdoptsAnotherWorkspaceOrIssuesSecondCreate()
    {
        var (_, vm, operation) = await Seed("allocate");
        cloud.LoseCreateResponse = true;
        await Hop();
        var created = cloud.Servers["server-owned"];
        cloud.Servers["server-owned"] = created with { Labels = new Dictionary<string, string>(created.Labels) { ["workspace"] = "foreign" } };
        await Hop();
        Assert.Equal(1, cloud.Creates);
        Assert.Null((await CurrentVm(vm.Id)).ProviderServerId);
        Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        Assert.Equal("allocate", (await CurrentOperation(operation.Id)).Step);
        Assert.Equal("blocked", (await CurrentVm(vm.Id)).Phase);
        Assert.Equal(0, cloud.Deletes);
    }

    [Theory]
    [InlineData(true, "IdleCandidate")]
    [InlineData(false, "Busy")]
    [InlineData(false, null)]
    public async Task ActualRuntimeTasksOrUnknownWorkInventoryKeepRecoveryRouteAndCompute(bool taskRunning, string? inventory)
    {
        var (_, vm, _) = await SeedStop("pending");
        runtime.TaskRunning = taskRunning; runtime.Inventory = inventory;
        await Hop("wait-for-tasks");
        var current = await CurrentVm(vm.Id);
        Assert.True(current.GatewayReady);
        Assert.True(current.ComputeReserved);
        Assert.Equal("draining", current.Phase);
        Assert.Null((await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Null((await db.DevelopmentWorkspaceGatewaySessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.True(tunnels.WebSocketConnections > 0);
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal(0, cloud.Detaches);
        Assert.DoesNotContain("quiesce", runtime.Actions);
    }

    [Fact]
    public async Task DirtyEphemeralGitStateKeepsGenerationRecoverableWithoutAnyVolumeOperation()
    {
        var (_, vm, _) = await SeedStop("pending", ephemeral: true);
        runtime.GitClean = false;
        await Hop("wait-for-save");
        var current = await CurrentVm(vm.Id);
        Assert.Equal("awaiting_git_save", current.Phase);
        Assert.Equal("workspace_git_save_required", current.ErrorCode);
        Assert.True(current.GatewayReady);
        Assert.True(current.ComputeReserved);
        Assert.Null(current.QuiescedGeneration);
        Assert.Null((await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Null((await db.DevelopmentWorkspaceGatewaySessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Contains("git-check", runtime.Actions);
        Assert.DoesNotContain("git-save", runtime.Actions);
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal(0, cloud.Detaches);
        Assert.Equal(0, cloud.VolumeCreates);
        Assert.Equal(0, cloud.Attaches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorAppliesLiveWorkspaceTimeoutAndResumesAfterCountdownCancellation(bool ephemeral)
    {
        var (workspace, vm, start) = await Seed("ready", ephemeral);
        vm.Phase = "ready"; vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        vm.LastActivityAt = clock.GetUtcNow().AddMinutes(-60);
        start.Status = "completed";
        await db.SaveChangesAsync();

        await Processor().MonitorVmAsync(await CurrentVm(vm.Id), default);
        Assert.Null((await CurrentVm(vm.Id)).ShutdownDeadline);
        await lifecycle.UpdateIdleTimeoutAsync(workspace.Id, "org", 60, default);
        await Processor().MonitorVmAsync(await CurrentVm(vm.Id), default);
        var deadline = clock.GetUtcNow().AddSeconds(workerOptions.IdleShutdownDelaySeconds);
        Assert.Equal(deadline, (await CurrentVm(vm.Id)).ShutdownDeadline);

        await lifecycle.UpdateIdleTimeoutAsync(workspace.Id, "org", 90, default);
        Assert.Null((await CurrentVm(vm.Id)).ShutdownDeadline);
        clock.Advance(TimeSpan.FromMinutes(30));
        await Processor().MonitorVmAsync(await CurrentVm(vm.Id), default);
        var current = await CurrentVm(vm.Id);
        Assert.Equal(clock.GetUtcNow(), current.LastHeartbeatAt);
        Assert.Equal(clock.GetUtcNow().AddSeconds(workerOptions.IdleShutdownDelaySeconds), current.ShutdownDeadline);
        Assert.NotNull(current.AllocationOperationId);
        Assert.Equal("running", current.DesiredState);
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal(0, cloud.Detaches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorNeverSchedulesIdleStopWhileKeepRunning(bool ephemeral)
    {
        var (workspace, vm, start) = await Seed("ready", ephemeral);
        vm.Phase = "ready"; vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        vm.LastActivityAt = clock.GetUtcNow().AddDays(-6);
        start.Status = "completed";
        await db.SaveChangesAsync();
        await lifecycle.UpdateIdleTimeoutAsync(workspace.Id, "org", 1, keepRunning: true, default);

        await Processor().MonitorVmAsync(await CurrentVm(vm.Id), default);
        var current = await CurrentVm(vm.Id);
        Assert.Equal(clock.GetUtcNow(), current.LastHeartbeatAt);
        Assert.Null(current.ShutdownDeadline);
        Assert.Equal("running", current.DesiredState);

        await lifecycle.UpdateIdleTimeoutAsync(workspace.Id, "org", 1, default);
        await Processor().MonitorVmAsync(await CurrentVm(vm.Id), default);
        Assert.NotNull((await CurrentVm(vm.Id)).ShutdownDeadline);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StopRetryRefreshesPrivateRuntimeWithoutRevivingExistingGatewayFence(bool ephemeral, bool busy)
    {
        var (_, vm, operation) = await SeedStop("wait-for-tasks", ephemeral);
        if (ephemeral) await EnableGitHubCredentials(vm);
        var tracked = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
        tracked.Phase = "blocked"; tracked.GatewayReady = false;
        tracked.RuntimeTokenExpiresAt = clock.GetUtcNow().AddSeconds(-1);
        if (ephemeral)
        {
            tracked.DiscardGeneration = vm.Generation; tracked.DiscardOperationId = operation.Id;
            tracked.DiscardRequestedAt = clock.GetUtcNow(); tracked.DiscardRequestedBy = "actor";
        }
        operation = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == operation.Id);
        operation.ProviderEvidenceJson = "{}";
        var revokedAt = clock.GetUtcNow().AddMinutes(-2);
        var route = await db.DevelopmentWorkspaceGatewayRoutes.SingleAsync();
        route.RevokedAt = revokedAt;
        var fencedCredentialReference = route.T3CredentialReference;
        var fencedRouteExpiry = route.ExpiresAt;
        var session = await db.DevelopmentWorkspaceGatewaySessions.SingleAsync();
        session.RevokedAt = revokedAt;
        workerOptions.PreviewServices["web"] = new() { Port = 8080 };
        db.DevelopmentWorkspaceGatewayRoutes.Add(new()
        {
            Id = "preview-route", OrganizationId = "org", WorkspaceId = vm.WorkspaceId,
            VmId = vm.Id, Generation = vm.Generation, Service = "web", LoopbackPort = 8080,
            RegisteredAt = revokedAt, ExpiresAt = clock.GetUtcNow().AddMinutes(30), RevokedAt = revokedAt
        });
        await db.SaveChangesAsync();
        runtime.IssuedSessionToken = new string('p', 48);
        runtime.TaskRunning = busy;
        runtime.WorkBranch = vm.WorkBranch ?? "";

        async Task AssertGatewayRemainsFenced()
        {
            Assert.False((await CurrentVm(vm.Id)).GatewayReady);
            var routes = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().ToListAsync();
            Assert.Equal(2, routes.Count);
            Assert.All(routes, current => Assert.Equal(revokedAt, current.RevokedAt));
            var fencedRoute = routes.Single(current => current.Service == "t3");
            Assert.Equal(fencedCredentialReference, fencedRoute.T3CredentialReference);
            Assert.Equal(fencedRouteExpiry, fencedRoute.ExpiresAt);
            Assert.Equal(revokedAt, (await db.DevelopmentWorkspaceGatewaySessions.AsNoTracking().SingleAsync()).RevokedAt);
            Assert.DoesNotContain((8080, "web"), tunnels.Requests);
        }

        await Hop(busy ? "wait-for-tasks" : "gateway-drain");
        var refreshed = await CurrentVm(vm.Id);
        Assert.Equal("draining", refreshed.Phase);
        Assert.True(refreshed.RuntimeTokenExpiresAt > clock.GetUtcNow().AddMinutes(10));
        Assert.Equal(1, runtime.SessionsIssued);
        Assert.True(tunnels.WebSocketConnections > 0);
        await AssertGatewayRemainsFenced();
        if (busy)
        {
            Assert.Empty(shellAuthorizations);
            Assert.DoesNotContain("discard", runtime.Actions);
            Assert.Equal(0, cloud.Deletes);
            return;
        }

        Assert.Equal("Bearer " + runtime.IssuedSessionToken, Assert.Single(shellAuthorizations));
        clock.Advance(TimeSpan.FromSeconds(76));
        await Hop("quiesce");
        t3SnapshotSequence = 23;
        await Hop(ephemeral ? "delete-intent" : "detach");
        var quiesced = await CurrentVm(vm.Id);
        Assert.Equal(vm.Generation, quiesced.QuiescedGeneration);
        Assert.Equal(operation.Id, quiesced.QuiescedOperationId);
        Assert.Contains(ephemeral ? "discard" : "quiesce", runtime.Actions);
        Assert.Equal(t3SnapshotSequence, runtime.LastStopArguments!.Value.GetProperty("t3SnapshotSequence").GetInt64());
        Assert.Equal(3, shellAuthorizations.Count);
        Assert.All(shellAuthorizations, authorization => Assert.Equal("Bearer " + runtime.IssuedSessionToken, authorization));
        await AssertGatewayRemainsFenced();
        Assert.Equal(0, cloud.Deletes);
    }

    [Fact]
    public async Task PublishingRefreshCannotRepublishRevokedGatewayRouteWithoutExplicitRevival()
    {
        var (workspace, vm, operation) = await SeedStop("wait-for-tasks");
        var tracked = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
        tracked.Phase = "ready"; tracked.DesiredState = "running"; tracked.GatewayReady = false;
        var heartbeat = tracked.LastHeartbeatAt;
        var revokedAt = clock.GetUtcNow().AddMinutes(-2);
        (await db.DevelopmentWorkspaceGatewayRoutes.SingleAsync()).RevokedAt = revokedAt;
        (await db.DevelopmentWorkspaceGatewaySessions.SingleAsync()).RevokedAt = revokedAt;
        await db.SaveChangesAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        var refresh = typeof(DevelopmentWorkspaceProcessor).GetMethod("RefreshRuntimeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            (Task)refresh.Invoke(Processor(), [workspace, vm, operation.Id, CancellationToken.None, false, true])!);

        Assert.Equal("A fenced workspace route cannot be revived by monitoring.", error.Message);
        var current = await CurrentVm(vm.Id);
        Assert.False(current.GatewayReady);
        Assert.Equal(heartbeat, current.LastHeartbeatAt);
        Assert.Equal(revokedAt, (await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Equal(revokedAt, (await db.DevelopmentWorkspaceGatewaySessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.True(tunnels.WebSocketConnections > 0);
        Assert.Empty(shellAuthorizations);
        Assert.Equal(0, cloud.Deletes);
    }

    [Theory]
    [InlineData("none", "T3 thread state changed after the HTTP idle snapshot")]
    [InlineData("none", "T3 application state changed after the HTTP idle snapshot")]
    [InlineData("resume", "T3 application state changed after the HTTP idle snapshot")]
    [InlineData("status", "T3 application state changed after the HTTP idle snapshot")]
    public async Task RuntimeRejectionIsDurableBeforeFreezeRecoveryEvenWhenRecoveryFails(
        string failureAction, string reason)
    {
        var recoveryFails = failureAction != "none";
        var (_, vm, operation) = await SeedStop("quiesce", ephemeral: true);
        runtime.Phase = "Frozen";
        runtime.Rejections["git-save"] = JsonSerializer.SerializeToElement(new
        {
            ok = false, state = "Blocked", reason,
            diagnostic = new { stage = "t3-snapshot", failureType = "blocked", credential = "private bearer" },
            password = "private bearer", path = "/private/credential"
        }, Json);
        var persistedBeforeRecovery = false;
        runtime.BeforeAction = action =>
        {
            if (action is not ("status" or "resume") || !runtime.Actions.Contains("git-save")) return;
            using var verify = Context();
            var durable = verify.DevelopmentWorkspaceOperations.AsNoTracking().Single(x => x.Id == operation.Id);
            persistedBeforeRecovery = durable.ProviderEvidenceJson!.Contains("t3_snapshot_changed");
            if (action == failureAction)
                runtime.Rejections[action] = JsonSerializer.SerializeToElement(new
                    { ok = false, state = "private bearer", reason = "/private/credential" }, Json);
        };
        await Hop(recoveryFails ? "quiesce" : "wait-for-tasks");
        Assert.True(persistedBeforeRecovery);
        var saved = await CurrentOperation(operation.Id);
        using var proof = JsonDocument.Parse(saved.ProviderEvidenceJson!);
        var rejection = proof.RootElement.GetProperty("lastRuntimeRejection");
        Assert.Equal("git-save", rejection.GetProperty("action").GetString());
        Assert.Equal("t3_snapshot_changed", rejection.GetProperty("reasonCode").GetString());
        Assert.Equal("t3-snapshot", rejection.GetProperty("stage").GetString());
        if (recoveryFails)
        {
            Assert.Equal(failureAction, proof.RootElement.GetProperty("lastRuntimeRecoveryRejection").GetProperty("action").GetString());
            Assert.Contains("t3_snapshot_changed", saved.ErrorMessage);
            Assert.DoesNotContain("private", saved.ErrorMessage);
        }
        Assert.DoesNotContain("private", saved.ProviderEvidenceJson);
        Assert.DoesNotContain("private", string.Join("\n", diagnostics.Entries.Select(x => x.Message)));
        Assert.Contains(diagnostics.Entries, x => x.Message.Contains("t3_snapshot_changed"));
        Assert.Equal(recoveryFails ? "blocked" : "draining", (await CurrentVm(vm.Id)).Phase);
        Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        Assert.Null((await CurrentVm(vm.Id)).QuiescenceReceipt);
        Assert.Null((await CurrentVm(vm.Id)).GitSaveReceipt);
        Assert.False(proof.RootElement.GetProperty("deleteCommitted").GetBoolean());
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal(0, cloud.Detaches);
    }

    [Fact]
    public async Task PrimaryStopDeletesOnlyOwnedComputeAndRestartReusesRetainedVolume()
    {
        var (workspace, vm, operation) = await SeedStop("quiesce");
        runtime.Phase = "Frozen";
        operation = await CurrentOperation(operation.Id);
        var unrelated = cloud.Servers["server-owned"] with { Id = "ordinary-app", Labels = new Dictionary<string, string>() };
        cloud.Servers.Add(unrelated.Id, unrelated);
        foreach (var expected in new[] { "detach", "detached", "delete-intent", "commit-delete", "delete", "delete-confirm", "delete", "cleanup" })
        {
            await Hop(expected);
            Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        }
        var quiesced = await CurrentVm(vm.Id);
        Assert.Equal(1, quiesced.QuiescedGeneration);
        Assert.Equal(operation.Id, quiesced.QuiescedOperationId);
        Assert.Contains("filesystem-retained", quiesced.QuiescenceReceipt);
        await Hop("stopped");
        var stopped = await CurrentVm(vm.Id);
        Assert.Equal("stopped", stopped.Phase);
        Assert.False(stopped.ComputeReserved);
        Assert.Null(stopped.ProviderServerId);
        Assert.Null(stopped.ProviderIpId);
        Assert.Null(stopped.ProviderFirewallId);
        Assert.Equal("completed", (await CurrentOperation(operation.Id)).Status);
        var retained = await db.DevelopmentWorkspaces.AsNoTracking().SingleAsync(x => x.Id == workspace.Id);
        Assert.Equal("volume-retained", retained.ProviderVolumeId);
        Assert.Equal("filesystem-retained", retained.VolumeFilesystemUuid);
        Assert.Equal("retained", retained.VolumeStatus);
        Assert.Null(retained.VolumeAttachedVmId);
        Assert.Equal("server-owned", Assert.Single(cloud.DeletedServers));
        Assert.Equal("volume-retained", Assert.Single(cloud.DetachedVolumes));
        Assert.Equal("ip-owned", Assert.Single(cloud.DeletedIps));
        Assert.Equal("firewall-owned", Assert.Single(cloud.DeletedFirewalls));
        Assert.True(cloud.Servers.ContainsKey("ordinary-app"));
        Assert.NotNull(cloud.Volume);
        Assert.Null(cloud.Volume!.ServerId);
        Assert.Contains((vm.Id, 1L), tunnels.Removed);

        await lifecycle.StartAsync(workspace.Id, "org", default);
        Assert.Equal(2, (await CurrentVm(vm.Id)).Generation);
        await Hop();
        await Hop();
        await Hop();
        await Hop();
        var restarted = await CurrentVm(vm.Id);
        Assert.Equal("firewall", (await CurrentOperation(restarted.OperationId!)).Step);
        var reused = await CurrentWorkspace(workspace.Id);
        Assert.Equal(retained.ProviderVolumeId, reused.ProviderVolumeId);
        Assert.Equal(retained.VolumeFilesystemUuid, reused.VolumeFilesystemUuid);
        Assert.Equal(0, cloud.VolumeCreates);
        Assert.Empty(cloud.DeletedVolumes);
    }

    [Theory]
    [InlineData(true, "workspace_delete_volume_mismatch")]
    [InlineData(false, "workspace_delete_volume_attached")]
    public async Task PermanentDeleteDoesNotReleaseStorageWhenProviderVolumeIsUnsafe(bool wrongOwner, string errorCode)
    {
        var workspace = await SeedRetainedVolume();
        var original = cloud.Volume!;
        cloud.Volume = wrongOwner
            ? original with { Labels = new Dictionary<string, string>(original.Labels) { ["workspace"] = "unrelated" } }
            : original with { ServerId = "unexpected-writer" };

        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        await Hop();
        var vm = await CurrentVm(workspace.PrimaryVmId);
        Assert.Equal("delete_failed", vm.Phase);
        Assert.Equal(errorCode, vm.ErrorCode);

        var failed = await CurrentWorkspace(workspace.Id);
        Assert.Equal(workspace.ProviderVolumeId, failed.ProviderVolumeId);
        Assert.Equal(workspace.VolumeFilesystemUuid, failed.VolumeFilesystemUuid);
        Assert.Equal(workspace.StorageMonthlyRate, failed.StorageMonthlyRate);
        Assert.NotNull(cloud.Volume);
        Assert.Empty(cloud.DetachedVolumes);
        Assert.Empty(cloud.DeletedVolumes);
        Assert.Empty(cloud.DeletedServers);
    }

    [Fact]
    public Task PermanentDeleteReconcilesLostResponseWithoutTouchingSiblingResources() =>
        VerifyPermanentDeletionAsync(loseResponse: true);

    [Fact]
    public Task PermanentDeleteCompletesWithoutTouchingSiblingResources() =>
        VerifyPermanentDeletionAsync(loseResponse: false);

    [Theory]
    [InlineData("region", "workspace_delete_volume_mismatch")]
    [InlineData("account", "workspace_delete_identity_mismatch")]
    [InlineData("missing", "workspace_delete_volume_unverified")]
    [InlineData("inventory", "workspace_delete_volume_unexpected")]
    public Task PermanentDeleteRejectsUnverifiedStorage(string mismatch, string errorCode) =>
        VerifyUnsafeDeletionAsync(mismatch, errorCode);

    [Fact]
    public Task PermanentDeleteCompletionRequiresCurrentVerifiedOperationAndLease() =>
        VerifyDeletionFencingAsync();

    internal async Task VerifyPermanentDeletionAsync(bool loseResponse)
    {
        var workspace = await SeedRetainedVolume();
        var sibling = await lifecycle.CreateAsync(
            new("org", "Sibling fixture", "image", "standard", "plan", 20, "sibling"), "actor", default);
        var siblingVolume = cloud.Volume! with { Id = "volume-sibling", Labels = VolumeLabels(sibling) };
        sibling.ProviderVolumeId = siblingVolume.Id;
        sibling.VolumeStatus = "retained";
        sibling.LastBackupAt = clock.GetUtcNow();
        sibling.BackupStatus = "completed";
        await db.SaveChangesAsync();
        var siblingLastBackupAt = (await CurrentWorkspace(sibling.Id)).LastBackupAt;
        cloud.SiblingVolumes.Add(siblingVolume.Id, siblingVolume);
        var siblingServer = cloud.Server(VolumeLabels(sibling)) with { Id = "server-sibling" };
        cloud.Servers.Add(siblingServer.Id, siblingServer);
        var imageId = sibling.Vms.Single().ProviderImage;
        cloud.LoseDeleteVolumeResponse = loseResponse;
        AddHistory(await CurrentVm(workspace.PrimaryVmId), 1, clock.GetUtcNow().AddMinutes(-1));
        AddHistory(sibling.Vms.Single(), 1, clock.GetUtcNow().AddMinutes(-1));
        await db.SaveChangesAsync();

        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        await Hop("delete-verified-volume");
        Assert.Empty(cloud.DeletedVolumes);
        await Hop();
        Assert.Equal("volume-retained", Assert.Single(cloud.DeletedVolumes));
        Assert.Null(cloud.Volume);
        if (loseResponse)
        {
            Assert.Equal("deleting", (await CurrentVm(workspace.PrimaryVmId)).Phase);
            var unconfirmed = await CurrentWorkspace(workspace.Id);
            Assert.Equal("volume-retained", unconfirmed.ProviderVolumeId);
            Assert.Equal(workspace.StorageMonthlyRate, unconfirmed.StorageMonthlyRate);
            Assert.False(await Processor().ProcessNextAsync(default));
            clock.Advance(TimeSpan.FromSeconds(5));
            await Hop();
        }

        var deleted = await CurrentWorkspace(workspace.Id);
        Assert.Equal("deleted", (await CurrentVm(workspace.PrimaryVmId)).Phase);
        Assert.Null(deleted.ProviderVolumeId);
        Assert.Null(deleted.VolumeFilesystemUuid);
        Assert.Equal(0m, deleted.StorageMonthlyRate);
        Assert.Single(cloud.DeletedVolumes);
        Assert.Equal(siblingVolume, Assert.Single(cloud.SiblingVolumes).Value);
        Assert.Equal(siblingServer, Assert.Single(cloud.Servers).Value);
        var survivingWorkspace = await CurrentWorkspace(sibling.Id);
        Assert.Equal(siblingVolume.Id, survivingWorkspace.ProviderVolumeId);
        Assert.Equal(siblingLastBackupAt, survivingWorkspace.LastBackupAt);
        Assert.Equal(sibling.BackupStatus, survivingWorkspace.BackupStatus);
        Assert.Equal("available", (await cloud.GetWorkspaceImageAsync(imageId, default))?.Status);
        Assert.Empty(cloud.DetachedVolumes);
        Assert.Empty(cloud.DeletedServers);
        Assert.Empty(cloud.DeletedIps);
        Assert.Empty(cloud.DeletedFirewalls);
        Assert.Equal(sibling.Id, Assert.Single(await db.DevelopmentWorkspaceVmResourceSamples.AsNoTracking().ToListAsync()).WorkspaceId);
        var completed = await CurrentOperation((await CurrentVm(workspace.PrimaryVmId)).OperationId!);
        Assert.Equal("completed", completed.Status);
        Assert.Equal("provider-absence-confirmed", completed.Step);
        Assert.Equal("volume-retained", completed.ProviderResourceId);
        Assert.Null(completed.ErrorCode);
        Assert.Null(completed.LeaseToken);
        var recreated = await lifecycle.CreateAsync(
            new("org", workspace.Name, "image", "standard", "plan", 20, workspace.IdempotencyKey), "actor", default);
        Assert.NotEqual(workspace.Id, recreated.Id);
        Assert.Equal("deleted", Assert.IsType<DevelopmentWorkspace>(
            await lifecycle.GetAsync(workspace.Id, "org", default)).VolumeStatus);
    }

    [Fact]
    public async Task PermanentDeleteOfNeverStartedWorkspaceDoesNotSendProviderDelete()
    {
        var workspace = await lifecycle.CreateAsync(
            new("org", "Never started", "image", "standard", "plan", 20, "never-started"), "actor", default);
        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);

        await Hop();

        Assert.Equal("deleted", (await CurrentVm(workspace.PrimaryVmId)).Phase);
        Assert.Equal(0m, (await CurrentWorkspace(workspace.Id)).StorageMonthlyRate);
        Assert.Empty(cloud.DeletedVolumes);
    }

    [Fact]
    public async Task PermanentDeleteReleasesNameAndCreateIdempotencyKey()
    {
        // The CLI's default create key for organization "org" and name "Reused" (pinned in CliWorkspaceTests).
        CreateDevelopmentWorkspaceRequest request = new("org", "Reused", "image", "standard", "plan", 20, "cli-c8da2dba717feac74db7f3315846c5d05c0ed396");
        var deleted = await lifecycle.CreateAsync(request, "actor", default);
        await lifecycle.DeleteAsync(deleted.Id, "org", true, "actor", default);
        await Hop();
        Assert.Equal("deleted", (await CurrentWorkspace(deleted.Id)).VolumeStatus);

        var recreated = await lifecycle.CreateAsync(request, "actor", default);
        Assert.NotEqual(deleted.Id, recreated.Id);
        Assert.Equal("Reused", recreated.Name);
        Assert.Equal(recreated.Id, (await lifecycle.CreateAsync(request, "actor", default)).Id);
        var conflict = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CreateAsync(request with { VolumeSizeGb = 30 }, "actor", default));
        Assert.Equal("workspace_idempotency_conflict", conflict.Code);
    }

    private async Task<DevelopmentWorkspace> SeedRetainedVolume()
    {
        var workspace = await lifecycle.CreateAsync(
            new("org", "Delete fixture", "image", "standard", "plan", 20, "delete"), "actor", default);
        workspace.ProviderVolumeId = "volume-retained";
        workspace.VolumeFilesystemUuid = "filesystem-retained";
        workspace.VolumeStatus = "retained";
        workspace.Vms.Single().Generation = 1;
        cloud.Volume = new("volume-retained", "retained", 20, "nbg1", null, "ext4", "/dev/disk/by-id/owned",
            VolumeLabels(workspace));
        await db.SaveChangesAsync();
        return workspace;
    }

    [Theory]
    [InlineData("filesystem")]
    [InlineData("generation")]
    public async Task InvalidPrimaryQuiescenceReceiptCannotDetachOrDeleteCompute(string mismatch)
    {
        var (_, vm, operation) = await SeedStop("quiesce");
        runtime.Phase = "Frozen";
        if (mismatch == "filesystem") runtime.ReceiptFilesystemUuid = "another-volume";
        else runtime.ReceiptGenerationDelta = 1;
        await Hop("quiesce");
        var current = await CurrentVm(vm.Id);
        Assert.Equal("blocked", current.Phase);
        Assert.True(current.ComputeReserved);
        Assert.Null(current.QuiescedGeneration);
        Assert.Equal("server-owned", current.ProviderServerId);
        Assert.Equal("pending", (await CurrentOperation(operation.Id)).Status);
        Assert.Equal(0, cloud.Detaches);
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal("server-owned", cloud.Volume!.ServerId);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("generation")]
    public async Task StaleAllocationCompletionCannotMutateVmAfterLeaseOrGenerationChanges(string stale)
    {
        var (_, vm, operation) = await Seed("allocate");
        cloud.BeforeCreate = async () =>
        {
            if (stale == "lease") clock.Advance(TimeSpan.FromSeconds(121));
            else
            {
                await using var changed = Context();
                var replacement = await changed.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
                replacement.Generation++; replacement.Revision++;
                await changed.SaveChangesAsync();
            }
        };
        await Hop();
        var current = await CurrentVm(vm.Id);
        Assert.Equal(stale == "generation" ? 2 : 1, current.Generation);
        Assert.Null(current.ProviderServerId);
        Assert.True(current.ComputeReserved);
        Assert.Equal("allocate", (await CurrentOperation(operation.Id)).Step);
        Assert.Single(cloud.Servers);
        Assert.Equal(0, cloud.Deletes);
    }

    [Fact]
    public async Task RecoveryRefreshPublishesOnlyExplicitHealthyPreviewWithoutT3Credential()
    {
        var (_, vm, _) = await SeedStop("pending");
        runtime.TaskRunning = true;
        workerOptions.PreviewServices["web"] = new() { Port = 8080 };
        await Hop("wait-for-tasks");
        var preview = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync(x => x.Service == "web");
        Assert.Equal(vm.Id, preview.VmId);
        Assert.Equal(vm.Generation, preview.Generation);
        Assert.Null(preview.T3CredentialReference);
        Assert.Null(preview.RevokedAt);
        Assert.Contains((8080, "web"), tunnels.Requests);
        Assert.Equal(0, cloud.Deletes);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("revoke-failure")]
    [InlineData("stage-failure")]
    [InlineData("read-failure")]
    public async Task RotationRetiresOldBeforeReplacementAndNewOnFailure(string scenario)
    {
        var (_, vm, operation) = await SeedStop("pending", ephemeral: true);
        await EnableGitHubCredentials(vm);
        github.FailToken = scenario == "revoke-failure" ? "old-token" : null;
        runtime.FailStage = scenario == "stage-failure";
        runtime.FailRead = scenario == "read-failure";
        await Hop(scenario switch { "success" => "gateway-drain", "read-failure" or "stage-failure" => "pending", _ => "wait-for-save" });
        if (scenario == "read-failure")
            Assert.Equal(0, github.Issued);
        else
        {
            Assert.Contains("revoke:old-token", runtime.Actions);
            if (scenario == "success")
            {
                Assert.True(runtime.Actions.IndexOf("revoke:old-token") < runtime.Actions.IndexOf("stage:new-token-1"));
                Assert.DoesNotContain("revoke:new-token-1", runtime.Actions);
            }
            else
            {
                Assert.Contains("revoke:new-token-1", runtime.Actions);
                if (scenario == "revoke-failure") Assert.DoesNotContain("stage:new-token-1", runtime.Actions);
            }
        }
        Assert.Equal(0, cloud.Deletes);
        Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        var durable = (await CurrentOperation(operation.Id)).ProviderEvidenceJson ?? "";
        Assert.DoesNotContain("old-token", durable);
        Assert.DoesNotContain("new-token", durable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalRetirementFollowsLastReceiptAndPrecedesDurableDeletionAcrossRetry(bool discard)
    {
        var (_, vm, operation) = await SeedStop("quiesce", ephemeral: true);
        await EnableGitHubCredentials(vm);
        runtime.Phase = "Frozen";
        runtime.WorkBranch = vm.WorkBranch!;
        if (discard)
        {
            var tracked = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
            tracked.DiscardGeneration = vm.Generation; tracked.DiscardOperationId = operation.Id;
            tracked.DiscardRequestedAt = clock.GetUtcNow(); tracked.DiscardRequestedBy = "actor";
            await db.SaveChangesAsync();
        }
        await Hop("delete-intent");
        runtime.Actions.Clear();
        github.BeforeRevoke = async token =>
        {
            if (!token.StartsWith("new-token", StringComparison.Ordinal) && token != "old-token") return;
            var evidence = (await CurrentOperation(operation.Id)).ProviderEvidenceJson!;
            Assert.False(JsonDocument.Parse(evidence).RootElement.GetProperty("deleteCommitted").GetBoolean());
            Assert.Equal(0, cloud.Deletes);
        };
        await Hop("commit-delete");
        var lastValidation = runtime.Actions.LastIndexOf("receipt-validate");
        var lastRevocation = runtime.Actions.FindLastIndex(x => x.StartsWith("revoke:", StringComparison.Ordinal));
        Assert.True(lastValidation >= 0 && lastRevocation > lastValidation);
        var proof = JsonDocument.Parse((await CurrentOperation(operation.Id)).ProviderEvidenceJson!).RootElement;
        Assert.True(proof.GetProperty("repositoryCredentialRevoked").GetBoolean());
        Assert.False(proof.GetProperty("deleteCommitted").GetBoolean());
        runtime.FailRead = true;
        runtime.FailReceipt = true;
        var calls = runtime.Actions.Count;
        await Hop("delete");
        Assert.Equal(calls, runtime.Actions.Count);
        Assert.Equal(0, cloud.Deletes);
        await Hop("delete-confirm");
        Assert.Equal(1, cloud.Deletes);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("receipt")]
    [InlineData("missing")]
    [InlineData("openbao")]
    public async Task UnconfirmedFinalRetirementNeverCommitsComputeDeletion(string failure)
    {
        var (_, vm, operation) = await SeedStop("quiesce", ephemeral: true);
        await EnableGitHubCredentials(vm);
        runtime.Phase = "Frozen"; runtime.WorkBranch = vm.WorkBranch!;
        await Hop("delete-intent");
        runtime.Actions.Clear();
        runtime.FailReceipt = failure == "receipt";
        if (failure == "missing") runtime.Credential = null;
        if (failure == "revoke")
            github.BeforeRevoke = token =>
            {
                if (runtime.Actions.Contains("receipt-validate")) github.FailToken = token;
                return Task.CompletedTask;
            };
        if (failure == "openbao")
        {
            var tracked = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
            tracked.RepositoryCredentialRef = JsonSerializer.Serialize(new StoredSecretReference("openbao", "external", "1"));
            // Explicit discard bypasses Git-save, never credential retirement.
            tracked.GitSavedOperationId = null;
            await db.SaveChangesAsync();
        }
        if (failure == "missing")
        {
            var tracked = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
            tracked.GitSavedOperationId = null;
            await db.SaveChangesAsync();
        }
        await Hop("delete-intent");
        var proof = JsonDocument.Parse((await CurrentOperation(operation.Id)).ProviderEvidenceJson!).RootElement;
        Assert.False(proof.GetProperty("deleteCommitted").GetBoolean());
        Assert.False(proof.GetProperty("repositoryCredentialRevoked").GetBoolean());
        Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        Assert.Equal(0, cloud.Deletes);
        if (failure == "receipt") Assert.DoesNotContain(runtime.Actions, x =>
            x.StartsWith("revoke:", StringComparison.Ordinal) && runtime.Actions.IndexOf(x) > runtime.Actions.IndexOf("receipt-validate"));
    }

    [Fact]
    public async Task LostRetirementCheckpointRetriesWithFreshGitValidationAndAlreadyRevokedToken()
    {
        var (_, vm, operation) = await SeedStop("quiesce", ephemeral: true);
        await EnableGitHubCredentials(vm);
        runtime.Phase = "Frozen"; runtime.WorkBranch = vm.WorkBranch!;
        await Hop("delete-intent");
        github.BeforeRevoke = token =>
        {
            if (runtime.Actions.Contains("receipt-validate"))
            {
                clock.Advance(TimeSpan.FromSeconds(121));
                github.BeforeRevoke = null;
            }
            return Task.CompletedTask;
        };
        await Hop("delete-intent");
        Assert.False(JsonDocument.Parse((await CurrentOperation(operation.Id)).ProviderEvidenceJson!)
            .RootElement.GetProperty("repositoryCredentialRevoked").GetBoolean());
        Assert.Equal(0, cloud.Deletes);
        runtime.Actions.Clear();
        await Hop("commit-delete");
        Assert.Contains("already-revoked:new-token-2", runtime.Actions);
        Assert.Contains("receipt-validate", runtime.Actions);
        Assert.True(runtime.Actions.IndexOf("receipt-validate") < runtime.Actions.IndexOf("revoke:new-token-3"));
        Assert.Equal(0, cloud.Deletes);
    }

    private async Task EnableGitHubCredentials(DevelopmentWorkspaceVm vm)
    {
        policy.Repositories[0].GitHubConnectionId = "connection";
        using var rsa = RSA.Create(2048);
        secrets.Add("app", JsonSerializer.Serialize(new GitHubAppCredentials(42, "fixture", 7, "fixture-owner",
            "Organization", rsa.ExportRSAPrivateKeyPem(), "webhook", "client", "secret")));
        db.GitHubAppConnections.Add(new()
        {
            Id = "connection", OrganizationId = "org", Status = "connected", AppId = 42, OwnerId = 7,
            OwnerLogin = "fixture-owner", OwnerType = "Organization", InstallationId = 900,
            LastVerifiedAt = clock.GetUtcNow(), SecretProvider = "openbao", SecretPath = "app", SecretVersion = "1"
        });
        var tracked = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
        tracked.RepositoryCredentialRef = JsonSerializer.Serialize(new StoredSecretReference("github-app", "connection", vm.Repository));
        runtime.Credential = JsonSerializer.SerializeToElement(new
            { origin = vm.Repository, username = "x-access-token", password = "old-token", expiresAt = clock.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds() });
        github.ExpiresAt = clock.GetUtcNow().AddMinutes(50);
        github.Actions = runtime.Actions;
        await db.SaveChangesAsync();
    }

    private async Task<(DevelopmentWorkspaceVm Vm, DevelopmentWorkspaceOperation Operation)> SeedRecovery(string step)
    {
        var (workspace, vm, previous) = await Seed(step, ephemeral: true);
        previous.Status = "completed";
        var operation = new DevelopmentWorkspaceOperation
        {
            Id = "recovery-start", WorkspaceId = workspace.Id, VmId = vm.Id, Generation = 2,
            Kind = "start", Step = step, PlanId = previous.PlanId, ProviderAccountId = previous.ProviderAccountId,
            Region = previous.Region, Sku = previous.Sku, ImageId = previous.ImageId,
            ProviderImage = previous.ProviderImage, ImageManifestSha256 = previous.ImageManifestSha256,
            ComputeHourlyRate = previous.ComputeHourlyRate, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow()
        };
        db.DevelopmentWorkspaceOperations.Add(operation);
        vm.Generation = 2; vm.SshHostKeyGeneration = 2; vm.WorkBranch = $"minicloud/{vm.Id}/g2";
        vm.OperationId = vm.AllocationOperationId = operation.Id;
        return (vm, operation);
    }

    private async Task<(DevelopmentWorkspace Workspace, DevelopmentWorkspaceVm Vm, DevelopmentWorkspaceOperation Operation)> Seed(string step, bool ephemeral = false)
    {
        var workspace = await lifecycle.CreateAsync(new("org", "Worker fixture", "image", "standard", "plan", 20, "create"), "actor", default);
        workspace = ephemeral
            ? await lifecycle.LaunchEphemeralAsync(workspace.Id, "org", new("image", "standard", "plan",
                "https://github.com/fixture-owner/project.git", "refs/heads/main", new('b', 40), true, true, "ephemeral"), "actor", default)
            : await lifecycle.StartAsync(workspace.Id, "org", default);
        var vm = workspace.Vms.Single(x => x.Kind == (ephemeral ? "ephemeral" : "primary"));
        var operation = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId);
        operation.Step = step;
        var bootstrap = new WorkspaceBootstrapSecret("synthetic-private-key", "ssh-rsa synthetic-host-key",
            "SHA256:" + new string('a', 43), "synthetic-enrollment-token");
        vm.SshBootstrapSecretRef = secrets.Add("bootstrap", JsonSerializer.Serialize(bootstrap, Json));
        vm.RuntimeCredentialRef = secrets.Add("t3", new string('t', 48));
        vm.RuntimeTokenExpiresAt = clock.GetUtcNow().AddMinutes(30);
        vm.SshHostKeyGeneration = 1; vm.SshHostKeyFingerprint = bootstrap.HostFingerprint;
        vm.ProviderFirewallId = "firewall-owned";
        vm.LastHeartbeatAt = clock.GetUtcNow(); vm.LastActivityAt = clock.GetUtcNow();
        if (!ephemeral)
        {
            workspace.ProviderVolumeId = "volume-retained"; workspace.VolumeFilesystemUuid = "filesystem-retained";
            workspace.VolumeStatus = "mounted"; workspace.VolumeAttachedVmId = vm.Id;
            cloud.Volume = new("volume-retained", "retained", 20, "nbg1", "server-owned", "ext4", "/dev/disk/by-id/owned",
                VolumeLabels(workspace));
        }
        await db.SaveChangesAsync();
        cloud.NextLabels = ComputeLabels(workspace, vm);
        return (workspace, vm, operation);
    }

    [Theory]
    [InlineData(true, true, "format:virtio-volume-retained")]
    [InlineData(true, false, "probe:virtio-volume-retained")]
    [InlineData(false, false, "probe:scsi-0HC_Volume_volume-retained")]
    public async Task MountFormatsOnlyARawVolumeThisOperationCreated(bool formatInGuest, bool newVolume, string expected)
    {
        var (workspace, vm, operation) = await Seed("mount");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        if (newVolume) workspace.VolumeFilesystemUuid = null;
        operation.ProviderEvidenceJson = JsonSerializer.Serialize(new { newVolume }, Json);
        await db.SaveChangesAsync();
        cloud.FormatInGuest = formatInGuest;
        if (formatInGuest) cloud.Volume = cloud.Volume! with { Filesystem = null };
        runtime.VolumeFilesystemUuid = "filesystem-retained";

        Assert.True(await Processor().ProcessNextAsync(default));

        Assert.Equal([expected], runtime.VolumeCalls);
        var saved = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == operation.Id);
        Assert.Equal("bootstrap", saved.Step);
        Assert.Equal(expected.Split(':')[1], JsonDocument.Parse(saved.ProviderEvidenceJson!).RootElement.GetProperty("volumeDeviceId").GetString());
        Assert.Equal("filesystem-retained", (await db.DevelopmentWorkspaces.AsNoTracking().SingleAsync(x => x.Id == workspace.Id)).VolumeFilesystemUuid);
    }

    [Fact]
    public async Task MountRefusesAProviderVolumeWithoutFilesystemWhenTheProviderFormatsVolumes()
    {
        var (_, vm, operation) = await Seed("mount");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        cloud.Volume = cloud.Volume! with { Filesystem = null };
        runtime.VolumeFilesystemUuid = "filesystem-retained";

        Assert.True(await Processor().ProcessNextAsync(default));

        Assert.Empty(runtime.VolumeCalls);
        Assert.Equal("workspace_reconciliation_required", (await CurrentVm(vm.Id)).ErrorCode);
        Assert.Equal("mount", (await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == operation.Id)).Step);
    }

    [Fact]
    public async Task HeartbeatStoresOnlyValidatedResourceSamplesForTheCurrentGeneration()
    {
        var (_, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        runtime.Capabilities = ["resources-v1", "disk-usage-v1", "directory-delete-v1", "unknown-capability"];
        runtime.Resources = ResourceSample(vm.Id, 1, utilization: 12.5);

        Assert.True(await Processor().ProcessNextAsync(default));

        var stored = await CurrentVm(vm.Id);
        Assert.Equal("directory-delete-v1,disk-usage-v1,resources-v1", stored.RuntimeCapabilities);
        var resources = lifecycle.ToVmResponse(stored).Resources!;
        Assert.Equal("current", resources.State);
        Assert.Equal(12.5, resources.Cpu!.UtilizationPercent);
        Assert.Equal(["/", "/workspace"], resources.Filesystems.Single().Paths);
        Assert.True(resources.DiskUsageSupported && resources.DirectoryDeletionSupported);

        var status = JsonSerializer.SerializeToElement(new { resources = ResourceSample("vm_other", 1, utilization: 12.5) }, Json);
        Assert.Null(DevelopmentWorkspaceService.NormalizeResourceSample(status, vm.Id, 1, clock.GetUtcNow()));
        status = JsonSerializer.SerializeToElement(new { resources = ResourceSample(vm.Id, 1, utilization: 140) }, Json);
        Assert.Null(DevelopmentWorkspaceService.NormalizeResourceSample(status, vm.Id, 1, clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("stale", lifecycle.ToVmResponse(await CurrentVm(vm.Id)).Resources!.State);
    }

    [Fact]
    public void ResourceSamplesKeepStorageTierRolesAndScratchBindMountCounts()
    {
        var observedAt = clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        object Sample(int scratchMounts) => new
        {
            resources = new
            {
                schemaVersion = 1, vmId = "vm-a", generation = 1, observedAt,
                filesystems = new object[]
                {
                    new { paths = new[] { "/" }, roles = new[] { "system", "scratch", "bogus" }, totalBytes = 40L << 30,
                        freeBytes = 30L << 30, availableBytes = 28L << 30, nestedMountsExcluded = 0, scratchBindMounts = 0 },
                    new { paths = new[] { "/workspace" }, roles = new[] { "workspace" }, totalBytes = 100L << 30,
                        freeBytes = 60L << 30, availableBytes = 55L << 30, nestedMountsExcluded = 1, scratchBindMounts = scratchMounts },
                },
                unavailable = new Dictionary<string, string> { ["filesystems.scratch"] = "separate_device" }
            }
        };
        var stored = DevelopmentWorkspaceService.NormalizeResourceSample(
            JsonSerializer.SerializeToElement(Sample(3), Json), "vm-a", 1, clock.GetUtcNow())!;
        using var document = JsonDocument.Parse(stored);
        var filesystems = document.RootElement.GetProperty("filesystems");
        Assert.Equal(["system", "scratch"], filesystems[0].GetProperty("roles").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(3, filesystems[1].GetProperty("scratchBindMounts").GetInt32());
        Assert.Equal("separate_device", document.RootElement.GetProperty("unavailable").GetProperty("filesystems.scratch").GetString());
        Assert.Null(DevelopmentWorkspaceService.NormalizeResourceSample(
            JsonSerializer.SerializeToElement(Sample(-1), Json), "vm-a", 1, clock.GetUtcNow()));
    }

    [Fact]
    public async Task OldImagesReportResourcesAsUnsupportedAndRejectFolderJobs()
    {
        var (workspace, vm) = await SeedFilesystemReady(capabilities: "");
        Assert.Equal("unsupported", lifecycle.ToVmResponse(await CurrentVm(vm.Id)).Resources!.State);
        var error = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() => lifecycle.StartDiskUsageScanAsync(
            workspace.Id, "org", vm.Id, new(1, "scan-key-0001"), "actor", default));
        Assert.Equal("workspace_disk_usage_unsupported", error.Code);
    }

    [Fact]
    public async Task ConfirmedFolderDeletionUsesMeasuredIdentityAndNeverRelaunchesAfterALostReply()
    {
        var (workspace, vm) = await SeedFilesystemReady();
        var root = await lifecycle.StartDiskUsageScanAsync(workspace.Id, "org", vm.Id, new(1, "scan-key-0001"), "actor", default);
        Assert.Equal(root.Id, (await lifecycle.StartDiskUsageScanAsync(workspace.Id, "org", vm.Id, new(1, "scan-key-0001"), "actor", default)).Id);
        await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() => lifecycle.StartDiskUsageScanAsync(
            workspace.Id, "org", vm.Id, new(1, "scan-key-0002"), "actor", default));
        runtime.FilesystemResult = ScanResult([], 11, 22, ("repos", 900L, true, false), ("lost+found", 0L, false, false));
        await Processor().FilesystemNextAsync(default);
        var start = runtime.FilesystemRequests.Single();
        Assert.Equal("scan", start.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, start.GetProperty("expect").ValueKind);
        var scanned = lifecycle.ToFilesystemJobResponse(await lifecycle.GetFilesystemJobAsync(workspace.Id, "org", vm.Id, root.Id, default));
        Assert.Equal("succeeded", scanned.State);
        Assert.Null(scanned.DiskUsage!.Entries.Single(x => x.Name == "lost+found").TargetToken);
        var repos = scanned.DiskUsage.Entries.Single(x => x.Name == "repos");
        Assert.Equal("/workspace/repos", repos.Path);
        Assert.False(repos.CanDelete);
        Assert.Equal("protected_path", repos.ProtectedReason);
        await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() => lifecycle.StartDirectoryDeletionAsync(workspace.Id, "org", vm.Id,
            new(1, repos.TargetToken!, "/workspace/repos", true, "delete-key-01"), "actor", default));

        runtime.FilesystemResult = null;
        var nested = await lifecycle.StartDiskUsageScanAsync(workspace.Id, "org", vm.Id, new(1, "scan-key-0003", repos.TargetToken), "actor", default);
        await Processor().FilesystemNextAsync(default);
        Assert.Equal(33, runtime.FilesystemRequests[^1].GetProperty("expect").GetProperty("ino").GetInt64());
        runtime.FilesystemResult = ScanResult(["repos"], 11, 33, ("project", 700L, true, true));
        await Processor().FilesystemNextAsync(default);
        Assert.Equal("fs-job-status", runtime.Actions[^1]);
        var project = lifecycle.ToFilesystemJobResponse(await lifecycle.GetFilesystemJobAsync(workspace.Id, "org", vm.Id, nested.Id, default))
            .DiskUsage!.Entries.Single();
        Assert.True(project.CanDelete);

        foreach (var (confirmation, acknowledged, code) in new[]
                 {
                     ("/workspace/repos/Project", true, "workspace_deletion_confirmation_mismatch"),
                     ("/workspace/repos/project", false, "workspace_deletion_not_acknowledged")
                 })
        {
            var refused = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() => lifecycle.StartDirectoryDeletionAsync(
                workspace.Id, "org", vm.Id, new(1, project.TargetToken!, confirmation, acknowledged, "delete-key-" + code.Length), "actor", default));
            Assert.Equal(code, refused.Code);
        }
        var forged = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() => lifecycle.StartDirectoryDeletionAsync(workspace.Id, "org",
            vm.Id, new(1, nested.Id + ".forged", "/workspace/repos/project", true, "delete-key-02"), "actor", default));
        Assert.Equal("workspace_folder_selection_expired", forged.Code);

        var deletion = await lifecycle.StartDirectoryDeletionAsync(workspace.Id, "org", vm.Id,
            new(1, project.TargetToken!, "/workspace/repos/project", true, "delete-key-03"), "actor", default);
        Assert.Equal(deletion.Id, (await lifecycle.StartDirectoryDeletionAsync(workspace.Id, "org", vm.Id,
            new(1, project.TargetToken!, "/workspace/repos/project", true, "delete-key-03"), "actor", default)).Id);
        runtime.FilesystemResult = null;
        runtime.FailFilesystemStart = true;
        await Processor().FilesystemNextAsync(default);
        runtime.FailFilesystemStart = false;
        runtime.FilesystemResult = new
        {
            schemaVersion = 1, mode = "delete", policyVersion = 1, path = new[] { "repos", "project" }, outcome = "succeeded",
            removedEntries = 12, removedBytes = 700, limitReached = (string?)null, errors = new { }
        };
        await Processor().FilesystemNextAsync(default);

        var deleteRequest = runtime.FilesystemRequests[^2];
        Assert.Equal("delete", deleteRequest.GetProperty("mode").GetString());
        Assert.Equal(["repos", "project"], deleteRequest.GetProperty("path").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(44, deleteRequest.GetProperty("expect").GetProperty("ino").GetInt64());
        Assert.Equal(1, runtime.FilesystemRequests.Count(x => x.GetProperty("jobId").GetString() == deletion.Id
            && x.TryGetProperty("mode", out _)));
        var done = lifecycle.ToFilesystemJobResponse(await lifecycle.GetFilesystemJobAsync(workspace.Id, "org", vm.Id, deletion.Id, default));
        Assert.Equal("succeeded", done.State);
        Assert.Equal(700, done.Deletion!.RemovedBytes);
        var invalidated = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() => lifecycle.StartDiskUsageScanAsync(
            workspace.Id, "org", vm.Id, new(1, "scan-key-0005", project.TargetToken), "actor", default));
        Assert.Equal("workspace_folder_selection_expired", invalidated.Code);
        var audit = await db.AuditEvents.AsNoTracking().Where(x => x.Action.StartsWith("development_workspace.folder_deletion"))
            .Select(x => x.Action).ToListAsync();
        Assert.Equal(["development_workspace.folder_deletion_completed", "development_workspace.folder_deletion_requested"], audit.Order());

        clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() => lifecycle.StartDiskUsageScanAsync(
            workspace.Id, "org", vm.Id, new(1, "scan-key-0004", repos.TargetToken), "actor", default));
        Assert.Equal("workspace_folder_selection_expired", expired.Code);
        clock.Advance(TimeSpan.FromDays(1));
        await Processor().FilesystemNextAsync(default);
        Assert.Equal([deletion.Id], await db.DevelopmentWorkspaceFilesystemJobs.AsNoTracking().Select(x => x.Id).ToListAsync());
    }

    [Fact]
    public async Task StoppingTheDevEnvironmentEndsRunningFolderJobsWithoutGuessingDeletionOutcomes()
    {
        var (workspace, vm) = await SeedFilesystemReady();
        var scan = await lifecycle.StartDiskUsageScanAsync(workspace.Id, "org", vm.Id, new(1, "scan-key-0001"), "actor", default);
        await Processor().FilesystemNextAsync(default);
        var cancelled = await lifecycle.CancelFilesystemJobAsync(workspace.Id, "org", vm.Id, scan.Id, default);
        Assert.True(cancelled.CancelRequested);
        await Processor().FilesystemNextAsync(default);
        Assert.Equal("fs-job-cancel", runtime.Actions[^1]);

        var stored = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
        stored.Phase = "draining"; stored.DesiredState = "stopped";
        await db.SaveChangesAsync();
        await Processor().FilesystemNextAsync(default);
        var job = lifecycle.ToFilesystemJobResponse(await lifecycle.GetFilesystemJobAsync(workspace.Id, "org", vm.Id, scan.Id, default));
        Assert.Equal("failed", job.State);
        Assert.Equal("dev_environment_stopped", job.ErrorCode);
    }

    private async Task<(DevelopmentWorkspace Workspace, DevelopmentWorkspaceVm Vm)> SeedFilesystemReady(
        string capabilities = "directory-delete-v1,disk-usage-v1,resources-v1")
    {
        var (workspace, vm, start) = await Seed("ready");
        vm.Phase = "ready"; vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10"; vm.GatewayReady = true;
        start.Status = "completed";
        vm.RuntimeCapabilities = capabilities; vm.ResourceSampleGeneration = 1; vm.ResourceSampleReceivedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (workspace, vm);
    }

    private object ResourceSample(string vmId, long generation, double utilization) => new
    {
        schemaVersion = 1, vmId, generation, observedAt = clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0,
        cpu = new { logicalCount = 4, utilizationPercent = utilization, windowSeconds = 30.0 },
        memory = new { totalBytes = 8L << 30, availableBytes = 6L << 30 },
        filesystems = new[] { new { paths = new[] { "/", "/workspace" }, totalBytes = 40L << 30, freeBytes = 30L << 30,
            availableBytes = 28L << 30, inodesTotal = 1000L, inodesAvailable = 900L, nestedMountsExcluded = 0 } },
        unavailable = new { }
    };

    private static object ScanResult(string[] path, long dev, long ino, params (string Name, long Bytes, bool Selectable, bool Deletable)[] entries) => new
    {
        schemaVersion = 1, mode = "scan", policyVersion = 1, path, target = new { dev, ino, uid = 2000 }, complete = true,
        limitReached = (string?)null, totalBytes = entries.Sum(x => x.Bytes), filesHereBytes = 0, omittedEntries = 0, omittedBytes = 0,
        scannedEntries = entries.Length, hardLinksApproximate = false, errors = new { }, elapsedSeconds = 0.1,
        entries = entries.Select(x => new
        {
            name = x.Name, bytes = x.Bytes, state = "complete", errors = Array.Empty<string>(), selectable = x.Selectable,
            deletable = x.Deletable, uid = 2000, dev, ino = ino + 11
        }).ToArray()
    };

    private async Task<(DevelopmentWorkspace Workspace, DevelopmentWorkspaceVm Vm, DevelopmentWorkspaceOperation Operation)> SeedStop(string step, bool ephemeral = false)
    {
        var (workspace, vm, start) = await Seed("ready", ephemeral);
        vm.Phase = "ready"; vm.ProviderServerId = "server-owned"; vm.ProviderIpId = "ip-owned";
        vm.IpAddress = "198.51.100.10"; vm.GatewayReady = true; vm.EnrolledAt = clock.GetUtcNow();
        start.Status = "completed";
        cloud.Servers["server-owned"] = cloud.Server(cloud.NextLabels);
        await db.SaveChangesAsync();
        workspace = await lifecycle.StopVmAsync(workspace.Id, "org", vm.Id, 1, default);
        vm = workspace.Vms.Single(x => x.Id == vm.Id);
        var stop = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId);
        stop.Step = step;
        stop.ProviderEvidenceJson = JsonSerializer.Serialize(new
            { gatewayRevokedAt = clock.GetUtcNow().AddSeconds(-76), t3SnapshotSequence = 7 }, Json);
        db.DevelopmentWorkspaceGatewayRoutes.Add(new() { Id = "t3-route", OrganizationId = "org", WorkspaceId = workspace.Id,
            VmId = vm.Id, Generation = 1, LoopbackPort = tunnels.Port, RegisteredAt = clock.GetUtcNow(),
            ExpiresAt = clock.GetUtcNow().AddMinutes(30), T3CredentialReference = vm.RuntimeCredentialRef });
        db.DevelopmentWorkspaceGatewaySessions.Add(new() { Id = "t3-session", OrganizationId = "org", WorkspaceId = workspace.Id,
            VmId = vm.Id, Generation = 1, LaunchDigest = "synthetic-launch-digest", SessionDigest = "synthetic-session-digest",
            LaunchExpiresAt = clock.GetUtcNow().AddMinutes(1), SessionExpiresAt = clock.GetUtcNow().AddMinutes(30) });
        await db.SaveChangesAsync();
        return (workspace, vm, stop);
    }

    private async Task Hop(string? expectedStep = null)
    {
        Assert.True(await Processor().ProcessNextAsync(default));
        if (expectedStep is not null)
        {
            var operation = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Kind == "stop" || x.Status != "completed");
            Assert.True(operation.Step == expectedStep, $"Expected {expectedStep}; got {operation.Step}/{operation.Status}/{operation.ErrorCode}.");
        }
    }

    private Task<DevelopmentWorkspaceVm> CurrentVm(string id) =>
        db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync(x => x.Id == id);
    private Task<DevelopmentWorkspace> CurrentWorkspace(string id) =>
        db.DevelopmentWorkspaces.AsNoTracking().SingleAsync(x => x.Id == id);
    private Task<DevelopmentWorkspaceOperation> CurrentOperation(string id) =>
        db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == id);
    private static Dictionary<string, string> VolumeLabels(DevelopmentWorkspace workspace) => new()
        { ["managed-by"] = "minicloud-workspaces", ["organization"] = workspace.OrganizationId, ["workspace"] = workspace.Id, ["kind"] = "primary" };
    private static Dictionary<string, string> ComputeLabels(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm) =>
        new(VolumeLabels(workspace)) { ["kind"] = vm.Kind, ["vm"] = vm.Id, ["generation"] = "1", ["operation"] = vm.AllocationOperationId! };

    public async Task DisposeAsync()
    {
        if (t3 is not null) await t3.DisposeAsync();
        if (services is not null) await services.DisposeAsync();
        else if (db is not null) await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private sealed class FrozenClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FakeTunnels : IWorkspaceTunnelManager
    {
        public int Port { get; set; }
        public int WebSocketConnections { get; set; }
        public List<(int, string)> Requests { get; } = [];
        public List<(string, long)> Removed { get; } = [];
        public Task<int> EnsureAsync(WorkspaceRuntimeTarget target, CancellationToken ct, int remotePort = 3773, string service = "t3")
        { Requests.Add((remotePort, service)); return Task.FromResult(Port); }
        public Task RemoveAsync(string vmId, long generation, CancellationToken ct)
        { Removed.Add((vmId, generation)); return Task.CompletedTask; }
    }

    private sealed class FakeRuntime : IWorkspaceRuntimeTransport
    {
        public bool ConnectAdmitted { get; set; } = true;
        public bool FailConnectExport { get; set; }
        public int ConnectAdmissionChecks { get; private set; }
        public int ConnectCredentialReads { get; private set; }
        public bool TaskRunning { get; set; }
        public string? Inventory { get; set; } = "IdleCandidate";
        public bool GitClean { get; set; } = true;
        public string Phase { get; set; } = "Ready";
        public string ReceiptFilesystemUuid { get; set; } = "filesystem-retained";
        public int ReceiptGenerationDelta { get; set; }
        public string WorkBranch { get; set; } = "";
        public string IssuedSessionToken { get; set; } = new string('t', 48);
        public int SessionsIssued { get; private set; }
        public JsonElement? LastStopArguments { get; private set; }
        public JsonElement? Credential { get; set; }
        public bool FailStage { get; set; }
        public bool FailRead { get; set; }
        public bool FailReceipt { get; set; }
        public JsonElement? BootstrapConfiguration { get; private set; }
        public bool RecoverySourceChanged { get; set; }
        public List<DevelopmentWorkspaceGitRecoverySource> VerifiedRecoverySources { get; } = [];
        public Dictionary<string, JsonElement> Rejections { get; } = [];
        public Action<string>? BeforeAction { get; set; }
        public List<string> Actions { get; } = [];
        public object? Resources { get; set; }
        public string[]? Capabilities { get; set; }
        public List<JsonElement> FilesystemRequests { get; } = [];
        public object? FilesystemResult { get; set; }
        public bool FailFilesystemStart { get; set; }
        public Task<JsonElement> CallAsync(WorkspaceRuntimeTarget target, string operationId, string action, object? arguments, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrEmpty(operationId);
            Actions.Add(action);
            BeforeAction?.Invoke(action);
            if (action is "quiesce" or "git-save" or "discard")
                LastStopArguments = JsonSerializer.SerializeToElement(arguments, Json);
            if (Rejections.TryGetValue(action, out var rejection)) return Task.FromResult(rejection);
            if (action is "resume" or "cancel-drain") Phase = "Ready";
            if (action == "receipt-validate" && FailReceipt) throw new InvalidOperationException("Receipt was not confirmed.");
            if (action == "status")
            {
                var result = new Dictionary<string, object?>
                {
                    ["vmId"] = target.VmId, ["generation"] = target.Generation, ["kind"] = target.Kind, ["phase"] = Phase,
                    ["tasks"] = TaskRunning ? new Dictionary<string, object> { ["runtime-task"] = new { state = "Running" } } : new()
                };
                if (Inventory is not null) result["workInventory"] = new { state = Inventory };
                if (Resources is not null) result["resources"] = Resources;
                if (Capabilities is not null) result["capabilities"] = Capabilities;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result }, Json));
            }
            if (action.StartsWith("fs-job-", StringComparison.Ordinal))
            {
                FilesystemRequests.Add(JsonSerializer.SerializeToElement(arguments, Json));
                if (action == "fs-job-start" && FailFilesystemStart) throw new IOException("Synthetic lost reply.");
                object job = FilesystemResult is null ? new { state = "running" } : new { state = "completed", result = FilesystemResult };
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = job }, Json));
            }
            if (action == "git-check" && !GitClean)
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = false, state = "AwaitingGitSave" }, Json));
            if (action == "quiesce")
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = new
                {
                    schemaVersion = 1, vmId = target.VmId, generation = target.Generation + ReceiptGenerationDelta, kind = target.Kind,
                    operationId, receiptId = "receipt-owned", type = "PrimaryQuiesced", unmounted = true,
                    filesystemUuid = ReceiptFilesystemUuid
                } }, Json));
            if (action is "git-save" or "discard")
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = new
                {
                    schemaVersion = 1, vmId = target.VmId, generation = target.Generation, kind = target.Kind,
                    operationId, receiptId = "receipt-owned", type = action == "git-save" ? "GitSaved" : "DiscardAuthorized",
                    repositories = new[] { new { path = "/workspace/repos/project", headSha = new string('c', 40),
                        remoteRef = "refs/heads/" + WorkBranch } }
                } }, Json));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = new { receiptId = "receipt-owned" } }, Json));
        }
        public string? VolumeFilesystemUuid { get; set; }
        public List<string> VolumeCalls { get; } = [];
        public Task<JsonElement> VolumeProbeAsync(WorkspaceRuntimeTarget target, string volumeId, CancellationToken ct) =>
            Volume("probe", volumeId);
        public Task<JsonElement> VolumeFormatAsync(WorkspaceRuntimeTarget target, string volumeId, CancellationToken ct) =>
            Volume("format", volumeId);
        private Task<JsonElement> Volume(string action, string volumeId)
        {
            if (VolumeFilesystemUuid is null) throw new InvalidOperationException("Volume access was not expected in this stage.");
            VolumeCalls.Add(action + ":" + volumeId);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = new { filesystemUuid = VolumeFilesystemUuid } }, Json));
        }
        public Task<JsonElement> BootstrapAsync(WorkspaceRuntimeTarget target, object config, object enrollment, CancellationToken ct)
        {
            BootstrapConfiguration = JsonSerializer.SerializeToElement(config, Json);
            throw new InvalidOperationException("Bootstrap was not expected in this stage.");
        }
        public Task VerifyGitRecoverySourceAsync(WorkspaceRuntimeTarget target, DevelopmentWorkspaceGitRecoverySource source, CancellationToken ct)
        {
            VerifiedRecoverySources.Add(source);
            if (RecoverySourceChanged) throw new DevelopmentWorkspaceException(
                "workspace_git_recovery_source_changed", "Synthetic changed saved source.");
            return Task.CompletedTask;
        }
        public Task<bool> CheckConnectAdmissionAsync(WorkspaceRuntimeTarget target, CancellationToken ct)
        {
            ConnectAdmissionChecks++;
            return Task.FromResult(ConnectAdmitted);
        }
        public Task<string> ReadConnectCredentialAsync(WorkspaceRuntimeTarget target, CancellationToken ct)
        {
            ConnectCredentialReads++;
            if (FailConnectExport) throw new InvalidOperationException("Management listener no longer admitted.");
            return Task.FromResult(new string('c', 64));
        }

        public Task<string> IssueT3SessionAsync(WorkspaceRuntimeTarget target, string operationId, int ttlSeconds, CancellationToken ct)
        {
            SessionsIssued++;
            return Task.FromResult(IssuedSessionToken);
        }
        public Task StageRepositoryCredentialAsync(WorkspaceRuntimeTarget target, JsonElement credential, CancellationToken ct)
        {
            Actions.Add("stage:" + credential.GetProperty("password").GetString());
            if (FailStage) throw new InvalidOperationException("Staging failed.");
            Credential = credential.Clone();
            return Task.CompletedTask;
        }
        public Task<JsonElement?> ReadRepositoryCredentialAsync(WorkspaceRuntimeTarget target, CancellationToken ct)
        {
            Actions.Add("read-credential");
            if (FailRead) throw new InvalidOperationException("Readback failed.");
            return Task.FromResult(Credential);
        }
    }

    private sealed class GitHubHandler : HttpMessageHandler
    {
        public DateTimeOffset ExpiresAt { get; set; }
        public List<string> Actions { get; set; } = [];
        public string? FailToken { get; set; }
        public int Issued { get; private set; }
        public Func<string, Task>? BeforeRevoke { get; set; }
        private readonly HashSet<string> revoked = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Delete && path == "/installation/token")
            {
                var token = request.Headers.Authorization!.Parameter!;
                if (BeforeRevoke is not null) await BeforeRevoke(token);
                Actions.Add("revoke:" + token);
                if (token == FailToken) return new(HttpStatusCode.ServiceUnavailable);
                if (!revoked.Add(token))
                {
                    Actions.Add("already-revoked:" + token);
                    return new(HttpStatusCode.Unauthorized);
                }
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get && path == "/app/installations/900")
                return Response(new { id = 900, app_id = 42, account = new { id = 7, login = "fixture-owner", type = "Organization" },
                    suspended_at = (string?)null, permissions = new { contents = "write" } });
            if (request.Method == HttpMethod.Get && path == "/repos/fixture-owner/project")
                return Response(new { id = 123, full_name = "fixture-owner/project",
                    clone_url = "https://github.com/fixture-owner/project.git", owner = new { id = 7 } });
            if (request.Method == HttpMethod.Post && path == "/app/installations/900/access_tokens")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (body.RootElement.TryGetProperty("repositories", out _))
                    return Response(new { token = "metadata-token" });
                return Response(new { token = "new-token-" + ++Issued, expires_at = ExpiresAt,
                    permissions = new { contents = "write", metadata = "read" }, repositories = new[] { new { id = 123 } } });
            }
            throw new InvalidOperationException("Unexpected mocked GitHub request.");
        }
        private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    private sealed class FakeSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = [];
        public string Add(string name, string value)
        {
            values[name] = value;
            return JsonSerializer.Serialize(new StoredSecretReference("openbao", name, "1"), Json);
        }
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct) => Task.FromResult(values[reference.Path]);
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct)
        {
            var path = OpenBaoSecretStore.IntegrationPathFor("tests", request.OrganizationId, request.IntegrationId!, request.CredentialGeneration!);
            values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference("openbao", path, "1"));
        }
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct) { values.Remove(reference.Path); return Task.CompletedTask; }
    }

    private sealed class FakeFactory(FakeCloud cloud) : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, NullLoggerFactory.Instance)
    {
        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId, CancellationToken ct, bool allowDisabled = true)
        {
            Assert.Equal("org", organizationId); Assert.Equal("account", providerAccountId);
            return Task.FromResult<IVpsProviderClient>(cloud);
        }
    }

    private sealed class DiagnosticLogger : ILogger<DevelopmentWorkspaceProcessor>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((formatter(state, exception), exception));
    }

    private sealed class FakeCloud : IVpsProviderClient, IDevelopmentWorkspaceProvider
    {
        public CloudProvider Provider => CloudProvider.Hetzner;
        public Dictionary<string, WorkspaceCloudServer> Servers { get; } = [];
        public WorkspaceCloudVolume? Volume { get; set; }
        public Dictionary<string, WorkspaceCloudVolume> SiblingVolumes { get; } = [];
        public Dictionary<string, string> NextLabels { get; set; } = [];
        public List<Dictionary<string, string>> DiscoveryLabels { get; } = [];
        public List<string> DeletedServers { get; } = [];
        public List<string> DetachedVolumes { get; } = [];
        public List<string> DeletedVolumes { get; } = [];
        public List<string> DeletedIps { get; } = [];
        public List<string> DeletedFirewalls { get; } = [];
        public int Creates { get; private set; }
        public int Deletes => DeletedServers.Count;
        public int Detaches => DetachedVolumes.Count;
        public int VolumeCreates { get; private set; }
        public int Attaches { get; private set; }
        public bool LoseCreateResponse { get; set; }
        public bool LoseDeleteVolumeResponse { get; set; }
        public Func<Task>? BeforeCreate { get; set; }
        public bool FormatInGuest { get; set; }
        bool IDevelopmentWorkspaceProvider.WorkspaceVolumesFormatInGuest => FormatInGuest;
        string IDevelopmentWorkspaceProvider.GetWorkspaceVolumeDeviceId(WorkspaceCloudVolume volume) =>
            FormatInGuest ? "virtio-" + volume.Id : "scsi-0HC_Volume_" + volume.Id;
        public WorkspaceCloudServer Server(IReadOnlyDictionary<string, string> labels) =>
            new("server-owned", "owned", "running", "nbg1", "standard-test", "image-qualified",
                "198.51.100.10", "ip-owned", new Dictionary<string, string>(labels), ["firewall-owned"], true);
        public Task<IReadOnlyList<WorkspaceCloudServer>> FindWorkspaceServersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            DiscoveryLabels.Add(new(labels));
            return Task.FromResult<IReadOnlyList<WorkspaceCloudServer>>(Servers.Values.Where(x =>
                labels.All(label => x.Labels.TryGetValue(label.Key, out var value) && value == label.Value)).ToArray());
        }
        public Task<WorkspaceCloudServer?> GetWorkspaceServerAsync(string id, CancellationToken ct) => Task.FromResult(Servers.GetValueOrDefault(id));
        public async Task<WorkspaceCloudAllocation> CreateWorkspaceServerAsync(WorkspaceCloudCreate request, CancellationToken ct)
        {
            Creates++;
            if (BeforeCreate is not null) await BeforeCreate();
            var server = Server(request.Labels); Servers[server.Id] = server;
            if (LoseCreateResponse) { LoseCreateResponse = false; throw new HttpRequestException("Synthetic response lost AFTER allocation."); }
            return new(server, new("servers", "created", "running"));
        }
        public Task<WorkspaceCloudAction?> DeleteWorkspaceServerAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            Assert.Equal("server-owned", id);
            Assert.All(labels, pair => Assert.Equal(pair.Value, Servers[id].Labels[pair.Key]));
            DeletedServers.Add(id); Servers.Remove(id);
            return Task.FromResult<WorkspaceCloudAction?>(new("servers", "deleted", "running"));
        }
        public Task SecureWorkspacePrimaryIpAsync(WorkspaceCloudServer server, IReadOnlyDictionary<string, string> labels, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteWorkspacePrimaryIpAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        { DeletedIps.Add(id); return Task.CompletedTask; }
        public Task<WorkspaceCloudImage?> GetWorkspaceImageAsync(string id, CancellationToken ct) =>
            Task.FromResult<WorkspaceCloudImage?>(new(id, "available", "snapshot", "x86", "ubuntu", new Dictionary<string, string>()));
        public Task<WorkspaceCloudAction> GetWorkspaceActionAsync(string resource, string id, CancellationToken ct) =>
            Task.FromResult(new WorkspaceCloudAction(resource, id, "success"));
        public Task<IReadOnlyList<WorkspaceCloudVolume>> FindWorkspaceVolumesAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkspaceCloudVolume>>(
                (Volume is { } volume ? SiblingVolumes.Values.Append(volume) : SiblingVolumes.Values)
                .Where(x => labels.All(label => x.Labels.TryGetValue(label.Key, out var value) && value == label.Value)).ToArray());
        public Task<WorkspaceCloudVolume?> GetWorkspaceVolumeAsync(string id, CancellationToken ct) =>
            Task.FromResult(Volume?.Id == id ? Volume : SiblingVolumes.GetValueOrDefault(id));
        public async Task DeleteWorkspaceVolumeAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            var volume = await GetWorkspaceVolumeAsync(id, ct);
            if (volume is null) return;
            HetznerClient.RequireWorkspaceOwnership(volume.Labels, labels);
            if (volume.ServerId is not null) throw new InvalidOperationException("Volume still has a writer.");
            DeletedVolumes.Add(id);
            if (Volume?.Id == id) Volume = null;
            else SiblingVolumes.Remove(id);
            if (LoseDeleteVolumeResponse)
            {
                LoseDeleteVolumeResponse = false;
                throw new HttpRequestException("Synthetic response lost AFTER volume deletion.");
            }
        }
        public Task<WorkspaceCloudVolumeAllocation> CreateWorkspaceVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        { VolumeCreates++; throw new InvalidOperationException("Retained volume must not be replaced."); }
        public Task<WorkspaceCloudAction?> AttachWorkspaceVolumeAsync(string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        { Attaches++; throw new InvalidOperationException("Stop must not attach storage."); }
        public Task<WorkspaceCloudAction?> DetachWorkspaceVolumeAsync(string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            Assert.Equal(Volume!.Id, id); Assert.Equal(Volume.ServerId, serverId);
            Assert.All(labels, pair => Assert.Equal(pair.Value, Volume.Labels[pair.Key]));
            DetachedVolumes.Add(id); Volume = Volume with { ServerId = null };
            return Task.FromResult<WorkspaceCloudAction?>(new("volumes", "detached", "running"));
        }
        public Task<IReadOnlyList<WorkspaceCloudFirewall>> FindWorkspaceFirewallsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
            throw new InvalidOperationException("Firewall lookup was not expected in this stage.");
        public Task<WorkspaceCloudFirewall> CreateWorkspaceFirewallAsync(string name, IReadOnlyList<string> managementAddresses, IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
            throw new InvalidOperationException("Firewall creation was not expected in this stage.");
        public Task DeleteWorkspaceFirewallAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        { DeletedFirewalls.Add(id); return Task.CompletedTask; }
        public Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Hosted app path is forbidden.");
        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Hosted app path is forbidden.");
        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Hosted app path is forbidden.");
        public Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Hosted app path is forbidden.");
    }
}
