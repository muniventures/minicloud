using System.Diagnostics;
using System.Text.Json;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Databases;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Databases;

public sealed class DatabaseOperationPostgresTests
{
    [Fact]
    public async Task SchedulerAndClaim_ProcessQueuedOperationOnPostgresWithoutRemoteCalls()
    {
        await WithPostgres(async options =>
        {
            await using var db = new MinicloudDbContext(options);
            await db.Database.MigrateAsync();
            db.Organizations.Add(new Organization { Id = "org", Name = "Synthetic", Slug = "synthetic" });
            db.DatabaseOperations.Add(new DatabaseOperation
            {
                Id = "operation", OrganizationId = "org", Kind = "create-server", Status = "queued",
                ActorJson = "{}", RequestJson = "{}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            var processor = new DatabaseOperationProcessor(db, new ControlPlaneAuthorization(db), null!, null!, null!, null!,
                new IdGenerator(), TimeProvider.System, null!, Options.Create(new SshDeploymentOptions()),
                Options.Create(new DatabasePlatformOptions()), null!);
            await processor.ScheduleBackupsAsync(default);
            Assert.True(await processor.ProcessNextAsync(default));
            var operation = await db.DatabaseOperations.AsNoTracking().SingleAsync();
            Assert.Equal("failed", operation.Status);
            Assert.Equal("database_authority_changed", operation.ErrorCode);
            Assert.Equal(1, operation.AttemptCount);
            Assert.Null(operation.OwnershipToken);
            Assert.Null(operation.LeaseExpiresAt);
            Assert.Empty(await db.Apps.ToListAsync());
            Assert.False(await processor.ProcessNextAsync(default));
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task PersistedApiActorWithScopes_ReloadsCurrentKeyAuthority(bool revoked, bool removedScope, bool webJson)
    {
        await WithPostgres(async options =>
        {
            await using var db = new MinicloudDbContext(options);
            await db.Database.MigrateAsync();
            var now = DateTimeOffset.UtcNow;
            db.Organizations.Add(new Organization { Id = "org", Name = "Synthetic", Slug = "synthetic" });
            db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org", Name = "Synthetic",
                CredentialPath = "synthetic", CredentialGeneration = "1" });
            db.ProviderPlans.Add(new ProviderPlan { Id = "plan", OrganizationId = "org", ProviderAccountId = "account",
                Name = "synthetic", NormalizedName = "SYNTHETIC", Region = "synthetic", Sku = "synthetic" });
            db.ManagedDatabaseServers.Add(new ManagedDatabaseServer { Id = "dbserver_test", OrganizationId = "org",
                Name = "synthetic", PlanId = "plan", Status = "deleting" });
            db.ApiKeys.Add(new ApiKey { Id = "key", OrganizationId = "org", Name = "Synthetic", KeyPrefix = "synthetic",
                KeyHash = "synthetic", RevokedAt = revoked ? now : null, ExpiresAt = now.AddHours(1),
                ScopesJson = JsonSerializer.Serialize(removedScope ? Array.Empty<string>() : new[] { ApiKeyScopes.DatabasesManage }) });
            var actor = new ActorIdentity("api_key", null, "key", "org", new HashSet<string> { ApiKeyScopes.DatabasesManage });
            db.DatabaseOperations.Add(new DatabaseOperation { Id = "operation", OrganizationId = "org", ServerId = "dbserver_test",
                Kind = "delete-server", Status = "queued", ActorJson = webJson
                    ? JsonSerializer.Serialize(actor, new JsonSerializerOptions(JsonSerializerDefaults.Web)) : JsonSerializer.Serialize(actor),
                RequestJson = "{}", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            var processor = new DatabaseOperationProcessor(db, new ControlPlaneAuthorization(db), null!, null!, null!, null!,
                new IdGenerator(), TimeProvider.System, null!, Options.Create(new SshDeploymentOptions()),
                Options.Create(new DatabasePlatformOptions()), null!);

            Assert.True(await processor.ProcessNextAsync(default));

            var operation = await db.DatabaseOperations.AsNoTracking().SingleAsync();
            var server = await db.ManagedDatabaseServers.AsNoTracking().SingleAsync();
            Assert.Equal(1, operation.AttemptCount);
            Assert.Null(operation.OwnershipToken);
            Assert.Null(operation.LeaseExpiresAt);
            if (revoked || removedScope)
            {
                Assert.Equal("failed", operation.Status);
                Assert.Equal("database_authority_changed", operation.ErrorCode);
                Assert.Equal("deleting", server.Status);
            }
            else
            {
                Assert.Equal("completed", operation.Status);
                Assert.Null(operation.ErrorCode);
                Assert.Equal("deleted", server.Status);
            }
            Assert.Empty(await db.Apps.ToListAsync());
        });
    }

    [Fact]
    public async Task FailedProvisioningPersistsFailedServerWithoutRemovingItsBackingApp()
    {
        await WithPostgres(async options =>
        {
            await using var db = new MinicloudDbContext(options);
            await db.Database.MigrateAsync();
            var now = DateTimeOffset.UtcNow;
            db.Organizations.Add(new Organization { Id = "org", Name = "Synthetic", Slug = "synthetic" });
            db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org", Name = "Synthetic",
                CredentialPath = "synthetic", CredentialGeneration = "1" });
            db.ProviderPlans.Add(new ProviderPlan { Id = "plan", OrganizationId = "org", ProviderAccountId = "account",
                Name = "synthetic", NormalizedName = "SYNTHETIC", Region = "synthetic", Sku = "synthetic" });
            db.Apps.Add(new MinicloudApp { Id = "app", OrganizationId = "org", Name = "Synthetic", Slug = "db-synthetic" });
            db.Deployments.Add(new Deployment { Id = "deployment", OrganizationId = "org", AppId = "app",
                Status = DeploymentStatus.Failed });
            db.ManagedDatabaseServers.Add(new ManagedDatabaseServer { Id = "dbserver_test", OrganizationId = "org",
                Name = "synthetic", PlanId = "plan", Status = "creating", RuntimeAppId = "app", RuntimeDeploymentId = "deployment" });
            db.ApiKeys.Add(new ApiKey { Id = "key", OrganizationId = "org", Name = "Synthetic", KeyPrefix = "synthetic",
                KeyHash = "synthetic", ExpiresAt = now.AddHours(1), ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.DatabasesManage }) });
            var actor = new ActorIdentity("api_key", null, "key", "org", new HashSet<string> { ApiKeyScopes.DatabasesManage });
            db.DatabaseOperations.Add(new DatabaseOperation { Id = "operation", OrganizationId = "org", ServerId = "dbserver_test",
                Kind = "create-server", Status = "queued", ActorJson = JsonSerializer.Serialize(actor),
                RequestJson = "{}", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            var processor = new DatabaseOperationProcessor(db, new ControlPlaneAuthorization(db), null!, null!, null!, null!,
                new IdGenerator(), TimeProvider.System, null!, Options.Create(new SshDeploymentOptions()),
                Options.Create(new DatabasePlatformOptions()), null!);

            Assert.True(await processor.ProcessNextAsync(default));

            var operation = await db.DatabaseOperations.AsNoTracking().SingleAsync();
            var server = await db.ManagedDatabaseServers.AsNoTracking().SingleAsync();
            Assert.Equal("failed", operation.Status);
            Assert.Equal("failed", server.Status);
            Assert.Equal("app", server.RuntimeAppId);
            Assert.Equal("deployment", server.RuntimeDeploymentId);
            Assert.Single(await db.Apps.ToListAsync());
        });
    }

    private static async Task WithPostgres(Func<DbContextOptions<MinicloudDbContext>, Task> test)
    {
        var name = "mc-database-operation-" + Guid.NewGuid().ToString("N");
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
