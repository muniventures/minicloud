using Minicloud.Common.Registry;
using System.Text.Json;
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
public sealed class DeploymentsController : ControllerBase
{
    [HttpGet]
    public async Task<IResult> ListDeployments([FromQuery] string organizationId, [FromQuery] string? appId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IConsoleUrlProvider consoleUrls, CancellationToken cancellationToken, [FromQuery] bool all = false)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), organizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var query = db.Deployments
            .Include(x => x.App)
            .ThenInclude(x => x.Domains)
            .Include(x => x.Services)
            .Include(x => x.Events.Where(e => e.Message == DeploymentLifecycleService.ManuallyQueuedMessage))
            .Where(x => x.OrganizationId == organizationId);
        if (!string.IsNullOrWhiteSpace(appId))
        {
            query = query.Where(x => x.AppId == appId);
        }

        var deployments = await query.ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: true, take: all ? null : 100, cancellationToken);
        var consoleBaseUrl = consoleUrls.GetConsoleBaseUrl();
        return Results.Ok(deployments.Select(x => x.ToResponse(consoleBaseUrl)));
    }

    [HttpPost("validate-secrets")]
    public async Task<IResult> ValidateDeploymentSecrets(
        [FromBody] ValidateDeploymentSecretsRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        CancellationToken cancellationToken)
    {
        if (!await db.Apps.AnyAsync(x => x.Id == request.AppId, cancellationToken))
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), request.AppId, OrganizationAction.CreateDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }
        var validation = ControlPlaneValidation.ValidateDeploymentSecrets(request);
        if (!validation.IsValid)
        {
            return EndpointResults.ValidationProblem("Deployment secret references are invalid.", validation);
        }
        try
        {
            await secrets.ValidateReferencesAsync(request.AppId, request.Services, cancellationToken);
            return Results.NoContent();
        }
        catch (MissingAppServiceSecretException ex)
        {
            return EndpointResults.Problem("missing_service_secret", ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    [HttpPost]
    public async Task<IResult> CreateDeployment([FromBody] CreateDeploymentRequest request, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] DeploymentLifecycleService deploymentLifecycle, [FromServices] AssetReleaseService assetReleases, [FromServices] DeploymentServiceSetService serviceSets, [FromServices] DomainBindingService domainBindings, [FromServices] DeploymentOrchestrator deploymentOrchestrator, [FromServices] ImagePublicationService imagePublications, [FromServices] AppServiceSecretService secrets, [FromServices] DeploymentPreparationService preparations, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var validation = ControlPlaneValidation.ValidateDeployment(request);
        if (!validation.IsValid)
        {
            return EndpointResults.ValidationProblem("Deployment request is invalid.", validation);
        }

        var appRecord = await db.Apps
            .Include(x => x.Organization)
            .SingleOrDefaultAsync(x => x.Id == request.AppId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appRecord.Id, OrganizationAction.CreateDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var managed = request.Services.Any(x => !string.IsNullOrWhiteSpace(x.ImagePublicationId));
        if (managed && !imagePublications.Enabled)
        {
            return EndpointResults.Problem(ImagePublicationErrors.Unavailable, "Managed image deployment is not available yet.", StatusCodes.Status503ServiceUnavailable);
        }

        async Task<IResult> CreateCore()
        {
            if (managed) { await imagePublications.LockAsync(cancellationToken); }
            var currentApp = await db.Apps.AsNoTracking().SingleAsync(x => x.Id == appRecord.Id, cancellationToken);
            if (currentApp.DeletionStatus is not null) { return EndpointResults.Conflict("app_deleting", "The app is being deleted."); }
            Deployment? preparation = null;
            if (request.PreparationId is not null)
            {
                preparation = db.Database.IsNpgsql()
                    ? await db.Deployments.FromSqlInterpolated($"SELECT * FROM deployments WHERE id = {request.PreparationId} AND app_id = {appRecord.Id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
                    : await db.Deployments.SingleOrDefaultAsync(x => x.Id == request.PreparationId && x.AppId == appRecord.Id, cancellationToken);
                if (preparation is null || !preparations.Matches(preparation, request.Services.Select(x => x.Name)))
                { return EndpointResults.Conflict("preparation_conflict", "Preparation does not match this app and selected service set."); }
                if (preparation.Status != DeploymentStatus.WaitingForBuild)
                { return EndpointResults.Conflict("preparation_closed", "Preparation is no longer waiting for a build."); }
                if (preparations.IsExpired(preparation))
                {
                    preparations.Finish(preparation, "expired");
                    return EndpointResults.Conflict("preparation_expired", "The local build heartbeat expired; run deploy again.");
                }
            }
            if (preparation is not null
                ? await preparations.HasActiveAsync(appRecord.Id, preparation.Id, cancellationToken)
                : await db.Deployments.AnyAsync(x => x.AppId == appRecord.Id && x.Status == DeploymentStatus.WaitingForBuild, cancellationToken))
            { return EndpointResults.Conflict("deployment_active", "This app already has an active deployment or local build."); }
            var deployment = deploymentLifecycle.CreateQueued(appRecord, request);
            if (preparation is not null) { deployment.Id = preparation.Id; }
            if (managed) { await imagePublications.FinalizeDeploymentAsync(appRecord, deployment, cancellationToken); }
            var artifactValidation = await FinalizeArtifactBackedServicesAsync(db, appRecord, deployment, cancellationToken);
            if (!artifactValidation.IsValid)
            {
                return EndpointResults.ValidationProblem("Deployment artifacts are invalid.", artifactValidation);
            }

            var desiredServices = await serviceSets.BuildMergedServicesAsync(db, appRecord.Id, deployment.Services, cancellationToken);
            var desiredValidation = ControlPlaneValidation.ValidateMergedDeploymentServices(desiredServices);
            if (!desiredValidation.IsValid)
            {
                return EndpointResults.ValidationProblem("Deployment request is invalid for the app's active service set.", desiredValidation);
            }

            try
            {
                await secrets.ValidateReferencesAsync(appRecord.Id, desiredServices.Select(x => new DeploymentSecretServiceRequest(x.Name, x.SecretEnv)).ToArray(), cancellationToken);
            }
            catch (MissingAppServiceSecretException ex)
            {
                return EndpointResults.Problem("missing_service_secret", ex.Message, StatusCodes.Status400BadRequest);
            }

            deploymentLifecycle.AddRetainedServices(deployment, desiredServices);

            try { await assetReleases.ValidateMountsAsync(appRecord.Id, appRecord.OrganizationId, desiredServices, cancellationToken); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "invalid_asset_mount", message = ex.Message }); }

            IReadOnlyList<Domain> defaultBindings;
            try
            {
                defaultBindings = await domainBindings.EnsureDefaultBindingsAsync(appRecord.Organization, appRecord, desiredServices, cancellationToken);
            }
            catch (DomainBindingConflictException ex)
            {
                return EndpointResults.Conflict(ex.Code, ex.Message);
            }

            if (preparation is not null)
            {
                preparation.Database = deployment.Database;
                preparation.CommitSha = deployment.CommitSha;
                preparation.Services = deployment.Services;
                deploymentLifecycle.TryTransition(preparation, DeploymentStatus.Queued, "Deployment queued.");
                deployment = preparation;
            }
            deployment.Domain = defaultBindings.FirstOrDefault()?.Hostname;
            // T50: API is a fast queue gateway. The deployment stays Queued and
            // minicloud-worker claims it via PostgreSQL SKIP LOCKED. No GitHub dispatch.
            var enqueue = await deploymentOrchestrator.EnqueueAsync(appRecord.Organization, appRecord, deployment, request.PostgresPassword, desiredServices, defaultBindings, cancellationToken);
            if (preparation is null) { db.Deployments.Add(deployment); }
            return Results.Created($"/v1/deployments/{deployment.Id}", new DeploymentCreateResponse(
                deployment.Id,
                deployment.AppId,
                ControlPlaneValidation.ToWireValue(deployment.Status),
                $"/v1/deployments/{deployment.Id}",
                deployment.WebsiteUrl,
                deployment.CreatedAt,
                enqueue.GeneratedPostgresPassword));
        }
        try
        {
            return await ImagePublicationsController.Handle(() => preparations.LockedAsync(appRecord.Id, CreateCore, cancellationToken));
        }
        catch (DbUpdateException)
        {
            return EndpointResults.Conflict("deployment_domain_taken", "Deployment domain is already assigned to another app or organization.");
        }
    }

    private static async Task<ValidationResult> FinalizeArtifactBackedServicesAsync(
        MinicloudDbContext db,
        MinicloudApp app,
        Deployment deployment,
        CancellationToken cancellationToken)
    {
        var result = new ValidationResult();
        foreach (var service in deployment.Services)
        {
            if (string.IsNullOrWhiteSpace(service.ArtifactId))
            {
                continue;
            }

            var artifact = await db.DeploymentArtifacts
                .SingleOrDefaultAsync(x => x.Id == service.ArtifactId, cancellationToken);
            if (artifact is null)
            {
                result.Add($"services.{service.Name}.artifactId", "Deployment artifact was not found.");
                continue;
            }

            if (artifact.OrganizationId != app.OrganizationId || artifact.AppId != app.Id)
            {
                result.Add($"services.{service.Name}.artifactId", "Deployment artifact does not belong to this app.");
                continue;
            }

            if (!string.Equals(artifact.ServiceName, service.Name, StringComparison.Ordinal))
            {
                result.Add($"services.{service.Name}.artifactId", "Deployment artifact service name does not match the deployment service.");
                continue;
            }

            if (artifact.Status != DeploymentArtifactStatus.Ready || artifact.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                result.Add($"services.{service.Name}.artifactId", "Deployment artifact is not ready.");
                continue;
            }

            service.Artifact = artifact;
            service.Image = ArtifactImage(app, deployment, service);
        }

        return result;
    }

    private static string ArtifactImage(MinicloudApp app, Deployment deployment, DeploymentService service) =>
        $"minicloud/{app.Slug}-{service.Name}:{deployment.Id}".ToLowerInvariant();

    [HttpGet("{deploymentId}")]
    public async Task<IResult> GetDeployment([FromRoute] string deploymentId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IConsoleUrlProvider consoleUrls, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var deployment = await db.Deployments
            .Include(x => x.App)
            .ThenInclude(x => x.Domains)
            .Include(x => x.Services)
            .Include(x => x.Events.Where(e => e.Message == DeploymentLifecycleService.ManuallyQueuedMessage))
            .SingleOrDefaultAsync(x => x.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return EndpointResults.NotFound("deployment_not_found", "Deployment was not found.");
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), deployment.OrganizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        return Results.Ok(deployment.ToResponse(consoleUrls.GetConsoleBaseUrl()));
    }

    [HttpPost("{deploymentId}/dispatch")]
    public async Task<IResult> DispatchDeployment([FromRoute] string deploymentId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] DeploymentLifecycleService deploymentLifecycle, [FromServices] DeploymentServiceSetService serviceSets, [FromServices] DomainBindingService domainBindings, [FromServices] DeploymentOrchestrator deploymentOrchestrator, [FromServices] IConsoleUrlProvider consoleUrls, [FromServices] ImagePublicationService imagePublications, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var deployment = await db.Deployments
            .Include(x => x.App)
            .ThenInclude(x => x.Organization)
            .Include(x => x.App)
            .ThenInclude(x => x.Domains)
            .Include(x => x.Services)
            .SingleOrDefaultAsync(x => x.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return EndpointResults.NotFound("deployment_not_found", "Deployment was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), deployment.AppId, OrganizationAction.CreateDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var managed = deployment.Services.Any(x => x.ImagePublicationId is not null);
        if (managed && !imagePublications.Enabled)
        {
            return EndpointResults.Problem(ImagePublicationErrors.Unavailable, "Managed image deployment is not available yet.", StatusCodes.Status503ServiceUnavailable);
        }

        async Task<IResult> DispatchCore()
        {
            await DeploymentMutation.LockAsync(db, deployment.Id, cancellationToken);
            if (deployment.OwnershipToken is not null)
            { return EndpointResults.Conflict("deployment_active", "Deployment execution has not stopped yet."); }
            if (deployment.Status is not (DeploymentStatus.Queued or DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Canceled))
            { return EndpointResults.Conflict("deployment_active", "An active deployment cannot be dispatched again."); }
            if (deployment.Services.Count == 0 && deployment.RequestedServiceNamesJson is not null)
            { return EndpointResults.Conflict("preparation_not_submitted", "Start a new deployment to build and submit its services."); }
            if (managed) { await imagePublications.FinalizeDeploymentAsync(deployment.App, deployment, cancellationToken); }
            var desiredServices = await serviceSets.BuildMergedServicesAsync(db, deployment.AppId, deployment.Services, cancellationToken);
            IReadOnlyList<Domain> defaultBindings;
            try
            {
                defaultBindings = await domainBindings.EnsureDefaultBindingsAsync(deployment.App.Organization, deployment.App, desiredServices, cancellationToken);
            }
            catch (DomainBindingConflictException ex)
            {
                return EndpointResults.Conflict(ex.Code, ex.Message);
            }

            deploymentLifecycle.ResetForManualDispatch(deployment);
            deployment.Domain = defaultBindings.FirstOrDefault()?.Hostname;
            // T50: manual re-queue for minicloud-worker. No GitHub dispatch.
            await deploymentOrchestrator.EnqueueAsync(deployment.App.Organization, deployment.App, deployment, requestedPostgresPassword: null, desiredServices, defaultBindings, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return EndpointResults.Conflict("deployment_domain_taken", "Deployment domain is already assigned to another app or organization.");
            }

            return Results.Ok(deployment.ToResponse(consoleUrls.GetConsoleBaseUrl()));
        }
        return await ImagePublicationsController.Handle(() => imagePublications.LockedAsync(DispatchCore, cancellationToken));
    }

    [HttpPost("{deploymentId}/cancel")]
    public async Task<IResult> CancelDeployment([FromRoute] string deploymentId,
        [FromServices] DeploymentCancellationService cancellations, CancellationToken cancellationToken)
    {
        var result = await cancellations.RequestAsync(RequirePrincipal(HttpContext), deploymentId, cancellationToken);
        return result.Code is not null
            ? EndpointResults.Problem(result.Code, result.Status == 404 ? "Deployment was not found." : "Deployment has already finished.", result.Status)
            : Results.Json(new DeploymentCancellationResponse(deploymentId, true), statusCode: result.Status);
    }

    [HttpPost("{deploymentId}/refresh")]
    public async Task<IResult> RefreshDeployment([FromRoute] string deploymentId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IConsoleUrlProvider consoleUrls, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var deployment = await db.Deployments
            .Include(x => x.App)
            .ThenInclude(x => x.Organization)
            .Include(x => x.App)
            .ThenInclude(x => x.Domains)
            .Include(x => x.Services)
            .Include(x => x.Events.Where(e => e.Message == DeploymentLifecycleService.ManuallyQueuedMessage))
            .SingleOrDefaultAsync(x => x.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return EndpointResults.NotFound("deployment_not_found", "Deployment was not found.");
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), deployment.OrganizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        // T50: minicloud-worker owns lifecycle transitions; refresh reads current state.
        return Results.Ok(deployment.ToResponse(consoleUrls.GetConsoleBaseUrl()));
    }

    [HttpPost("{deploymentId}/resolve-secrets")]
    public async Task<IResult> ResolveDeploymentSecrets(
        [FromRoute] string deploymentId,
        [FromBody] ResolveDeploymentSecretsRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        [FromServices] ILogger<DeploymentsController> logger,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var deployment = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return EndpointResults.NotFound("deployment_not_found", "Deployment was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), deployment.AppId, OrganizationAction.CreateDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        try
        {
            return Results.Ok(await secrets.ResolveReferencesAsync(deployment.AppId, request.Services, cancellationToken));
        }
        catch (MissingAppServiceSecretException ex)
        {
            return EndpointResults.Problem("missing_service_secret", ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (SecretStoreUnavailableException ex)
        {
            logger.LogWarning(ex, "Secret store unavailable while resolving deployment secrets for deployment {DeploymentId}.", deploymentId);
            return EndpointResults.Problem("secret_store_unavailable", $"Minicloud could not resolve deployment secrets right now: {ex.Message}", StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpGet("{deploymentId}/events")]
    public async Task<IResult> GetDeploymentEvents([FromRoute] string deploymentId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var deployment = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return EndpointResults.NotFound("deployment_not_found", "Deployment was not found.");
        }
        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), deployment.OrganizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var eventRows = await db.DeploymentEvents
            .Where(x => x.DeploymentId == deploymentId)
            .ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: false, take: null, cancellationToken);
        var events = eventRows
            .Select(x => new DeploymentEventResponse(x.Id, ControlPlaneValidation.ToWireValue(x.Level), x.Message, x.MetadataJson, x.CreatedAt))
            .ToList();

        return Results.Ok(events);
    }

    [HttpGet("{deploymentId}/activities")]
    public async Task<IResult> GetDeploymentActivities([FromRoute] string deploymentId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var deployment = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return EndpointResults.NotFound("deployment_not_found", "Deployment was not found.");
        }
        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), deployment.OrganizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        // T90: GitHub workflow polling was decommissioned. minicloud-worker owns
        // lifecycle transitions and surfaces progress via deployment events/logs.
        // Historical rows may still carry a GithubRunId; no GitHub lookup occurs.
        return Results.Ok(Array.Empty<DeploymentActivityResponse>());
    }

    [HttpGet("{deploymentId}/logs")]
    public async Task<IResult> GetDeploymentLogs([FromRoute] string deploymentId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var deployment = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deploymentId, cancellationToken);
        if (deployment is null)
        {
            return EndpointResults.NotFound("deployment_not_found", "Deployment was not found.");
        }
        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), deployment.OrganizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var logRows = await db.DeploymentLogs
            .Where(x => x.DeploymentId == deploymentId)
            .ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: false, take: null, cancellationToken);
        var logs = logRows
            .Select(x => new DeploymentLogResponse(x.Id, x.Source, x.Content, x.CreatedAt))
            .ToList();

        var runtimeRows = await db.RuntimeLogRecords
            .AsNoTracking()
            .Where(x => x.AppId == deployment.AppId && x.ObservedAt >= deployment.CreatedAt.AddMinutes(-5))
            .ToDateTimeOffsetOrderedListAsync(db, x => x.ObservedAt, x => x.ObservedAt, descending: false, take: 500, cancellationToken);
        logs.AddRange(runtimeRows.Select(x => new DeploymentLogResponse(
            x.Id,
            string.IsNullOrWhiteSpace(x.Service) ? x.Source : $"{x.Source}/{x.Service}",
            x.Content,
            x.ObservedAt)));

        logs = logs
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();

        return Results.Ok(logs);
    }
}
