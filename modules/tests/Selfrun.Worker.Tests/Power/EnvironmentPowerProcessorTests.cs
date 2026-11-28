using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Selfrun.Common.Models;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Dns;
using Selfrun.Common.Providers.Models;
using Selfrun.Common.Services;
using Selfrun.Worker.Power;
using Selfrun.Worker.Ssh;

namespace Selfrun.Worker.Tests.Power;

public sealed class EnvironmentPowerProcessorTests
{
    [Fact]
    public async Task SleepsInSafeOrderAndKeepsOnlyTheNewCurrentSnapshot()
    {
        await using var fixture = await CreateFixture(retainedAddress: false, priorSnapshot: true);

        Assert.True(await fixture.Processor.ProcessNextAsync(default));

        var settings = await fixture.Db.EnvironmentSleepSettings.SingleAsync();
        var operation = await fixture.Db.EnvironmentPowerOperations.SingleAsync();
        var oldSnapshot = await fixture.Db.EnvironmentSnapshots.SingleAsync(x => x.Id == "old-operation");
        var newSnapshot = await fixture.Db.EnvironmentSnapshots.SingleAsync(x => x.Id == operation.Id);
        Assert.Equal(EnvironmentPowerStates.Asleep, settings.PowerState);
        Assert.Equal(EnvironmentPowerOperationStatuses.Completed, operation.Status);
        Assert.Equal(EnvironmentSnapshotStatuses.Deleted, oldSnapshot.Status);
        Assert.Equal(EnvironmentSnapshotStatuses.Current, newSnapshot.Status);
        Assert.Equal(ServerInstance.RetiredStatus, (await fixture.Db.ServerInstances.SingleAsync()).Status);
        Assert.Null((await fixture.Db.RuntimeServers.SingleAsync()).ProviderServerId);

        AssertBefore(fixture.Events, "quiesce", "power_off");
        AssertBefore(fixture.Events, "power_off", "create_snapshot");
        AssertBefore(fixture.Events, "create_snapshot", "delete_snapshot:old-image");
        AssertBefore(fixture.Events, "delete_snapshot:old-image", "dns:app.example.apps.example.com");
        AssertBefore(fixture.Events, "dns:app.example.apps.example.com", "delete_server");
    }

    [Fact]
    public async Task DnsFailureLeavesPoweredOffServerForIdempotentRetry()
    {
        await using var fixture = await CreateFixture(retainedAddress: false, priorSnapshot: false);
        fixture.Dns.Fail = true;

        Assert.True(await fixture.Processor.ProcessNextAsync(default));

        var operation = await fixture.Db.EnvironmentPowerOperations.SingleAsync();
        Assert.True(operation.Status == EnvironmentPowerOperationStatuses.Queued,
            $"{operation.ErrorCode}: {operation.ErrorMessage}");
        Assert.Equal("remove_dns", operation.Step);
        Assert.Equal(EnvironmentPowerStates.SleepFailed,
            (await fixture.Db.EnvironmentSleepSettings.SingleAsync()).PowerState);
        Assert.False(fixture.Provider.ServerRunning);
        Assert.True(fixture.Provider.ServerExists);
        Assert.DoesNotContain("delete_server", fixture.Events);
        Assert.Contains("dns:app.example.apps.example.com", fixture.Events);

        fixture.Dns.Fail = false;
        Assert.True(await fixture.Processor.ProcessNextAsync(default));

        Assert.Equal(EnvironmentPowerOperationStatuses.Completed,
            (await fixture.Db.EnvironmentPowerOperations.SingleAsync()).Status);
        Assert.Equal(EnvironmentPowerStates.Asleep,
            (await fixture.Db.EnvironmentSleepSettings.SingleAsync()).PowerState);
        Assert.False(fixture.Provider.ServerExists);
        Assert.Equal(2, fixture.Dns.Deleted.Count);
    }

