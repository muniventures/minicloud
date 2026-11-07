using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1/deployments/preparations")]
public sealed class DeploymentPreparationsController : ControllerBase
{
    [HttpPost]
    public async Task<IResult> Start([FromBody] StartDeploymentPreparationRequest request,
        [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] DeploymentPreparationService preparations, CancellationToken ct)
    {
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), request.AppId, OrganizationAction.CreateDeployments, ct))
        { return EndpointResults.Forbidden(); }
        var validation = ControlPlaneValidation.ValidateDeploymentPreparation(request);
        if (!validation.IsValid) { return EndpointResults.ValidationProblem("Local build preparation is invalid.", validation); }
        var key = Guid.Parse(request.IdempotencyKey);
        return await preparations.LockedAsync(request.AppId, async () =>
        {
            var app = await db.Apps.SingleAsync(x => x.Id == request.AppId, ct);
            if (app.DeletionStatus is not null) { return EndpointResults.Conflict("app_deleting", "The app is being deleted."); }
            var id = "dep_" + key.ToString("N");
            var existing = await db.Deployments.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (existing is not null)
            {
                if (existing.AppId != app.Id || !preparations.Matches(existing, request.ServiceNames))
                { return EndpointResults.Conflict("preparation_conflict", "Preparation key is already bound to another request."); }
                if (existing.Status != DeploymentStatus.WaitingForBuild || preparations.IsExpired(existing))
                { return EndpointResults.Conflict("preparation_closed", "Preparation has ended; use a new key."); }
                return Results.Ok(ToResponse(existing));
            }
            if (await preparations.HasActiveAsync(app.Id, null, ct))
            { return EndpointResults.Conflict("deployment_active", "This app already has an active deployment or local build."); }
            var deployment = preparations.Start(app, id, request.ServiceNames);
            db.Deployments.Add(deployment);
            return Results.Created($"/v1/deployments/{deployment.Id}", ToResponse(deployment));
        }, ct);
    }

    [HttpPost("{id}")]
    public async Task<IResult> Update(string id, [FromBody] UpdateDeploymentPreparationRequest request,
        [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] DeploymentPreparationService preparations, CancellationToken ct)
    {
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), request.AppId, OrganizationAction.CreateDeployments, ct))
        { return EndpointResults.Forbidden(); }
        if (request.Status is not ("heartbeat" or "failed" or "canceled") || request.Stage is not ("build" or "preparation" or "admission"))
        { return EndpointResults.Problem("invalid_preparation_status", "Expected heartbeat, failed or canceled.", 400); }
        return await preparations.LockedAsync(request.AppId, async () =>
        {
            var deployment = db.Database.IsNpgsql()
                ? await db.Deployments.FromSqlInterpolated($"SELECT * FROM deployments WHERE id = {id} AND app_id = {request.AppId} FOR UPDATE").SingleOrDefaultAsync(ct)
                : await db.Deployments.SingleOrDefaultAsync(x => x.Id == id && x.AppId == request.AppId, ct);
            if (deployment is null) { return EndpointResults.NotFound("deployment_not_found", "Deployment was not found."); }
            if (deployment.Status != DeploymentStatus.WaitingForBuild)
            { return EndpointResults.Conflict("preparation_closed", "Preparation is no longer waiting for a local build."); }
            if (preparations.IsExpired(deployment))
            {
                preparations.Finish(deployment, "expired");
                return EndpointResults.Conflict("preparation_expired", "The local build heartbeat expired; run deploy again.");
            }
            if (request.Status == "heartbeat") { preparations.Heartbeat(deployment); }
            else { preparations.Finish(deployment, request.Status, request.Stage); }
            return Results.NoContent();
        }, ct);
    }

    private static DeploymentCreateResponse ToResponse(Deployment d) => new(d.Id, d.AppId,
        ControlPlaneValidation.ToWireValue(d.Status), $"/v1/deployments/{d.Id}", null, d.CreatedAt);
}
