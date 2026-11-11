using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Tests.Services;

// T50 acceptance coverage for deferred SSH key registration at allocation time. All provider
// traffic is served by SyntheticSshProvider; nothing is registered with a real provider.
public sealed class ProviderSshKeyAllocationTests
{
    private static readonly (string PublicKey, string Fingerprint) Selected = Key(1);
    private static readonly (string PublicKey, string Fingerprint) Edited = Key(2);

    public enum ActorChange { None, DemotedToDeveloper, DemotedToViewer, RemovedMember, RevokedApiKey, ExpiredApiKey, ApiKeyWithoutProvidersWrite, ApiKeyWithoutProvidersRead }

    [Theory]
    [InlineData("new", ActorChange.DemotedToDeveloper)]
    [InlineData("existing", ActorChange.DemotedToViewer)]
    [InlineData("existing", ActorChange.RemovedMember)]
    [InlineData("new", ActorChange.RevokedApiKey)]
    [InlineData("existing", ActorChange.ExpiredApiKey)]
    [InlineData("new", ActorChange.ApiKeyWithoutProvidersWrite)]
    [InlineData("existing", ActorChange.ApiKeyWithoutProvidersRead)]
    public async Task AllocationRevalidatesTheSelectingActorsCurrentAuthorityBeforeProviderCalls(string mode, ActorChange change)
    {
        await using var fixture = await Fixture.CreateAsync("actor-" + mode + change, mode,
            useApiKey: change is ActorChange.RevokedApiKey or ActorChange.ExpiredApiKey or ActorChange.ApiKeyWithoutProvidersWrite or ActorChange.ApiKeyWithoutProvidersRead);
        await fixture.ReserveAsync();
        await fixture.ChangeActorAsync(change);

        var error = await Assert.ThrowsAsync<ProviderSshKeyException>(() => fixture.ResolveAsync());

        Assert.Equal("ssh_key_changed", error.Code);
        Assert.Equal(0, fixture.Factory.Created);
        Assert.Equal(0, fixture.Provider.ListCalls);
        Assert.Equal(0, fixture.Provider.RegisterCalls);
    }

    [Fact]
    public async Task AllocationAcceptsAnActorWhoseCurrentRoleStillGrantsExistingKeyUse()
    {
        await using var fixture = await Fixture.CreateAsync("actor-developer", "existing");
        await fixture.ReserveAsync();
        await fixture.ChangeActorAsync(ActorChange.DemotedToDeveloper);

        Assert.Equal(["42"], await fixture.ResolveAsync());
        Assert.Equal(0, fixture.Provider.RegisterCalls);
    }