    [Fact]
    public async Task SnapshotFailureRestoresServicesAndRetainsServerAndPreviousSnapshot()
    {
        await using var fixture = await CreateFixture(retainedAddress: false, priorSnapshot: true);
        fixture.Provider.FailSnapshotCreation = true;

        Assert.True(await fixture.Processor.ProcessNextAsync(default));

        var operation = await fixture.Db.EnvironmentPowerOperations.SingleAsync();
        Assert.Equal(EnvironmentPowerOperationStatuses.Failed, operation.Status);
        Assert.Equal(EnvironmentPowerStates.Awake,
            (await fixture.Db.EnvironmentSleepSettings.SingleAsync()).PowerState);
        Assert.True(fixture.Provider.ServerExists);
        Assert.True(fixture.Provider.ServerRunning);
        Assert.DoesNotContain("delete_server", fixture.Events);
        Assert.DoesNotContain("delete_snapshot:old-image", fixture.Events);
        Assert.Contains("start_services", fixture.Events);
        Assert.Equal(EnvironmentSnapshotStatuses.Current,
            (await fixture.Db.EnvironmentSnapshots.SingleAsync(x => x.Id == "old-operation")).Status);
    }

    [Fact]
    public async Task MalformedSshPlacementMetadataRollsBackBeforeDeletingServer()
    {
        await using var fixture = await CreateFixture(
            retainedAddress: false, priorSnapshot: false, sshReferences: "[");

        Assert.True(await fixture.Processor.ProcessNextAsync(default));

        var operation = await fixture.Db.EnvironmentPowerOperations.SingleAsync();
        Assert.Equal(EnvironmentPowerOperationStatuses.Failed, operation.Status);
        Assert.Equal(EnvironmentPowerStates.Awake,
            (await fixture.Db.EnvironmentSleepSettings.SingleAsync()).PowerState);
        Assert.True(fixture.Provider.ServerExists);
        Assert.True(fixture.Provider.ServerRunning);
        Assert.DoesNotContain("create_snapshot", fixture.Events);
        Assert.DoesNotContain("delete_server", fixture.Events);
        Assert.Contains("start_services", fixture.Events);
    }

