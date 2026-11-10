using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/plans")]
public sealed class PlansController(MinicloudDbContext db, ControlPlaneAuthorization authorization,
    ProviderCatalogService catalog, AuditEventService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List(string organizationId, CancellationToken ct, string? providerAccountId = null)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadPlans, ct)) return EndpointResults.Forbidden();
        var plans = await db.ProviderPlans.AsNoTracking().Include(x => x.ProviderAccount)
            .Where(x => x.OrganizationId == organizationId && (providerAccountId == null || x.ProviderAccountId == providerAccountId))
            .OrderBy(x => x.Name).ToListAsync(ct);
        return Results.Ok(plans.Select(ToResponse));
    }

    [HttpGet("{id}")]
    public async Task<IResult> Get(string id, CancellationToken ct)
    {
        var plan = await db.ProviderPlans.AsNoTracking().Include(x => x.ProviderAccount).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (plan is null) return EndpointResults.NotFound("plan_not_found", "Plan was not found.");
        if (!await CanAsync(plan.OrganizationId, OrganizationAction.ReadPlans, ct)) return EndpointResults.Forbidden();
        return Results.Ok(ToResponse(plan));
    }

    [HttpPost]
    public async Task<IResult> Create(CreateProviderPlanRequest request, CancellationToken ct)
    {
        if (!await CanAsync(request.OrganizationId, OrganizationAction.ManagePlans, ct)) return EndpointResults.Forbidden();
        return await ProviderEndpointResults.RunAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var plan = await catalog.CreateAsync(request, ct);
            await SaveAuditAsync(plan, "plan.created", ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/plans/{plan.Id}", ToResponse(plan));
        });
    }

    [HttpPatch("{id}")]
    public async Task<IResult> Update(string id, UpdateProviderPlanRequest request, CancellationToken ct) =>
        await MutateAsync(id, async plan =>
        {
            await catalog.UpdateAsync(plan, request, ct);
            await SaveAuditAsync(plan, "plan.updated", ct);
            return Results.Ok(ToResponse(plan));
        }, ct);

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id, CancellationToken ct) =>
        await MutateAsync(id, async plan =>
        {
            await catalog.DeleteAsync(plan, ct);
            await SaveAuditAsync(plan, "plan.deleted", ct);
            return Results.NoContent();
        }, ct);

    private async Task<IResult> MutateAsync(string id, Func<ProviderPlan, Task<IResult>> action, CancellationToken ct)
    {
        var org = await db.ProviderPlans.Where(x => x.Id == id).Select(x => x.OrganizationId).SingleOrDefaultAsync(ct);
        if (org is null) return EndpointResults.NotFound("plan_not_found", "Plan was not found.");
        if (!await CanAsync(org, OrganizationAction.ManagePlans, ct)) return EndpointResults.Forbidden();
        return await ProviderEndpointResults.RunAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var plan = await catalog.LockedPlanAsync(org, id, ct);
            var result = await action(plan);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private Task<bool> CanAsync(string org, OrganizationAction action, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), org, action, ct);

    private Task SaveAuditAsync(ProviderPlan plan, string action, CancellationToken ct) =>
        audit.AppendAsync(plan.OrganizationId, null, AuditEventService.ActorFor(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity()),
            action, System.Text.Json.JsonSerializer.Serialize(new { planId = plan.Id, providerAccountId = plan.ProviderAccountId, plan.Revision }), ct);

    public static ProviderPlanResponse ToResponse(ProviderPlan plan) => new(plan.Id, plan.OrganizationId, plan.ProviderAccountId,
        plan.Name, plan.ProviderAccount.Name, ProviderAccountService.WireType(plan.ProviderAccount.ProviderType), plan.Region,
        plan.Sku, plan.Image, plan.BuildPlatform, plan.Enabled, plan.ProviderAccount.Enabled, plan.Revision, plan.ProvisioningLockedAt is not null);
}
