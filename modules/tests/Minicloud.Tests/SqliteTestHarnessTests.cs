using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class SqliteTestHarnessTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness _harness;

    public SqliteTestHarnessTests(SqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task Harness_CreatesSchemaAndPersistsControlPlaneRows()
    {
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            var user = new User
            {
                Id = "usr_sqlite_test",
                KratosIdentityId = "kratos_sqlite_test",
                Email = "sqlite-test@example.com",
                DisplayName = "SQLite Test",
                CreatedAt = now,
                UpdatedAt = now
            };

            var organization = new Organization
            {
                Id = "org_sqlite_test",
                Name = "SQLite Test",
                Slug = "sqlite-test",
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Users.Add(user);
            db.Organizations.Add(organization);
            db.OrganizationMembers.Add(new OrganizationMember
            {
                Id = "mem_sqlite_test",
                OrganizationId = organization.Id,
                UserId = user.Id,
                Role = OrganizationRole.Owner,
                CreatedAt = now,
                UpdatedAt = now
            });

            await db.SaveChangesAsync();
        }

        await using var verificationDb = _harness.CreateDbContext();
        var membership = await verificationDb.OrganizationMembers
            .Include(x => x.Organization)
            .Include(x => x.User)
            .SingleAsync(x => x.Id == "mem_sqlite_test");

        Assert.Equal("sqlite-test", membership.Organization.Slug);
        Assert.Equal("sqlite-test@example.com", membership.User.Email);
    }

    [Fact]
    public async Task DateTimeOffsetOrdering_WorksWithSqlite()
    {
        var older = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var newer = older.AddMinutes(5);

        await using (var db = _harness.CreateDbContext())
        {
            var organization = new Organization
            {
                Id = "org_sqlite_ordering",
                Name = "SQLite Ordering",
                Slug = "sqlite-ordering",
                CreatedAt = older,
                UpdatedAt = older
            };
            var app = new MinicloudApp
            {
                Id = "app_sqlite_ordering",
                OrganizationId = organization.Id,
                Name = "SQLite Ordering",
                Slug = "sqlite-ordering",
                Plan = AppPlan.P0,
                Database = DatabaseMode.Sqlite,
                CreatedAt = older,
                UpdatedAt = older
            };

            db.Organizations.Add(organization);
            db.Apps.Add(app);
            db.Deployments.AddRange(
                new Deployment
                {
                    Id = "dep_sqlite_ordering_older",
                    OrganizationId = organization.Id,
                    AppId = app.Id,
                    Database = DatabaseMode.Sqlite,
                    Status = DeploymentStatus.Queued,
                    CreatedAt = older,
                    UpdatedAt = older
                },
                new Deployment
                {
                    Id = "dep_sqlite_ordering_newer",
                    OrganizationId = organization.Id,
                    AppId = app.Id,
                    Database = DatabaseMode.Sqlite,
                    Status = DeploymentStatus.Queued,
                    CreatedAt = newer,
                    UpdatedAt = newer
                });

            await db.SaveChangesAsync();
        }

        await using var verificationDb = _harness.CreateDbContext();
        var deployments = await verificationDb.Deployments
            .Where(x => x.OrganizationId == "org_sqlite_ordering")
            .ToDateTimeOffsetOrderedListAsync(
                verificationDb,
                x => x.CreatedAt,
                x => x.CreatedAt,
                descending: true,
                take: 1,
                CancellationToken.None);

        var deployment = Assert.Single(deployments);
        Assert.Equal("dep_sqlite_ordering_newer", deployment.Id);
    }

    [Fact]
    public async Task ActiveDeploymentMonitorOrdering_WorksWithSqlite()
    {
        var older = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
        var newer = older.AddMinutes(5);

        await using (var db = _harness.CreateDbContext())
        {
            var organization = new Organization
            {
                Id = "org_sqlite_monitor_ordering",
                Name = "SQLite Monitor Ordering",
                Slug = "sqlite-monitor-ordering",
                CreatedAt = older,
                UpdatedAt = older
            };
            var app = new MinicloudApp
            {
                Id = "app_sqlite_monitor_ordering",
                OrganizationId = organization.Id,
                Name = "SQLite Monitor Ordering",
                Slug = "sqlite-monitor-ordering",
                Plan = AppPlan.P0,
                Database = DatabaseMode.Sqlite,
                CreatedAt = older,
                UpdatedAt = older
            };

            db.Organizations.Add(organization);
            db.Apps.Add(app);
            db.Deployments.AddRange(
                new Deployment
                {
                    Id = "dep_sqlite_monitor_ordering_newer",
                    OrganizationId = organization.Id,
                    AppId = app.Id,
                    Database = DatabaseMode.Sqlite,
                    Status = DeploymentStatus.Queued,
                    CreatedAt = newer,
                    UpdatedAt = newer
                },
                new Deployment
                {
                    Id = "dep_sqlite_monitor_ordering_older",
                    OrganizationId = organization.Id,
                    AppId = app.Id,
                    Database = DatabaseMode.Sqlite,
                    Status = DeploymentStatus.Dispatching,
                    CreatedAt = older,
                    UpdatedAt = older
                });

            await db.SaveChangesAsync();
        }

        await using var verificationDb = _harness.CreateDbContext();
        var deployments = await verificationDb.Deployments
            .Include(x => x.App)
            .ThenInclude(x => x.Organization)
            .Include(x => x.Services)
            .Where(x => x.OrganizationId == "org_sqlite_monitor_ordering")
            .Where(x => x.Status == DeploymentStatus.Queued || x.Status == DeploymentStatus.Dispatching || x.Status == DeploymentStatus.Deploying || x.Status == DeploymentStatus.Verifying)
            .ToDateTimeOffsetOrderedListAsync(
                verificationDb,
                x => x.CreatedAt,
                x => x.CreatedAt,
                descending: false,
                take: 25,
                CancellationToken.None);

        Assert.Equal(["dep_sqlite_monitor_ordering_older", "dep_sqlite_monitor_ordering_newer"], deployments.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task AppQueries_LoadOrganizationForResponseMapping()
    {
        var now = new DateTimeOffset(2026, 5, 11, 21, 30, 0, TimeSpan.Zero);

        await using (var db = _harness.CreateDbContext())
        {
            var organization = new Organization
            {
                Id = "org_app_list_mapping",
                Name = "App List Mapping",
                Slug = "app-list-mapping",
                CreatedAt = now,
                UpdatedAt = now
            };
            var app = new MinicloudApp
            {
                Id = "app_list_mapping",
                OrganizationId = organization.Id,
                Name = "App List Mapping",
                Slug = "app-list-mapping",
                Plan = AppPlan.P0,
                Database = DatabaseMode.Postgres,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Organizations.Add(organization);
            db.Apps.Add(app);

            await db.SaveChangesAsync();
        }

        await using var verificationDb = _harness.CreateDbContext();
        var apps = await verificationDb.Apps
            .Include(x => x.Organization)
            .Include(x => x.Deployments)
            .Include(x => x.Domains)
            .Where(x => x.OrganizationId == "org_app_list_mapping")
            .OrderBy(x => x.Name)
            .ToListAsync();

        var response = Assert.Single(apps.Select(x => x.ToResponse()));
        Assert.Empty(response.Domains);

        await using var detailDb = _harness.CreateDbContext();
        var detailApp = await detailDb.Apps
            .Include(x => x.Organization)
            .Include(x => x.Deployments)
            .Include(x => x.Domains)
            .SingleAsync(x => x.Id == "app_list_mapping");

        var detailResponse = detailApp.ToResponse();
        Assert.Empty(detailResponse.Domains);
    }
}
