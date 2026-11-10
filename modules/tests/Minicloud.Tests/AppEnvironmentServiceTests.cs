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
        var application = new MinicloudApplication { Id = $"application_{parent.Id}", Organization = organization,
            OrganizationId = organization.Id, Name = parent.Name, Slug = parent.Slug, NormalizedSlug = parent.Slug,
            DefaultEnvironmentId = parent.Id };
        parent.Application = application;
        parent.ApplicationId = application.Id;
        parent.NormalizedEnvironmentName = parent.EnvironmentName;
        db.Applications.Add(application);
        db.Apps.Add(parent);
        db.AppSecrets.Add(new AppSecret { Id = "secret_env", OrganizationId = organization.Id, App = parent, AppId = parent.Id, Name = "TOKEN", EncryptedValue = "encrypted" });
        await db.SaveChangesAsync();
        var service = new AppEnvironmentService(db, new Ids(), TimeProvider.System);
        var staging = await service.CreateForApplicationAsync(application, new("staging", "p0-v-0", "postgres"), default);
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
        var duplicate = await Assert.ThrowsAsync<AppEnvironmentException>(() => service.CreateForApplicationAsync(application, new("QA", "p0-h-0"), default));
        Assert.Equal("environment_name_taken", duplicate.Code);
        await Assert.ThrowsAsync<AppEnvironmentException>(() => service.CreateForApplicationAsync(application, new("prod", "p0-v-0"), default));
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

    [Fact]
    public async Task Named_catalog_plans_select_their_own_account_and_survive_rename()
    {
        await using var db = harness.CreateDbContext();
        var ids = new Ids();
        var organization = new Organization { Id = "org_named_environment", Name = "Named", Slug = "named-env" };
        var first = new ProviderAccount { Id = "provider_env_first", Organization = organization, OrganizationId = organization.Id,
            Name = "First", CredentialPath = "fixture/first", CredentialGeneration = "1" };
        var second = new ProviderAccount { Id = "provider_env_second", Organization = organization, OrganizationId = organization.Id,
            Name = "Second", CredentialPath = "fixture/second", CredentialGeneration = "1" };
        db.ProviderAccounts.AddRange(first, second);
        var parent = new MinicloudApp { Id = "app_named_env", Organization = organization, OrganizationId = organization.Id,
            Name = "Shop", Slug = "named-shop", Database = DatabaseMode.None, EnvironmentName = "production" };
        var application = new MinicloudApplication { Id = $"application_{parent.Id}", Organization = organization,
            OrganizationId = organization.Id, Name = parent.Name, Slug = parent.Slug, NormalizedSlug = parent.Slug,
            DefaultEnvironmentId = parent.Id };
        parent.Application = application;
        parent.ApplicationId = application.Id;
        parent.NormalizedEnvironmentName = parent.EnvironmentName;
        db.Applications.Add(application);
        db.Apps.Add(parent);
        await db.SaveChangesAsync();
        var catalog = new ProviderCatalogService(db, ids, TimeProvider.System);
        var primary = await catalog.CreateAsync(new(organization.Id, first.Id, "nbg1", "cx23", "Primary"), default);
        var secondary = await catalog.CreateAsync(new(organization.Id, second.Id, "nbg1", "cx23", "Produção econômica"), default);
        await db.SaveChangesAsync();
        await catalog.BindAsync(parent, primary.Name, default);
        await db.SaveChangesAsync();
        var service = new AppEnvironmentService(db, ids, TimeProvider.System, catalog);
        var staging = await service.CreateForApplicationAsync(application, new("staging", "Produção econômica"), default);
        Assert.Equal(secondary.Id, staging.PlanId);
        Assert.Equal(second.Id, staging.SelectedPlan!.ProviderAccountId);
        Assert.Equal(primary.Id, parent.PlanId);
        Assert.Equal("Produção econômica", staging.ToResponse().Plan);
        await catalog.UpdateAsync(secondary, new(secondary.Revision, Name: "Secondary renamed"), default);
        await db.SaveChangesAsync();
        Assert.Equal(secondary.Id, staging.ToResponse().PlanId);
        Assert.Equal("Secondary renamed", staging.ToResponse().Plan);
        var invalid = await Assert.ThrowsAsync<AppEnvironmentException>(() => service.CreateForApplicationAsync(application, new("invalid-plan", "missing"), default));
        Assert.Equal("plan_not_found", invalid.Code);
        Assert.DoesNotContain(db.Apps, x => x.EnvironmentName == "invalid-plan");
    }

    private sealed class Ids : IIdGenerator
    {
        public string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    }
}
