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
[Route("v1/[controller]")]
public sealed class AppsController : ControllerBase
{
    [HttpGet]
    public async Task<IResult> ListApps([FromQuery] string organizationId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken, [FromQuery] bool includeBranches = false)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), organizationId, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var apps = await db.Apps
            .Include(x => x.Organization)
            .Include(x => x.Deployments)
            .Include(x => x.Domains)
            .Include(x => x.Branches)
            .ThenInclude(x => x.Deployments)
            .Include(x => x.Branches)
            .ThenInclude(x => x.Domains)
            .Where(x => x.OrganizationId == organizationId && (includeBranches || x.ParentAppId == null))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(apps.Select(x => x.ToResponse()));
    }

    [HttpPost]
    public async Task<IResult> CreateApp([FromBody] CreateAppRequest request, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!AppEnvironmentService.IsValidName(request.EnvironmentName))
            return Results.BadRequest(new ErrorResponse(new ErrorBody("environment_name_invalid", "Environment names must contain 1 to 63 letters, numbers, dashes or underscores.", [])));
        var validation = ControlPlaneValidation.ValidateApp(request);
        if (!validation.IsValid)
        {
            return EndpointResults.ValidationProblem("App request is invalid.", validation);
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), request.OrganizationId, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var organization = await db.Organizations.SingleAsync(x => x.Id == request.OrganizationId, cancellationToken);
        if (await db.Apps.AnyAsync(x => x.OrganizationId == request.OrganizationId && x.Slug.ToLower() == request.Slug.ToLower(), cancellationToken))
        {
            return EndpointResults.Conflict("app_slug_taken", "App slug is already taken in this organization.");
        }

        ControlPlaneValidation.TryParseEnum<AppPlan>(request.Plan, out var plan);
        ControlPlaneValidation.TryParseEnum<DatabaseMode>(request.Database, out var database);

        var now = timeProvider.GetUtcNow();
        var app = new MinicloudApp
        {
            Id = ids.NewId("app"),
            OrganizationId = request.OrganizationId,
            Organization = organization,
            Name = request.Name,
            Slug = request.Slug,
            EnvironmentName = request.EnvironmentName.Trim(),
            Plan = plan,
            Database = database,
            CreatedAt = now,
            UpdatedAt = now
        };
        app.EnvironmentKey = $"{app.Id}:{app.EnvironmentName!.ToLowerInvariant()}";
        db.Apps.Add(app);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/v1/apps/{app.Id}", app.ToResponse());
    }

    [HttpGet("{appId}")]
    public async Task<IResult> GetApp([FromRoute] string appId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var app = await db.Apps
            .Include(x => x.Organization)
            .Include(x => x.Deployments)
            .Include(x => x.Domains)
            .Include(x => x.Branches)
            .ThenInclude(x => x.Deployments)
            .Include(x => x.Branches)
            .ThenInclude(x => x.Domains)
            .SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        return app is null ? EndpointResults.NotFound("app_not_found", "App was not found.") : Results.Ok(app.ToResponse());
    }

    [HttpPost("{appId}/environments")]
    public async Task<IResult> CreateEnvironment(string appId, CreateAppEnvironmentRequest request,
        [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppEnvironmentService environments, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.WriteApps, cancellationToken))
            return EndpointResults.Forbidden();
        var parent = await db.Apps.Include(x => x.Organization).SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (parent is null) return EndpointResults.NotFound("app_not_found", "App was not found.");
        try
        {
            var environment = await environments.CreateAsync(parent, request, cancellationToken);
            return Results.Created($"/v1/apps/{environment.Id}", environment.ToResponse());
        }
        catch (AppEnvironmentException ex)
        { return Results.BadRequest(new ErrorResponse(new ErrorBody(ex.Code, ex.Message, []))); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }
            or Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        { return EndpointResults.Conflict("environment_conflict", "An environment or app with this name already exists."); }
    }

    [HttpPatch("{appId}/environment")]
    public async Task<IResult> RenameEnvironment(string appId, RenameAppEnvironmentRequest request,
        [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppEnvironmentService environments, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.WriteApps, cancellationToken))
            return EndpointResults.Forbidden();
        var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null) return EndpointResults.NotFound("app_not_found", "App was not found.");
        try
        {
            await environments.RenameAsync(app, request.Name, cancellationToken);
            return Results.Ok(app.ToResponse());
        }
        catch (AppEnvironmentException ex)
        { return Results.BadRequest(new ErrorResponse(new ErrorBody(ex.Code, ex.Message, []))); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }
            or Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        { return EndpointResults.Conflict("environment_conflict", "An environment with this name already exists."); }
    }

    [HttpPost("{appId}/branches")]
    public async Task<IResult> EnsureBranch(
        [FromRoute] string appId,
        [FromBody] EnsureAppBranchRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppBranchService branches,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var parent = await db.Apps
            .Include(x => x.Organization)
            .SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (parent is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        try
        {
            var branch = await branches.EnsureAsync(parent, request.BranchName, cancellationToken);
            return Results.Ok(branch.ToResponse());
        }
        catch (AppBranchException ex)
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody(ex.Code, ex.Message, [])));
        }
        catch (SecretStoreUnavailableException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Branch secrets could not be copied.", detail: ex.Message);
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
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var branch = await db.Apps
            .Include(x => x.Organization)
            .Include(x => x.Domains)
            .SingleOrDefaultAsync(x => x.Id == branchAppId && x.ParentAppId == appId && x.BranchName != null, cancellationToken);
        if (branch is null)
        {
            return EndpointResults.NotFound("branch_not_found", "Branch deployment was not found.");
        }

        var error = await deletion.RequestAsync(principal, branch.Id, branch.Slug, cancellationToken);
        return error is null ? Results.NoContent()
            : Results.Json(new ErrorResponse(new ErrorBody(error.Value.Code, error.Value.Message, [])), statusCode: error.Value.Status);
    }

    [HttpDelete("{appId}")]
    public async Task<IResult> DeleteApp(
        string appId, [FromBody] DeleteAppRequest request,
        [FromServices] AppDeletionService deletion, CancellationToken cancellationToken)
    {
        var error = await deletion.RequestAsync(RequirePrincipal(HttpContext), appId, request.Confirmation, cancellationToken);
        return error is null ? Results.Accepted($"/v1/apps/{appId}")
            : Results.Json(new ErrorResponse(new ErrorBody(error.Value.Code, error.Value.Message, [])), statusCode: error.Value.Status);
    }

    [HttpPatch("{appId}")]
    public async Task<IResult> UpdateApp([FromRoute] string appId, [FromBody] UpdateAppRequest request, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var app = await db.Apps
            .Include(x => x.Organization)
            .Include(x => x.Deployments)
            .Include(x => x.Domains)
            .Include(x => x.Branches)
            .ThenInclude(x => x.Deployments)
            .Include(x => x.Branches)
            .ThenInclude(x => x.Domains)
            .SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        if (request.Plan is not null && ControlPlaneValidation.TryParseEnum<AppPlan>(request.Plan, out var plan))
        {
            app.Plan = plan;
        }
        if (request.Database is not null && ControlPlaneValidation.TryParseEnum<DatabaseMode>(request.Database, out var database))
        {
            app.Database = database;
        }
        app.Name = request.Name ?? app.Name;
        app.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(app.ToResponse());
    }
}
