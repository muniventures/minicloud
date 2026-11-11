using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline.Steps;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class RuntimeAgentDeploymentTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;
    public RuntimeAgentDeploymentTests(WorkerSqliteTestHarness harness) => _harness = harness;

    [Fact]
    public async Task RegistersScopedAgentAndRotatesOnlyAfterSuccessfulInstall()
    {
        var context = await SeedAsync("agent-register");
        using var bundle = new Bundle();
        var service = CreateService(bundle.Path);
        var first = await service.PrepareAsync(context, Observe(), default);
        var config = JsonSerializer.SerializeToElement(first.Configuration);
        Assert.Equal(context.AppId, config.GetProperty("appId").GetString());
        Assert.Equal("https://api.cloud.muni.dev", config.GetProperty("apiBaseUrl").GetString());
        Assert.Equal("minicloud-" + context.AppSlug, config.GetProperty("composeProject").GetString());
        Assert.Equal(4, first.Uploads.Count);
        foreach (var upload in first.Uploads)
        {
            await using var stream = await upload.OpenStream(default);
            Assert.True(stream.Length > 0);
        }
        var second = await service.PrepareAsync(context, Observe(), default);
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CompleteAsync(context, second, cancelled.Token));
        }
        await using (var db = _harness.CreateDbContext())
        {
            Assert.Single(await db.RuntimeServers.Where(s => s.AppId == context.AppId).ToListAsync());
            var firstToken = await db.RuntimeTokens.SingleAsync(t => t.Id == first.TokenId);
            Assert.Null(firstToken.RevokedAt);
            Assert.NotEqual(first.Token, firstToken.TokenHash);
            Assert.True(new RuntimeTokenService(db, TimeProvider.System).Verify(first.Token, firstToken.TokenHash));
            var contacted = await db.RuntimeTokens.Include(t => t.RuntimeServer).SingleAsync(t => t.Id == second.TokenId);
            contacted.LastUsedAt = DateTimeOffset.UtcNow;
            contacted.RuntimeServer.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        await service.CompleteAsync(context, second, default);
        await using (var db = _harness.CreateDbContext())
        {
            Assert.NotNull((await db.RuntimeTokens.SingleAsync(t => t.Id == first.TokenId)).RevokedAt);
            Assert.Null((await db.RuntimeTokens.SingleAsync(t => t.Id == second.TokenId)).RevokedAt);
        }
    }

    [Theory]
    [InlineData(0, "reuse", false, false)]
    [InlineData(10, "configure", false, false)]
    [InlineData(11, "rotate", true, false)]
    [InlineData(12, "install", true, true)]
    [InlineData(13, "restart", false, false)]
    public async Task HealthyCredentialChoosesBoundedHostAction(int observed, string action, bool createsCredential, bool uploads)
    {
        var context = await SeedAsync("agent-plan-" + Guid.NewGuid().ToString("N")[..6]);
        using var bundle = new Bundle();
        var service = CreateService(bundle.Path);
        var initial = await service.PrepareAsync(context, Observe(), default);
        await using (var db = _harness.CreateDbContext())
        {
            var token = await db.RuntimeTokens.Include(t => t.RuntimeServer).SingleAsync(t => t.Id == initial.TokenId);
            token.LastUsedAt = DateTimeOffset.UtcNow;
            token.RuntimeServer.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var next = await service.PrepareAsync(context, command =>
        {
            Assert.DoesNotContain(initial.Token, command);
            if (command == RuntimeAgentBundle.ArchitectureCommand) { return Task.FromResult(20); }
            Assert.Contains("sudo python3", command);
            return Task.FromResult(observed);
        }, default);
        Assert.Equal(action, next.Action);
        Assert.Equal(createsCredential, next.CreatesCredential);
        Assert.Equal(uploads, next.Uploads.Count > 0);
        if (!createsCredential)
        {
            Assert.Equal(initial.TokenId, next.TokenId);
            Assert.Empty(next.Token);
        }
        await using var verify = _harness.CreateDbContext();
        Assert.Equal(createsCredential ? 2 : 1, await verify.RuntimeTokens.CountAsync(t => t.RuntimeServer.AppId == context.AppId));
    }

    [Theory]
    [InlineData(-121, 48)]
    [InlineData(-10, 24)]
    [InlineData(-10, -1)]
    public async Task StaleOrExpiringCredentialsCannotReuse(int heartbeatSeconds, int expiryHours)
    {
        var context = await SeedAsync("agent-stale-" + Guid.NewGuid().ToString("N")[..6]);
        using var bundle = new Bundle();
        var service = CreateService(bundle.Path);
        var initial = await service.PrepareAsync(context, Observe(), default);
        await using (var db = _harness.CreateDbContext())
        {
            var token = await db.RuntimeTokens.Include(t => t.RuntimeServer).SingleAsync(t => t.Id == initial.TokenId);
            token.LastUsedAt = DateTimeOffset.UtcNow.AddSeconds(heartbeatSeconds);
            token.RuntimeServer.LastSeenAt = token.LastUsedAt;
            token.ExpiresAt = DateTimeOffset.UtcNow.AddHours(expiryHours);
            await db.SaveChangesAsync();
        }
        var next = await service.PrepareAsync(context, Observe(0), default);
        Assert.Equal("rotate", next.Action);
        Assert.Empty(next.Uploads);
        Assert.NotEqual(initial.TokenId, next.TokenId);
    }

    [Fact]
    public async Task MissingBundleFailsBeforeCreatingCredentials()
    {
        var context = await SeedAsync("agent-missing");
        await Assert.ThrowsAsync<PipelineStepException>(() => CreateService("/missing-agent-bundle").PrepareAsync(context, Observe(), default));
        await using var db = _harness.CreateDbContext();
        Assert.False(await db.RuntimeServers.AnyAsync(s => s.AppId == context.AppId));
    }

    [Fact]
    public async Task RejectsLostOwnershipAndCrossAppServer()
    {
        var context = await SeedAsync("agent-fenced");
        using var bundle = new Bundle();
        var service = CreateService(bundle.Path);
        await using (var db = _harness.CreateDbContext())
        {
            var server = await db.ServerInstances.SingleAsync(s => s.Id == context.ServerInstanceId);
            var other = await _harness.SeedOrgAndAppAsync("agent-other");
            db.RuntimeServers.Add(new RuntimeServer
            {
                Id = "rtsrv-other",
                AppId = other.App.Id,
                OrganizationId = other.Org.Id,
                Provider = server.Provider,
                ProviderServerId = server.ProviderServerId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<PipelineStepException>(() => service.PrepareAsync(context, Observe(), default));
        Assert.Contains("another app", error.Message);
        await using (var db = _harness.CreateDbContext())
        {
            db.RuntimeServers.Remove(await db.RuntimeServers.SingleAsync(s => s.Id == "rtsrv-other"));
            (await db.Deployments.FindAsync(context.DeploymentId))!.OwnershipToken = "new-owner";
            await db.SaveChangesAsync();
        }
        error = await Assert.ThrowsAsync<PipelineStepException>(() => service.PrepareAsync(context, Observe(), default));
        Assert.Contains("ownership", error.Message);
    }

    [Theory]
    [InlineData("http://api.cloud.muni.dev")]
    [InlineData("https://user:password@api.cloud.muni.dev")]
    public async Task RejectsUnsafeApiUrl(string url)
    {
        var context = await SeedAsync("agent-url-" + Guid.NewGuid().ToString("N")[..5]);
        using var bundle = new Bundle();
        var service = new RuntimeAgentDeployment(_harness.CreateScopeFactory(),
            Options.Create(new RuntimeAgentOptions { BundlePath = bundle.Path, ApiBaseUrl = url }), TimeProvider.System);
        await Assert.ThrowsAsync<PipelineStepException>(() => service.PrepareAsync(context, Observe(), default));
    }

    [Theory]
    [InlineData(20, "linux-x64")]
    [InlineData(21, "linux-arm64")]
    public async Task TargetArchitectureSelectsUploadedBinaryAndBothVerificationHashes(int architecture, string rid)
    {
        var context = await SeedAsync("agent-arch-" + architecture);
        using var bundle = new Bundle();
        var commands = new List<string>();
        var package = await CreateService(bundle.Path).PrepareAsync(context, command =>
        {
            commands.Add(command);
            return Observe(12, architecture)(command);
        }, default);
        Assert.Equal(RuntimeAgentBundle.ArchitectureCommand, commands[0]);
        Assert.Contains("sudo python3", commands[1]);
        var upload = Assert.Single(package.Uploads, upload => upload.RemoteFileName == "minicloud-agent");
        Assert.Equal("runtime-agent", upload.ServiceName);
        await using var stream = await upload.OpenStream(default);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        var expected = await File.ReadAllBytesAsync(System.IO.Path.Combine(bundle.Path, rid, "minicloud-agent"));
        Assert.Equal(expected, bytes.ToArray());
        Assert.Equal(expected.LongLength, upload.SizeBytes);
        var digest = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        foreach (var verification in new[] { package.Verification, package.FinalVerification })
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(verification!));
            Assert.Equal(digest, document.RootElement.GetProperty("files").GetProperty("minicloud-agent").GetString());
        }
    }

    [Theory]
    [InlineData(22, "Unsupported")]
    [InlineData(23, "Could not determine")]
    [InlineData(0, "Could not determine")]
    public async Task UnsupportedOrFailedArchitectureProbeCannotCreateCredentials(int result, string message)
    {
        var context = await SeedAsync("agent-probe-" + result);
        using var bundle = new Bundle();
        var error = await Assert.ThrowsAsync<PipelineStepException>(() =>
            CreateService(bundle.Path).PrepareAsync(context, Observe(12, result), default));
        Assert.Contains(message, error.Message);
        await using var db = _harness.CreateDbContext();
        Assert.False(await db.RuntimeServers.AnyAsync(s => s.AppId == context.AppId));
        Assert.False(await db.RuntimeTokens.AnyAsync(t => t.RuntimeServer.AppId == context.AppId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-machine")]
    [InlineData("invalid")]
    [InlineData("truncated")]
    public async Task MissingOrInvalidTargetBinaryCannotCreateCredentials(string failure)
    {
        var context = await SeedAsync("agent-binary-" + failure);
        using var bundle = new Bundle();
        var path = System.IO.Path.Combine(bundle.Path, "linux-arm64", "minicloud-agent");
        if (failure == "missing")
        {
            File.Delete(path);
        }
        else if (failure == "wrong-machine")
        {
            File.Copy(System.IO.Path.Combine(bundle.Path, "linux-x64", "minicloud-agent"), path, true);
        }
        else
        {
            File.WriteAllBytes(path, new byte[failure == "truncated" ? 4 : 20]);
        }
        var error = await Assert.ThrowsAsync<PipelineStepException>(() =>
            CreateService(bundle.Path).PrepareAsync(context, Observe(12, 21), default));
        Assert.Contains("linux-arm64", error.Message);
        await using var db = _harness.CreateDbContext();
        Assert.False(await db.RuntimeServers.AnyAsync(s => s.AppId == context.AppId));
        Assert.False(await db.RuntimeTokens.AnyAsync(t => t.RuntimeServer.AppId == context.AppId));
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public async Task FlatDevelopmentBundleMustMatchActualTarget(int architecture, bool matches)
    {
        var context = await SeedAsync("agent-flat-" + architecture);
        using var bundle = new Bundle(flat: true);
        var prepare = () => CreateService(bundle.Path).PrepareAsync(context, Observe(12, architecture), default);
        if (matches)
        {
            Assert.Equal(4, (await prepare()).Uploads.Count);
        }
        else
        {
            var error = await Assert.ThrowsAsync<PipelineStepException>(prepare);
            Assert.Contains("linux-arm64", error.Message);
        }
    }

    [Fact]
    public async Task MissingTargetProbeFailsClosed()
    {
        var context = await SeedAsync("agent-no-probe");
        using var bundle = new Bundle();
        var error = await Assert.ThrowsAsync<PipelineStepException>(() =>
            CreateService(bundle.Path).PrepareAsync(context, default));
        Assert.Contains("remote target architecture probe", error.Message);
        await using var db = _harness.CreateDbContext();
        Assert.False(await db.RuntimeServers.AnyAsync(s => s.AppId == context.AppId));
    }

    private static Func<string, Task<int>> Observe(int observation = 12, int architecture = 20) =>
        command => Task.FromResult(command == RuntimeAgentBundle.ArchitectureCommand ? architecture : observation);

    private RuntimeAgentDeployment CreateService(string path) => new(_harness.CreateScopeFactory(),
        Options.Create(new RuntimeAgentOptions { BundlePath = path }), TimeProvider.System);

    private async Task<PipelineStepContext> SeedAsync(string name)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync(name);
        var now = DateTimeOffset.UtcNow;
        var context = new PipelineStepContext
        {
            DeploymentId = "dep-" + name,
            OrganizationId = org.Id,
            AppId = app.Id,
            AppSlug = app.Slug,
            OwnershipToken = "owner-" + name,
            ServerInstanceId = "srv-" + name,
            ServerIp = "192.0.2.10"
        };
        await using var db = _harness.CreateDbContext();
        db.Deployments.Add(new Deployment
        {
            Id = context.DeploymentId,
            OrganizationId = org.Id,
            AppId = app.Id,
            Status = DeploymentStatus.Deploying,
            OwnershipToken = context.OwnershipToken,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.ServerInstances.Add(new ServerInstance
        {
            Id = context.ServerInstanceId,
            OrganizationId = org.Id,
            AppId = app.Id,
            Provider = "hetzner",
            ProviderServerId = name,
            IpAddress = context.ServerIp,
            Region = "test",
            Status = "ready",
            BootstrapStatus = "ready",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return context;
    }

    private sealed class Bundle : IDisposable
    {
        public string Path { get; }
        public Bundle(bool flat = false)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(System.IO.Path.Combine(root.FullName, "Minicloud.slnx")))
            {
                root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
            }
            Path = Directory.CreateDirectory(System.IO.Path.Combine(root.FullName, ".local", "artifacts",
                "runtime-agent-tests", Guid.NewGuid().ToString("N"))).FullName;
            foreach (var file in new[] { "minicloud-agent.service", "apply-minicloud-routing.py", "install-minicloud-agent.sh" })
            {
                File.WriteAllText(System.IO.Path.Combine(Path, file), "test bundle");
            }
            foreach (var (rid, machine) in new[] { ("linux-x64", 62), ("linux-arm64", 183) })
            {
                if (flat && rid == "linux-arm64") { continue; }
                var directory = flat ? Path : Directory.CreateDirectory(System.IO.Path.Combine(Path, rid)).FullName;
                var bytes = new byte[20];
                new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1 }.CopyTo(bytes, 0);
                bytes[18] = (byte)machine;
                File.WriteAllBytes(System.IO.Path.Combine(directory, "minicloud-agent"), bytes);
            }
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
