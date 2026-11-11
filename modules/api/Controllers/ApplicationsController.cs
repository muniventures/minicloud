using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/apps")]
public sealed class ApplicationsController : ControllerBase
{
    private static readonly TimeSpan ObservationStaleThreshold = TimeSpan.FromMinutes(2);

    [HttpGet]
    public async Task<IResult> ListApplications(
        [FromQuery] string organizationId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), organizationId, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }


        var applications = await db.Applications.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Select(x => new MinicloudApplication
            {
                Id = x.Id,
                OrganizationId = x.OrganizationId,
                Name = x.Name,
                Slug = x.Slug,
                NormalizedSlug = x.NormalizedSlug,
                DefaultEnvironmentId = x.DefaultEnvironmentId,
                LegacyRootAppId = x.LegacyRootAppId,
                DeletionStatus = x.DeletionStatus,
                DeletionError = x.DeletionError,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        if (applications.Count > 0)
        {
            var applicationIds = applications.Select(x => x.Id).ToArray();
            var environments = await new AppSummaryReader(db).ReadEnvironmentsAsync(
                organizationId,
                db.Apps.Where(x => x.ApplicationId != null && applicationIds.Contains(x.ApplicationId) &&
                    x.EnvironmentKind != EnvironmentKinds.Internal),
                cancellationToken);
            var environmentsByApplication = environments.ToLookup(x => x.ApplicationId, StringComparer.Ordinal);
            foreach (var application in applications)
            {
                application.Environments = environmentsByApplication[application.Id].ToList();
            }
        }

        var responses = await BuildApplicationResponsesAsync(db, applications, timeProvider.GetUtcNow(), cancellationToken);
        return Results.Ok(responses);
    }

    [HttpPost]
    public async Task<IResult> CreateApplication(
        [FromBody] CreateApplicationRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] IIdGenerator ids,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ProviderCatalogService catalog,
        [FromServices] EnvironmentSshAccessService sshAccess,
        [FromServices] AuditEventService audit,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var rawEnvName = request.InitialEnvironment?.Name;
        var envName = string.IsNullOrWhiteSpace(rawEnvName) ? "production" : rawEnvName.Trim();
        var planSpec = request.InitialEnvironment?.Plan ?? string.Empty;
        var databaseSpec = request.InitialEnvironment?.Database ?? "none";

        if (!AppEnvironmentService.IsValidName(envName))
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody("environment_name_invalid", "Environment names must contain 1 to 63 letters, numbers, dashes or underscores.", [])));
        }

        var validationRequest = new CreateAppRequest(
            request.OrganizationId,
            request.Name,
            request.Slug,
            planSpec,
            databaseSpec,
            envName);
        var validation = ControlPlaneValidation.ValidateApp(validationRequest);
        if (!validation.IsValid)
        {
            return EndpointResults.ValidationProblem("Application request is invalid.", validation);
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), request.OrganizationId, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }


        var normalizedSlug = request.Slug.Trim().ToLowerInvariant();
        var organization = await db.Organizations.SingleAsync(x => x.Id == request.OrganizationId, cancellationToken);
        if (await db.Applications.AnyAsync(x => x.OrganizationId == request.OrganizationId && x.NormalizedSlug == normalizedSlug, cancellationToken) ||
            await db.Apps.AnyAsync(x => x.OrganizationId == request.OrganizationId && x.Slug.ToLower() == normalizedSlug, cancellationToken))
        {
            return EndpointResults.Conflict("app_slug_taken", "Application slug is already taken in this organization.");
        }

        ProviderPlan plan;
        try
        {
            plan = await catalog.ResolveAsync(request.OrganizationId, planSpec, cancellationToken);
        }
        catch (ProviderCatalogException ex)
        {
            return EndpointResults.Problem(ex.Code, ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (SecretStoreUnavailableException)
        {
            return EndpointResults.Problem("secret_store_unavailable", "Provider credentials are temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
        }

        ControlPlaneValidation.TryParseEnum<DatabaseMode>(databaseSpec, out var database);

        var now = timeProvider.GetUtcNow();
        var normalizedEnvName = envName.ToLowerInvariant();
        var envSlug = normalizedEnvName == "production"
            ? normalizedSlug
            : AppBranchService.BranchSlug(normalizedSlug, normalizedEnvName);

        if (envSlug != normalizedSlug && await db.Apps.AnyAsync(x => x.OrganizationId == request.OrganizationId && x.Slug.ToLower() == envSlug.ToLower(), cancellationToken))
        {
            return EndpointResults.Conflict("environment_slug_taken", "An environment with this URL slug already exists.");
        }

        var envId = ids.NewId("app");
        var application = new MinicloudApplication
        {
            Id = ids.NewId("appl"),
            OrganizationId = request.OrganizationId,
            Organization = organization,
            Name = request.Name.Trim(),
            Slug = normalizedSlug,
            NormalizedSlug = normalizedSlug,
            DefaultEnvironmentId = envId,
            LegacyRootAppId = envId,
            CreatedAt = now,
            UpdatedAt = now
        };
        var environment = new MinicloudApp
        {
            Id = envId,
            OrganizationId = request.OrganizationId,
            Organization = organization,
            ApplicationId = application.Id,
            Application = application,
            Name = request.Name.Trim(),
            Slug = envSlug,
            EnvironmentName = envName,
            NormalizedEnvironmentName = normalizedEnvName,
            EnvironmentKind = EnvironmentKinds.Persistent,
            EnvironmentKey = $"{envId}:{normalizedEnvName}",
            PlanId = plan.Id,
            SelectedPlan = plan,
            Database = database,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (request.InitialEnvironment?.SshAccess is { } initialSshAccess)
        {
            try
            {
                await sshAccess.ApplySelectionAsync(environment, plan.ProviderAccountId, initialSshAccess,
                    principal.ToActorIdentity(), isPatch: false, cancellationToken);
            }
            catch (SshAccessException ex)
            {
                return EndpointResults.Problem(ex.Code, ex.Message, ex.StatusCode);
            }
        }
        application.Environments.Add(environment);

        db.Applications.Add(application);
        db.Apps.Add(environment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }
            or Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        {
            return EndpointResults.Conflict("app_slug_taken", "Application slug is already taken in this organization.");
        }

        await audit.AppendAsync(
            request.OrganizationId,
            environment.Id,
            AuditEventService.ActorFor(principal.ToActorIdentity()),
            "application.created",
            $"{application.Id}:{environment.Id}",
            cancellationToken);
        if (environment.SshRevision > 0)
        {
            await audit.AppendAsync(
                request.OrganizationId,
                environment.Id,
                AuditEventService.ActorFor(principal.ToActorIdentity()),
                "environment.ssh_access_selected",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    mode = environment.SshAccessMode,
                    providerAccountId = environment.SshProviderAccountId,
                    fingerprint = environment.SshFingerprint,
                    revision = environment.SshRevision
                }),
                cancellationToken);
        }

        var response = (await BuildApplicationResponsesAsync(db, [application], now, cancellationToken)).Single();
        return Results.Created($"/api/apps/{application.Id}", response);
    }

    [HttpGet("{applicationId}")]
    public async Task<IResult> GetApplication(
        [FromRoute] string applicationId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);

        var application = await db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return EndpointResults.NotFound("app_not_found", "Application was not found.");
        }

        if (!await authorization.CanAccessApplicationAsync(principal.ToActorIdentity(), application.Id, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        application.Environments = await new AppSummaryReader(db).ReadEnvironmentsAsync(application.OrganizationId,
            db.Apps.Where(x => x.ApplicationId == application.Id && x.EnvironmentKind != EnvironmentKinds.Internal), cancellationToken);

        var response = (await BuildApplicationResponsesAsync(db, [application], timeProvider.GetUtcNow(), cancellationToken)).Single();
        return Results.Ok(response);
    }

    [HttpPatch("{applicationId}")]
    public async Task<IResult> UpdateApplication(
        [FromRoute] string applicationId,
        [FromBody] UpdateApplicationRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] TimeProvider timeProvider,
        [FromServices] AuditEventService audit,
        [FromServices] EnvironmentSshAccessService sshAccess,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);

        if (request.Plan is not null || request.Database is not null)
        {
            var validation = new ValidationResult();
            if (request.Plan is not null)
            {
                validation.Add("plan", "Plan belongs to an environment, not an application. Update the target environment instead.");
            }
            if (request.Database is not null)
            {
                validation.Add("database", "Database mode belongs to an environment, not an application. Update the target environment instead.");
            }
            return EndpointResults.ValidationProblem("Application update request is invalid.", validation);
        }

        var application = await db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return EndpointResults.NotFound("app_not_found", "Application was not found.");
        }

        if (!await authorization.CanAccessApplicationAsync(principal.ToActorIdentity(), application.Id, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (db.Database.IsNpgsql())
                await db.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {applicationId} FOR UPDATE")
                    .AsNoTracking().SingleAsync(cancellationToken);
            application = await LoadApplicationsQuery(db).SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
            if (application is null)
                return EndpointResults.NotFound("app_not_found", "Application was not found.");

            if (application.DeletionStatus is not null)
            {
                return EndpointResults.Conflict("app_deleting", "The application is being deleted.");
            }

            var now = timeProvider.GetUtcNow();
            if (request.Name is not null)
            {
                var trimmedName = request.Name.Trim();
                if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 80)
                {
                    var validation = new ValidationResult();
                    validation.Add("name", "Name must be between 1 and 80 characters.");
                    return EndpointResults.ValidationProblem("Application update request is invalid.", validation);
                }

                application.Name = trimmedName;
                foreach (var env in application.Environments.Where(x => x.ParentAppId == null && x.EnvironmentKind == EnvironmentKinds.Persistent))
                {
                    env.Name = trimmedName;
                    env.UpdatedAt = now;
                }
            }

            if (request.DefaultEnvironmentId is not null)
            {
                var targetEnv = application.Environments.SingleOrDefault(x => x.Id == request.DefaultEnvironmentId);
                if (targetEnv is null)
                {
                    return EndpointResults.NotFound("environment_not_found", "Target default environment does not belong to this application.");
                }
                if (targetEnv.DeletionStatus is not null)
                {
                    return EndpointResults.Conflict("environment_deleting", "Cannot set a deleting environment as the default environment.");
                }
                if (targetEnv.EnvironmentKind != EnvironmentKinds.Persistent)
                {
                    return Results.BadRequest(new ErrorResponse(new ErrorBody("default_environment_invalid", "Only persistent environments can be set as the default dashboard environment.", [])));
                }
                application.DefaultEnvironmentId = targetEnv.Id;
            }

            application.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await audit.AppendAsync(
                application.OrganizationId,
                application.DefaultEnvironmentId,
                AuditEventService.ActorFor(principal.ToActorIdentity()),
                "application.updated",
                application.Id,
                cancellationToken);

            var response = (await BuildApplicationResponsesAsync(db, [application], now, cancellationToken)).Single();
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(response);
        });
    }

    [HttpDelete("{applicationId}")]
    public async Task<IResult> DeleteApplication(
        [FromRoute] string applicationId,
        [FromBody] DeleteApplicationRequest request,
        [FromServices] AppDeletionService deletion,
        CancellationToken cancellationToken)
    {
        var error = await deletion.RequestApplicationDeletionAsync(RequirePrincipal(HttpContext), applicationId, request.Confirmation, cancellationToken);
        return error is null
            ? Results.Accepted($"/api/apps/{applicationId}")
            : Results.Json(new ErrorResponse(new ErrorBody(error.Value.Code, error.Value.Message, [])), statusCode: error.Value.Status);
    }

    [HttpGet("{applicationId}/environments")]
    public async Task<IResult> ListEnvironments(
        [FromRoute] string applicationId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);

        var application = await db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return EndpointResults.NotFound("app_not_found", "Application was not found.");
        }

        if (!await authorization.CanAccessApplicationAsync(principal.ToActorIdentity(), application.Id, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        application.Environments = await new AppSummaryReader(db).ReadEnvironmentsAsync(application.OrganizationId,
            db.Apps.Where(x => x.ApplicationId == application.Id && x.EnvironmentKind != EnvironmentKinds.Internal), cancellationToken);

        var appResponse = (await BuildApplicationResponsesAsync(db, [application], timeProvider.GetUtcNow(), cancellationToken)).Single();
        return Results.Ok(appResponse.Environments);
    }

    [HttpPost("{applicationId}/environments")]
    public async Task<IResult> CreateEnvironment(
        [FromRoute] string applicationId,
        [FromBody] CreateEnvironmentRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppEnvironmentService environments,
        [FromServices] TimeProvider timeProvider,
        [FromServices] AuditEventService audit,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);

        var application = await LoadApplicationsQuery(db)
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return EndpointResults.NotFound("app_not_found", "Application was not found.");
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), application.OrganizationId, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        try
        {
            var env = await environments.CreateForApplicationAsync(application, request, principal.ToActorIdentity(), cancellationToken);
            await audit.AppendAsync(
                application.OrganizationId,
                env.Id,
                AuditEventService.ActorFor(principal.ToActorIdentity()),
                "environment.created",
                $"{application.Id}:{env.Id}:{env.EnvironmentName}",
                cancellationToken);
            if (env.SshRevision > 0)
            {
                await audit.AppendAsync(
                    application.OrganizationId,
                    env.Id,
                    AuditEventService.ActorFor(principal.ToActorIdentity()),
                    "environment.ssh_access_selected",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        mode = env.SshAccessMode,
                        providerAccountId = env.SshProviderAccountId,
                        fingerprint = env.SshFingerprint,
                        revision = env.SshRevision
                    }),
                    cancellationToken);
            }

            var observations = await BuildObservationsByEnvIdAsync(db, [env.Id], timeProvider.GetUtcNow(), cancellationToken);
            var (lockedIds, _) = await GetEnvironmentLockIdsAsync(db, [env.Id], cancellationToken);
            var response = env.ToResponse(
                application.Id,
                observations.GetValueOrDefault(env.Id),
                lockedIds.Contains(env.Id));
            return Results.Created($"/api/apps/{application.Id}/environments/{env.Id}", response);
        }
        catch (AppEnvironmentException ex)
        {
            var status = ex.Code is "environment_conflict" or "environment_slug_taken" or "app_deleting"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Results.Json(new ErrorResponse(new ErrorBody(ex.Code, ex.Message, [])), statusCode: status);
        }
        catch (SshAccessException ex)
        {
            return EndpointResults.Problem(ex.Code, ex.Message, ex.StatusCode);
        }
        catch (ProviderCatalogException ex)
        {
            return EndpointResults.Problem(ex.Code, ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (SecretStoreUnavailableException)
        {
            return EndpointResults.Problem("secret_store_unavailable", "Provider credentials are temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpGet("{applicationId}/environments/{environmentId}")]
    public async Task<IResult> GetEnvironment(
        [FromRoute] string applicationId,
        [FromRoute] string environmentId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);

        var application = await db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return EndpointResults.NotFound("app_not_found", "Application was not found.");
        }

        if (!await authorization.CanAccessApplicationAsync(principal.ToActorIdentity(), application.Id, OrganizationAction.ReadApps, cancellationToken))
            return EndpointResults.Forbidden();
        application.Environments = await new AppSummaryReader(db).ReadEnvironmentsAsync(application.OrganizationId,
            db.Apps.Where(x => x.ApplicationId == application.Id && x.Id == environmentId && x.EnvironmentKind != EnvironmentKinds.Internal), cancellationToken);

        var env = application.Environments.SingleOrDefault(x => x.Id == environmentId);
        if (env is null)
        {
            return EndpointResults.NotFound("environment_not_found", "Environment was not found in this application.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), env.Id, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var now = timeProvider.GetUtcNow();
        var observations = await BuildObservationsByEnvIdAsync(db, [env.Id], now, cancellationToken);
        var (lockedIds, sshLockedIds) = await GetEnvironmentLockIdsAsync(db, [env.Id], cancellationToken);
        return Results.Ok(env.ToResponse(application.Id, observations.GetValueOrDefault(env.Id),
            lockedIds.Contains(env.Id), sshLockedIds.Contains(env.Id)));
    }

    [HttpPatch("{applicationId}/environments/{environmentId}")]
    public async Task<IResult> UpdateEnvironment(
        [FromRoute] string applicationId,
        [FromRoute] string environmentId,
        [FromBody] UpdateEnvironmentRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppEnvironmentService environments,
        [FromServices] ProviderCatalogService catalog,
        [FromServices] DeploymentPreparationService preparations,
        [FromServices] EnvironmentSshAccessService sshAccess,
        [FromServices] TimeProvider timeProvider,
        [FromServices] AuditEventService audit,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);

        var application = await LoadApplicationsQuery(db)
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return EndpointResults.NotFound("app_not_found", "Application was not found.");
        }

        var env = application.Environments.SingleOrDefault(x => x.Id == environmentId);
        if (env is null)
        {
            return EndpointResults.NotFound("environment_not_found", "Environment was not found in this application.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), env.Id, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        if (request.Database is not null && !ControlPlaneValidation.TryParseEnum<DatabaseMode>(request.Database, out _))
        {
            return EndpointResults.Problem("database_invalid", "Select a supported database mode.", StatusCodes.Status400BadRequest);
        }

        try
        {
            return await preparations.LockedAsync(env.Id, async () =>
            {
                await db.Entry(env).ReloadAsync(cancellationToken);
                await db.Entry(application).ReloadAsync(cancellationToken);
                if (env.DeletionStatus is not null || application.DeletionStatus is not null)
                {
                    return EndpointResults.Conflict("app_deleting", "The environment or application is being deleted.");
                }

                var now = timeProvider.GetUtcNow();
                var previousSshRevision = env.SshRevision;
                var previousPlanAccountId = env.SelectedPlan?.ProviderAccountId;
                if (request.SetAsDefault is true && env.EnvironmentKind != EnvironmentKinds.Persistent)
                    return Results.BadRequest(new ErrorResponse(new ErrorBody("default_environment_invalid", "Only persistent environments can be set as the default dashboard environment.", [])));
                var database = env.Database;
                if (request.Database is not null && ControlPlaneValidation.TryParseEnum<DatabaseMode>(request.Database, out var requestedDatabase))
                {
                    database = requestedDatabase;
                    if (database != env.Database && await preparations.HasActiveAsync(env.Id, null, cancellationToken))
                        return EndpointResults.Conflict("deployment_active", "Finish or cancel active deployments before changing database mode.");
                }
                if (request.Name is not null && !AppEnvironmentService.IsValidName(request.Name))
                    return EndpointResults.Problem("environment_name_invalid", "Choose a valid environment name.", StatusCodes.Status400BadRequest);
                if (request.Plan is not null)
                    await catalog.BindAsync(env, request.Plan, cancellationToken);
                var destinationAccountId = env.SelectedPlan?.ProviderAccountId;
                // LockedAsync saves tracked changes, so rejected updates must discard the plan binding above.
                if (request.SshAccess is null && env.SshAccessMode != "none" &&
                    !string.Equals(previousPlanAccountId, destinationAccountId, StringComparison.Ordinal))
                {
                    db.ChangeTracker.Clear();
                    return EndpointResults.Conflict("ssh_selection_account_mismatch",
                        "Changing provider accounts requires an explicit SSH access selection for the destination account.");
                }
                if (request.SshAccess is not null)
                {
                    if (await HasSshAllocationLockAsync(db, env.Id, cancellationToken))
                    {
                        db.ChangeTracker.Clear();
                        return EndpointResults.Conflict("ssh_selection_locked",
                            "SSH access cannot be changed during or after server allocation.");
                    }
                    await sshAccess.ApplySelectionAsync(env, destinationAccountId, request.SshAccess,
                        principal.ToActorIdentity(), isPatch: true, cancellationToken);
                }
                if (request.Name is not null && !string.Equals(request.Name.Trim(), env.EnvironmentName, StringComparison.Ordinal))
                    await environments.RenameAsync(env, request.Name, cancellationToken, allowPreview: true);
                env.Database = database;
                if (request.SetAsDefault is true)
                {
                    application.DefaultEnvironmentId = env.Id;
                    application.UpdatedAt = now;
                }

                env.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                await db.Entry(env).Reference(x => x.SelectedPlan).LoadAsync(cancellationToken);

                await audit.AppendAsync(
                    application.OrganizationId,
                    env.Id,
                    AuditEventService.ActorFor(principal.ToActorIdentity()),
                    "environment.updated",
                    $"{application.Id}:{env.Id}",
                    cancellationToken);
                if (env.SshRevision != previousSshRevision)
                {
                    await audit.AppendAsync(
                        application.OrganizationId,
                        env.Id,
                        AuditEventService.ActorFor(principal.ToActorIdentity()),
                        "environment.ssh_access_selected",
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            mode = env.SshAccessMode,
                            providerAccountId = env.SshProviderAccountId,
                            fingerprint = env.SshFingerprint,
                            revision = env.SshRevision
                        }),
                        cancellationToken);
                }

                var observations = await BuildObservationsByEnvIdAsync(db, [env.Id], now, cancellationToken);
                var (lockedIds, sshLockedIds) = await GetEnvironmentLockIdsAsync(db, [env.Id], cancellationToken);
                return Results.Ok(env.ToResponse(application.Id, observations.GetValueOrDefault(env.Id),
                    lockedIds.Contains(env.Id), sshLockedIds.Contains(env.Id)));
            }, cancellationToken);
        }
        catch (AppEnvironmentException ex)
        {
            var status = ex.Code is "environment_conflict" or "app_deleting"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Results.Json(new ErrorResponse(new ErrorBody(ex.Code, ex.Message, [])), statusCode: status);
        }
        catch (SshAccessException ex)
        {
            return EndpointResults.Problem(ex.Code, ex.Message, ex.StatusCode);
        }
        catch (ProviderCatalogException ex)
        {
            return EndpointResults.Problem(ex.Code, ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (SecretStoreUnavailableException)
        {
            return EndpointResults.Problem("secret_store_unavailable", "Provider credentials are temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpDelete("{applicationId}/environments/{environmentId}")]
    public async Task<IResult> DeleteEnvironment(
        [FromRoute] string applicationId,
        [FromRoute] string environmentId,
        [FromBody] DeleteEnvironmentRequest request,
        [FromServices] AppDeletionService deletion,
        CancellationToken cancellationToken)
    {
        var error = await deletion.RequestEnvironmentDeletionAsync(
            RequirePrincipal(HttpContext),
            applicationId,
            environmentId,
            request.Confirmation,
            cancellationToken);
        return error is null
            ? Results.Accepted($"/api/apps/{applicationId}/environments/{environmentId}")
            : Results.Json(new ErrorResponse(new ErrorBody(error.Value.Code, error.Value.Message, [])), statusCode: error.Value.Status);
    }

    private static IQueryable<MinicloudApplication> LoadApplicationsQuery(MinicloudDbContext db)
    {
        var query = db.Applications
            .AsSplitQuery()
            .Include(x => x.Organization)
            .Include(x => x.Environments).ThenInclude(x => x.SelectedPlan)
            .Include(x => x.Environments).ThenInclude(x => x.Domains);
        // PostgreSQL is the hosted control-plane store. Restrict its history in SQL;
        // SQLite cannot translate DateTimeOffset ordering used by local test fixtures.
        return db.Database.IsNpgsql()
            ? query.Include(x => x.Environments).ThenInclude(x => x.Deployments
                .Where(d => d.AssetReleaseId == null)
                .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(1))
            : query.Include(x => x.Environments).ThenInclude(x => x.Deployments);
    }

    private static async Task<bool> HasSshAllocationLockAsync(MinicloudDbContext db, string environmentId, CancellationToken ct)
    {
        if (await db.ServerInstances.AnyAsync(x => x.AppId == environmentId, ct) ||
            await db.RuntimeServers.AnyAsync(x => x.AppId == environmentId, ct))
            return true;

        return await db.Deployments.AnyAsync(x => x.AppId == environmentId &&
            (x.ProviderCreationAttempted ||
             (x.SshSnapshotTakenAt != null &&
              ((x.Status == DeploymentStatus.Queued || x.Status == DeploymentStatus.WaitingForBuild ||
                x.Status == DeploymentStatus.Dispatching || x.Status == DeploymentStatus.Provisioning ||
                x.Status == DeploymentStatus.Deploying || x.Status == DeploymentStatus.Verifying) ||
               x.SshRegistrationState == "registering" || x.SshRegistrationState == "unknown"))), ct);
    }

    internal static async Task<IReadOnlyList<ApplicationResponse>> BuildApplicationResponsesAsync(
        MinicloudDbContext db,
        IReadOnlyList<MinicloudApplication> applications,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (applications.Count == 0)
        {
            return [];
        }

        var envIds = applications
            .SelectMany(x => x.Environments)
            .Where(x => x.EnvironmentKind != EnvironmentKinds.Internal)
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var observations = await BuildObservationsByEnvIdAsync(db, envIds, now, ct);
        var (lockedIds, sshLockedIds) = await GetEnvironmentLockIdsAsync(db, envIds, ct);

        return applications
            .Select(app => app.ToResponse(observations, lockedIds, sshLockedIds))
            .ToArray();
    }

    internal static async Task<Dictionary<string, EnvironmentRuntimeObservationResponse>> BuildObservationsByEnvIdAsync(
        MinicloudDbContext db,
        IReadOnlyCollection<string> envIds,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var result = new Dictionary<string, EnvironmentRuntimeObservationResponse>(StringComparer.Ordinal);
        if (envIds.Count == 0)
        {
            return result;
        }

        var servers = await db.RuntimeServers.AsNoTracking()
            .Where(x => envIds.Contains(x.AppId))
            .ToListAsync(ct);
        var serverIds = servers.Select(x => x.Id).ToArray();
        var snapshots = serverIds.Length == 0
            ? []
            : db.Database.IsNpgsql()
                ? await db.RuntimeStatusSnapshots.FromSqlInterpolated($"SELECT DISTINCT ON (runtime_server_id) * FROM runtime_status_snapshots WHERE runtime_server_id = ANY ({serverIds}) ORDER BY runtime_server_id, observed_at DESC, id DESC")
                    .AsNoTracking().ToListAsync(ct)
                : await db.RuntimeStatusSnapshots.FromSqlInterpolated($"""
                    SELECT s.* FROM runtime_status_snapshots s
                    WHERE s.runtime_server_id IN (SELECT value FROM json_each({JsonSerializer.Serialize(serverIds)}))
                      AND s.id = (SELECT s2.id FROM runtime_status_snapshots s2
                        WHERE s2.runtime_server_id = s.runtime_server_id
                        ORDER BY datetime(s2.observed_at) DESC,
                          CASE WHEN instr(s2.observed_at, '.') > 0
                            THEN CAST('0.' || substr(s2.observed_at, instr(s2.observed_at, '.') + 1) AS REAL) ELSE 0 END DESC,
                          s2.id DESC LIMIT 1)
                    """).AsNoTracking().ToListAsync(ct);

        var newestServers = servers.GroupBy(x => x.AppId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(s => s.LastSeenAt ?? s.CreatedAt).First(), StringComparer.Ordinal);
        var newestSnapshots = snapshots.ToDictionary(x => x.RuntimeServerId, StringComparer.Ordinal);

        foreach (var envId in envIds)
        {
            if (!newestServers.TryGetValue(envId, out var server))
            {
                result[envId] = new EnvironmentRuntimeObservationResponse(
                    "unprovisioned",
                    Connected: false,
                    Stale: false,
                    ObservedAt: null,
                    LastSeenAt: null,
                    TotalServices: 0,
                    HealthyServices: 0);
                continue;
            }

            newestSnapshots.TryGetValue(server.Id, out var snapshot);
            var stored = snapshot is null
                ? null
                : JsonSerializer.Deserialize<StoredRuntimeStatus>(snapshot.StatusJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var observedAt = stored?.ObservedAt ?? snapshot?.ObservedAt;
            var agentOnline = server.LastSeenAt is not null && server.LastSeenAt > now.Subtract(ObservationStaleThreshold);
            var observationFresh = observedAt is not null && observedAt > now.Subtract(ObservationStaleThreshold);
            var services = stored?.Services ?? [];
            var totalServices = services.Count;
            var healthyServices = services.Count(s =>
                string.Equals(s.State, "running", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.Health, "unhealthy", StringComparison.OrdinalIgnoreCase));

            if (!agentOnline || !observationFresh)
            {
                var state = observedAt is null && server.LastSeenAt is null ? "unavailable" : "stale";
                result[envId] = new EnvironmentRuntimeObservationResponse(
                    state,
                    Connected: agentOnline,
                    Stale: true,
                    ObservedAt: observedAt,
                    LastSeenAt: server.LastSeenAt,
                    TotalServices: totalServices,
                    HealthyServices: healthyServices);
                continue;
            }

            var hasUnhealthy = healthyServices < totalServices;
            result[envId] = new EnvironmentRuntimeObservationResponse(
                totalServices == 0 ? "unavailable" : hasUnhealthy ? "degraded" : "healthy",
                Connected: true,
                Stale: false,
                ObservedAt: observedAt,
                LastSeenAt: server.LastSeenAt,
                TotalServices: totalServices,
                HealthyServices: healthyServices);
        }

        return result;
    }

    internal static async Task<(HashSet<string> Placement, HashSet<string> SshSelection)> GetEnvironmentLockIdsAsync(
        MinicloudDbContext db,
        IReadOnlyCollection<string> envIds,
        CancellationToken ct)
    {
        if (envIds.Count == 0) return ([], []);

        var serverEnvIds = await db.ServerInstances.AsNoTracking()
            .Where(x => envIds.Contains(x.AppId))
            .Select(x => x.AppId)
            .Distinct()
            .ToListAsync(ct);
        var runtimeEnvIds = await db.RuntimeServers.AsNoTracking()
            .Where(x => envIds.Contains(x.AppId))
            .Select(x => x.AppId)
            .Distinct()
            .ToListAsync(ct);
        var deployments = await db.Deployments.AsNoTracking().Where(x => envIds.Contains(x.AppId))
            .Select(x => new
            {
                x.AppId, x.ProviderAccountId, x.ProviderCreationAttempted, x.SshRegistrationState,
                x.SshSnapshotTakenAt, x.Status
            }).ToListAsync(ct);

        var placement = serverEnvIds
            .Concat(runtimeEnvIds)
            .Concat(deployments.Where(x => x.ProviderAccountId is not null ||
                x.Status == DeploymentStatus.Queued || x.Status == DeploymentStatus.WaitingForBuild ||
                x.Status == DeploymentStatus.Provisioning || x.Status == DeploymentStatus.Deploying ||
                x.Status == DeploymentStatus.Verifying).Select(x => x.AppId))
            .ToHashSet(StringComparer.Ordinal);
        var sshSelection = serverEnvIds.Concat(runtimeEnvIds)
            .Concat(deployments.Where(x => x.ProviderCreationAttempted || x.SshRegistrationState is "unknown" or "registering" ||
                (x.SshSnapshotTakenAt is not null &&
                 (x.Status is DeploymentStatus.Queued or DeploymentStatus.WaitingForBuild or DeploymentStatus.Dispatching or
                  DeploymentStatus.Provisioning or DeploymentStatus.Deploying or DeploymentStatus.Verifying)))
                .Select(x => x.AppId))
            .ToHashSet(StringComparer.Ordinal);
        return (placement, sshSelection);
    }
}
