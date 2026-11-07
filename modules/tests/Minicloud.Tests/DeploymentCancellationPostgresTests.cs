extern alias WorkerAssembly;

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using WorkerAssembly::Minicloud.Worker;
using WorkerAssembly::Minicloud.Worker.Pipeline;
using WorkerAssembly::Minicloud.Worker.Pipeline.Steps;
using WorkerAssembly::Minicloud.Worker.Queue;

namespace Minicloud.Tests;

// Real PostgreSQL is required: SQLite cannot qualify row locking and claim races.
public sealed class DeploymentCancellationPostgresTests
{
    [Fact]
    public async Task CancellationSerializesWithClaimsDuplicatesAndSuccessfulFinalization()
    {
        var container = "mc-cancel-" + Guid.NewGuid().ToString("N");
        try
        {
            await Docker("run", "--rm", "-d", "--name", container, "-e", "POSTGRES_PASSWORD=" + container,
                "-p", "127.0.0.1::5432", "postgres:16-alpine");
            var port = (await Docker("port", container, "5432/tcp")).Trim().Split(':')[^1];
            var options = new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseNpgsql($"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;Password={container}").Options;
            var ready = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await using var probe = new MinicloudDbContext(options);
                if (await probe.Database.CanConnectAsync()) { ready = true; break; }
                await Task.Delay(500);
            }
            Assert.True(ready, "Isolated PostgreSQL did not become ready.");
            await using (var db = new MinicloudDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
                db.Apps.Add(new MinicloudApp { Id = "app", OrganizationId = "org", Name = "App", Slug = "app" });
                await db.SaveChangesAsync();
            }
            using var provider = new ServiceCollection().AddScoped(_ => new MinicloudDbContext(options)).BuildServiceProvider();
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var consumer = new PostgresDeploymentQueueConsumer(scopes, Options.Create(new WorkerOptions { WorkerId = "race-worker" }),
                TimeProvider.System, NullLogger<PostgresDeploymentQueueConsumer>.Instance);
            var principal = new ControlPlanePrincipal("api_key", null, "key", "org", new HashSet<string> { ApiKeyScopes.DeploymentsCreate });
            async Task<int> Cancel(string id)
            {
                await using var db = new MinicloudDbContext(options);
                return (await new DeploymentCancellationService(db, new ControlPlaneAuthorization(db),
                    new AuditEventService(db, new IdGenerator(), TimeProvider.System), TimeProvider.System).RequestAsync(principal, id, default)).Status;
            }
            async Task Seed(string id, bool verifying = false)
            {
                await using var db = new MinicloudDbContext(options);
                db.Deployments.Add(new Deployment { Id = id, OrganizationId = "org", AppId = "app",
                    Status = verifying ? DeploymentStatus.Verifying : DeploymentStatus.Queued,
                    OwnershipToken = verifying ? "owner-" + id : null, WorkerId = verifying ? "race-worker" : null,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            for (var index = 0; index < 8; index++)
            {
                var id = "claim-" + index;
                await Seed(id);
                var cancel = Cancel(id);
                var claim = consumer.TryClaimNextDeploymentAsync(default);
                await Task.WhenAll(cancel, claim);
                Assert.Contains(await cancel, new[] { 200, 202 });
                await using var db = new MinicloudDbContext(options);
                var stored = await db.Deployments.SingleAsync(x => x.Id == id);
                Assert.NotNull(stored.CancellationRequestedAt);
                if (await claim is null)
                {
                    Assert.Equal(DeploymentStatus.Canceled, stored.Status);
                    Assert.Null(stored.OwnershipToken);
                }
                else
                {
                    Assert.Equal(DeploymentStatus.Provisioning, stored.Status);
                    Assert.NotNull(stored.OwnershipToken);
                    Assert.Equal(202, await cancel);
                    // No pipeline was started; release this test's claim for the next iteration.
                    stored.Status = DeploymentStatus.Canceled;
                    stored.OwnershipToken = null;
                    stored.WorkerId = null;
                    await db.SaveChangesAsync();
                }
            }
            await Seed("duplicates");
            Assert.All(await Task.WhenAll(Cancel("duplicates"), Cancel("duplicates")), status => Assert.Equal(200, status));
            await using (var db = new MinicloudDbContext(options))
            {
                Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.Details == "duplicates"));
            }
            for (var index = 0; index < 8; index++)
            {
                var id = "finish-" + index;
                await Seed(id, verifying: true);
                var health = new HealthCheckFinalizeStep(scopes, new HttpClient(), new DeploymentPipelineOptions(), TimeProvider.System);
                var context = new PipelineStepContext { DeploymentId = id, OrganizationId = "org", AppId = "app", OwnershipToken = "owner-" + id };
                var cancel = Cancel(id);
                var finalize = health.ExecuteAsync(context, default);
                try { await finalize; } catch (OperationCanceledException) { }
                var status = await cancel;
                await using var db = new MinicloudDbContext(options);
                var stored = await db.Deployments.SingleAsync(x => x.Id == id);
                if (status == 409)
                {
                    Assert.Equal(DeploymentStatus.Succeeded, stored.Status);
                    Assert.Null(stored.CancellationRequestedAt);
                }
                else
                {
                    Assert.Equal(202, status);
                    Assert.Equal(DeploymentStatus.Verifying, stored.Status);
                    Assert.NotNull(stored.CancellationRequestedAt);
                    Assert.True(finalize.IsCanceled);
                    stored.Status = DeploymentStatus.Canceled;
                    stored.OwnershipToken = null;
                    stored.WorkerId = null;
                    await db.SaveChangesAsync();
                }
            }
        }
        finally { await Docker("rm", "-f", container); }
    }

    private static async Task<string> Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
