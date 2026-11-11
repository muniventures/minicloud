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
[Route("api/[controller]")]
public sealed class DeploymentsController : ControllerBase
{
    [HttpGet]
    public async Task<IResult> ListDeployments([FromQuery] string organizationId, [FromQuery] string? appId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IConsoleUrlProvider consoleUrls, CancellationToken cancellationToken, [FromQuery] bool all = false, [FromQuery] string? applicationId = null, [FromQuery] string? environmentId = null)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), organizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }

        var query = db.Deployments
            .Include(x => x.App)
            .ThenInclude(x => x.Domains)
            .Include(x => x.Services)
            .Include(x => x.Events.Where(e => e.Message == DeploymentLifecycleService.ManuallyQueuedMessage))
            .Where(x => x.OrganizationId == organizationId);
        if (!string.IsNullOrWhiteSpace(targetAppId))
        {
            query = query.Where(x => x.AppId == targetAppId);
        }
        else if (!string.IsNullOrWhiteSpace(applicationId))
        {
            query = query.Where(x => x.App.ApplicationId == applicationId);
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
        var ownershipError = await VerifyTargetOwnershipAsync(db, request.AppId, request.ApplicationId, request.EnvironmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }
        var targetAppId = !string.IsNullOrWhiteSpace(request.EnvironmentId) ? request.EnvironmentId! : request.AppId;
        if (!await db.Apps.AnyAsync(x => x.Id == targetAppId, cancellationToken))
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), targetAppId, OrganizationAction.CreateDeployments, cancellationToken))
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
            await secrets.ValidateReferencesAsync(targetAppId, request.Services, cancellationToken);
            return Results.NoContent();
        }
        catch (MissingAppServiceSecretException ex)
        {
            return EndpointResults.Problem("missing_service_secret", ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    [HttpPost]
    public async Task<IResult> CreateDeployment([FromBody] CreateDeploymentRequest request, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] DeploymentLifecycleService deploymentLifecycle, [FromServices] AssetReleaseService assetReleases, [FromServices] DeploymentServiceSetService serviceSets, [FromServices] DomainBindingService domainBindings, [FromServices] DeploymentOrchestrator deploymentOrchestrator, [FromServices] ImagePublicationService imagePublications, [FromServices] AppServiceSecretService secrets, [FromServices] DeploymentPreparationService preparations, [FromServices] ProviderCatalogService catalog, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var ownershipError = await VerifyTargetOwnershipAsync(db, request.AppId, request.ApplicationId, request.EnvironmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }
        var validation = ControlPlaneValidation.ValidateDeployment(request);
        if (!validation.IsValid)
        {
            return EndpointResults.ValidationProblem("Deployment request is invalid.", validation);
        }

        var targetAppId = !string.IsNullOrWhiteSpace(request.EnvironmentId) ? request.EnvironmentId! : request.AppId;
        var appRecord = await db.Apps
            .Include(x => x.Organization)
            .SingleOrDefaultAsync(x => x.Id == targetAppId, cancellationToken);
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

        var placementTouched = false;
        async Task<IResult> CreateCore()
        {
            if (managed) { await imagePublications.LockAsync(cancellationToken); }
            await db.Entry(appRecord).ReloadAsync(cancellationToken);
            await db.Entry(appRecord).Reference(x => x.SelectedPlan).LoadAsync(cancellationToken);
            var currentApp = appRecord;
            if (currentApp.DeletionStatus is not null || await db.Applications.AnyAsync(x => x.Id == currentApp.ApplicationId && x.DeletionStatus != null, cancellationToken)) { return EndpointResults.Conflict("app_deleting", "The app is being deleted."); }
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
            placementTouched = request.Plan is not null || appRecord.PlanId is not null;
            if (request.Plan is not null)
            {
                if (preparation is null) await catalog.BindAsync(appRecord, request.Plan, cancellationToken);
                else if (preparation.PlanId != (await catalog.ResolveAsync(appRecord.OrganizationId, request.Plan, cancellationToken, requireEnabled: false)).Id)
                    return EndpointResults.Conflict("preparation_conflict", "Preparation is bound to another plan.");
            }
            var deployment = deploymentLifecycle.CreateQueued(appRecord, request);
            if (preparation is not null)
            {
                deployment.Id = preparation.Id;
                if (appRecord.PlanId != preparation.PlanId)
                    return EndpointResults.Conflict("preparation_conflict", "The app plan changed after build preparation.");
            }
            else await catalog.ReserveAsync(appRecord, deployment, cancellationToken);
            if (managed) { await imagePublications.FinalizeDeploymentAsync(appRecord, deployment, cancellationToken); }
            var artifactValidation = await FinalizeArtifactBackedServicesAsync(db, appRecord, deployment, cancellationToken);
            if (!artifactValidation.IsValid)
            {
                return EndpointResults.ValidationProblem("Deployment artifacts are invalid.", artifactValidation);
            }

            var desiredServices = await serviceSets.BuildMergedServicesAsync(db, appRecord.Id, deployment.Services, cancellationToken);
            var desiredValidation = ControlPlaneValidation.ValidateMergedDeploymentServices(desiredServices, deployment.Database == DatabaseMode.Sqlite);
            if (!desiredValidation.IsValid)
            {
                return EndpointResults.ValidationProblem("Deployment request is invalid for the app's active service set.", desiredValidation);
            }

            if (desiredServices.Any(x => x.Storage is { Count: > 0 }))
            {
                var servers = await db.ServerInstances.Where(x => x.AppId == appRecord.Id).ToListAsync(cancellationToken);
                if (servers.Count > 1 || servers.Any(x => x.OrganizationId != appRecord.OrganizationId))
                { return EndpointResults.Conflict("storage_server_conflict", "Storage requires one verified app server."); }
                var options = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProviderPlanOptions>>().Value;
                var provider = servers.SingleOrDefault()?.Provider ?? preparation?.ProviderType ?? deployment.ProviderType
                    ?? ProviderPlanMapping.FromAppPlan(appRecord.Plan, options).Provider.ToString();
                if (!string.Equals(provider, "Hetzner", StringComparison.OrdinalIgnoreCase))
                { return EndpointResults.Problem("storage_provider_unsupported", "Durable storage is currently supported only on Hetzner.", StatusCodes.Status400BadRequest); }
            }

            var storedVolumes = await db.AppStorageVolumes.Where(x => x.AppId == appRecord.Id).ToListAsync(cancellationToken);
            var desiredVolumes = desiredServices.SelectMany(x => x.Storage ?? new Dictionary<string, StorageMountRequest>()).ToArray();
            foreach (var (name, mount) in desiredVolumes)
            {
                var stored = storedVolumes.SingleOrDefault(x => x.Name == name);
                if (stored is not null && (mount.SizeGb < stored.SizeGb || mount.Uid != stored.Uid || mount.Gid != stored.Gid))
                { return EndpointResults.Problem("invalid_storage_change", "Storage cannot shrink or change ownership. Retain its size, UID and GID.", StatusCodes.Status400BadRequest); }
            }
            var retainedSizes = storedVolumes.ToDictionary(x => x.Name, x => x.SizeGb);
            foreach (var (name, mount) in desiredVolumes) { retainedSizes[name] = mount.SizeGb; }
            if (retainedSizes.Count > StorageValidation.MaxVolumes || retainedSizes.Values.Sum(x => (long)x) > StorageValidation.MaxSizeGb)
            { return EndpointResults.Problem("storage_limit_exceeded", "Retained and requested storage exceeds the app's volume count or capacity limit.", StatusCodes.Status400BadRequest); }

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
            return new DeploymentCreated(deployment, enqueue.GeneratedPostgresPassword);
        }
        try
        {
            var result = await ImagePublicationsController.Handle(() => preparations.LockedAsync(appRecord.Id, async () =>
            {
                var result = await CreateCore();
                // A rejected admission must not commit a newly selected plan or freeze its mapping.
                if (placementTouched && result is IStatusCodeHttpResult { StatusCode: >= 400 })
                    throw new PlanAdmissionRejectedException(result);
                return result;
            }, cancellationToken));
            if (result is not DeploymentCreated created) { return result; }
            // The number is allocated with the insert, so links are built only after commit.
            var deployment = created.Deployment;
            var location = DeploymentLinks.ApiPath(deployment.OrganizationId, deployment.Number);
            return Results.Created(location, new DeploymentCreateResponse(
                deployment.Id,
                deployment.Number,
                deployment.AppId,
                ControlPlaneValidation.ToWireValue(deployment.Status),
                location,
                deployment.WebsiteUrl,
                deployment.CreatedAt,
                created.GeneratedPostgresPassword,
                appRecord.ApplicationId,
                appRecord.Id));
        }
        catch (PlanAdmissionRejectedException ex) { return ex.Result; }
        catch (DeploymentNumberExhaustedException) { return EndpointResults.DeploymentNumberExhausted(); }
        catch (ProviderCatalogException ex)
        { return EndpointResults.Problem(ex.Code, ex.Message, StatusCodes.Status400BadRequest); }
        catch (SecretStoreUnavailableException)
        { return EndpointResults.Problem("secret_store_unavailable", "Provider credentials are temporarily unavailable.", StatusCodes.Status503ServiceUnavailable); }
        catch (DbUpdateException)
        {
            return EndpointResults.Conflict("deployment_domain_taken", "Deployment domain is already assigned to another app or organization.");
        }
    }

    private sealed class PlanAdmissionRejectedException(IResult result) : Exception
    {
        public IResult Result { get; } = result;
    }

    private sealed class DeploymentCreated(Deployment deployment, string? generatedPostgresPassword) : IResult
    {
        public Deployment Deployment { get; } = deployment;
        public string? GeneratedPostgresPassword { get; } = generatedPostgresPassword;
        public Task ExecuteAsync(HttpContext httpContext) => throw new InvalidOperationException("Deployment creation must be mapped to its response.");
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

    [HttpGet("{deploymentNumber}")]
    public async Task<IResult> GetDeployment([FromRoute] string deploymentNumber, [FromQuery] string? organizationId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IConsoleUrlProvider consoleUrls, CancellationToken cancellationToken)
    {
        var lookup = await DeploymentNumberResolver.ResolveAsync(HttpContext, authorization, db.Deployments
            .Include(x => x.App)
            .ThenInclude(x => x.Domains)
            .Include(x => x.Services)
            .Include(x => x.Events.Where(e => e.Message == DeploymentLifecycleService.ManuallyQueuedMessage)),
            organizationId, deploymentNumber, OrganizationAction.ReadDeployments, cancellationToken);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        return Results.Ok(lookup.Deployment!.ToResponse(consoleUrls.GetConsoleBaseUrl()));
    }

    [HttpPost("{deploymentNumber}/dispatch")]
    public async Task<IResult> DispatchDeployment([FromRoute] string deploymentNumber, [FromQuery] string? organizationId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] DeploymentLifecycleService deploymentLifecycle, [FromServices] DeploymentServiceSetService serviceSets, [FromServices] DomainBindingService domainBindings, [FromServices] DeploymentOrchestrator deploymentOrchestrator, [FromServices] IConsoleUrlProvider consoleUrls, [FromServices] ImagePublicationService imagePublications, [FromServices] ProviderCatalogService catalog, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var lookup = await DeploymentNumberResolver.ResolveAsync(HttpContext, authorization, db.Deployments
            .Include(x => x.App)
            .ThenInclude(x => x.Organization)
            .Include(x => x.App)
            .ThenInclude(x => x.Domains)
            .Include(x => x.Services),
            organizationId, deploymentNumber, OrganizationAction.CreateDeployments, cancellationToken);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        var deployment = lookup.Deployment!;
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

            await catalog.ReserveAsync(deployment.App, deployment, cancellationToken);
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
        try { return await ImagePublicationsController.Handle(() => imagePublications.LockedAsync(DispatchCore, cancellationToken)); }
        catch (ProviderCatalogException ex) { return EndpointResults.Problem(ex.Code, ex.Message, StatusCodes.Status400BadRequest); }
        catch (SecretStoreUnavailableException) { return EndpointResults.Problem("secret_store_unavailable", "Provider credentials are temporarily unavailable.", StatusCodes.Status503ServiceUnavailable); }
    }

    [HttpPost("{deploymentNumber}/cancel")]
    public async Task<IResult> CancelDeployment([FromRoute] string deploymentNumber, [FromQuery] string? organizationId,
        [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] DeploymentCancellationService cancellations, CancellationToken cancellationToken)
    {
        // Cancellation keeps its resource-hiding 404 for inaccessible organizations.
        var lookup = await DeploymentNumberResolver.ResolveAsync(HttpContext, authorization, db.Deployments.AsNoTracking(),
            organizationId, deploymentNumber, OrganizationAction.CreateDeployments, cancellationToken, hideUnauthorized: true);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        var deployment = lookup.Deployment!;
        var result = await cancellations.RequestAsync(RequirePrincipal(HttpContext), deployment.Id, cancellationToken);
        return result.Code is not null
            ? EndpointResults.Problem(result.Code, result.Status == 404 ? "Deployment was not found." : "Deployment has already finished.", result.Status)
            : Results.Json(new DeploymentCancellationResponse(deployment.Id, deployment.Number, true), statusCode: result.Status);
    }

    [HttpPost("{deploymentNumber}/refresh")]
    public async Task<IResult> RefreshDeployment([FromRoute] string deploymentNumber, [FromQuery] string? organizationId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IConsoleUrlProvider consoleUrls, CancellationToken cancellationToken)
    {
        var lookup = await DeploymentNumberResolver.ResolveAsync(HttpContext, authorization, db.Deployments
            .Include(x => x.App)
            .ThenInclude(x => x.Organization)
            .Include(x => x.App)
            .ThenInclude(x => x.Domains)
            .Include(x => x.Services)
            .Include(x => x.Events.Where(e => e.Message == DeploymentLifecycleService.ManuallyQueuedMessage)),
            organizationId, deploymentNumber, OrganizationAction.ReadDeployments, cancellationToken);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        // T50: minicloud-worker owns lifecycle transitions; refresh reads current state.
        return Results.Ok(lookup.Deployment!.ToResponse(consoleUrls.GetConsoleBaseUrl()));
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

    [HttpGet("{deploymentNumber}/events")]
    public async Task<IResult> GetDeploymentEvents([FromRoute] string deploymentNumber, [FromQuery] string? organizationId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var lookup = await DeploymentNumberResolver.ResolveAsync(HttpContext, authorization, db.Deployments.AsNoTracking().Include(x => x.App),
            organizationId, deploymentNumber, OrganizationAction.ReadDeployments, cancellationToken);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        var deployment = lookup.Deployment!;
        var eventRows = await db.DeploymentEvents
            .Where(x => x.DeploymentId == deployment.Id)
            .ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: false, take: null, cancellationToken);
        var events = eventRows
            .Select(x => new DeploymentEventResponse(x.Id, ControlPlaneValidation.ToWireValue(x.Level), x.Message, x.MetadataJson, x.CreatedAt, deployment.App.ApplicationId, deployment.AppId))
            .ToList();

        return Results.Ok(events);
    }

    [HttpGet("{deploymentNumber}/activities")]
    public async Task<IResult> GetDeploymentActivities([FromRoute] string deploymentNumber, [FromQuery] string? organizationId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var lookup = await DeploymentNumberResolver.ResolveAsync(HttpContext, authorization, db.Deployments.AsNoTracking(),
            organizationId, deploymentNumber, OrganizationAction.ReadDeployments, cancellationToken);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        // T90: GitHub workflow polling was decommissioned. minicloud-worker owns
        // lifecycle transitions and surfaces progress via deployment events/logs.
        // Historical rows may still carry a GithubRunId; no GitHub lookup occurs.
        return Results.Ok(Array.Empty<DeploymentActivityResponse>());
    }

    [HttpGet("{deploymentNumber}/logs")]
    public async Task<IResult> GetDeploymentLogs([FromRoute] string deploymentNumber, [FromQuery] string? organizationId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var lookup = await DeploymentNumberResolver.ResolveAsync(HttpContext, authorization, db.Deployments.AsNoTracking(),
            organizationId, deploymentNumber, OrganizationAction.ReadDeployments, cancellationToken);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        var deployment = lookup.Deployment!;
        var logRows = await db.DeploymentLogs
            .Where(x => x.DeploymentId == deployment.Id)
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
