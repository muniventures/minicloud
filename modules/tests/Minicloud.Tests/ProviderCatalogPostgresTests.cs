using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Vultr;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ProviderCatalogPostgresTests
{
    [Fact]
    public async Task MigrationPreservesLegacyRowsAndReservationsSerializeWithMappingEdits()
    {
        var container = "mc-providers-" + Guid.NewGuid().ToString("N");
        try
        {
            await Docker("run", "--rm", "-d", "--name", container, "-e", "POSTGRES_PASSWORD=" + container,
                "-p", "127.0.0.1::5432", "postgres:16-alpine");
            var port = (await Docker("port", container, "5432/tcp")).Trim().Split(':')[^1];
            var options = new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseNpgsql($"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;Password={container}").Options;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await using var probe = new MinicloudDbContext(options);
                if (await probe.Database.CanConnectAsync()) break;
                await Task.Delay(250);
            }
            await using (var db = new MinicloudDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261108023245_ApplicationEnvironments");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO organizations(id,name,slug,created_at,updated_at) VALUES ('org','Org','org',now(),now());
                    INSERT INTO apps(id,organization_id,name,slug,plan,database,created_at,updated_at)
                        VALUES ('legacy','org','Legacy','legacy','P0H0','None',now(),now());
                    INSERT INTO app_storage_volumes(id,app_id,name,region,provider_volume_id,creation_attempted,size_gb,uid,gid)
                        VALUES ('old-disk','legacy','data','nbg1','same-id',false,10,0,0);
                    """);
                await db.Database.MigrateAsync();
                Assert.Null((await db.Apps.SingleAsync()).PlanId);
                var oldDisk = await db.AppStorageVolumes.SingleAsync();
                Assert.Null(oldDisk.ProviderAccountId);
                Assert.Equal("hetzner", oldDisk.Provider);
                Assert.Equal("same-id", oldDisk.ProviderVolumeId);
                db.ProviderAccounts.AddRange(Account("a"), Account("b"));
                await db.SaveChangesAsync();
                var plan = await Catalog(db).CreateAsync(new CreateProviderPlanRequest("org", "a", "nbg1", "cx23", "Small"), default);
                await db.SaveChangesAsync();
                var app = new MinicloudApp { Id = "new-app", OrganizationId = "org", Name = "New app", Slug = "new-app" };
                db.Apps.Add(app);
                await Catalog(db).BindAsync(app, "small", default);
                await db.SaveChangesAsync();
            }

            await using var owner = new MinicloudDbContext(options);
            await using var tx = await owner.Database.BeginTransactionAsync();
            var selectedApp = await owner.Apps.SingleAsync(x => x.Id == "new-app");
            var reservation = new Deployment
            {
                Id = "reserved", OrganizationId = "org", AppId = selectedApp.Id, Database = DatabaseMode.None,
                Status = DeploymentStatus.Queued, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            await Catalog(owner).ReserveAsync(selectedApp, reservation, default);
            owner.Deployments.Add(reservation);
            await owner.SaveChangesAsync();
            var secrets = new TestSecrets();
            var rotation = Task.Run(async () =>
            {
                await using var contender = new MinicloudDbContext(options);
                var account = await contender.ProviderAccounts.SingleAsync(x => x.Id == "a");
                return await Assert.ThrowsAsync<ProviderCatalogException>(() => Accounts(contender, secrets).ReplaceCredentialsAsync(account,
                    new ReplaceProviderCredentialRequest("candidate-from-another-scope", account.Revision), default));
            });
            await secrets.Written.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var edit = Task.Run(async () =>
            {
                await using var contender = new MinicloudDbContext(options);
                await using var editTx = await contender.Database.BeginTransactionAsync();
                started.SetResult();
                var locked = await Catalog(contender).LockedPlanAsync("org", selectedApp.PlanId!, default);
                return await Assert.ThrowsAsync<ProviderCatalogException>(() => Catalog(contender).UpdateAsync(locked,
                    new UpdateProviderPlanRequest(locked.Revision, Sku: "different"), default));
            });
            await started.Task;
            await Task.Delay(200);
            Assert.False(edit.IsCompleted, "A mapping edit must wait for the provisioning reservation lock.");
            Assert.False(rotation.IsCompleted, "Credential replacement must wait for the account reservation lock.");
            await tx.CommitAsync();
            Assert.Equal("plan_mapping_locked", (await edit).Code);
            Assert.Equal("provider_scope_unverified", (await rotation).Code);

            await using (var verify = new MinicloudDbContext(options))
            {
                var saved = await verify.Deployments.SingleAsync();
                Assert.Equal("a", saved.ProviderAccountId);
                Assert.Equal("cx23", saved.ProviderSku);
                Assert.Equal("Hetzner", saved.ProviderType);
                Assert.Equal("synthetic/a", (await verify.ProviderAccounts.SingleAsync(x => x.Id == "a")).CredentialPath);
                // External IDs are account-scoped, while ownership remains enforced.
                verify.AppStorageVolumes.AddRange(
                    new AppStorageVolume { Id = "a-disk", OrganizationId = "org", ProviderAccountId = "a", AppId = "orphan-a", Name = "data", Region = "nbg1", ProviderVolumeId = "same-id" },
                    new AppStorageVolume { Id = "b-disk", OrganizationId = "org", ProviderAccountId = "b", AppId = "orphan-b", Name = "data", Region = "nbg1", ProviderVolumeId = "same-id" });
                await verify.SaveChangesAsync();
                Assert.Equal(3, await verify.AppStorageVolumes.CountAsync());
                verify.ProviderPlanNames.Add(new ProviderPlanName { OrganizationId = "org", NormalizedName = "SMALL", PlanId = "other" });
                await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
            }
            await using (var crossOrganization = new MinicloudDbContext(options))
            {
                crossOrganization.Organizations.Add(new Organization { Id = "other-org", Name = "Other", Slug = "other" });
                await crossOrganization.SaveChangesAsync();
                crossOrganization.ProviderPlans.Add(new ProviderPlan
                {
                    Id = "foreign", OrganizationId = "other-org", ProviderAccountId = "a", Name = "Foreign", NormalizedName = "FOREIGN", Region = "nbg1", Sku = "cx23"
                });
                await Assert.ThrowsAsync<DbUpdateException>(() => crossOrganization.SaveChangesAsync());
            }

            // Deletion holds the same account lock while removing credentials, so a
            // concurrent plan cannot be committed against credentials being removed.
            await using (var seed = new MinicloudDbContext(options))
            { seed.ProviderAccounts.Add(Account("unused")); await seed.SaveChangesAsync(); }
            var deletingSecrets = new TestSecrets { DelayDeletion = true };
            var deletion = Task.Run(async () =>
            {
                await using var deleting = new MinicloudDbContext(options);
                var account = await deleting.ProviderAccounts.SingleAsync(x => x.Id == "unused");
                await Accounts(deleting, deletingSecrets).DeleteAsync(account, default);
            });
            await deletingSecrets.Deleting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var staleCreation = Task.Run(async () =>
            {
                await using var creating = new MinicloudDbContext(options);
                await using var creationTx = await creating.Database.BeginTransactionAsync();
                return await Assert.ThrowsAsync<ProviderCatalogException>(() => Catalog(creating).CreateAsync(
                    new CreateProviderPlanRequest("org", "unused", "nbg1", "cx23", "Too late"), default));
            });
            await Task.Delay(200);
            Assert.False(staleCreation.IsCompleted);
            deletingSecrets.ReleaseDeletion.SetResult();
            await deletion;
            Assert.Equal("provider_not_found", (await staleCreation).Code);
        }
        finally { await Docker("rm", "-f", container); }
    }

    private static ProviderCatalogService Catalog(MinicloudDbContext db) => new(db, new IdGenerator(), TimeProvider.System);
    private static ProviderAccountService Accounts(MinicloudDbContext db, TestSecrets secrets) => new(db, secrets,
        new EmptyFactory(db, secrets), new IdGenerator(), TimeProvider.System,
        Options.Create(new OpenBaoSecretStoreOptions { EnvironmentPrefix = "postgres-tests" }));
    private static ProviderAccount Account(string id) => new()
    {
        Id = id, OrganizationId = "org", Name = id, ProviderType = CloudProvider.Hetzner,
        CredentialPath = "synthetic/" + id, CredentialGeneration = "fixture", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static async Task<string> Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    private sealed class EmptyFactory(MinicloudDbContext db, ISecretStore secrets) : ProviderAccountClientFactory(db, secrets, null!,
        Options.Create(new HetznerOptions()), Options.Create(new DigitalOceanOptions()), Options.Create(new VultrOptions()), NullLoggerFactory.Instance)
    {
        public override IVpsProviderClient CreateCandidate(ProviderAccount account, string credential) => new EmptyClient();
    }
    private sealed class EmptyClient : IVpsProviderClient
    {
        public CloudProvider Provider => CloudProvider.Hetzner;
        public Task<ProviderServerInfo?> FindServerAsync(string name, CancellationToken ct = default) => Task.FromResult<ProviderServerInfo?>(null);
        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken ct = default) => throw new InvalidOperationException("No allocation is permitted in these tests.");
        public Task<ProviderServerStatus> GetServerStatusAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task DeleteServerAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException();
    }
    private sealed class TestSecrets : ISecretStore
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Deleting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDeletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayDeletion { get; init; }
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct)
        {
            Written.TrySetResult();
            return Task.FromResult(new StoredSecretReference("openbao", OpenBaoSecretStore.ProviderPathFor("postgres-tests", request.OrganizationId,
                request.ProviderAccountId!, request.CredentialGeneration!), "1"));
        }
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct) => Task.FromResult("existing-credential");
        public async Task DeleteAsync(StoredSecretReference reference, CancellationToken ct)
        {
            Deleting.TrySetResult();
            if (DelayDeletion) await ReleaseDeletion.Task.WaitAsync(ct);
        }
    }
}