    [Fact]
    public async Task ExplicitWakeCancelsQueuedSleepBeforeQuiescingServices()
    {
        await using var fixture = await CreateFixture(retainedAddress: false, priorSnapshot: false);
        var settings = await fixture.Db.EnvironmentSleepSettings.SingleAsync();
        settings.KeepAwakeUntil = DateTimeOffset.UtcNow.AddMinutes(45);
        await fixture.Db.SaveChangesAsync();

        Assert.False(await fixture.Processor.ProcessNextAsync(default));

        var operation = await fixture.Db.EnvironmentPowerOperations.SingleAsync();
        Assert.Equal(EnvironmentPowerOperationStatuses.Cancelled, operation.Status);
        Assert.Equal("completed", operation.Step);
        Assert.Equal(EnvironmentPowerStates.Awake,
            (await fixture.Db.EnvironmentSleepSettings.SingleAsync()).PowerState);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task RetainedAddressIsVerifiedBeforeDeletingTheServer()
    {
        await using var fixture = await CreateFixture(retainedAddress: true, priorSnapshot: false);

        Assert.True(await fixture.Processor.ProcessNextAsync(default));

        var operation = await fixture.Db.EnvironmentPowerOperations.SingleAsync();
        Assert.True(operation.Status == EnvironmentPowerOperationStatuses.Completed,
            $"{operation.ErrorCode}: {operation.ErrorMessage}");
        AssertBefore(fixture.Events, "preserve_address", "delete_server");
        Assert.Empty(fixture.Dns.Deleted);
        Assert.Equal(EnvironmentRetainedAddressStates.Unassigned,
            (await fixture.Db.EnvironmentRetainedAddresses.SingleAsync()).State);
        Assert.False(fixture.Provider.ServerExists);
    }

    [Fact]
    public async Task StaleOwnerCannotAdvanceAfterSnapshotCreation()
    {
        await using var fixture = await CreateFixture(retainedAddress: false, priorSnapshot: false);
        fixture.Provider.AfterSnapshotCreated = async () =>
        {
            await using var competingDb = fixture.Harness.CreateDbContext();
            var operation = await competingDb.EnvironmentPowerOperations.SingleAsync();
            operation.OwnershipToken = "new-owner";
            operation.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
            await competingDb.SaveChangesAsync();
        };

        Assert.True(await fixture.Processor.ProcessNextAsync(default));

        fixture.Db.ChangeTracker.Clear();
        var operationRow = await fixture.Db.EnvironmentPowerOperations.SingleAsync();
        Assert.Equal("new-owner", operationRow.OwnershipToken);
        Assert.Equal("snapshot", operationRow.Step);
        Assert.DoesNotContain("delete_server", fixture.Events);
        Assert.Empty(fixture.Dns.Deleted);
        Assert.True(fixture.Provider.ServerExists);
    }

    private static async Task<Fixture> CreateFixture(
        bool retainedAddress, bool priorSnapshot, string? sshReferences = null)
    {
        var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        var (organization, app) = await harness.SeedOrgAndAppAsync("sleep");
        var db = harness.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var accountId = "provider-account";
        var serverName = ManagedServerName.ForApp(organization.Slug, app.Slug);
        var provider = new FakeProvider(organization.Slug, app.Slug);
        db.ProviderAccounts.Add(new ProviderAccount
        {
            Id = accountId,
            OrganizationId = organization.Id,
            Name = "Synthetic Hetzner",
            ProviderType = CloudProvider.Hetzner,
            CredentialPath = "synthetic/provider",
            CredentialGeneration = "test"
        });
        db.ServerInstances.Add(new ServerInstance
        {
            Id = "server-instance",
            OrganizationId = organization.Id,
            AppId = app.Id,
            ProviderAccountId = accountId,
            Provider = "hetzner",
            ProviderServerId = "provider-server",
            IpAddress = "203.0.113.20",
            Region = "nbg1",
            Sku = "cx22",
            Status = "active",
            BootstrapStatus = "ready",
            HostKeyFingerprint = "SHA256:synthetic"
        });
        db.RuntimeServers.Add(new RuntimeServer
        {
            Id = "runtime-server",
            OrganizationId = organization.Id,
            AppId = app.Id,
            ProviderAccountId = accountId,
            Provider = "hetzner",
            ProviderServerId = "provider-server",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.EnvironmentSleepSettings.Add(new EnvironmentSleepSettings
        {
            EnvironmentId = app.Id,
            AddressMode = retainedAddress ? EnvironmentAddressModes.Retained : EnvironmentAddressModes.Dynamic,
            SleepAfterMinutes = 120,
            PowerState = EnvironmentPowerStates.Awake,
            StateChangedAt = now,
            LastActivityAt = now
        });
        if (retainedAddress)
        {
            db.EnvironmentRetainedAddresses.Add(new EnvironmentRetainedAddress
            {
                Id = "retained-address-row",
                EnvironmentId = app.Id,
                OrganizationId = organization.Id,
                Provider = "hetzner",
                ProviderAccountId = accountId,
                ProviderAddressId = "primary-ip",
                Ipv4 = "203.0.113.20",
                Region = "nbg1",
                Datacenter = "nbg1-dc3",
                State = EnvironmentRetainedAddressStates.Assigned,
                CreatedAt = now
            });
            provider.RetainedAddress = new ProviderRetainedAddress(
                "primary-ip", "203.0.113.20", "nbg1", "nbg1-dc3");
        }
        else
        {
            db.Domains.Add(new Domain
            {
                Id = "managed-domain",
                OrganizationId = organization.Id,
                AppId = app.Id,
                ServiceName = "web",
                Hostname = "app.example.apps.example.com",
                Kind = DomainBindingService.KindSelfrunSubdomain,
                Status = "active",
                DnsStatus = DomainBindingService.DnsVerified,
                ApplyStatus = "applied",
                SslStatus = "ready",
                TargetIp = "203.0.113.20"
            });
        }
        if (sshReferences is not null)
        {
            db.Deployments.Add(new Deployment
            {
                Id = "successful-deployment",
                OrganizationId = organization.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Succeeded,
                SshProviderReferencesJson = sshReferences,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        db.EnvironmentPowerOperations.Add(new EnvironmentPowerOperation
        {
            Id = "sleep-operation",
            OrganizationId = organization.Id,
            EnvironmentId = app.Id,
            Kind = "sleep",
            Trigger = "manual_cli",
            Status = EnvironmentPowerOperationStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now
        });
        if (priorSnapshot)
        {
            db.EnvironmentSnapshots.Add(new EnvironmentSnapshot
            {
                Id = "old-operation",
                EnvironmentId = app.Id,
                OrganizationId = organization.Id,
                Provider = "hetzner",
                ProviderAccountId = accountId,
                ProviderImageId = "old-image",
                Region = "nbg1",
                Sku = "cx22",
                ServerName = serverName,
                AddressMode = EnvironmentAddressModes.Dynamic,
                Status = EnvironmentSnapshotStatuses.Current,
                CreatedAt = now.AddDays(-1)
            });
            provider.AddSnapshot("old-image", "old-operation", organization.Id, app.Id, now.AddDays(-1));
        }
        await db.SaveChangesAsync();

        var scopes = harness.CreateScopeFactory();
        var dns = new FakeDns(provider.Events);
        var ssh = new FakeSsh(provider.Events);
        var processor = new EnvironmentPowerProcessor(
            db,
            new FakeAccountClientFactory(provider),
            dns,
            ssh,
            Options.Create(new SshDeploymentOptions { PrivateKeyContent = "synthetic-private-key" }),
            Options.Create(new ManagedDomainOptions { Suffix = "apps.example.com", ManageDnsRecords = true }),
            Options.Create(new EnvironmentPowerOptions
            {
                HeartbeatInterval = TimeSpan.FromHours(1),
                SnapshotTimeout = TimeSpan.FromSeconds(1),
                PollInterval = TimeSpan.FromMilliseconds(1),
                SnapshotDeleteAttempts = 1
            }),
            scopes,
            TimeProvider.System,
            NullLogger<EnvironmentPowerProcessor>.Instance,
            MaintenanceParticipant.CreateLocal("worker-test", TimeProvider.System));
        return new Fixture(harness, db, provider, dns, processor);
    }

    private static void AssertBefore(IReadOnlyList<string> events, string before, string after)
    {
        var ordered = events.ToList();
        Assert.True(ordered.IndexOf(before) >= 0 && ordered.IndexOf(before) < ordered.IndexOf(after),
            $"Expected '{before}' before '{after}'. Actual order: {string.Join(", ", events)}");
    }

    private sealed class Fixture(
        WorkerSqliteTestHarness harness,
        Selfrun.Common.Data.SelfrunDbContext db,
        FakeProvider provider,
        FakeDns dns,
        EnvironmentPowerProcessor processor) : IAsyncDisposable
    {
        public WorkerSqliteTestHarness Harness { get; } = harness;
        public Selfrun.Common.Data.SelfrunDbContext Db { get; } = db;
        public FakeProvider Provider { get; } = provider;
        public FakeDns Dns { get; } = dns;
        public EnvironmentPowerProcessor Processor { get; } = processor;
        public IReadOnlyList<string> Events => Provider.Events;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }

    private sealed class FakeAccountClientFactory(FakeProvider provider)
        : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!)
    {
        public override Task<IVpsProviderClient> CreateAsync(
            string organizationId, string providerAccountId, CancellationToken ct, bool allowDisabled = true) =>
            Task.FromResult<IVpsProviderClient>(provider);
    }

    private sealed class FakeProvider(string organizationSlug, string appSlug)
        : IVpsProviderClient, IVpsProviderSnapshotClient, IVpsProviderAddressClient
    {
        private readonly Dictionary<string, ProviderSnapshot> snapshots = new(StringComparer.Ordinal);
        public CloudProvider Provider => CloudProvider.Hetzner;
        public List<string> Events { get; } = [];
        public bool ServerExists { get; set; } = true;
        public bool ServerRunning { get; set; } = true;
        public bool FailSnapshotCreation { get; set; }
        public ProviderRetainedAddress? RetainedAddress { get; set; }
        public Func<Task>? AfterSnapshotCreated { get; set; }

        public void AddSnapshot(string id, string operationId, string organizationId, string environmentId, DateTimeOffset createdAt)
        {
            snapshots[id] = new ProviderSnapshot(id, "old", true, 8, createdAt, ["nbg1"],
                SnapshotLabels(operationId, organizationId, environmentId));
        }

        public Task<ProviderServerInfo?> FindServerAsync(string name, CancellationToken cancellationToken = default)
        {
            if (!ServerExists)
                return Task.FromResult<ProviderServerInfo?>(null);
            return Task.FromResult<ProviderServerInfo?>(new ProviderServerInfo(
                "provider-server", name, "203.0.113.20", ServerRunning ? "running" : "off",
                Labels: new Dictionary<string, string>
                {
                    ["managed-by"] = "selfrun",
                    ["customer"] = organizationSlug,
                    ["app"] = appSlug,
                    ["env"] = "production"
                }));
        }

        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
        {
            if (!ServerExists)
                throw new ProviderApiException("missing", HttpStatusCode.NotFound, Provider);
            return Task.FromResult(new ProviderServerStatus(serverId, ServerRunning ? "running" : "off",
                "203.0.113.20", IsRunning: ServerRunning));
        }

        public Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
        {
            Events.Add("delete_server");
            ServerExists = false;
            ServerRunning = false;
            return Task.CompletedTask;
        }

        public Task PowerOffAsync(string serverId, CancellationToken ct)
        {
            Events.Add("power_off");
            ServerRunning = false;
            return Task.CompletedTask;
        }

        public Task PowerOnAsync(string serverId, CancellationToken ct)
        {
            Events.Add("power_on");
            ServerRunning = true;
            return Task.CompletedTask;
        }

        public async Task<ProviderSnapshot> CreateSnapshotAsync(
            string serverId, string description, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            Events.Add("create_snapshot");
            if (FailSnapshotCreation)
                throw new InvalidOperationException("Synthetic snapshot failure.");
            var snapshot = new ProviderSnapshot("new-image", description, true, 9,
                DateTimeOffset.UtcNow, ["nbg1"], new Dictionary<string, string>(labels));
            snapshots[snapshot.Id] = snapshot;
            if (AfterSnapshotCreated is not null)
                await AfterSnapshotCreated();
            return snapshot;
        }

        public Task<ProviderSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken ct)
        {
            snapshots.TryGetValue(snapshotId, out var snapshot);
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<ProviderSnapshot>> ListSnapshotsAsync(
            IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderSnapshot>>(snapshots.Values
                .Where(snapshot => labels.All(label =>
                    snapshot.Labels.TryGetValue(label.Key, out var value) && value == label.Value))
                .ToArray());

        public Task DeleteSnapshotAsync(string snapshotId, CancellationToken ct)
        {
            Events.Add($"delete_snapshot:{snapshotId}");
            snapshots.Remove(snapshotId);
            return Task.CompletedTask;
        }

        public Task<ProviderServerInfo> CreateServerFromSnapshotAsync(
            CreateServerRequest request, string snapshotId, string? retainedAddressId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProviderRetainedAddress> RetainServerAddressAsync(string serverId, CancellationToken cancellationToken = default) =>
            Task.FromResult(RetainedAddress ?? throw new InvalidOperationException("No retained address was configured."));

        public Task<ProviderRetainedAddress?> GetRetainedAddressAsync(string addressId, CancellationToken cancellationToken = default) =>
            Task.FromResult(RetainedAddress?.Id == addressId ? RetainedAddress : null);

        public Task EnsureSurvivesServerDeletionAsync(string addressId, CancellationToken cancellationToken = default)
        {
            Events.Add("preserve_address");
            return Task.CompletedTask;
        }

        public Task AssignRetainedAddressAsync(string addressId, string serverId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReleaseRetainedAddressAsync(string addressId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private static IReadOnlyDictionary<string, string> SnapshotLabels(
            string operationId, string organizationId, string environmentId) =>
            new Dictionary<string, string>
            {
                ["managed-by"] = "selfrun",
                ["organization-id"] = organizationId,
                ["environment-id"] = environmentId,
                ["operation-id"] = operationId
            };
    }

    private sealed class FakeDns(List<string> events) : ICloudflareDnsClient
    {
        public List<string> Deleted { get; } = [];
        public bool Fail { get; set; }

        public Task DeleteDnsRecordsAsync(string hostname, CancellationToken cancellationToken = default)
        {
            events.Add($"dns:{hostname}");
            Deleted.Add(hostname);
            if (Fail)
                throw new InvalidOperationException("Synthetic DNS failure.");
            return Task.CompletedTask;
        }

        public Task UpsertDnsRecordAsync(string hostname, string ipAddress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WaitForPropagationAsync(
            string hostname, string expectedIp, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSsh(List<string> events) : ISshDeploymentClient
    {
        public Task<SshDeploymentResult> ExecuteDeploymentAsync(
            SshDeploymentRequest request, Func<string, string, Task> onLogLine, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> ExecuteCommandAsync(
            SshTarget target, string username, string privateKeyContent, string command,
            Func<string, string, Task> onLogLine, CancellationToken cancellationToken)
        {
            events.Add(command.Contains(" compose ") && command.Contains(" stop", StringComparison.Ordinal)
                ? "quiesce"
                : "start_services");
            return Task.FromResult(0);
        }
    }
}
