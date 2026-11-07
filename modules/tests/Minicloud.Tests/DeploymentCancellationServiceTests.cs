using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DeploymentCancellationServiceTests
{
    [Theory]
    [InlineData(DeploymentStatus.Queued, 200, DeploymentStatus.Canceled)]
    [InlineData(DeploymentStatus.WaitingForBuild, 200, DeploymentStatus.Canceled)]
    [InlineData(DeploymentStatus.Provisioning, 202, DeploymentStatus.Provisioning)]
    [InlineData(DeploymentStatus.Deploying, 202, DeploymentStatus.Deploying)]
    [InlineData(DeploymentStatus.Verifying, 202, DeploymentStatus.Verifying)]
    [InlineData(DeploymentStatus.Succeeded, 409, DeploymentStatus.Succeeded)]
    [InlineData(DeploymentStatus.Failed, 409, DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Canceled, 200, DeploymentStatus.Canceled)]
    public async Task RequestsAreDurableIdempotentAndPreserveOwnership(DeploymentStatus status, int expected, DeploymentStatus final)
    {
        await using var harness = new SqliteTestHarness();
        await harness.InitializeAsync();
        await using var db = harness.CreateDbContext();
        db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
        db.Apps.Add(new MinicloudApp { Id = "app", OrganizationId = "org", Name = "Demo", Slug = "demo" });
        var owned = status is DeploymentStatus.Provisioning or DeploymentStatus.Deploying or DeploymentStatus.Verifying;
        db.Deployments.Add(new Deployment { Id = "dep", AppId = "app", OrganizationId = "org", Status = status,
            OwnershipToken = owned ? "owner" : null, WorkerId = owned ? "worker" : null });
        await db.SaveChangesAsync();
        var service = new DeploymentCancellationService(db, new ControlPlaneAuthorization(db),
            new AuditEventService(db, new IdGenerator(), TimeProvider.System), TimeProvider.System);
        var principal = Principal("org", ApiKeyScopes.DeploymentsCreate);
        Assert.Equal(expected, (await service.RequestAsync(principal, "dep", default)).Status);
        var stored = await db.Deployments.SingleAsync();
        Assert.Equal(final, stored.Status);
        Assert.Equal(owned ? "owner" : null, stored.OwnershipToken);
        if (status is not (DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Canceled))
        {
            Assert.NotNull(stored.CancellationRequestedAt);
            Assert.NotNull(stored.CancellationRequestedBy);
            Assert.Equal(final == DeploymentStatus.Canceled, stored.CompletedAt.HasValue);
            var requestedAt = stored.CancellationRequestedAt;
            Assert.Equal(expected, (await service.RequestAsync(principal, "dep", default)).Status);
            Assert.Equal(requestedAt, stored.CancellationRequestedAt);
            Assert.Single(await db.AuditEvents.ToListAsync());
            Assert.Single(await db.DeploymentEvents.ToListAsync());
        }
    }

    [Theory]
    [InlineData("other", ApiKeyScopes.DeploymentsCreate)]
    [InlineData("org", ApiKeyScopes.DeploymentsRead)]
    public async Task RejectsWrongTenantAndMissingWriteScope(string organization, string scope)
    {
        await using var harness = new SqliteTestHarness();
        await harness.InitializeAsync();
        await using var db = harness.CreateDbContext();
        db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
        db.Apps.Add(new MinicloudApp { Id = "app", OrganizationId = "org", Name = "Demo", Slug = "demo" });
        db.Deployments.Add(new Deployment { Id = "dep", AppId = "app", OrganizationId = "org", Status = DeploymentStatus.Queued });
        await db.SaveChangesAsync();
        var service = new DeploymentCancellationService(db, new ControlPlaneAuthorization(db),
            new AuditEventService(db, new IdGenerator(), TimeProvider.System), TimeProvider.System);
        Assert.Equal(404, (await service.RequestAsync(Principal(organization, scope), "dep", default)).Status);
        Assert.Equal(DeploymentStatus.Queued, (await db.Deployments.SingleAsync()).Status);
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    private static ControlPlanePrincipal Principal(string org, string scope) =>
        new("api_key", null, "key", org, new HashSet<string> { scope });
}
