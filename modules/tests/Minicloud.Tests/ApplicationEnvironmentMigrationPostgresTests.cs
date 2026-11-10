using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Npgsql;

namespace Minicloud.Tests;

public sealed partial class ApplicationEnvironmentMigrationPostgresTests
{
    [Fact]
    public async Task PopulatedUpgradePreservesResourcesAndRejectsDuplicateNamesAcrossConnections()
    {
        await WithPostgres(async options =>
        {
            await using (var legacy = new MinicloudDbContext(options))
            {
                await legacy.GetService<IMigrator>().MigrateAsync("20261109161756_DatabasePlatform");
                await legacy.Database.ExecuteSqlRawAsync("""
                    INSERT INTO organizations(id,name,slug,created_at,updated_at) VALUES ('org','Org','org',now(),now());
                    INSERT INTO apps(id,organization_id,name,slug,plan,database,environment_name,parent_app_id,branch_name,created_at,updated_at)
                    VALUES ('app_root','org','Product','product','P0H0','Postgres','Staging',NULL,NULL,now(),now()),
                           ('app_prod','org','Product prod','product-production','P0H0','Postgres','Production','app_root',NULL,now(),now()),
                           ('app_preview','org','Preview','product-feature','P0H0','Postgres',NULL,'app_root','feature-cart',now(),now()),
                           ('app_db_customer','org','DB browser','db-browser','P0H0','None',NULL,NULL,NULL,now(),now()),
                           ('app_repo','org','Repository','dbrepo-repository','P0H0','None',NULL,NULL,NULL,now(),now());
                    INSERT INTO app_secrets(id,organization_id,app_id,name,encrypted_value,created_at,updated_at)
                    VALUES ('secret','org','app_root','TOKEN','synthetic-ciphertext',now(),now());
                    INSERT INTO app_service_secrets(id,organization_id,app_id,service_name,name,store_provider,store_path,store_version,status,created_at,updated_at)
                    VALUES ('service-secret','org','app_root','web','TOKEN','openbao','synthetic/org/app_root/web/TOKEN','7','active',now(),now());
                    INSERT INTO domains(id,organization_id,app_id,service_name,hostname,kind,path_prefix,dns_status,apply_status,ssl_status,status,is_primary,created_at,updated_at)
                    VALUES ('domain','org','app_root','web','original.example.test','custom','/','ready','applied','active','active',true,now(),now());
                    INSERT INTO app_storage_volumes(id,app_id,name,region,provider_volume_id,creation_attempted,size_gb,uid,gid)
                    VALUES ('disk','app_root','data','nbg1','volume-original',true,10,1000,1000);
                    INSERT INTO runtime_servers(id,organization_id,app_id,provider,provider_server_id,created_at,updated_at)
                    VALUES ('runtime','org','app_root','Hetzner','server-original',now(),now());
                    INSERT INTO deployments(id,organization_id,app_id,database,status,provider_creation_attempted,created_at,updated_at)
                    VALUES ('deployment','org','app_root','Postgres','Queued',true,now(),now());
                    """);
                await legacy.Database.MigrateAsync();
            }

            await using (var db = new MinicloudDbContext(options))
            {
                var product = await db.Applications.SingleAsync(x => x.LegacyRootAppId == "app_root");
                Assert.Equal("appl_root", product.Id);
                Assert.Equal("app_root", product.DefaultEnvironmentId);
                var environments = await db.Apps.Where(x => x.ApplicationId == product.Id).ToListAsync();
                Assert.Equal(3, environments.Count);
                Assert.Equal("Staging", environments.Single(x => x.Id == "app_root").EnvironmentName);
                var preview = environments.Single(x => x.Id == "app_preview");
                Assert.Equal(EnvironmentKinds.Preview, preview.EnvironmentKind);
                Assert.Equal("feature-cart", preview.SourceBranch);
                Assert.Equal("app_root", preview.SourceEnvironmentId);
                Assert.Equal("product-feature", preview.Slug);
                Assert.Equal("product", preview.RuntimeParentSlug);
                Assert.Equal("internal", (await db.Apps.SingleAsync(x => x.Id == "app_repo")).EnvironmentKind);
                Assert.NotNull((await db.Apps.SingleAsync(x => x.Id == "app_db_customer")).ApplicationId);
                Assert.Equal("synthetic-ciphertext", (await db.AppSecrets.SingleAsync()).EncryptedValue);
                var serviceSecret = await db.AppServiceSecrets.SingleAsync();
                Assert.Equal("synthetic/org/app_root/web/TOKEN", serviceSecret.StorePath);
                Assert.Equal("7", serviceSecret.StoreVersion);
                var domain = await db.Domains.SingleAsync();
                Assert.Equal("original.example.test", domain.Hostname);
                Assert.Equal("active", domain.SslStatus);
                Assert.Equal("app_root", domain.AppId);
                Assert.Equal("volume-original", (await db.AppStorageVolumes.SingleAsync()).ProviderVolumeId);
                Assert.Equal("server-original", (await db.RuntimeServers.SingleAsync()).ProviderServerId);
                Assert.Equal("app_root", (await db.Deployments.SingleAsync()).AppId);
                Assert.Equal(DeploymentStatus.Queued, (await db.Deployments.SingleAsync()).Status);

                await ApplicationEnvironmentMigrationService.BackfillAsync(db);
                await ApplicationEnvironmentMigrationService.BackfillAsync(db);
                Assert.Equal(2, await db.Applications.CountAsync());
                Assert.Equal(5, await db.Apps.CountAsync());
                product.Name = "Renamed product";
                await db.SaveChangesAsync();
                Assert.Equal("product", product.Slug);
            }

            // The unique constraint is enforced by PostgreSQL, across separate clients.
            await using (var first = new MinicloudDbContext(options))
            {
                var staging = await first.Apps.SingleAsync(x => x.Id == "app_root");
                staging.EnvironmentName = "QA";
                staging.NormalizedEnvironmentName = "qa";
                await first.SaveChangesAsync();
            }
            await using (var second = new MinicloudDbContext(options))
            {
                var prod = await second.Apps.SingleAsync(x => x.Id == "app_prod");
                prod.EnvironmentName = "qa";
                prod.NormalizedEnvironmentName = "qa";
                var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
                Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
            }

            await using (var foreign = new MinicloudDbContext(options))
            {
                foreign.Organizations.Add(new Organization { Id = "foreign-org", Name = "Foreign", Slug = "foreign" });
                await foreign.SaveChangesAsync();
                var mismatch = await Assert.ThrowsAsync<PostgresException>(() => foreign.Database.ExecuteSqlRawAsync(
                    "UPDATE apps SET organization_id = 'foreign-org' WHERE id = 'app_prod'"));
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, mismatch.SqlState);
            }

            // A rerun after deleting the legacy root must not split detached siblings
            // into new applications or change preview identity/default selection.
            await using (var db = new MinicloudDbContext(options))
            {
                var siblings = await db.Apps.Where(x => x.ParentAppId == "app_root").ToListAsync();
                foreach (var sibling in siblings) sibling.ParentAppId = null;
                var application = await db.Applications.SingleAsync(x => x.Id == "appl_root");
                application.DefaultEnvironmentId = "app_prod";
                var root = await db.Apps.SingleAsync(x => x.Id == "app_root");
                db.Apps.Remove(root);
                await db.SaveChangesAsync();
                await ApplicationEnvironmentMigrationService.BackfillAsync(db);
                Assert.Equal(2, await db.Applications.CountAsync());
                Assert.Equal("app_prod", application.DefaultEnvironmentId);
                Assert.All(await db.Apps.Where(x => x.Id == "app_prod" || x.Id == "app_preview").ToListAsync(),
                    env => Assert.Equal("appl_root", env.ApplicationId));
                Assert.Equal(EnvironmentKinds.Preview, (await db.Apps.SingleAsync(x => x.Id == "app_preview")).EnvironmentKind);
                Assert.Equal("volume-original", (await db.AppStorageVolumes.SingleAsync()).ProviderVolumeId);
            }
        });
    }

    [Theory]
    [InlineData("Bad name", null, "invalid_environment_name")]
    [InlineData("production", "feature-cart", "branch_environment_name_conflict")]
    public async Task PreflightFailureRollsBackMigrationAndCanRetryAfterExplicitMapping(string name, string? branch, string code)
    {
        await WithPostgres(async options =>
        {
            await using var db = new MinicloudDbContext(options);
            await db.GetService<IMigrator>().MigrateAsync("20261109161756_DatabasePlatform");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO organizations(id,name,slug,created_at,updated_at) VALUES ('org','Org','org',now(),now());
                INSERT INTO apps(id,organization_id,name,slug,plan,database,environment_name,created_at,updated_at)
                VALUES ('app_root','org','Product','product','P0H0','None','production',now(),now());
                """);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO apps(id,organization_id,name,slug,plan,database,environment_name,parent_app_id,branch_name,created_at,updated_at)
                VALUES ('app_child','org','Child','child','P0H0','None',{name},'app_root',{branch},now(),now());
                """);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains(code, failure.MessageText);
            Assert.DoesNotContain("20261110042000_ApplicationEnvironmentModel", await db.Database.GetAppliedMigrationsAsync());
            await db.Database.ExecuteSqlRawAsync("UPDATE apps SET environment_name = 'explicit-mapping' WHERE id = 'app_child'");
            await db.Database.MigrateAsync();
            Assert.Equal("explicit-mapping", (await db.Apps.SingleAsync(x => x.Id == "app_child")).EnvironmentName);
            Assert.Equal(1, await db.Applications.CountAsync());
        });
    }

    private static async Task WithPostgres(Func<DbContextOptions<MinicloudDbContext>, Task> test)
    {
        var container = "mc-app-environments-" + Guid.NewGuid().ToString("N");
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
            await test(options);
        }
        finally { await Docker("rm", "-f", container); }
    }

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
}
