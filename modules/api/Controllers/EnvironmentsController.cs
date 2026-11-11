using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Api.Providers;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/environments")]
public sealed class EnvironmentsController : ControllerBase
{
    [HttpGet("{appId}")]
    public async Task<IResult> GetEnvironment(string appId, [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), appId, OrganizationAction.ReadApps, cancellationToken))
            return EndpointResults.Forbidden();
        var organizationId = await db.Apps.Where(x => x.Id == appId).Select(x => x.OrganizationId).SingleOrDefaultAsync(cancellationToken);
        if (organizationId is null) return EndpointResults.NotFound("environment_not_found", "Environment was not found.");
        var response = await new AppSummaryReader(db).GetAsync(organizationId, appId, cancellationToken);
        return response is null ? EndpointResults.NotFound("environment_not_found", "Environment was not found.") : Results.Ok(response);
    }

    [HttpPost("{appId}/branches")]
    public async Task<IResult> EnsureBranch(
        [FromRoute] string appId,
        [FromBody] EnsureAppBranchRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppBranchService branches,
        [FromServices] DeploymentPreparationService preparations,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.WriteApps, cancellationToken))
            return EndpointResults.Forbidden();
        var parent = await db.Apps.Include(x => x.SelectedPlan).Include(x => x.Organization)
            .SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (parent is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        try
        {
            return await preparations.LockedAsync(parent.Id, async () =>
            {
                var branch = await branches.EnsureAsync(parent, request.BranchName, cancellationToken, request.Plan);
                return Results.Ok(branch.ToResponse());
            }, cancellationToken);
        }
        catch (AppBranchException ex)
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody(ex.Code, ex.Message, [])));
        }
        catch (SecretStoreUnavailableException)
        {
            return EndpointResults.Problem("secret_store_unavailable", "Required credentials are temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpDelete("{appId}/branches/{branchAppId}")]
    public async Task<IResult> DestroyBranch(
        [FromRoute] string appId,
        [FromRoute] string branchAppId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppDeletionService deletion,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);

        var canAccess = await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.WriteApps, cancellationToken);
        if (!canAccess)
        {
            return EndpointResults.Forbidden();
        }

        var branch = await db.Apps
            .Include(x => x.SelectedPlan)
            .Include(x => x.Organization)
            .Include(x => x.Domains)
            .SingleOrDefaultAsync(x => x.Id == branchAppId && x.ParentAppId == appId && x.BranchName != null, cancellationToken);
        if (branch is null)
        {
            return EndpointResults.NotFound("branch_not_found", "Branch deployment was not found.");
        }

        if (branch.ApplicationId is null)
            return EndpointResults.Conflict("environment_unassigned", "Environment must belong to an application.");
        var error = await deletion.RequestEnvironmentDeletionAsync(principal, branch.ApplicationId, branch.Id, branch.Slug, cancellationToken);
        return error is null ? Results.NoContent()
            : Results.Json(new ErrorResponse(new ErrorBody(error.Value.Code, error.Value.Message, [])), statusCode: error.Value.Status);
    }

    [HttpPost("{appId}/plan")]
    public async Task<IResult> SelectPlan(string appId, SelectAppPlanRequest request,
        [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] ProviderCatalogService catalog, [FromServices] DeploymentPreparationService preparations, CancellationToken ct)
    {
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), appId, OrganizationAction.CreateDeployments, ct))
            return EndpointResults.Forbidden();
        try
        {
            return await preparations.LockedAsync(appId, async () =>
            {
                var app = await db.Apps.Include(x => x.SelectedPlan).Include(x => x.Organization).SingleAsync(x => x.Id == appId, ct);
                if (app.DeletionStatus is not null || await db.Applications.AnyAsync(x => x.Id == app.ApplicationId && x.DeletionStatus != null, ct))
                    return EndpointResults.Conflict("app_deleting", "The application or environment is being deleted.");
                var previousAccountId = app.SelectedPlan?.ProviderAccountId;
                await catalog.BindAsync(app, request.Plan, ct);
                if (app.SshAccessMode != "none" &&
                    !string.Equals(previousAccountId, app.SelectedPlan?.ProviderAccountId, StringComparison.Ordinal))
                {
                    // LockedAsync saves tracked changes; discard the binding when the SSH selection would be orphaned.
                    db.ChangeTracker.Clear();
                    return EndpointResults.Conflict("ssh_selection_account_mismatch",
                        "Changing provider accounts requires an explicit SSH access selection for the destination account.");
                }
                return Results.Ok(app.ToResponse());
            }, ct);
        }
        catch (ProviderCatalogException ex) { return EndpointResults.Problem(ex.Code, ex.Message, StatusCodes.Status400BadRequest); }
        catch (SecretStoreUnavailableException) { return EndpointResults.Problem("secret_store_unavailable", "Provider credentials are temporarily unavailable.", StatusCodes.Status503ServiceUnavailable); }
    }

}
