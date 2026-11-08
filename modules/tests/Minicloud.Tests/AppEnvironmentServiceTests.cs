using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class AppEnvironmentServiceTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness harness;
    public AppEnvironmentServiceTests(SqliteTestHarness harness) => this.harness = harness;

    [Fact]
    public async Task Environments_keep_independent_plans_and_secrets_and_do_not_appear_as_branches()
    {
        await using var db = harness.CreateDbContext();
        var organization = new Organization { Id = "org_env", Name = "Test", Slug = "env" };
        var parent = new MinicloudApp { Id = "app_env", Organization = organization, OrganizationId = organization.Id,
            Name = "Store", Slug = "store", Plan = AppPlan.P0H0, Database = DatabaseMode.Postgres, EnvironmentName = "prod" };
        db.Apps.Add(parent);
        db.AppSecrets.Add(new AppSecret { Id = "secret_env", OrganizationId = organization.Id, App = parent, AppId = parent.Id, Name = "TOKEN", EncryptedValue = "encrypted" });
        await db.SaveChangesAsync();
        var service = new AppEnvironmentService(db, new Ids(), TimeProvider.System);
        var staging = await service.CreateAsync(parent, new("staging", "p0-v-0"), default);
        Assert.Equal(AppPlan.P0V0, staging.Plan);
        Assert.Equal(AppPlan.P0H0, parent.Plan);
        Assert.Equal(DatabaseMode.Postgres, staging.Database);
        Assert.Empty(db.AppSecrets.Where(x => x.AppId == staging.Id));
        Assert.Empty(parent.ToResponse().Branches);
        Assert.Equal(2, parent.ToResponse().Environments!.Count);
        var domains = new DomainBindingService(db, new Ids(), TimeProvider.System);
        var web = new DeploymentServiceRequest("web", "example/web", 8080, true, "/", "/health");
        var productionDomain = Assert.Single(await domains.EnsureDefaultBindingsAsync(organization, parent, [web], default));
        var stagingDomain = Assert.Single(await domains.EnsureDefaultBindingsAsync(organization, staging, [web], default));
        await db.SaveChangesAsync();
        Assert.Equal("env-store-web.app.muni.dev", productionDomain.Hostname);
        Assert.Equal("env-store-staging-web.app.muni.dev", stagingDomain.Hostname);
        Assert.NotEqual(productionDomain.Hostname, stagingDomain.Hostname);
        var slug = staging.Slug;
        await service.RenameAsync(staging, "qa", default);
        Assert.Equal(slug, staging.Slug);
        Assert.Equal("qa", staging.EnvironmentName);
        Assert.Equal("Store (qa)", staging.Name);
        var renamedDomain = Assert.Single(await domains.EnsureDefaultBindingsAsync(organization, staging, [web], default));
        Assert.Equal(stagingDomain.Hostname, renamedDomain.Hostname);
        var duplicate = await Assert.ThrowsAsync<AppEnvironmentException>(() => service.CreateAsync(parent, new("QA", "p0-h-0"), default));
        Assert.Equal("environment_name_taken", duplicate.Code);
        await Assert.ThrowsAsync<AppEnvironmentException>(() => service.CreateAsync(parent, new("prod", "p0-v-0"), default));
        await Assert.ThrowsAsync<AppEnvironmentException>(() => service.CreateAsync(staging, new("nested", "p0-v-0"), default));
        await Assert.ThrowsAsync<AppEnvironmentException>(() => service.RenameAsync(staging, "PROD", default));
    }

    [Theory]
    [InlineData("my-env", true)]
    [InlineData("QA_team", true)]
    [InlineData("", false)]
    [InlineData("-staging", false)]
    [InlineData("a/b", false)]
    [InlineData("bad name", false)]
    public void Environment_names_are_user_chosen_and_validated(string name, bool valid) =>
        Assert.Equal(valid, AppEnvironmentService.IsValidName(name));

    private sealed class Ids : IIdGenerator
    {
        public string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    }
}
