using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Pipeline.Steps;
using Minicloud.Worker.Ssh;
using Minicloud.Worker.Storage;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class SshDeployStepTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;

    public SshDeployStepTests(WorkerSqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task UnpinnedAssetsResolveOnlySuccessfulPublicationsInTheOwningApp()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("asset-current");
        await using var db = _harness.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        foreach (var (name, offset, status) in new[] {
            ("first", 0, DeploymentStatus.Succeeded),
            ("second", 1, DeploymentStatus.Succeeded),
            ("failed", 2, DeploymentStatus.Failed) })
        {
            var artifact = Artifact("art_current_" + name, org.Id, app.Id, "cli", now);
            var release = new AssetRelease {
                Id = "asset_current_" + name, AppId = app.Id, OrganizationId = org.Id,
                Name = "cli", Artifact = artifact, InputSha256 = new string('a', 64),
                OutputPath = "/cli-releases", CreatedAt = now, ReadyAt = now.AddMinutes(offset),
                ManifestJson = "{}", ManifestSha256 = new string('b', 64)
            };
            var deployment = Deployment("dep_current_" + name, org.Id, app.Id, status, now);
            deployment.AssetRelease = release;
            db.Deployments.Add(deployment);
        }
        await db.SaveChangesAsync();
        Assert.Equal("asset_current_second", (await AssetReleaseService.ResolveAsync(db, app.Id, org.Id, "cli", null, default))!.Id);
        Assert.Equal("asset_current_first", (await AssetReleaseService.ResolveAsync(db, app.Id, org.Id, "cli", "asset_current_first", default))!.Id);
        Assert.Null(await AssetReleaseService.ResolveAsync(db, app.Id, "foreign-org", "cli", null, default));
        Assert.Null(await AssetReleaseService.ResolveAsync(db, "foreign-app", org.Id, "cli", null, default));
        Assert.Null(await AssetReleaseService.ResolveAsync(db, app.Id, org.Id, "other-asset", null, default));
    }

    [Fact]
    public async Task AssetPublicationTransfersSourceAndPersistsManifestWithoutAgentOrRuntimeServices()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("asset-publish");
        var now = DateTimeOffset.UtcNow;
        await using (var db = _harness.CreateDbContext())
        {
            var artifact = Artifact("art_asset_publish", org.Id, app.Id, "cli", now);
            artifact.Status = DeploymentArtifactStatus.Ready;
            var release = new AssetRelease
            {
                Id = "asset_publish",
                AppId = app.Id,
                OrganizationId = org.Id,
                Name = "cli",
                Artifact = artifact,
                InputSha256 = new string('a', 64),
                OutputPath = "/cli-releases",
                CreatedAt = now
            };
            var deployment = Deployment("dep_asset_publish", org.Id, app.Id, DeploymentStatus.Deploying, now);
            deployment.OwnershipToken = "owner_asset_publish";
            deployment.AssetRelease = release;
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }
        var ssh = new MockSshDeploymentClient(request =>
        {
            using var payload = System.Text.Json.JsonDocument.Parse(request.PayloadJson);
            Assert.Equal(0, payload.RootElement.GetProperty("services").GetArrayLength());
            Assert.False(payload.RootElement.TryGetProperty("runtime_agent", out _));
            Assert.Equal(3, request.Artifacts.Count);
            Assert.Contains(request.Artifacts, a => a.RemoteFileName == "runtime-maintenance.py");
            Assert.Contains(request.Artifacts, a => a.RemoteFileName == "cli.zip");
            var manifest = new
            {
                protocolVersion = 1,
                version = "1.0.0+fixture",
                packages = new[] { "osx-arm64", "osx-x64", "linux-arm64", "linux-x64", "win-x64" }
                .Select(r => new { runtimeIdentifier = r, fileName = "si-" + r + ".zip", sizeBytes = 10, sha256 = new string('a', 64) }).ToArray()
            };
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var metadata = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                manifest,
                manifestBase64 = Convert.ToBase64String(bytes),
                manifestSha256 = digest,
                removedReleaseIds = Array.Empty<string>()
            }));
            return Task.FromResult(new SshDeploymentResult(0, true, "dep_asset_publish", ["__MINICLOUD_ASSET_MANIFEST__=" + metadata]));
        });
        var step = new SshDeployStep(_harness.CreateScopeFactory(), new MockArtifactRetriever([1, 2, 3]), ssh,
            Options.Create(new SshDeploymentOptions { PrivateKeyContent = "MOCK_PRIVATE_KEY" }), Options.Create(new DeploymentPipelineOptions()),
            new TestHostEnvironment(), TimeProvider.System);
        await step.ExecuteAsync(new PipelineStepContext
        {
            DeploymentId = "dep_asset_publish",
            OwnershipToken = "owner_asset_publish",
            OrganizationId = org.Id,
            OrganizationSlug = org.Slug,
            AppId = app.Id,
            AppSlug = app.Slug,
            ServerIp = "192.0.2.2"
        }, default);
        await using var replay = _harness.CreateDbContext();
        var published = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(replay.AssetReleases, r => r.Id == "asset_publish");
        Assert.NotNull(published.ReadyAt);
        Assert.Equal(64, published.ManifestSha256!.Length);
        Assert.Empty(replay.DeploymentServices.Where(s => s.DeploymentId == "dep_asset_publish"));
        Assert.Empty(replay.RuntimeTokens.Where(t => t.RuntimeServer.AppId == app.Id));
    }

    [Fact]
    public void ClassifySource_IdentifiesSourcesCorrectly()
    {
        Assert.Equal("build", SshDeployStep.ClassifySource("[build:api] Step 1/5 : FROM alpine"));
        Assert.Equal("compose", SshDeployStep.ClassifySource("Container app-api Recreated"));
        Assert.Equal("health", SshDeployStep.ClassifySource("Waiting for health checks..."));
        Assert.Equal("deploy", SshDeployStep.ClassifySource("Extracting artifacts"));
    }

    [Fact]
    public void SanitizeId_RemovesSpecialCharacters()
    {
        Assert.Equal("dep-123-abc", SshDeployStep.SanitizeId("dep_123_abc"));
        Assert.Equal("deploy", SshDeployStep.SanitizeId(""));
    }

    [Fact]
    public void ResolveFailureCode_UsesRuntimePhaseMarker()
    {
        Assert.Equal("health_check_failed", SshDeployStep.ResolveFailureCode(
            ["Health check failed for admin", "__MINICLOUD_DEPLOY_FAILURE__=health_check_failed"]));
        Assert.Equal(SshDeployStep.FailureCode, SshDeployStep.ResolveFailureCode(
            ["__MINICLOUD_DEPLOY_FAILURE__=invalid code"]));
    }

    [Fact]
    public void ResolveServiceHostsUsesExactServiceMappingForPrefixNames()
    {
        var context = new PipelineStepContext
        {
            DeploymentId = "dep-host-map",
            OrganizationId = "org-host-map",
            AppId = "app-host-map"
        };
        context.ServiceHostnames["api-admin"] = ["admin.example.test"];
        context.ServiceHostnames["api"] = ["api.example.test", "legacy.example.test"];

        Assert.Equal(new[] { "api.example.test", "legacy.example.test" }, SshDeployStep.ResolveServiceHosts(
            context, new DeploymentService { Name = "api", Public = true }));
        Assert.Empty(SshDeployStep.ResolveServiceHosts(
            context, new DeploymentService { Name = "disabled", Public = true }));
    }

    [Fact]
    public async Task PostgresDeploymentIncludesStoredPasswordInPayload()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("sshpostgres");
        var now = DateTimeOffset.UtcNow;
        var encryption = new SecretEncryptionService(Options.Create(new SecretEncryptionOptions { EncryptionKey = "test-encryption-key" }));
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment
            {
                Id = "dep-ssh-postgres",
                OrganizationId = org.Id,
                AppId = app.Id,
                OwnershipToken = "owner-ssh-postgres",
                Status = DeploymentStatus.Deploying,
                Database = DatabaseMode.Postgres,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.AppSecrets.Add(new AppSecret
            {
                Id = "sec-ssh-postgres",
                OrganizationId = org.Id,
                AppId = app.Id,
                Name = AppSecretService.PostgresPasswordSecretName,
                EncryptedValue = encryption.Encrypt("correct-password"),
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        SshDeploymentRequest? captured = null;
        var ssh = new MockSshDeploymentClient(exec: request =>
        {
            captured = request;
            return Task.FromResult(new SshDeploymentResult(0, true, "abc1234", ["__MINICLOUD_DEPLOY_COMPLETE__=abc1234"]));
        });
        var tempDir = Directory.CreateTempSubdirectory("minicloud-test-postgres-");
        try
        {
            var scriptDir = Path.Combine(tempDir.FullName, "scripts", "github-actions", "minicloud-deploy");
            Directory.CreateDirectory(scriptDir);
            await File.WriteAllTextAsync(Path.Combine(scriptDir, "remote-deploy.sh"), "#!/bin/bash\necho deploy");
            var step = new SshDeployStep(
                _harness.CreateScopeFactory(services => services.AddSingleton(encryption)),
                new MockArtifactRetriever([]), ssh,
                Options.Create(new SshDeploymentOptions { PrivateKeyContent = "MOCK_PRIVATE_KEY" }),
                Options.Create(new DeploymentPipelineOptions()),
                new TestHostEnvironment { ContentRootPath = tempDir.FullName },
                TimeProvider.System, NullLogger<SshDeployStep>.Instance);

            await step.ExecuteAsync(new PipelineStepContext
            {
                DeploymentId = "dep-ssh-postgres",
                OrganizationId = org.Id,
                AppId = app.Id,
                OwnershipToken = "owner-ssh-postgres",
                ServerIp = "192.0.2.77"
            }, CancellationToken.None);

            Assert.NotNull(captured);
            using var payload = System.Text.Json.JsonDocument.Parse(captured.PayloadJson);
            Assert.Equal("correct-password", payload.RootElement.GetProperty("postgres_password").GetString());
            Assert.True(payload.RootElement.GetProperty("postgres_password_update").GetBoolean());
            var cache = payload.RootElement.GetProperty("build_cache");
            Assert.Equal(4294967296L, cache.GetProperty("max_bytes").GetInt64());
            Assert.Equal(168, cache.GetProperty("max_age_hours").GetInt32());
            Assert.Equal(2147483648L, cache.GetProperty("min_free_bytes").GetInt64());
            Assert.Equal(10, cache.GetProperty("min_free_percent").GetInt32());
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public async Task ExecutesSshDeploymentSuccessfully()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("sshsucc");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            var art = new DeploymentArtifact
            {
                Id = "art-ssh-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                ServiceName = "api",
                FileName = "api.zip",
                ContentType = "application/zip",
                ManifestJson = "{}",
                StoragePath = "artifacts/test.zip",
                SizeBytes = 100,
                Sha256 = "dummy",
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = now.AddDays(7)
            };
            db.DeploymentArtifacts.Add(art);

            var dep = new Deployment
            {
                Id = "dep-ssh-1",
                OwnershipToken = "owner-ssh-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Deploying,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(dep);

            db.DeploymentServices.Add(new DeploymentService
            {
                Id = "svc-api",
                DeploymentId = dep.Id,
                Name = "api",
                Image = "ghcr.io/muniventures/app/api:dep-ssh-1",
                Path = "/",
                HealthPath = "/health",
                ArtifactId = art.Id,
                Public = true,
                Port = 8080
            });
            await db.SaveChangesAsync();
        }

        SshDeploymentRequest? capturedRequest = null;
        var mockSsh = new MockSshDeploymentClient(
            exec: req =>
            {
                capturedRequest = req;
                return Task.FromResult(new SshDeploymentResult(
                    0, true, "abc1234", ["__MINICLOUD_DEPLOY_COMPLETE__=abc1234"]));
            });

        var mockArtifact = new MockArtifactRetriever(
            "dummy zip content"u8.ToArray());

        var tempDir = Directory.CreateTempSubdirectory("minicloud-test-ssh-");
        try
        {
            var scriptDir = Path.Combine(tempDir.FullName, "scripts", "github-actions", "minicloud-deploy");
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, "remote-deploy.sh");
            await File.WriteAllTextAsync(scriptPath, "#!/bin/bash\necho deploy");

            var env = new TestHostEnvironment { ContentRootPath = tempDir.FullName };

            var agent = new StubRuntimeAgentDeployment();
            var step = new SshDeployStep(
                _harness.CreateScopeFactory(services => services.AddSingleton<IAppServiceSecretResolver>(new FixedSecretResolver())),
                mockArtifact,
                mockSsh,
                Options.Create(new SshDeploymentOptions
                {
                    PrivateKeyContent = "MOCK_PRIVATE_KEY",
                    Username = "minicloud"
                }),
                Options.Create(new DeploymentPipelineOptions()),
                env,
                TimeProvider.System,
                NullLogger<SshDeployStep>.Instance, agent);

            var context = new PipelineStepContext
            {
                DeploymentId = "dep-ssh-1",
                OwnershipToken = "owner-ssh-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                ServerIp = "192.0.2.77"
            };

            await step.ExecuteAsync(context, CancellationToken.None);

            Assert.NotNull(capturedRequest);
            Assert.Equal("192.0.2.77", capturedRequest.Host);
            Assert.Equal("minicloud", capturedRequest.Username);
            Assert.Equal("MOCK_PRIVATE_KEY", capturedRequest.PrivateKeyContent);
            Assert.Contains("build_artifacts", capturedRequest.PayloadJson);
            using var payload = System.Text.Json.JsonDocument.Parse(capturedRequest.PayloadJson);
            Assert.False(payload.RootElement.GetProperty("build_artifacts")[0].TryGetProperty("dockerfile_path", out _));
            var serviceImage = payload.RootElement.GetProperty("services")[0].GetProperty("image").GetString();
            var buildImage = payload.RootElement.GetProperty("build_artifacts")[0].GetProperty("image_tag").GetString();
            Assert.Equal(buildImage, serviceImage);
            Assert.StartsWith("minicloud/", serviceImage);
            Assert.DoesNotContain("ghcr.io/", capturedRequest.PayloadJson);
            Assert.Equal("resolved-secret", payload.RootElement.GetProperty("services")[0].GetProperty("env").GetProperty("API_KEY").GetString());
            Assert.Equal(3, capturedRequest.Artifacts.Count);
            Assert.Contains(capturedRequest.Artifacts, a => a.RemoteFileName == "runtime-maintenance.py");
            Assert.Equal("fixture-token", payload.RootElement.GetProperty("runtime_agent").GetProperty("token").GetString());
            Assert.Contains(capturedRequest.Artifacts, a => a.RemoteFileName == "minicloud-agent");
            Assert.True(agent.Completed);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public async Task PartialDeploymentBuildsOnlyChangedArtifactAndPreservesRetainedRuntimeShape()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("sshpartial");
        var now = DateTimeOffset.UtcNow;
        await using (var db = _harness.CreateDbContext())
        {
            var oldWeb = Artifact("art-web-old", org.Id, app.Id, "web", now);
            var newApi = Artifact("art-api-new", org.Id, app.Id, "api", now);
            db.DeploymentArtifacts.AddRange(oldWeb, newApi);
            var prior = Deployment("dep-prior", org.Id, app.Id, DeploymentStatus.Succeeded, now.AddMinutes(-2));
            db.Deployments.Add(prior);
            db.DeploymentServices.AddRange(
                Service("svc-prior-web", prior.Id, "web", oldWeb.Id, null, 3000, true, "/", "/ready", "{\"MODE\":\"production\"}"),
                Service("svc-prior-cache", prior.Id, "cache", null, "redis:7-alpine", 6379, false, "/", "/health"));

            var candidate = Deployment("dep-partial", org.Id, app.Id, DeploymentStatus.Deploying, now);
            candidate.OwnershipToken = "owner-partial";
            db.Deployments.Add(candidate);
            db.DeploymentServices.AddRange(
                Service("svc-candidate-web", candidate.Id, "web", oldWeb.Id, null, 3000, true, "/", "/ready", "{\"MODE\":\"production\"}"),
                Service("svc-candidate-api", candidate.Id, "api", newApi.Id, null, 8080, false, "/api", "/health"),
                Service("svc-candidate-cache", candidate.Id, "cache", null, "redis:7-alpine", 6379, false, "/", "/health"));
            await db.SaveChangesAsync();
        }

        SshDeploymentRequest? captured = null;
        string? retainedCheck = null;
        var ssh = new MockSshDeploymentClient(
            exec: request =>
            {
                captured = request;
                return Task.FromResult(new SshDeploymentResult(0, true, "sha", ["__MINICLOUD_DEPLOY_COMPLETE__=sha"]));
            },
            commandCallback: command =>
            {
                retainedCheck = command;
                return Task.FromResult(0);
            });

        await ExecuteStepAsync(org.Id, app.Id, app.Slug, "dep-partial", "owner-partial", ssh, new MockArtifactRetriever("zip"u8.ToArray()),
            new FixedSecretResolver());

        Assert.NotNull(captured);
        Assert.Contains($"minicloud/{app.Slug}-web:dep-prior", retainedCheck);
        Assert.Equal(2, captured.Artifacts.Count);
        Assert.Contains(captured.Artifacts, artifact => artifact.ServiceName == "api");
        Assert.Contains(captured.Artifacts, artifact => artifact.RemoteFileName == "runtime-maintenance.py");
        using var payload = System.Text.Json.JsonDocument.Parse(captured.PayloadJson);
        var services = payload.RootElement.GetProperty("services").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        Assert.Equal($"minicloud/{app.Slug}-web:dep-prior", services["web"].GetProperty("image").GetString());
        Assert.Equal(3000, services["web"].GetProperty("port").GetInt32());
        Assert.Equal(new[] { "legacy.example.test", "nexus.example.test" },
            services["web"].GetProperty("hosts").EnumerateArray().Select(host => host.GetString()));
        Assert.Equal("production", services["web"].GetProperty("env").GetProperty("MODE").GetString());
        Assert.Equal("redis:7-alpine", services["cache"].GetProperty("image").GetString());
        Assert.False(services["api"].GetProperty("public").GetBoolean());
        Assert.Empty(services["api"].GetProperty("hosts").EnumerateArray());
        var builds = payload.RootElement.GetProperty("build_artifacts").EnumerateArray().ToArray();
        Assert.Single(builds);
        Assert.Equal("api", builds[0].GetProperty("service_name").GetString());
    }

    [Fact]
    public async Task MissingRetainedImageFailsBeforeArtifactTransferOrDeployScript()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("sshmissing");
        var now = DateTimeOffset.UtcNow;
        await using (var db = _harness.CreateDbContext())
        {
            var artifact = Artifact("art-missing", org.Id, app.Id, "web", now);
            db.DeploymentArtifacts.Add(artifact);
            var prior = Deployment("dep-owner", org.Id, app.Id, DeploymentStatus.Succeeded, now.AddMinutes(-1));
            db.Deployments.Add(prior);
            db.DeploymentServices.Add(Service("svc-owner", prior.Id, "web", artifact.Id, null, 3000, true, "/", "/health"));
            var candidate = Deployment("dep-retain", org.Id, app.Id, DeploymentStatus.Deploying, now);
            candidate.OwnershipToken = "owner-retain";
            db.Deployments.Add(candidate);
            db.DeploymentServices.Add(Service("svc-retain", candidate.Id, "web", artifact.Id, null, 3000, true, "/", "/health"));
            await db.SaveChangesAsync();
        }

        var executeCount = 0;
        var ssh = new MockSshDeploymentClient(
            exec: request =>
            {
                executeCount++;
                return Task.FromResult(new SshDeploymentResult(0, true, "sha", []));
            },
            commandCallback: _ => Task.FromResult(1));

        var ex = await Assert.ThrowsAsync<PipelineStepException>(() => ExecuteStepAsync(
            org.Id, app.Id, app.Slug, "dep-retain", "owner-retain", ssh, new MockArtifactRetriever([]), new FixedSecretResolver()));

        Assert.Equal(SshDeployStep.FailureCode, ex.FailureCode);
        Assert.Contains("retained service image is missing", ex.Message);
        Assert.Equal(0, executeCount);
    }

    private sealed class FixedSecretResolver : IAppServiceSecretResolver
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ResolveForDeploymentAsync(
            string appId, IReadOnlyList<DeploymentServiceRequest> services, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["api"] = new Dictionary<string, string> { ["API_KEY"] = "resolved-secret" }
                });
    }

    [Fact]
    public async Task ThrowsDockerBuildFailedWhenExitCodeNonZero()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("sshfail");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            var dep = new Deployment
            {
                Id = "dep-ssh-fail",
                OwnershipToken = "owner-ssh-fail",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Deploying,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
        }

        var mockSsh = new MockSshDeploymentClient(
            exec: _ => Task.FromResult(new SshDeploymentResult(1, false, null, ["error building image"])));

        var tempDir = Directory.CreateTempSubdirectory("minicloud-test-ssh-");
        try
        {
            var scriptDir = Path.Combine(tempDir.FullName, "scripts", "github-actions", "minicloud-deploy");
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, "remote-deploy.sh");
            await File.WriteAllTextAsync(scriptPath, "#!/bin/bash\necho deploy");

            var env = new TestHostEnvironment { ContentRootPath = tempDir.FullName };

            var step = new SshDeployStep(
                _harness.CreateScopeFactory(),
                new MockArtifactRetriever([]),
                mockSsh,
                Options.Create(new SshDeploymentOptions
                {
                    PrivateKeyContent = "MOCK_PRIVATE_KEY"
                }),
                Options.Create(new DeploymentPipelineOptions()),
                env,
                TimeProvider.System,
                NullLogger<SshDeployStep>.Instance);

            var context = new PipelineStepContext
            {
                DeploymentId = "dep-ssh-fail",
                OwnershipToken = "owner-ssh-fail",
                OrganizationId = org.Id,
                AppId = app.Id,
                ServerIp = "192.0.2.77"
            };

            var ex = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, CancellationToken.None));
            Assert.Equal(SshDeployStep.FailureCode, ex.FailureCode);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    private async Task ExecuteStepAsync(
        string organizationId,
        string appId,
        string appSlug,
        string deploymentId,
        string ownershipToken,
        ISshDeploymentClient ssh,
        IArtifactRetriever artifacts,
        IAppServiceSecretResolver secretResolver)
    {
        var tempDir = Directory.CreateTempSubdirectory("minicloud-test-partial-");
        try
        {
            var scriptDir = Path.Combine(tempDir.FullName, "scripts", "github-actions", "minicloud-deploy");
            Directory.CreateDirectory(scriptDir);
            await File.WriteAllTextAsync(Path.Combine(scriptDir, "remote-deploy.sh"), "#!/bin/bash\necho deploy");
            var step = new SshDeployStep(
                _harness.CreateScopeFactory(services => services.AddSingleton(secretResolver)), artifacts, ssh,
                Options.Create(new SshDeploymentOptions { PrivateKeyContent = "MOCK_PRIVATE_KEY" }),
                Options.Create(new DeploymentPipelineOptions()),
                new TestHostEnvironment { ContentRootPath = tempDir.FullName }, TimeProvider.System,
                NullLogger<SshDeployStep>.Instance);
            await step.ExecuteAsync(new PipelineStepContext
            {
                DeploymentId = deploymentId,
                OwnershipToken = ownershipToken,
                OrganizationId = organizationId,
                AppId = appId,
                AppSlug = appSlug,
                ServerIp = "192.0.2.77",
                ServiceHostnames = { ["web"] = ["legacy.example.test", "nexus.example.test"] }
            }, CancellationToken.None);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    private static DeploymentArtifact Artifact(string id, string organizationId, string appId, string serviceName, DateTimeOffset now) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        AppId = appId,
        ServiceName = serviceName,
        FileName = serviceName + ".zip",
        ContentType = "application/zip",
        ManifestJson = "{}",
        StoragePath = "artifacts/" + id + ".zip",
        SizeBytes = 3,
        Sha256 = id,
        CreatedAt = now,
        UpdatedAt = now,
        ExpiresAt = now.AddDays(1)
    };

    private static Deployment Deployment(string id, string organizationId, string appId, DeploymentStatus status, DateTimeOffset now) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        AppId = appId,
        Status = status,
        Database = DatabaseMode.None,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static DeploymentService Service(string id, string deploymentId, string name, string? artifactId, string? image,
        int port, bool isPublic, string path, string healthPath, string? envJson = null) => new()
        {
            Id = id,
            DeploymentId = deploymentId,
            Name = name,
            ArtifactId = artifactId,
            Image = image ?? string.Empty,
            Port = port,
            Public = isPublic,
            Path = path,
            HealthPath = healthPath,
            EnvJson = envJson
        };

    private sealed class MockSshDeploymentClient(
        Func<SshDeploymentRequest, Task<SshDeploymentResult>>? exec = null,
        Func<string, Task<int>>? commandCallback = null) : ISshDeploymentClient
    {
        public Task<SshDeploymentResult> ExecuteDeploymentAsync(
            SshDeploymentRequest request,
            Func<string, string, Task>? onOutputLine = null,
            CancellationToken cancellationToken = default) =>
            exec is not null
                ? exec(request)
                : Task.FromResult(new SshDeploymentResult(0, true, "sha123", []));

        public Task<int> ExecuteCommandAsync(
            string serverIp,
            string username,
            string privateKey,
            string command,
            Func<string, string, Task>? onOutputLine = null,
            CancellationToken cancellationToken = default) =>
            commandCallback is not null ? commandCallback(command) : Task.FromResult(0);
    }

    private sealed class MockArtifactRetriever(byte[] data) : IArtifactRetriever
    {
        public Task<Stream> OpenArtifactStreamAsync(string artifactId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(data));

        public Task<string> DownloadArtifactToTempFileAsync(string artifactId, CancellationToken cancellationToken = default) =>
            Task.FromResult("/tmp/mock-artifact.zip");
    }

    private sealed class StubRuntimeAgentDeployment : IRuntimeAgentDeployment
    {
        public bool Completed { get; private set; }
        public Task<RuntimeAgentPackage> PrepareAsync(PipelineStepContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeAgentPackage("fixture-token-id", new { appId = context.AppId }, "fixture-token",
                [new SshDeploymentArtifactUpload("runtime-agent", "minicloud-agent", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])), 3)]));
        public Task CompleteAsync(PipelineStepContext context, RuntimeAgentPackage package, CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
