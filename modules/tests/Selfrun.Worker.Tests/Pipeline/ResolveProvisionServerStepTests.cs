using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Selfrun.Common.Models;
using Selfrun.Common.Services;
using Selfrun.Worker.Pipeline;
using Selfrun.Worker.Pipeline.Steps;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Models;
using Microsoft.Extensions.DependencyInjection;
using Selfrun.Common.Providers.Common;
using System.Net;
using Selfrun.Common.Providers.DigitalOcean;
using Selfrun.Worker.Tests.Providers;
using Selfrun.Worker.Tests.Services;
using Selfrun.Worker.Ssh;
using System.Text.RegularExpressions;

namespace Selfrun.Worker.Tests.Pipeline;

public sealed class ResolveProvisionServerStepTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;

    public ResolveProvisionServerStepTests(WorkerSqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task CancelAfterProviderCreationRetainsPendingServerIdentity()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("cancel-provision");
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment { Id = "dep-cancel-provision", OrganizationId = org.Id, AppId = app.Id,
                Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var provider = new MockVpsProviderClient(createServer: request =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ProviderServerInfo("srv-cancel-provision", request.Name, "", "starting"));
        });
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => provider,
            new DeploymentPipelineOptions { SshPublicKey = "ssh-ed25519 TEST" }, TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step.ExecuteAsync(new PipelineStepContext {
            DeploymentId = "dep-cancel-provision", OrganizationId = org.Id, AppId = app.Id
        }, cancellation.Token));
        await using var verify = _harness.CreateDbContext();
        var registered = await verify.ServerInstances.SingleAsync(x => x.AppId == app.Id);
        Assert.Equal("srv-cancel-provision", registered.ProviderServerId);
        Assert.Equal("pending", registered.BootstrapStatus);
    }

    [Fact]
    public void BuildCloudInit_IncludesCompleteBootstrap()
    {
        var cloudInit = ResolveProvisionServerStep.BuildCloudInit(
            "myorg", "myapp", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGeneratedKey");

        Assert.Contains("#cloud-config", cloudInit);
        Assert.Contains("hostname: selfrun-myorg-myapp", cloudInit);
        Assert.Contains("name: selfrun", cloudInit);
        Assert.Contains("ssh_authorized_keys:", cloudInit);
        Assert.Contains("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGeneratedKey", cloudInit);
        Assert.Contains("fallocate -l 2G /swapfile", cloudInit);
        Assert.Contains("vm.swappiness=10", cloudInit);
        Assert.Contains("apt-get install -y python3 ca-certificates curl gnupg jq ufw caddy docker.io docker-compose-v2 docker-buildx", cloudInit);
        Assert.Contains("docker-buildx e2fsprogs util-linux", cloudInit);
        Assert.Contains("usermod -aG docker selfrun", cloudInit);
        Assert.Contains("printf 'import /opt/selfrun/caddy/apps/*.caddy\\n' > /etc/caddy/Caddyfile", cloudInit);
        Assert.Contains("ufw allow OpenSSH", cloudInit);
        Assert.Contains("ufw allow 80/tcp", cloudInit);
        Assert.Contains("ufw allow 443/tcp", cloudInit);
        Assert.Contains("systemctl enable --now docker", cloudInit);
        Assert.Contains("systemctl enable --now caddy", cloudInit);
    }

    [Fact]
    public async Task ReusesExistingServerWhenPresentInDb()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("reuse");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance
            {
                Id = "srv-existing-123",
                OrganizationId = org.Id,
                AppId = app.Id,
                Provider = "hetzner",
                ProviderServerId = "provider-123",
                IpAddress = "192.0.2.55",
                Region = "nbg1",
                Status = "active",
                BootstrapStatus = "ready",
                CreatedAt = now,
                UpdatedAt = now
            });

            db.Deployments.Add(new Deployment
            {
                Id = "dep-reuse-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var providerCalled = false;
        var mockProvider = new MockVpsProviderClient(() =>
        {
            providerCalled = true;
            return Task.FromResult<ProviderServerInfo?>(null);
        }, getServerStatus: id => Task.FromResult(new ProviderServerStatus(id, "running", "192.0.2.55", null, true)));

        var step = new ResolveProvisionServerStep(
            _harness.CreateScopeFactory(),
            _ => mockProvider,
            Options.Create(new DeploymentPipelineOptions()),
            TimeProvider.System,
            NullLogger<ResolveProvisionServerStep>.Instance);

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-reuse-1",
            OrganizationId = org.Id,
            AppId = app.Id
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.False(providerCalled, "Provider client should not be called when server already exists in DB");
        Assert.Equal("192.0.2.55", context.ServerIp);
        Assert.Equal("srv-existing-123", context.ServerInstanceId);
    }

    [Fact]
    public async Task ProvisionsNewServerWhenMissingFromDb()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("prov");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment
            {
                Id = "dep-prov-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        CreateServerRequest? capturedRequest = null;
        var mockProvider = new MockVpsProviderClient(
            findServer: () => Task.FromResult<ProviderServerInfo?>(null),
            createServer: req =>
            {
                capturedRequest = req;
                return Task.FromResult(new ProviderServerInfo(
                    "srv-hetz-999", req.Name, "198.51.100.42", "running", null, req.Labels));
            },
            getServerStatus: id => Task.FromResult(new ProviderServerStatus(id, "running", "198.51.100.42", null, true)));

        var step = new ResolveProvisionServerStep(
            _harness.CreateScopeFactory(),
            _ => mockProvider,
            Options.Create(new DeploymentPipelineOptions
            {
                SshPublicKey = "ssh-ed25519 TEST_PUBLIC_KEY",
                SshReadyTimeout = TimeSpan.FromMilliseconds(100)
            }),
            TimeProvider.System,
            NullLogger<ResolveProvisionServerStep>.Instance,
            sshProbe: (_, _, _) => Task.FromResult(true),
            hostKeyProbe: new EnrollingHostKeyProbe(() => capturedRequest?.UserDataCloudInit));

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-prov-1",
            OrganizationId = org.Id,
            AppId = app.Id
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Empty(capturedRequest.SshKeyReferences);
        Assert.Contains("TEST_PUBLIC_KEY", capturedRequest.UserDataCloudInit);
        Assert.Equal("198.51.100.42", context.ServerIp);

        await using (var db = _harness.CreateDbContext())
        {
            var saved = db.ServerInstances.FirstOrDefault(s => s.AppId == app.Id);
            Assert.NotNull(saved);
            Assert.Equal("198.51.100.42", saved.IpAddress);
            Assert.Equal("srv-hetz-999", saved.ProviderServerId);
        }
    }

    [Fact]
    public async Task FreshServerEnrollsHostKeyPinThatTheSshClientTrusts()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("enroll");
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment { Id = "dep-enroll", OrganizationId = org.Id, AppId = app.Id, Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
        }
        CreateServerRequest? created = null;
        var provider = new MockVpsProviderClient(createServer: request =>
        {
            created = request;
            return Task.FromResult(new ProviderServerInfo("srv-enroll", request.Name, "198.51.100.77", "running", null, request.Labels));
        });
        // The first observation predates cloud-init's banner; enrollment retries within the SSH-ready window.
        var probe = new EnrollingHostKeyProbe(() => created?.UserDataCloudInit, unprovenObservations: 1);
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => provider,
            new DeploymentPipelineOptions { SshPublicKey = "ssh-ed25519 TEST", SshReadyTimeout = TimeSpan.FromSeconds(30) },
            TimeProvider.System, sshProbe: (_, _, _) => Task.FromResult(true), hostKeyProbe: probe);

        await step.ExecuteAsync(new PipelineStepContext { DeploymentId = "dep-enroll", OrganizationId = org.Id, AppId = app.Id }, CancellationToken.None);

        Assert.Equal(2, probe.Calls);
        var (serverId, secret) = EnrollingHostKeyProbe.Material(created!.UserDataCloudInit!);
        await using (var db = _harness.CreateDbContext())
        {
            var saved = await db.ServerInstances.SingleAsync(s => s.AppId == app.Id);
            Assert.Equal(serverId, saved.Id);
            Assert.Equal(probe.Fingerprint, saved.HostKeyFingerprint);
            Assert.NotNull(saved.HostKeyVerifiedAt);
            Assert.Null(saved.HostKeyEnrollmentSecret);
            var events = await db.DeploymentEvents.Where(e => e.DeploymentId == "dep-enroll").Select(e => e.Message).ToListAsync();
            Assert.Contains(events, message => message.Contains(probe.Fingerprint));
            Assert.DoesNotContain(events, message => message.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        var pinSource = new ServerHostKeyPinSource(_harness.CreateScopeFactory());
        Assert.Equal(probe.Fingerprint, await pinSource.GetPinnedFingerprintAsync(new SshTarget(serverId, "198.51.100.77"), CancellationToken.None));
    }

    [Fact]
    public async Task UnprovenHostKeyFailsWithSpecificCodeAndResumesOnRetry()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("enroll-resume");
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment { Id = "dep-enroll-resume-1", OrganizationId = org.Id, AppId = app.Id, Status = DeploymentStatus.Provisioning });
            db.Deployments.Add(new Deployment { Id = "dep-enroll-resume-2", OrganizationId = org.Id, AppId = app.Id, Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
        }
        CreateServerRequest? created = null;
        var provider = new MockVpsProviderClient(createServer: request =>
        {
            created = request;
            return Task.FromResult(new ProviderServerInfo("srv-enroll-resume", request.Name, "198.51.100.78", "running", null, request.Labels));
        }, getServerStatus: id => Task.FromResult(new ProviderServerStatus(id, "running", "198.51.100.78", null, true)));
        var options = new DeploymentPipelineOptions { SshPublicKey = "ssh-ed25519 TEST", SshReadyTimeout = TimeSpan.FromMilliseconds(300) };
        // A key without a valid proof (for example an interposed host) is never pinned.
        var forged = new EnrollingHostKeyProbe(() => created?.UserDataCloudInit, unprovenObservations: int.MaxValue);
        var first = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => provider, options, TimeProvider.System,
            sshProbe: (_, _, _) => Task.FromResult(true), hostKeyProbe: forged);

        var failure = await Assert.ThrowsAsync<PipelineStepException>(() => first.ExecuteAsync(
            new PipelineStepContext { DeploymentId = "dep-enroll-resume-1", OrganizationId = org.Id, AppId = app.Id }, CancellationToken.None));

        Assert.Equal("ssh_host_key_unverified", failure.FailureCode);
        Assert.Contains("cloud-init", failure.Message);
        await using (var db = _harness.CreateDbContext())
        {
            var pending = await db.ServerInstances.SingleAsync(s => s.AppId == app.Id);
            Assert.Null(pending.HostKeyFingerprint);
            Assert.NotNull(pending.HostKeyEnrollmentSecret);
            Assert.DoesNotContain(EnrollingHostKeyProbe.Material(created!.UserDataCloudInit!).Secret, pending.HostKeyEnrollmentSecret,
                StringComparison.OrdinalIgnoreCase);
            var untrusted = await Assert.ThrowsAsync<SshHostKeyTrustException>(() =>
                new ServerHostKeyPinSource(_harness.CreateScopeFactory()).GetPinnedFingerprintAsync(
                    new SshTarget(pending.Id, "198.51.100.78"), CancellationToken.None));
            Assert.Equal("ssh_host_key_untrusted", untrusted.FailureCode);
        }

        var proven = new EnrollingHostKeyProbe(() => created?.UserDataCloudInit);
        var retry = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => provider,
            new DeploymentPipelineOptions { SshPublicKey = "ssh-ed25519 TEST", SshReadyTimeout = TimeSpan.FromSeconds(30) }, TimeProvider.System,
            sshProbe: (_, _, _) => Task.FromResult(true), hostKeyProbe: proven);
        await retry.ExecuteAsync(new PipelineStepContext { DeploymentId = "dep-enroll-resume-2", OrganizationId = org.Id, AppId = app.Id }, CancellationToken.None);

        await using var verify = _harness.CreateDbContext();
        var enrolled = await verify.ServerInstances.SingleAsync(s => s.AppId == app.Id);
        Assert.Equal(proven.Fingerprint, enrolled.HostKeyFingerprint);
        Assert.Null(enrolled.HostKeyEnrollmentSecret);
        Assert.Equal("active", enrolled.Status);
    }

    [Fact]
    public async Task MapsFailureToProviderProvisionFailedCode()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("fail");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment
            {
                Id = "dep-fail-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var mockProvider = new MockVpsProviderClient(
            findServer: () => Task.FromResult<ProviderServerInfo?>(null),
            createServer: _ => throw new HttpRequestException("Hetzner API unavailable"));

        var step = new ResolveProvisionServerStep(
            _harness.CreateScopeFactory(),
            _ => mockProvider,
            Options.Create(new DeploymentPipelineOptions()),
            TimeProvider.System,
            NullLogger<ResolveProvisionServerStep>.Instance);

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-fail-1",
            OrganizationId = org.Id,
            AppId = app.Id
        };

        var ex = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, CancellationToken.None));
        Assert.Equal("provider_provision_failed", ex.FailureCode);
    }

    [SshdFact]
    public async Task PendingEnrollmentRecoversThroughVerifiedOperatorHostPin()
    {
        // The pin source rejects duplicate active IPs, including loopback used by other tests.
        await using var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        await using var sshd = await ServerHostKeyEnrollmentTests.LocalSshd.StartAsync();
        await sshd.WriteAsync("banner", "");
        await sshd.ListenAsync();
        var (org, app) = await harness.SeedOrgAndAppAsync("operator-pin-recovery");
        const string serverId = "abcdef0123456789abcdef0123456789";
        var secret = ServerHostKeyEnrollment.CreateSecret();
        var encryption = new SecretEncryptionService(Options.Create(new SecretEncryptionOptions
        {
            EncryptionKey = "synthetic-worker-test-key"
        }));
        var protectedSecret = encryption.Encrypt(Convert.ToHexString(secret));
        await using (var db = harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance
            {
                Id = serverId, OrganizationId = org.Id, AppId = app.Id, Provider = "hetzner",
                ProviderServerId = "provider-pending", IpAddress = "192.0.2.97", Region = "synthetic",
                Status = "provisioning", BootstrapStatus = "pending", HostKeyEnrollmentSecret = protectedSecret
            });
            foreach (var id in new[] { "pin-unproven", "pin-wrong", "pin-recovered", "pin-enrolled" })
            {
                db.Deployments.Add(new Deployment
                {
                    Id = id, OrganizationId = org.Id, AppId = app.Id, Status = DeploymentStatus.Provisioning
                });
            }
            await db.SaveChangesAsync();
        }
        var provider = new MockVpsProviderClient(
            createServer: _ => throw new InvalidOperationException("Recovery must never provision."),
            getServerStatus: id => Task.FromResult(new ProviderServerStatus(id, "running", "127.0.0.1", IsRunning: true)));
        // Neither the old stored IP nor an entry for a different host can bypass enrollment.
        var file = await sshd.WriteAsync("host-pins", $"192.0.2.97 {sshd.Fingerprint}\n");
        var sshOptions = new SshDeploymentOptions { Port = sshd.Port, HostKeyPinsFile = file };
        var probe = new LocalHostKeyProbe(sshd.Port);
        var step = new ResolveProvisionServerStep(harness.CreateScopeFactory(), _ => provider,
            new DeploymentPipelineOptions { SshReadyTimeout = TimeSpan.FromMilliseconds(300) },
            TimeProvider.System, sshProbe: (_, _, _) => Task.FromResult(true), hostKeyProbe: probe, sshOptions: sshOptions);

        var failure = await Assert.ThrowsAsync<PipelineStepException>(() =>
            step.ExecuteAsync(Context("pin-unproven"), default));
        Assert.Equal("ssh_host_key_unverified", failure.FailureCode);
        Assert.True(probe.Calls > 0);
        var calls = probe.Calls;

        var wrongFingerprint = "SHA256:" + Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(ServerHostKeyEnrollmentTests.Ed25519Blob())).TrimEnd('=');
        await sshd.WriteAsync("host-pins", $"127.0.0.1 {wrongFingerprint}\n");
        var wrongContext = Context("pin-wrong");
        await step.ExecuteAsync(wrongContext, default);
        Assert.Equal(calls, probe.Calls);
        var client = new SshDeploymentClient(sshOptions, pinSource: new ServerHostKeyPinSource(harness.CreateScopeFactory()));
        var mismatch = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => client.ExecuteCommandAsync(
            new SshTarget(wrongContext.ServerInstanceId, wrongContext.ServerIp), Environment.UserName, sshd.ClientPrivateKey,
            "true", (_, _) => Task.CompletedTask, default));
        Assert.Equal("ssh_host_key_mismatch", mismatch.FailureCode);

        await sshd.WriteAsync("host-pins", $"127.0.0.1 {sshd.Fingerprint}\n");
        var recovered = Context("pin-recovered");
        await step.ExecuteAsync(recovered, default);
        Assert.Equal(calls, probe.Calls);
        Assert.Equal(serverId, recovered.ServerInstanceId);
        Assert.Equal("127.0.0.1", recovered.ServerIp);
        Assert.Equal(0, await client.ExecuteCommandAsync(new SshTarget(serverId, recovered.ServerIp),
            Environment.UserName, sshd.ClientPrivateKey, "true", (_, _) => Task.CompletedTask, default));
        await using (var db = harness.CreateDbContext())
        {
            var pending = await db.ServerInstances.SingleAsync(s => s.Id == serverId);
            Assert.Null(pending.HostKeyFingerprint);
            Assert.Null(pending.HostKeyVerifiedAt);
            Assert.Equal(protectedSecret, pending.HostKeyEnrollmentSecret);
            Assert.Equal("active", pending.Status);
            Assert.Contains(await db.DeploymentEvents.Where(e => e.DeploymentId == "pin-recovered").ToListAsync(),
                e => e.Level == EventLevel.Warning && e.Message.Contains("host-pins"));
        }

        await sshd.WriteAsync("host-pins", "");
        var untrusted = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => client.ExecuteCommandAsync(
            new SshTarget(serverId, "127.0.0.1"), Environment.UserName, sshd.ClientPrivateKey,
            "true", (_, _) => Task.CompletedTask, default));
        Assert.Equal("ssh_host_key_untrusted", untrusted.FailureCode);
        var publicKey = (await File.ReadAllTextAsync(sshd.HostPublicKeyPath)).Split(' ')[1];
        await sshd.WriteAsync("banner", ServerHostKeyEnrollmentTests.Banner(serverId, secret, Convert.FromBase64String(publicKey)));
        await step.ExecuteAsync(Context("pin-enrolled"), default);
        Assert.True(probe.Calls > calls);
        await using var verify = harness.CreateDbContext();
        var enrolled = await verify.ServerInstances.SingleAsync(s => s.Id == serverId);
        Assert.Equal(sshd.Fingerprint, enrolled.HostKeyFingerprint);
        Assert.Null(enrolled.HostKeyEnrollmentSecret);

        PipelineStepContext Context(string deploymentId) => new()
        {
            DeploymentId = deploymentId, OrganizationId = org.Id, AppId = app.Id
        };
    }

    private sealed class LocalHostKeyProbe(int port) : IServerHostKeyProbe
    {
        public int Calls { get; private set; }
        public Task<SshHostKeyObservation?> ObserveAsync(string host, int ignoredPort, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal("127.0.0.1", host);
            Calls++;
            return new SshNetServerHostKeyProbe().ObserveAsync(host, port, timeout, cancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeRegistrationIsReusedAndConflictsBlockCreation(bool conflicting)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("runtime-" + conflicting);
        await using (var db = _harness.CreateDbContext())
        {
            db.RuntimeServers.Add(new RuntimeServer
            {
                Id = "runtime-" + conflicting, OrganizationId = org.Id, AppId = app.Id,
                Provider = "hetzner", ProviderServerId = "original-server"
            });
            if (conflicting)
            {
                db.ServerInstances.Add(new ServerInstance
                {
                    Id = "accidental-server", OrganizationId = org.Id, AppId = app.Id,
                    Provider = "hetzner", ProviderServerId = "replacement-server", Region = "nbg1",
                    Status = "active", BootstrapStatus = "ready"
                });
            }
            await db.SaveChangesAsync();
        }
        var client = new MockVpsProviderClient(
            findServer: () => throw new Exception("Must not discover by name"),
            createServer: _ => throw new Exception("Must not create"),
            getServerStatus: id =>
            {
                Assert.Equal("original-server", id);
                return Task.FromResult(new ProviderServerStatus(id, "running", "192.0.2.10", null, true));
            });
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => client,
            new DeploymentPipelineOptions(), TimeProvider.System);
        var context = new PipelineStepContext { AppId = app.Id, OrganizationId = org.Id, DeploymentId = "unused" };
        if (conflicting)
        {
            var error = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
            Assert.Contains("conflict", error.Message);
        }
        else
        {
            // Events require a deployment row.
            await using var db = _harness.CreateDbContext();
            db.Deployments.Add(new Deployment { Id = "unused", AppId = app.Id, OrganizationId = org.Id, Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
            await step.ExecuteAsync(context, default);
            Assert.Equal("192.0.2.10", context.ServerIp);
            Assert.Equal(CloudProvider.Hetzner, context.Provider);
        }
    }

    [Fact]
    public async Task PreviouslyDeployedAppCannotSilentlyProvisionReplacement()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("established");
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment { Id = "prior-success", AppId = app.Id, OrganizationId = org.Id, Status = DeploymentStatus.Succeeded });
            await db.SaveChangesAsync();
        }
        var client = new MockVpsProviderClient(createServer: _ => throw new Exception("Must not create"));
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => client,
            new DeploymentPipelineOptions(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(
            new PipelineStepContext { AppId = app.Id, OrganizationId = org.Id, DeploymentId = "unused" }, default));
        Assert.Contains("refusing to create a replacement", error.Message);
    }

    [Fact]
    public void ServerNamePreservesProductionIdentity()
    {
        Assert.Equal("selfrun-muni-production-example-suite", ResolveProvisionServerStep.BuildServerName("muni", "example-suite"));
    }

    [Theory]
    [InlineData("db-0123456789abcdef01234567")]
    [InlineData("dbrepo-0123456789abcdef01234567")]
    public void GeneratedDatabaseServerNamesUseTheSharedDiscoveryIdentity(string appSlug)
    {
        const string organization = "selfrun-staging";
        var name = ResolveProvisionServerStep.BuildServerName(organization, appSlug);
        Assert.Equal(ManagedServerName.ForApp(organization, appSlug), name);
        Assert.InRange(name.Length, 1, 63);
        var longOrganization = new string('o', 55);
        Assert.Contains("hostname: " + ManagedServerName.ForHostname(longOrganization, appSlug),
            ResolveProvisionServerStep.BuildCloudInit(longOrganization, appSlug));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoversEstablishedProductionNameWithoutCreating(bool initiallyPending)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("discovery-" + initiallyPending);
        await using (var db = _harness.CreateDbContext())
        {
            app = await db.Apps.FindAsync(app.Id) ?? throw new Exception();
            app.Plan = AppPlan.P0H0;
            db.Deployments.Add(new Deployment { Id = "discover-" + initiallyPending, AppId = app.Id, OrganizationId = org.Id, Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
        }
        var expectedName = ResolveProvisionServerStep.BuildServerName(org.Slug, app.Slug);
        var found = new ProviderServerInfo("original", expectedName, initiallyPending ? "" : "192.0.2.44",
            initiallyPending ? "initializing" : "running", Labels: new Dictionary<string, string>
            {
                ["managed-by"] = "selfrun", ["customer"] = org.Slug, ["app"] = app.Slug, ["env"] = "production"
            });
        var client = new MockVpsProviderClient(findServer: () => Task.FromResult<ProviderServerInfo?>(found),
            createServer: _ => throw new Exception("Must not create replacement"));
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => client,
            new DeploymentPipelineOptions { SshReadyTimeout = TimeSpan.Zero, ProvisionTimeout = TimeSpan.Zero }, TimeProvider.System);
        var context = new PipelineStepContext { AppId = app.Id, OrganizationId = org.Id, DeploymentId = "discover-" + initiallyPending };
        if (initiallyPending)
        {
            var error = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
            Assert.Contains("did not become ready", error.Message);
        }
        else
        {
            await step.ExecuteAsync(context, default);
            Assert.Equal("192.0.2.44", context.ServerIp);
        }
        Assert.Equal(expectedName, client.RequestedName);
    }

    [Fact]
    public async Task CatalogPlanWithoutReservationNeverUsesGlobalProviderCredentials()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("missing-reservation");
        await using (var db = _harness.CreateDbContext())
        {
            var account = new ProviderAccount { Id = "missing-reservation-account", OrganizationId = org.Id,
                Name = "secondary", ProviderType = CloudProvider.Hetzner,
                CredentialPath = "synthetic/path", CredentialGeneration = "test" };
            var plan = new ProviderPlan { Id = "missing-reservation-plan", OrganizationId = org.Id,
                ProviderAccount = account, ProviderAccountId = account.Id, Name = "secondary small", NormalizedName = "SECONDARY SMALL",
                Region = "nbg1", Sku = "cx23" };
            db.ProviderPlans.Add(plan);
            (await db.Apps.SingleAsync(x => x.Id == app.Id)).PlanId = plan.Id;
            db.Deployments.Add(new Deployment { Id = "missing-reservation-deployment", AppId = app.Id, OrganizationId = org.Id,
                PlanId = plan.Id, Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
        }
        var globalCalled = false;
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ =>
        {
            globalCalled = true;
            throw new InvalidOperationException("Global credentials must not be used.");
        }, new DeploymentPipelineOptions(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(new PipelineStepContext
        { AppId = app.Id, OrganizationId = org.Id, DeploymentId = "missing-reservation-deployment" }, default));
        Assert.Contains("reservation", error.Message);
        Assert.False(globalCalled);
    }

    [Fact]
    public async Task CatalogProvisioningUsesReservedAccountAndPersistsIt()
    {
        var context = await SeedCatalogAsync("catalog-provision");
        var factory = new AccountFactory(new MockVpsProviderClient());
        var step = CatalogStep(factory);
        await step.ExecuteAsync(context, default);
        Assert.Equal([context.ProviderAccountId!], factory.RequestedAccounts);
        await using var db = _harness.CreateDbContext();
        var server = await db.ServerInstances.SingleAsync(x => x.AppId == context.AppId);
        Assert.Equal(context.ProviderAccountId, server.ProviderAccountId);
        Assert.Equal(context.PlanId, server.PlanId);
        Assert.Equal("cx23", server.Sku);
    }

    [Fact]
    public async Task AmbiguousCatalogCreateCannotIssueAnotherCreateWhenDiscoveryIsEmpty()
    {
        var context = await SeedCatalogAsync("catalog-ambiguous");
        var creates = 0;
        var factory = new AccountFactory(new MockVpsProviderClient(createServer: _ =>
        {
            creates++;
            throw new HttpRequestException("Response lost.");
        }));
        var step = CatalogStep(factory);
        await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        var retry = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        Assert.Contains("unresolved", retry.Message);
        Assert.Equal(1, creates);
        await using var db = _harness.CreateDbContext();
        Assert.True((await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId)).ProviderCreationAttempted);
        Assert.All(factory.RequestedAccounts, x => Assert.Equal(context.ProviderAccountId, x));
    }

    [Fact]
    public async Task DefiniteCatalogQuotaRejectionCanRetryOnlyTheReservedAccount()
    {
        var context = await SeedCatalogAsync("catalog-quota");
        var creates = 0;
        var factory = new AccountFactory(new MockVpsProviderClient(createServer: request =>
        {
            if (++creates == 1) { throw new ProviderApiException("quota", HttpStatusCode.TooManyRequests); }
            return Task.FromResult(new ProviderServerInfo("quota-retry-server", request.Name, "192.0.2.25", "running"));
        }));
        var step = CatalogStep(factory);
        await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        await using (var db = _harness.CreateDbContext())
        { Assert.False((await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId)).ProviderCreationAttempted); }
        await step.ExecuteAsync(context, default);
        Assert.Equal(2, creates);
        Assert.All(factory.RequestedAccounts, x => Assert.Equal(context.ProviderAccountId, x));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task DigitalOceanProjectAssignmentFailureRetainsCreationMarkerAndPreventsAnotherAllocation(HttpStatusCode status)
    {
        var context = await SeedCatalogAsync("do-" + (int)status + "-project");
        await using (var db = _harness.CreateDbContext())
        {
            (await db.ProviderAccounts.SingleAsync(x => x.Id == context.ProviderAccountId)).ProviderType = CloudProvider.DigitalOcean;
            var plan = await db.ProviderPlans.SingleAsync(x => x.Id == context.PlanId);
            plan.Region = "nyc1";
            plan.Sku = "s-1vcpu-1gb";
            var deployment = await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId);
            deployment.ProviderType = "DigitalOcean";
            deployment.ProviderRegion = plan.Region;
            deployment.ProviderSku = plan.Sku;
            await db.SaveChangesAsync();
        }

        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"droplets":[]}""");
        handler.QueueJsonResponse("""{"droplet":{"id":6002,"name":"created-droplet","status":"new"}}""", HttpStatusCode.Created);
        handler.QueueJsonResponse("""{"message":"Project assignment rejected"}""", status);
        handler.QueueJsonResponse("""{"droplets":[]}""");
        using var http = new HttpClient(handler);
        var factory = new AccountFactory(new DigitalOceanClient(http, new DigitalOceanOptions
        {
            ApiToken = "synthetic-token", ProjectId = "synthetic-project", BaseUrl = "https://api.digitalocean.com/v2/"
        }));
        var step = CatalogStep(factory);

        await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        var retry = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));

        Assert.Contains("unresolved", retry.Message);
        Assert.Single(handler.RecordedRequests, x => x.Method == HttpMethod.Post && x.Uri.EndsWith("/droplets", StringComparison.Ordinal));
        await using var verify = _harness.CreateDbContext();
        Assert.True((await verify.Deployments.SingleAsync(x => x.Id == context.DeploymentId)).ProviderCreationAttempted);
        Assert.All(factory.RequestedAccounts, x => Assert.Equal(context.ProviderAccountId, x));
    }

    [Fact]
    public async Task DisabledCatalogAccountReusesItsCurrentServerDespiteRetiredPlacementAndExternalIdInAnotherAccount()
    {
        var context = await SeedCatalogAsync("catalog-reuse");
        var (otherOrg, otherApp) = await _harness.SeedOrgAndAppAsync("catalog-reuse-other");
        await using (var db = _harness.CreateDbContext())
        {
            var original = await db.ProviderAccounts.SingleAsync(x => x.Id == context.ProviderAccountId);
            original.Enabled = false;
            db.ProviderAccounts.Add(new ProviderAccount { Id = "reuse-other-account", OrganizationId = otherOrg.Id,
                Name = "Other scope", ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic/other", CredentialGeneration = "test" });
            db.ServerInstances.AddRange(new ServerInstance { Id = "reuse-original-server", AppId = context.AppId, OrganizationId = context.OrganizationId,
                ProviderAccountId = context.ProviderAccountId, PlanId = context.PlanId, Provider = "hetzner", ProviderServerId = "same-id",
                Region = "nbg1", Status = "active", BootstrapStatus = "ready" },
                new ServerInstance { Id = "reuse-other-server", AppId = otherApp.Id, OrganizationId = otherOrg.Id,
                    ProviderAccountId = "reuse-other-account", Provider = "hetzner", ProviderServerId = "same-id",
                    Region = "nbg1", Status = "active", BootstrapStatus = "ready" },
                new ServerInstance { Id = "reuse-retired-server", AppId = context.AppId, OrganizationId = context.OrganizationId,
                    ProviderAccountId = context.ProviderAccountId, PlanId = context.PlanId, Provider = "hetzner", ProviderServerId = "retired-other-id",
                    Region = "nbg1", Status = ServerInstance.RetiredStatus, BootstrapStatus = "ready", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var factory = new AccountFactory(new MockVpsProviderClient(findServer: () => throw new Exception("Registered server must not use discovery.")));
        await CatalogStep(factory).ExecuteAsync(context, default);
        Assert.Equal([context.ProviderAccountId!], factory.RequestedAccounts);
        Assert.Equal("127.0.0.1", context.ServerIp);
        Assert.Equal("reuse-original-server", context.ServerInstanceId);
    }

    [Fact]
    public async Task RetiredOnlyPlacementDoesNotAllowOrdinaryProvisioning()
    {
        var context = await SeedCatalogAsync("retired-only");
        await using (var db = _harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance { Id = "retired-only-server", AppId = context.AppId, OrganizationId = context.OrganizationId,
                ProviderAccountId = context.ProviderAccountId, PlanId = context.PlanId, Provider = "hetzner", ProviderServerId = "retired-id",
                Region = "nbg1", Status = ServerInstance.RetiredStatus, BootstrapStatus = "ready" });
            await db.SaveChangesAsync();
        }

        var factory = new AccountFactory(new MockVpsProviderClient());
        var error = await Assert.ThrowsAsync<PipelineStepException>(() => CatalogStep(factory).ExecuteAsync(context, default));
        Assert.Contains("refusing replacement provisioning", error.Message);
        Assert.Empty(factory.RequestedAccounts);
    }

    [Fact]
    public async Task DiscoveredCatalogResourceKeepsOriginalScopeWhenReadinessFails()
    {
        var context = await SeedCatalogAsync("catalog-discovered");
        ProviderServerInfo found;
        await using (var db = _harness.CreateDbContext())
        {
            var app = await db.Apps.Include(x => x.Organization).SingleAsync(x => x.Id == context.AppId);
            found = new ProviderServerInfo("discovered-id", ResolveProvisionServerStep.BuildServerName(app.Organization.Slug, app.Slug), "", "starting",
                Labels: new Dictionary<string, string> { ["managed-by"] = "selfrun", ["customer"] = app.Organization.Slug,
                    ["app"] = app.Slug, ["env"] = "production" });
        }
        var factory = new AccountFactory(new MockVpsProviderClient(findServer: () => Task.FromResult<ProviderServerInfo?>(found),
            createServer: _ => throw new Exception("Must not create after discovery.")));
        var step = new ResolveProvisionServerStep(
            _harness.CreateScopeFactory(services => services.AddSingleton<ProviderAccountClientFactory>(factory)),
            _ => throw new Exception("Must not use global credentials."),
            new DeploymentPipelineOptions { SshReadyTimeout = TimeSpan.Zero, ProvisionTimeout = TimeSpan.Zero }, TimeProvider.System);
        await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        await using var verify = _harness.CreateDbContext();
        Assert.True((await verify.Deployments.SingleAsync(x => x.Id == context.DeploymentId)).ProviderCreationAttempted);
        var saved = await verify.ServerInstances.SingleAsync(x => x.AppId == context.AppId);
        Assert.Equal(context.ProviderAccountId, saved.ProviderAccountId);
        Assert.Equal("discovered-id", saved.ProviderServerId);
    }

    [Fact]
    public async Task AmbiguousCreateWithSshKeyIsReconciledWithoutReallocationOrReregistration()
    {
        var context = await SeedCatalogSshAsync("ssh-ambiguous");
        var provider = new ProviderSshKeyAllocationTests.SyntheticSshProvider();
        var requests = new List<CreateServerRequest>();
        provider.CreateServer = request =>
        {
            requests.Add(request);
            throw new HttpRequestException("Response lost.");
        };
        var step = CatalogStep(new AccountFactory(provider));

        await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        var retry = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));

        Assert.Contains("unresolved", retry.Message);
        var request = Assert.Single(requests);
        Assert.Equal(["500"], request.SshKeyReferences);
        Assert.Equal(1, provider.RegisterCalls);

        // Reconciliation discovers the server the lost response created; no second allocation occurs.
        provider.FindServer = name => Task.FromResult<ProviderServerInfo?>(
            new ProviderServerInfo("srv-reconciled", name, "192.0.2.81", "running", null, request.Labels));
        await step.ExecuteAsync(context, default);

        Assert.Single(requests);
        Assert.Equal(1, provider.RegisterCalls);
        Assert.Equal("192.0.2.81", context.ServerIp);
        await using var db = _harness.CreateDbContext();
        Assert.Equal("srv-reconciled", (await db.ServerInstances.SingleAsync(x => x.AppId == context.AppId)).ProviderServerId);
        var key = await db.ProviderSshKeys.SingleAsync(x => x.ProviderAccountId == context.ProviderAccountId);
        Assert.Equal("500", key.ProviderKeyId);
        Assert.Equal("ready", key.RegistrationState);
    }

    [Fact]
    public async Task UnknownSshKeyRegistrationBlocksServerAllocation()
    {
        var context = await SeedCatalogSshAsync("ssh-unknown");
        var provider = new ProviderSshKeyAllocationTests.SyntheticSshProvider
        {
            RegisterFailure = new HttpRequestException("Response lost."),
            HideRegisteredKeys = true
        };
        var step = CatalogStep(new AccountFactory(provider));

        var error = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));

        Assert.Equal("ssh_registration_unknown", error.FailureCode);
        await using var db = _harness.CreateDbContext();
        var deployment = await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId);
        Assert.False(deployment.ProviderCreationAttempted);
        Assert.Equal("unknown", deployment.SshRegistrationState);
        Assert.False(await db.ServerInstances.AnyAsync(x => x.AppId == context.AppId));
    }

    private async Task<PipelineStepContext> SeedCatalogSshAsync(string name)
    {
        var context = await SeedCatalogAsync(name);
        var (publicKey, fingerprint) = ProviderSshKeyAllocationTests.Key(7);
        var now = DateTimeOffset.UtcNow;
        await using var db = _harness.CreateDbContext();
        db.Users.Add(new User { Id = "user_" + name, Email = name + "@example.test", CreatedAt = now, UpdatedAt = now });
        db.OrganizationMembers.Add(new OrganizationMember { Id = "member_" + name, OrganizationId = context.OrganizationId,
            UserId = "user_" + name, Role = OrganizationRole.Owner, CreatedAt = now, UpdatedAt = now });
        var deployment = await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId);
        deployment.SshSelectionMode = "new";
        deployment.SshSelectionRevision = 1;
        deployment.SshProviderAccountId = context.ProviderAccountId;
        deployment.SshCredentialGeneration = "test";
        deployment.SshSelectingActor = "user_" + name;
        deployment.SshKeyName = "Workstation";
        deployment.SshPublicKey = publicKey;
        deployment.SshFingerprint = fingerprint;
        deployment.SshKeyType = "ed25519";
        deployment.SshRegistrationState = "not_registered";
        deployment.SshSnapshotTakenAt = now;
        await db.SaveChangesAsync();
        return context;
    }

    private ResolveProvisionServerStep CatalogStep(AccountFactory factory) => new(
        _harness.CreateScopeFactory(services => services.AddSingleton<ProviderAccountClientFactory>(factory)),
        _ => throw new InvalidOperationException("Catalog plan cannot use global provider credentials."),
        new DeploymentPipelineOptions { SshReadyTimeout = TimeSpan.Zero }, TimeProvider.System);

    private async Task<PipelineStepContext> SeedCatalogAsync(string name)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync(name);
        var account = new ProviderAccount { Id = name + "-account", OrganizationId = org.Id,
            Name = name, ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic/" + name, CredentialGeneration = "test" };
        var plan = new ProviderPlan { Id = name + "-plan", OrganizationId = org.Id, ProviderAccount = account,
            ProviderAccountId = account.Id, Name = name, NormalizedName = name.ToUpperInvariant(), Region = "nbg1", Sku = "cx23" };
        await using var db = _harness.CreateDbContext();
        db.ProviderPlans.Add(plan);
        (await db.Apps.SingleAsync(x => x.Id == app.Id)).PlanId = plan.Id;
        db.Deployments.Add(new Deployment { Id = name + "-deployment", AppId = app.Id, OrganizationId = org.Id,
            Status = DeploymentStatus.Provisioning, PlanId = plan.Id, ProviderAccountId = account.Id,
            ProviderType = "Hetzner", ProviderRegion = "nbg1", ProviderSku = "cx23" });
        await db.SaveChangesAsync();
        return new PipelineStepContext { AppId = app.Id, OrganizationId = org.Id, DeploymentId = name + "-deployment",
            PlanId = plan.Id, ProviderAccountId = account.Id };
    }

    /// <summary>Simulates a server whose cloud-init proves its Ed25519 host key with the delivered enrollment secret.</summary>
    private sealed class EnrollingHostKeyProbe(Func<string?> userData, int unprovenObservations = 0) : IServerHostKeyProbe
    {
        private readonly byte[] _key = ServerHostKeyEnrollmentTests.Ed25519Blob();

        public int Calls { get; private set; }
        public string Fingerprint => "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(_key)).TrimEnd('=');

        public static (string ServerId, string Secret) Material(string cloudInit) => (
            Regex.Match(cloudInit, "SERVER_ID = \"([0-9a-f]{32})\"").Groups[1].Value,
            Regex.Match(cloudInit, "SECRET = bytes.fromhex\\(\"([0-9a-f]{64})\"\\)").Groups[1].Value);

        public Task<SshHostKeyObservation?> ObserveAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= unprovenObservations)
            {
                return Task.FromResult<SshHostKeyObservation?>(new("ssh-ed25519", _key, null));
            }
            var (serverId, secret) = Material(userData()!);
            return Task.FromResult<SshHostKeyObservation?>(new("ssh-ed25519", _key,
                ServerHostKeyEnrollmentTests.Banner(serverId, Convert.FromHexString(secret), _key)));
        }
    }

    private sealed class AccountFactory(IVpsProviderClient client) : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!)
    {
        public List<string> RequestedAccounts { get; } = [];
        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId, CancellationToken ct, bool allowDisabled = true)
        {
            RequestedAccounts.Add(providerAccountId);
            return Task.FromResult(client);
        }
    }

    private sealed class MockVpsProviderClient(
        Func<Task<ProviderServerInfo?>>? findServer = null,
        Func<CreateServerRequest, Task<ProviderServerInfo>>? createServer = null,
        Func<string, Task<ProviderServerStatus>>? getServerStatus = null) : IVpsProviderClient
    {
        public CloudProvider Provider => CloudProvider.Hetzner;

        public string? RequestedName { get; private set; }

        public Task<ProviderServerInfo?> FindServerAsync(string name, CancellationToken cancellationToken = default)
        {
            RequestedName = name;
            return findServer is not null ? findServer() : Task.FromResult<ProviderServerInfo?>(null);
        }

        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default) =>
            createServer is not null
                ? createServer(request)
                : Task.FromResult(new ProviderServerInfo("id-1", request.Name, "127.0.0.1", "running", null, request.Labels));

        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default) =>
            getServerStatus is not null
                ? getServerStatus(serverId)
                : Task.FromResult(new ProviderServerStatus(serverId, "running", "127.0.0.1", null, true));

        public Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
