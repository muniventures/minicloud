using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class AppBranchServiceTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness _harness;

    public AppBranchServiceTests(SqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Theory]
    [InlineData("feature/Deploy_Branch", "feature-deploy-branch")]
    [InlineData("fix---dns", "fix-dns")]
    [InlineData(" release/v1.2 ", "release-v1-2")]
    public void NormalizeBranchName_creates_dns_safe_labels(string input, string expected)
    {
        Assert.Equal(expected, AppBranchService.NormalizeBranchName(input));
    }

    [Fact]
    public async Task EnsureAsync_creates_reusable_child_inheriting_parent_plan_and_copies_secrets()
    {
        await using var db = _harness.CreateDbContext();
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var organization = new Organization { Id = "org_branch", Name = "Branch Org", Slug = "branch-org", CreatedAt = now, UpdatedAt = now };
        var parent = new MinicloudApp
        {
            Id = "app_branch_parent",
            OrganizationId = organization.Id,
            Organization = organization,
            Name = "Store",
            Slug = "store",
            Plan = AppPlan.P0H0,
            Database = DatabaseMode.Postgres,
            CreatedAt = now,
            UpdatedAt = now
        };
        var application = new MinicloudApplication { Id = "application_branch", OrganizationId = organization.Id,
            Organization = organization, Name = "Store", Slug = "store", NormalizedSlug = "store", DefaultEnvironmentId = parent.Id };
        parent.ApplicationId = application.Id;
        parent.Application = application;
        parent.EnvironmentName = "production";
        parent.NormalizedEnvironmentName = "production";
        db.Organizations.Add(organization);
        db.Applications.Add(application);
        db.Apps.Add(parent);
        await db.SaveChangesAsync();

        var store = new FakeSecretStore();
        var ids = new TestIdGenerator();
        var serviceSecrets = new AppServiceSecretService(db, store, ids, new TestTimeProvider(now));
        await serviceSecrets.SetAsync(parent, new Minicloud.Common.Contracts.SetAppServiceSecretRequest("api", "API_TOKEN", "secret-value"), CancellationToken.None);
        var branches = new AppBranchService(db, serviceSecrets, ids, new TestTimeProvider(now));

        var created = await branches.EnsureAsync(parent, "feature/checkout", CancellationToken.None);
        var reused = await branches.EnsureAsync(parent, "feature/checkout", CancellationToken.None);

        Assert.Equal(created.Id, reused.Id);
        Assert.Equal(parent.Id, created.ParentAppId);
        Assert.Equal("feature-checkout", created.BranchName);
        Assert.Equal("store-feature-checkout", created.Slug);
        Assert.Equal(parent.Plan, created.Plan);
        Assert.Equal(DatabaseMode.Postgres, created.Database);
        Assert.Single(db.Apps.Where(x => x.ParentAppId == parent.Id));
        var copied = Assert.Single(db.AppServiceSecrets.Where(x => x.AppId == created.Id));
        Assert.Equal("secret-value", store.Values[copied.StorePath]);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken)
        {
            var path = $"{request.OrganizationId}/{request.AppId}/{request.ServiceName}/{request.Name}";
            Values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference(OpenBaoSecretStore.ProviderName, path, "1"));
        }

        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(Values[reference.Path]);

        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken)
        {
            Values.Remove(reference.Path);
            return Task.CompletedTask;
        }
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        private int _next;
        public string NewId(string prefix) => $"{prefix}_branch_{++_next}";
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