    [Fact]
    public async Task ExistingKeyReconcilesAChangedProviderReferenceByFingerprint()
    {
        await using var fixture = await Fixture.CreateAsync("existing-moved", "existing");
        fixture.Provider.Keys.Clear();
        fixture.Provider.Keys.Add(new ProviderSshKeyInfo("99", "Recreated", Selected.PublicKey + " recreated"));
        await fixture.ReserveAsync();

        Assert.Equal(["99"], await fixture.ResolveAsync());

        await using var db = fixture.Harness.CreateDbContext();
        var record = await db.ProviderSshKeys.SingleAsync();
        Assert.Equal("99", record.ProviderKeyId);
        Assert.Equal("ready", record.RegistrationState);
        var deployment = await db.Deployments.SingleAsync();
        Assert.Equal("[\"99\"]", deployment.SshProviderReferencesJson);
        Assert.Equal("ready", deployment.SshRegistrationState);
        Assert.Equal("ready", (await db.Apps.SingleAsync()).SshRegistrationState);
        Assert.Equal(0, fixture.Provider.RegisterCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingKeyWhoseProviderMaterialChangedOrVanishedFailsWithoutRegistering(bool replacedMaterial)
    {
        await using var fixture = await Fixture.CreateAsync("existing-changed-" + replacedMaterial, "existing");
        fixture.Provider.Keys.Clear();
        if (replacedMaterial)
            fixture.Provider.Keys.Add(new ProviderSshKeyInfo("42", "Laptop", Edited.PublicKey));
        await fixture.ReserveAsync();

        var error = await Assert.ThrowsAsync<ProviderSshKeyException>(() => fixture.ResolveAsync());

        Assert.Equal("ssh_key_changed", error.Code);
        Assert.Equal(0, fixture.Provider.RegisterCalls);
        await using var db = fixture.Harness.CreateDbContext();
        Assert.Equal("failed", (await db.Deployments.SingleAsync()).SshRegistrationState);
        Assert.Equal("failed", (await db.Apps.SingleAsync()).SshRegistrationState);
        Assert.Equal("42", (await db.ProviderSshKeys.SingleAsync()).ProviderKeyId);
    }

    [Fact]
    public async Task NewKeySelectionsForTheSameAccountAndFingerprintRegisterOnce()
    {
        await using var fixture = await Fixture.CreateAsync("dedupe", "new");
        await fixture.ReserveAsync();
        var second = await fixture.AddEnvironmentAsync("dedupe-second");

        var first = await fixture.ResolveAsync();
        var again = await fixture.ResolveAsync(second.DeploymentId, second.AppId);

        Assert.Equal(first, again);
        Assert.Equal(1, fixture.Provider.RegisterCalls);
        await using var db = fixture.Harness.CreateDbContext();
        var record = await db.ProviderSshKeys.SingleAsync();
        Assert.Equal(first.Single(), record.ProviderKeyId);
        Assert.Equal("ready", record.RegistrationState);
        Assert.All(await db.Deployments.ToListAsync(), x => Assert.Equal($"[\"{record.ProviderKeyId}\"]", x.SshProviderReferencesJson));
    }

    [Fact]
    public async Task ConcurrentNewKeyRegistrationsOnPostgresAreSerializedByTheAccountLock()
    {
        await WithPostgresAsync(async options =>
        {
            await using (var migrate = new MinicloudDbContext(options))
                await migrate.Database.MigrateAsync();
            var provider = new SyntheticSshProvider { RegisterDelay = TimeSpan.FromMilliseconds(500) };
            var now = DateTimeOffset.UtcNow;
            await using (var db = new MinicloudDbContext(options))
            {
                db.Organizations.Add(new Organization { Id = "org_pg", Name = "PG", Slug = "pg", CreatedAt = now, UpdatedAt = now });
                db.Users.Add(new User { Id = "user_pg", Email = "pg@example.test", CreatedAt = now, UpdatedAt = now });
                db.OrganizationMembers.Add(new OrganizationMember { Id = "member_pg", OrganizationId = "org_pg", UserId = "user_pg",
                    Role = OrganizationRole.Owner, CreatedAt = now, UpdatedAt = now });
                db.ProviderAccounts.Add(new ProviderAccount { Id = "provider_pg", OrganizationId = "org_pg", Name = "PG",
                    CredentialPath = "synthetic", CredentialGeneration = "gen" });
                foreach (var index in new[] { 1, 2 })
                {
                    db.Apps.Add(new MinicloudApp { Id = $"app_pg_{index}", OrganizationId = "org_pg", Name = $"PG {index}",
                        Slug = $"pg-{index}", CreatedAt = now, UpdatedAt = now, SshAccessMode = "new" });
                    db.Deployments.Add(new Deployment { Id = $"deployment_pg_{index}", OrganizationId = "org_pg", AppId = $"app_pg_{index}",
                        Database = DatabaseMode.None, Status = DeploymentStatus.Provisioning, CreatedAt = now, UpdatedAt = now,
                        SshSelectionMode = "new", SshSelectionRevision = 1, SshProviderAccountId = "provider_pg",
                        SshCredentialGeneration = "gen", SshSelectingActor = "user_pg", SshKeyName = $"Key {index}",
                        SshPublicKey = Selected.PublicKey, SshFingerprint = Selected.Fingerprint, SshKeyType = "ed25519",
                        SshSnapshotTakenAt = now });
                }
                await db.SaveChangesAsync();
            }

            async Task<IReadOnlyList<string>> ResolveAsync(int index)
            {
                await using var db = new MinicloudDbContext(options);
                var service = new ProviderSshKeyService(db, new StaticClientFactory(provider), new ControlPlaneAuthorization(db),
                    new IdGenerator(), TimeProvider.System);
                return await service.ResolveForAllocationAsync($"deployment_pg_{index}", "org_pg", $"app_pg_{index}", default);
            }

            var results = await Task.WhenAll(ResolveAsync(1), ResolveAsync(2));

            Assert.Equal(results[0], results[1]);
            Assert.Equal(1, provider.RegisterCalls);
            await using var verify = new MinicloudDbContext(options);
            var record = await verify.ProviderSshKeys.SingleAsync();
            Assert.Equal("ready", record.RegistrationState);
            Assert.Equal(results[0].Single(), record.ProviderKeyId);
        });
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("new")]
    public async Task CredentialRotationAfterSnapshotBlocksProviderCallsForEitherMode(string mode)
    {
        await using var fixture = await Fixture.CreateAsync("rotated-" + mode, mode);
        await fixture.ReserveAsync();
        await using (var db = fixture.Harness.CreateDbContext())
        {
            (await db.ProviderAccounts.SingleAsync()).CredentialGeneration = "rotated";
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<ProviderSshKeyException>(() => fixture.ResolveAsync());

        Assert.Equal("ssh_key_changed", error.Code);
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task ReservationRejectsASelectionScopedToAnotherProviderAccount()
    {
        await using var fixture = await Fixture.CreateAsync("other-account", "new");
        await using (var db = fixture.Harness.CreateDbContext())
        {
            db.ProviderAccounts.Add(new ProviderAccount { Id = "provider_moved", OrganizationId = fixture.OrganizationId,
                Name = "Moved", CredentialPath = "synthetic", CredentialGeneration = "gen" });
            (await db.Apps.SingleAsync()).SshProviderAccountId = "provider_moved";
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => fixture.ReserveAsync());

        Assert.Equal("ssh_key_changed", error.Code);
        await using var verify = fixture.Harness.CreateDbContext();
        Assert.Null((await verify.Deployments.SingleAsync()).SshSnapshotTakenAt);
    }

    [Fact]
    public async Task SnapshotStaysImmutableAcrossReservationRetriesAndEnvironmentEdits()
    {
        await using var fixture = await Fixture.CreateAsync("immutable", "new");
        await fixture.ReserveAsync();
        Deployment before;
        await using (var db = fixture.Harness.CreateDbContext())
        {
            before = await db.Deployments.AsNoTracking().SingleAsync();
            var app = await db.Apps.SingleAsync();
            app.SshKeyName = "Edited";
            app.SshPublicKey = Edited.PublicKey;
            app.SshFingerprint = Edited.Fingerprint;
            app.SshRevision = 2;
            app.SshSelectedBy = "user_someone_else";
            await db.SaveChangesAsync();
        }

        await fixture.ReserveAsync();
        var references = await fixture.ResolveAsync();
        var retried = await fixture.ResolveAsync();

        await using var verify = fixture.Harness.CreateDbContext();
        var after = await verify.Deployments.SingleAsync();
        Assert.Equal(before.SshSnapshotTakenAt, after.SshSnapshotTakenAt);
        Assert.Equal(1, after.SshSelectionRevision);
        Assert.Equal(Selected.Fingerprint, after.SshFingerprint);
        Assert.Equal(Selected.PublicKey, after.SshPublicKey);
        Assert.Equal(fixture.ActorId, after.SshSelectingActor);
        Assert.Equal([Selected.PublicKey], fixture.Provider.RegisteredPublicKeys);
        Assert.Equal(references, retried);
        Assert.Equal(1, fixture.Provider.RegisterCalls);
    }

    [Fact]
    public async Task AmbiguousRegistrationPersistsUnknownAndReconcilesOnRetryWithoutRegisteringTwice()
    {
        await using var fixture = await Fixture.CreateAsync("ambiguous-key", "new");
        await fixture.ReserveAsync();
        fixture.Provider.RegisterFailure = new HttpRequestException("Synthetic response lost.");
        fixture.Provider.HideRegisteredKeys = true;

        var error = await Assert.ThrowsAsync<ProviderSshKeyException>(() => fixture.ResolveAsync());

        Assert.Equal("ssh_registration_unknown", error.Code);
        await using (var db = fixture.Harness.CreateDbContext())
        {
            Assert.Equal("unknown", (await db.ProviderSshKeys.SingleAsync()).RegistrationState);
            Assert.Equal("unknown", (await db.Deployments.SingleAsync()).SshRegistrationState);
            Assert.Equal("unknown", (await db.Apps.SingleAsync()).SshRegistrationState);
        }

        // The lost response had actually created the key; inventory now shows it.
        fixture.Provider.RegisterFailure = null;
        fixture.Provider.HideRegisteredKeys = false;
        var references = await fixture.ResolveAsync();

        Assert.Single(references);
        Assert.Equal(1, fixture.Provider.RegisterCalls);
        await using var verify = fixture.Harness.CreateDbContext();
        var record = await verify.ProviderSshKeys.SingleAsync();
        Assert.Equal("ready", record.RegistrationState);
        Assert.Equal(references.Single(), record.ProviderKeyId);
    }

    [Fact]
    public async Task DefiniteRegistrationRejectionIsReportedAsFailed()
    {
        await using var fixture = await Fixture.CreateAsync("rejected-key", "new");
        await fixture.ReserveAsync();
        fixture.Provider.RegisterFailure = new ProviderApiException("synthetic", HttpStatusCode.UnprocessableEntity);
        fixture.Provider.HideRegisteredKeys = true;

        var error = await Assert.ThrowsAsync<ProviderSshKeyException>(() => fixture.ResolveAsync());

        Assert.Equal("ssh_registration_failed", error.Code);
        await using var db = fixture.Harness.CreateDbContext();
        Assert.Equal("failed", (await db.ProviderSshKeys.SingleAsync()).RegistrationState);
        Assert.Equal("failed", (await db.Deployments.SingleAsync()).SshRegistrationState);
    }

    internal static (string PublicKey, string Fingerprint) Key(byte seed)
    {
        using var stream = new MemoryStream();
        WriteString(stream, Encoding.ASCII.GetBytes("ssh-ed25519"));
        WriteString(stream, Enumerable.Range(0, 32).Select(x => (byte)(x + seed * 41)).ToArray());
        var blob = stream.ToArray();
        return ("ssh-ed25519 " + Convert.ToBase64String(blob), "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='));
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(WorkerSqliteTestHarness harness, string organizationId, string appId, string deploymentId, string actorId, string? apiKeyId)
        {
            Harness = harness;
            OrganizationId = organizationId;
            AppId = appId;
            DeploymentId = deploymentId;
            ActorId = actorId;
            ApiKeyId = apiKeyId;
            Factory = new StaticClientFactory(Provider);
        }

        public WorkerSqliteTestHarness Harness { get; }
        public string OrganizationId { get; }
        public string AppId { get; }
        public string DeploymentId { get; }
        public string ActorId { get; }
        public string? ApiKeyId { get; }
        public SyntheticSshProvider Provider { get; } = new();
        public StaticClientFactory Factory { get; }

        public static async Task<Fixture> CreateAsync(string name, string mode, bool useApiKey = false)
        {
            var harness = new WorkerSqliteTestHarness();
            await harness.InitializeAsync();
            var (organization, app) = await harness.SeedOrgAndAppAsync(name);
            await using var db = harness.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            var account = new ProviderAccount { Id = "provider_" + name, OrganizationId = organization.Id, Name = "Synthetic",
                CredentialPath = "synthetic", CredentialGeneration = "gen" };
            var plan = new ProviderPlan { Id = "plan_" + name, OrganizationId = organization.Id, ProviderAccountId = account.Id,
                ProviderAccount = account, Name = name, NormalizedName = name.ToUpperInvariant(), Region = "nbg1", Sku = "cx23",
                CreatedAt = now, UpdatedAt = now };
            db.Users.Add(new User { Id = "user_" + name, Email = name + "@example.test", CreatedAt = now, UpdatedAt = now });
            db.OrganizationMembers.Add(new OrganizationMember { Id = "member_" + name, OrganizationId = organization.Id,
                UserId = "user_" + name, Role = OrganizationRole.Owner, CreatedAt = now, UpdatedAt = now });
            string actor = "user_" + name;
            string? apiKeyId = null;
            if (useApiKey)
            {
                apiKeyId = "key_" + name;
                actor = "api_key:" + apiKeyId;
                db.ApiKeys.Add(new ApiKey { Id = apiKeyId, OrganizationId = organization.Id, Name = "Synthetic",
                    KeyPrefix = "mc_" + name[..4], KeyHash = "hash_" + name,
                    ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsWrite, ApiKeyScopes.ProvidersRead, ApiKeyScopes.ProvidersWrite }),
                    CreatedAt = now, UpdatedAt = now });
            }
            db.ProviderAccounts.Add(account);
            db.ProviderPlans.Add(plan);
            var tracked = await db.Apps.SingleAsync(x => x.Id == app.Id);
            tracked.PlanId = plan.Id;
            ApplySelection(tracked, mode, account.Id, actor);
            if (mode == "existing")
            {
                db.ProviderSshKeys.Add(new ProviderSshKey { Id = "sshkey_" + name, OrganizationId = organization.Id,
                    ProviderAccountId = account.Id, ProviderKeyId = "42", Name = "Laptop", PublicKey = Selected.PublicKey,
                    Fingerprint = Selected.Fingerprint, KeyType = "ed25519", RegistrationState = "discovered",
                    CreatedAt = now, UpdatedAt = now });
                tracked.SshKeyId = "sshkey_" + name;
            }
            var deploymentId = "deployment_" + name;
            db.Deployments.Add(new Deployment { Id = deploymentId, OrganizationId = organization.Id, AppId = app.Id,
                Database = DatabaseMode.None, Status = DeploymentStatus.Queued, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            var fixture = new Fixture(harness, organization.Id, app.Id, deploymentId, actor, apiKeyId);
            fixture.Provider.Keys.Add(new ProviderSshKeyInfo("42", "Laptop", mode == "existing" ? Selected.PublicKey : Key(9).PublicKey));
            return fixture;
        }

        private static void ApplySelection(MinicloudApp app, string mode, string accountId, string actor)
        {
            app.SshAccessMode = mode;
            app.SshProviderAccountId = accountId;
            app.SshKeyName = mode == "existing" ? "Laptop" : "Workstation";
            app.SshPublicKey = Selected.PublicKey;
            app.SshFingerprint = Selected.Fingerprint;
            app.SshKeyType = "ed25519";
            app.SshRegistrationState = mode == "existing" ? "discovered" : "not_registered";
            app.SshRevision = 1;
            app.SshSelectedBy = actor;
        }

        public async Task<(string AppId, string DeploymentId)> AddEnvironmentAsync(string name)
        {
            await using var db = Harness.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            var source = await db.Apps.AsNoTracking().SingleAsync(x => x.Id == AppId);
            var app = new MinicloudApp { Id = "app_" + name, OrganizationId = OrganizationId, Name = name,
                Slug = name, PlanId = source.PlanId, Plan = source.Plan, Database = DatabaseMode.None, CreatedAt = now, UpdatedAt = now };
            ApplySelection(app, source.SshAccessMode, source.SshProviderAccountId!, ActorId);
            db.Apps.Add(app);
            db.Deployments.Add(new Deployment { Id = "deployment_" + name, OrganizationId = OrganizationId, AppId = app.Id,
                Database = DatabaseMode.None, Status = DeploymentStatus.Queued, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            await ReserveAsync(app.Id, "deployment_" + name);
            return (app.Id, "deployment_" + name);
        }

        public Task ReserveAsync() => ReserveAsync(AppId, DeploymentId);

        private async Task ReserveAsync(string appId, string deploymentId)
        {
            await using var db = Harness.CreateDbContext();
            var app = await db.Apps.SingleAsync(x => x.Id == appId);
            var deployment = await db.Deployments.SingleAsync(x => x.Id == deploymentId);
            await new ProviderCatalogService(db, new IdGenerator(), TimeProvider.System).ReserveAsync(app, deployment, default);
            await db.SaveChangesAsync();
        }

        public async Task ChangeActorAsync(ActorChange change)
        {
            await using var db = Harness.CreateDbContext();
            var member = await db.OrganizationMembers.SingleAsync();
            switch (change)
            {
                case ActorChange.DemotedToDeveloper: member.Role = OrganizationRole.Developer; break;
                case ActorChange.DemotedToViewer: member.Role = OrganizationRole.Viewer; break;
                case ActorChange.RemovedMember: db.OrganizationMembers.Remove(member); break;
                case ActorChange.RevokedApiKey: (await db.ApiKeys.SingleAsync()).RevokedAt = DateTimeOffset.UtcNow; break;
                case ActorChange.ExpiredApiKey: (await db.ApiKeys.SingleAsync()).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case ActorChange.ApiKeyWithoutProvidersWrite:
                    (await db.ApiKeys.SingleAsync()).ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsWrite, ApiKeyScopes.ProvidersRead });
                    break;
                case ActorChange.ApiKeyWithoutProvidersRead:
                    (await db.ApiKeys.SingleAsync()).ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsWrite });
                    break;
            }
            await db.SaveChangesAsync();
        }

        public Task<IReadOnlyList<string>> ResolveAsync() => ResolveAsync(DeploymentId, AppId);

        public async Task<IReadOnlyList<string>> ResolveAsync(string deploymentId, string appId)
        {
            await using var db = Harness.CreateDbContext();
            var service = new ProviderSshKeyService(db, Factory, new ControlPlaneAuthorization(db), new IdGenerator(), TimeProvider.System);
            return await service.ResolveForAllocationAsync(deploymentId, OrganizationId, appId, default);
        }

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    internal sealed class StaticClientFactory(IVpsProviderClient client) : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!)
    {
        private int _created;
        public int Created => _created;

        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId, CancellationToken ct, bool allowDisabled = true)
        {
            Interlocked.Increment(ref _created);
            return Task.FromResult(client);
        }
    }

    internal sealed class SyntheticSshProvider : IVpsProviderClient, IVpsProviderSshKeyClient
    {
        private readonly object _gate = new();
        private readonly List<ProviderSshKeyInfo> _registered = [];
        private int _listCalls;
        private int _registerCalls;
        private int _nextId = 500;

        public List<ProviderSshKeyInfo> Keys { get; } = [];
        public List<string> RegisteredPublicKeys { get; } = [];
        public Exception? RegisterFailure { get; set; }
        public bool HideRegisteredKeys { get; set; }
        public TimeSpan RegisterDelay { get; set; }
        public int ListCalls => _listCalls;
        public int RegisterCalls => _registerCalls;
        public Func<CreateServerRequest, Task<ProviderServerInfo>>? CreateServer { get; set; }
        public Func<string, Task<ProviderServerInfo?>>? FindServer { get; set; }
        public CloudProvider Provider => CloudProvider.Hetzner;

        public Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _listCalls);
            lock (_gate)
                return Task.FromResult<IReadOnlyList<ProviderSshKeyInfo>>(HideRegisteredKeys ? Keys.ToArray() : Keys.Concat(_registered).ToArray());
        }

        public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _registerCalls);
            if (RegisterDelay > TimeSpan.Zero)
                await Task.Delay(RegisterDelay, cancellationToken);
            ProviderSshKeyInfo created;
            lock (_gate)
            {
                created = new ProviderSshKeyInfo((_nextId++).ToString(), name, publicKey);
                RegisteredPublicKeys.Add(publicKey);
                if (RegisterFailure is not ProviderApiException { StatusCode: HttpStatusCode.UnprocessableEntity })
                    _registered.Add(created);
            }
            if (RegisterFailure is not null)
                throw RegisterFailure;
            return created;
        }

        public Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default) =>
            FindServer is null ? Task.FromResult<ProviderServerInfo?>(null) : FindServer(serverName);

        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default) =>
            CreateServer?.Invoke(request) ?? throw new InvalidOperationException("Unexpected server allocation.");

        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderServerStatus(serverId, "running", "192.0.2.80", null, true));

        public Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected server deletion.");
    }

    private static async Task WithPostgresAsync(Func<DbContextOptions<MinicloudDbContext>, Task> test)
    {
        var name = "mc-ssh-allocation-" + Guid.NewGuid().ToString("N");
        try
        {
            await Docker("run", "--rm", "-d", "--name", name, "-e", "POSTGRES_PASSWORD=" + name,
                "-p", "127.0.0.1::5432", "postgres:16-alpine");
            var port = (await Docker("port", name, "5432/tcp")).Trim().Split(':')[^1];
            var options = new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseNpgsql($"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;Password={name}").Options;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await using var probe = new MinicloudDbContext(options);
                if (await probe.Database.CanConnectAsync()) break;
                await Task.Delay(250);
            }
            await test(options);
        }
        finally { await Docker("rm", "-f", name); }
    }

    private static async Task<string> Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Disposable PostgreSQL fixture command failed: " + await stderr);
        return await stdout;
    }
}
