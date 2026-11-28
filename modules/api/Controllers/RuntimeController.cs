using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Selfrun.Api.Auth;
using Selfrun.Common.Data;
using Selfrun.Common.Contracts;
using Selfrun.Common.Models;
using Selfrun.Api.Providers;
using Selfrun.Api.Services;
using Selfrun.Common.Services;
using Selfrun.Common.Validation;
using static Selfrun.Api.Controllers.EndpointResults;

namespace Selfrun.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class RuntimeController : ControllerBase
{
    [HttpPost("heartbeat")]
    [AllowAnonymous]
    public async Task<IResult> Heartbeat([FromBody] RuntimeHeartbeatRequest request, [FromServices] SelfrunDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var auth = await runtimeTokens.AuthenticateAsync(Request.Headers.Authorization, cancellationToken);
        if (auth is null)
        {
            return Results.Unauthorized();
        }

        var now = timeProvider.GetUtcNow();
        auth.Server.AgentVersion = request.AgentVersion ?? auth.Server.AgentVersion;
        auth.Server.LastSeenAt = now;
        auth.Server.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { accepted = true, serverId = auth.Server.Id, observedAt = request.ObservedAt ?? now });
    }

    [HttpPost("ingest/logs")]
    [AllowAnonymous]
    public async Task<IResult> IngestLogs([FromBody] RuntimeLogIngestRequest request, [FromServices] SelfrunDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var measurement = RuntimeIngestionDiagnostics.Start("ingest.logs", Request.ContentLength ?? -1, request.Records.Count, cancellationToken);
        try
        {
            var auth = await runtimeTokens.AuthenticateAsync(Request.Headers.Authorization, cancellationToken);
            if (auth is null)
            {
                return Results.Unauthorized();
            }

            var validation = ValidateRuntimeLogRequest(request, timeProvider.GetUtcNow());
            if (!validation.IsValid)
            {
                return EndpointResults.ValidationProblem("Runtime log request is invalid.", validation);
            }

            var deployedServices = await LatestDeploymentServicesAsync(db, auth.Server.AppId, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var rejected = 0;
            var cursors = new Dictionary<string, string>(StringComparer.Ordinal);
            var eligible = new List<RuntimeLogIngestRecord>(request.Records.Count);

            foreach (var record in request.Records)
            {
                if (string.Equals(record.Message?.Trim(), "-- No entries --", StringComparison.Ordinal))
                {
                    rejected++;
                    continue;
                }

                if (!RuntimeSourceAllowedForServices(record.Source, record.Service, deployedServices))
                {
                    rejected++;
                    continue;
                }

                eligible.Add(record);
                cursors[record.Source] = record.Cursor;
            }

            // Check for replays before pruning so a replay cannot resurrect a row pruned by this batch.
            var pending = await RuntimeLogBatch.FindNewAsync(db, auth.Server.Id, eligible, cancellationToken);
            var pruneStartedAt = Stopwatch.GetTimestamp();
            try
            {
                await PruneRuntimeLogsAsync(db, auth.Server.AppId, now, cancellationToken);
            }
            finally
            {
                RuntimeIngestionDiagnostics.RecordLogPrune(pruneStartedAt);
            }
            var accepted = await RuntimeLogBatch.SaveAsync(db, auth.Server, eligible, pending, ids, now, cancellationToken);
            measurement.Complete("success");
            return Results.Ok(new RuntimeLogIngestResponse(accepted, rejected, cursors));
        }
        catch (OperationCanceledException)
        {
            measurement.Complete("cancelled");
            throw;
        }
        catch
        {
            measurement.Complete("failure");
            throw;
        }
    }

    [HttpPost("ingest/status")]
    [AllowAnonymous]
    public async Task<IResult> IngestStatus([FromBody] RuntimeStatusIngestRequest request, [FromServices] SelfrunDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, [FromServices] DeploymentServiceSetService serviceSets, CancellationToken cancellationToken)
    {
        using var measurement = RuntimeIngestionDiagnostics.Start("ingest.status", Request.ContentLength ?? -1, request.Services.Count, cancellationToken);
        try
        {
            var auth = await runtimeTokens.AuthenticateAsync(Request.Headers.Authorization, cancellationToken);
            if (auth is null)
            {
                return Results.Unauthorized();
            }

            if (request.ObservedAt > timeProvider.GetUtcNow().Add(RuntimeIngestionLimits.MaxFutureSkew))
            {
                return Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", "Runtime status request is invalid.", [new("observedAt", "Observed timestamp is too far in the future.")])));
            }

            var now = timeProvider.GetUtcNow();
            if (request.HttpRequests is { Count: > RuntimeIngestionLimits.MaxHttpRequestObservations })
            {
                return Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", "Runtime status request is invalid.",
                    [new("httpRequests", $"At most {RuntimeIngestionLimits.MaxHttpRequestObservations} host observations can be ingested per request.")])));
            }

            var acceptedHttpRequests = Array.Empty<RuntimeHttpRequestObservation>();
            if (request.HttpRequests is { Count: > 0 })
            {
                var configured = await LoadConfiguredRoutesAsync(db, auth.Server, serviceSets, cancellationToken);
                var allowedHostnames = configured.Bindings.Select(binding => binding.Hostname).ToHashSet(StringComparer.OrdinalIgnoreCase);
                acceptedHttpRequests = request.HttpRequests
                    .Where(observation =>
                        observation is not null &&
                        !string.IsNullOrWhiteSpace(observation.Hostname) &&
                        observation.Hostname.Length <= 253 &&
                        allowedHostnames.Contains(observation.Hostname) &&
                        observation.LastRequestAt >= now.Subtract(RuntimeIngestionLimits.MaxHttpRequestObservationAge) &&
                        observation.LastRequestAt <= now.Add(RuntimeIngestionLimits.MaxFutureSkew))
                    .Select(observation => observation.LastRequestAt > now
                        ? observation with { LastRequestAt = now }
                        : observation)
                    .GroupBy(observation => observation.Hostname, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.MaxBy(observation => observation.LastRequestAt)!)
                    .ToArray();

                var latestHttpRequestAt = acceptedHttpRequests
                    .Select(observation => (DateTimeOffset?)observation.LastRequestAt)
                    .Max();
                if (latestHttpRequestAt is { } observedAt)
                {
                    var sleepSettings = await db.EnvironmentSleepSettings
                        .SingleOrDefaultAsync(settings => settings.EnvironmentId == auth.Server.AppId, cancellationToken);
                    if (sleepSettings is not null)
                    {
                        if (sleepSettings.LastHttpRequestAt is null || observedAt > sleepSettings.LastHttpRequestAt)
                        {
                            sleepSettings.LastHttpRequestAt = observedAt;
                        }

                        if (sleepSettings.HttpTrafficIsActivity &&
                            (sleepSettings.LastActivityAt is null || observedAt > sleepSettings.LastActivityAt))
                        {
                            sleepSettings.LastActivityAt = observedAt;
                            sleepSettings.LastActivityKind = "http_request";
                        }
                    }
                }
            }

            auth.Server.AgentVersion = request.AgentVersion ?? auth.Server.AgentVersion;
            auth.Server.LastSeenAt = now;
            auth.Server.UpdatedAt = now;
            var stored = new StoredRuntimeStatus(request.AgentVersion, request.ObservedAt, request.ComposeProject, request.Services, request.Caddy, acceptedHttpRequests);
            db.RuntimeStatusSnapshots.Add(new RuntimeStatusSnapshot
            {
                Id = ids.NewId("rtstat"),
                OrganizationId = auth.Server.OrganizationId,
                AppId = auth.Server.AppId,
                RuntimeServerId = auth.Server.Id,
                AgentVersion = request.AgentVersion,
                StatusJson = JsonSerializer.Serialize(stored, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ObservedAt = request.ObservedAt,
                ReceivedAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
            measurement.Complete("success");
            return Results.Ok(new { accepted = true, serverId = auth.Server.Id });
        }
        catch (OperationCanceledException)
        {
            measurement.Complete("cancelled");
            throw;
        }
        catch
        {
            measurement.Complete("failure");
            throw;
        }
    }

    [HttpGet("routing")]
    [AllowAnonymous]
    public async Task<IResult> GetDesiredRouting([FromServices] SelfrunDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] DeploymentServiceSetService serviceSets, CancellationToken cancellationToken)
    {
        var auth = await runtimeTokens.AuthenticateAsync(Request.Headers.Authorization, cancellationToken);
        if (auth is null)
        {
            return Results.Unauthorized();
        }

        if (await db.Deployments.AnyAsync(d => d.AppId == auth.Server.AppId && d.OwnershipToken != null, cancellationToken))
        {
            return EndpointResults.Conflict("deployment_in_progress", "Routing reconciliation waits for the active deployment to finish.");
        }

        var configured = await LoadConfiguredRoutesAsync(db, auth.Server, serviceSets, cancellationToken);
        var bindings = configured.Bindings;
        var routes = bindings.Select(binding =>
            new RuntimeDesiredRouteResponse(
                binding.Id,
                binding.Hostname,
                "/",
                binding.ServiceName,
                configured.PublicServicePorts[binding.ServiceName]))
            .ToArray();
        var version = bindings.Count == 0
            ? DateTimeOffset.UnixEpoch.ToString("O")
            : bindings.Max(x => x.UpdatedAt).ToString("O");

        return Results.Ok(new RuntimeDesiredRoutingResponse(version, auth.Server.AppId, $"selfrun-{auth.Server.App.Slug}", routes));
    }

    private static async Task<RuntimeConfiguredRoutes> LoadConfiguredRoutesAsync(
        SelfrunDbContext db, RuntimeServer server, DeploymentServiceSetService serviceSets, CancellationToken cancellationToken)
    {
        var publicServicePorts = (await serviceSets.BuildActiveServicesAsync(db, server.AppId, cancellationToken))
            .Where(service => service.Public)
            .ToDictionary(service => service.Name, service => service.Port, StringComparer.Ordinal);
        var serviceNames = publicServicePorts.Keys.ToArray();
        var bindings = await db.Domains.AsNoTracking()
            .Where(x =>
                x.AppId == server.AppId &&
                (x.Kind == DomainBindingService.KindSelfrunSubdomain ||
                    (x.Kind == DomainBindingService.KindCustomDomain && x.DnsStatus == DomainBindingService.DnsVerified &&
                        db.ServerInstances.Any(s => s.AppId == server.AppId && s.OrganizationId == server.OrganizationId &&
                            s.ProviderAccountId == server.ProviderAccountId && s.Provider.ToLower() == server.Provider.ToLower() &&
                            s.ProviderServerId == server.ProviderServerId && s.IpAddress == x.TargetIp))) &&
                x.Status != DomainBindingService.StatusDisabled &&
                serviceNames.Contains(x.ServiceName))
            .OrderBy(x => x.Hostname)
            .ToListAsync(cancellationToken);
        return new RuntimeConfiguredRoutes(bindings, publicServicePorts);
    }

    private sealed record RuntimeConfiguredRoutes(
        IReadOnlyList<Domain> Bindings,
        IReadOnlyDictionary<string, int> PublicServicePorts);

    [HttpPost("routing/apply-results")]
    [AllowAnonymous]
    public async Task<IResult> IngestRoutingApplyResults([FromBody] RuntimeRoutingApplyResultsRequest request, [FromServices] SelfrunDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var measurement = RuntimeIngestionDiagnostics.Start("routing.results", Request.ContentLength ?? -1, request.Results.Count, cancellationToken);
        try
        {
            var auth = await runtimeTokens.AuthenticateAsync(Request.Headers.Authorization, cancellationToken);
            if (auth is null)
            {
                return Results.Unauthorized();
            }

            var ids = request.Results.Select(x => x.BindingId).ToHashSet(StringComparer.Ordinal);
            var bindings = await db.Domains
                .Where(x => x.AppId == auth.Server.AppId && ids.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, StringComparer.Ordinal, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var accepted = 0;

            foreach (var result in request.Results)
            {
                if (!bindings.TryGetValue(result.BindingId, out var binding) || binding.Status == DomainBindingService.StatusDisabled ||
                    !DateTimeOffset.TryParse(request.Version, out var requestedVersion) || binding.UpdatedAt > requestedVersion)
                {
                    continue;
                }

                binding.ApplyStatus = NormalizeApplyStatus(result.ApplyStatus);
                binding.SslStatus = NormalizeSslStatus(result.SslStatus);
                binding.FailureCode = result.FailureCode;
                binding.FailureMessage = result.FailureMessage;
                binding.LastAppliedAt = result.ObservedAt;
                binding.UpdatedAt = now;
                binding.Status = binding.ApplyStatus == DomainBindingService.ApplyFailed
                    ? DomainBindingService.StatusFailed
                    : binding.DnsStatus != DomainBindingService.DnsVerified
                        ? binding.Kind == DomainBindingService.KindCustomDomain ? DomainBindingService.StatusPendingDns : DomainBindingService.StatusPendingApply
                        : binding.SslStatus == DomainBindingService.SslReady
                            ? DomainBindingService.StatusActive
                            : DomainBindingService.StatusSslPending;
                accepted++;
            }

            await db.SaveChangesAsync(cancellationToken);
            measurement.Complete("success");
            return Results.Ok(new { accepted });
        }
        catch (OperationCanceledException)
        {
            measurement.Complete("cancelled");
            throw;
        }
        catch
        {
            measurement.Complete("failure");
            throw;
        }
    }

    [HttpPost("servers")]
    public async Task<IResult> CreateRuntimeServer([FromQuery] string appId, [FromBody] CreateRuntimeServerRequest request, [FromServices] SelfrunDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken, [FromQuery] string? applicationId = null, [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null) { return ownershipError; }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        var appRecord = await db.Apps.SingleOrDefaultAsync(x => x.Id == targetAppId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appRecord.Id, OrganizationAction.ManageRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", "Runtime server request is invalid.", [
                new("provider", "Provider is required.")
            ])));
        }

        var accountId = request.ProviderAccountId;
        if (appRecord.PlanId is not null)
        {
            var selectedPlan = await db.ProviderPlans.AsNoTracking().Include(x => x.ProviderAccount)
                .SingleOrDefaultAsync(x => x.Id == appRecord.PlanId && x.OrganizationId == appRecord.OrganizationId, cancellationToken);
            accountId ??= selectedPlan?.ProviderAccountId;
            if (selectedPlan is null || selectedPlan.ProviderAccountId != accountId ||
                !string.Equals(selectedPlan.ProviderAccount.ProviderType.ToString(), request.Provider, StringComparison.OrdinalIgnoreCase))
            { return EndpointResults.Conflict("runtime_account_conflict", "Runtime provider account does not match the app plan."); }
            if (string.IsNullOrWhiteSpace(request.ProviderServerId) || !await db.ServerInstances.AnyAsync(x =>
                x.AppId == targetAppId && x.OrganizationId == appRecord.OrganizationId && x.ProviderAccountId == accountId &&
                x.Provider.ToLower() == request.Provider.ToLower() && x.ProviderServerId == request.ProviderServerId, cancellationToken))
            { return EndpointResults.Conflict("runtime_server_conflict", "Register the exact managed server saved for this environment."); }
        }
        else if (accountId is not null)
        { return EndpointResults.Conflict("runtime_account_conflict", "Select an organization plan and register its managed server before using a provider account."); }
        if (accountId is not null)
        {
            var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId && x.OrganizationId == appRecord.OrganizationId, cancellationToken);
            if (account is null || !string.Equals(account.ProviderType.ToString(), request.Provider, StringComparison.OrdinalIgnoreCase))
            { return EndpointResults.Conflict("runtime_account_conflict", "Runtime provider account does not belong to this organization or provider type."); }
        }
        if (await db.ServerInstances.AnyAsync(x => x.ProviderAccountId == accountId && x.Provider.ToLower() == request.Provider.ToLower() &&
                x.ProviderServerId == request.ProviderServerId && (x.AppId != targetAppId || x.OrganizationId != appRecord.OrganizationId), cancellationToken) ||
            await db.RuntimeServers.AnyAsync(x => x.ProviderAccountId == accountId && x.Provider.ToLower() == request.Provider.ToLower() &&
                x.ProviderServerId == request.ProviderServerId && (x.AppId != targetAppId || x.OrganizationId != appRecord.OrganizationId), cancellationToken))
        { return EndpointResults.Conflict("runtime_server_conflict", "Runtime server is registered to another application."); }
        var now = timeProvider.GetUtcNow();
        var runtimeServer = await db.RuntimeServers
            .SingleOrDefaultAsync(x =>
                x.AppId == targetAppId &&
                x.ProviderAccountId == accountId &&
                x.Provider.ToLower() == request.Provider.ToLower() &&
                x.ProviderServerId == request.ProviderServerId,
                cancellationToken);

        if (runtimeServer is null)
        {
            runtimeServer = new RuntimeServer
            {
                Id = ids.NewId("rtsrv"),
                OrganizationId = appRecord.OrganizationId,
                AppId = appRecord.Id,
                ProviderAccountId = accountId,
                Provider = request.Provider.ToLowerInvariant(),
                ProviderServerId = request.ProviderServerId,
                Hostname = request.Hostname,
                AgentVersion = request.AgentVersion,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.RuntimeServers.Add(runtimeServer);
        }
        else
        {
            runtimeServer.Hostname = request.Hostname ?? runtimeServer.Hostname;
            runtimeServer.AgentVersion = request.AgentVersion ?? runtimeServer.AgentVersion;
            runtimeServer.UpdatedAt = now;
        }

        var token = runtimeTokens.Generate();
        db.RuntimeTokens.Add(new RuntimeToken
        {
            Id = ids.NewId("rttok"),
            RuntimeServer = runtimeServer,
            TokenPrefix = token.Prefix,
            TokenHash = runtimeTokens.Hash(token.Token),
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/runtime/servers/{runtimeServer.Id}?appId={Uri.EscapeDataString(targetAppId)}", new RuntimeServerCreateResponse(
            runtimeServer.Id,
            runtimeServer.OrganizationId,
            runtimeServer.AppId,
            token.Prefix,
            token.Token,
            runtimeServer.CreatedAt));
    }

    [HttpPost("servers/{serverId}/rotate-token")]
    public async Task<IResult> RotateRuntimeServerToken([FromQuery] string appId, [FromRoute] string serverId, [FromServices] SelfrunDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken, [FromQuery] string? applicationId = null, [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null) { return ownershipError; }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        var server = await db.RuntimeServers.SingleOrDefaultAsync(x => x.Id == serverId && x.AppId == targetAppId, cancellationToken);
        if (server is null)
        {
            return EndpointResults.NotFound("runtime_server_not_found", "Runtime server was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), server.AppId, OrganizationAction.ManageRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var now = timeProvider.GetUtcNow();
        var token = runtimeTokens.Generate();
        db.RuntimeTokens.Add(new RuntimeToken
        {
            Id = ids.NewId("rttok"),
            RuntimeServerId = server.Id,
            TokenPrefix = token.Prefix,
            TokenHash = runtimeTokens.Hash(token.Token),
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new RuntimeServerCreateResponse(server.Id, server.OrganizationId, server.AppId, token.Prefix, token.Token, server.CreatedAt));
    }

    [HttpGet("logs")]
    public async Task<IResult> GetLogs([FromQuery] string appId, [FromQuery] string? source, [FromQuery] string? service, [FromQuery] int? tail, [FromQuery] string? since, [FromServices] SelfrunDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken, [FromQuery] string? applicationId = null, [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null) { return ownershipError; }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), targetAppId, OrganizationAction.ReadRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var appRecord = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetAppId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        var cutoff = ParseSince(since, timeProvider.GetUtcNow());
        var take = Math.Clamp(tail ?? 200, 1, 1000);
        var query = db.RuntimeLogRecords.AsNoTracking().Where(x => x.AppId == targetAppId);
        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(x => x.Source == source);
        }
        if (!string.IsNullOrWhiteSpace(service))
        {
            query = query.Where(x => x.Service == service);
        }
        var rows = db.Database.IsSqlite()
            ? (await query.ToListAsync(cancellationToken))
                .Where(x => cutoff is null || x.ObservedAt >= cutoff)
                .OrderByDescending(x => x.ObservedAt)
                .Take(take)
                .ToList()
            : await query
                .Where(x => cutoff == null || x.ObservedAt >= cutoff)
                .OrderByDescending(x => x.ObservedAt)
                .Take(take)
                .ToListAsync(cancellationToken);

        return Results.Ok(rows
            .OrderBy(x => x.ObservedAt)
            .Select(ToRuntimeLogResponse)
            .ToArray());
    }

    [HttpGet("status")]
    public async Task<IResult> GetRuntimeStatus([FromQuery] string appId, [FromServices] SelfrunDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken, [FromQuery] string? applicationId = null, [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null) { return ownershipError; }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), targetAppId, OrganizationAction.ReadRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var appRecord = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetAppId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        var response = await LatestRuntimeStatusAsync(db, targetAppId, timeProvider.GetUtcNow(), cancellationToken);
        return response is null
            ? Results.Ok(new RuntimeStatusResponse("", null, null, false, false, null, [], null))
            : Results.Ok(response);
    }

    [HttpGet("log-sources")]
    public async Task<IResult> GetLogSources([FromQuery] string appId, [FromServices] SelfrunDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken, [FromQuery] string? applicationId = null, [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null) { return ownershipError; }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), targetAppId, OrganizationAction.ReadRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var appRecord = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetAppId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        var sourceRows = await db.RuntimeLogRecords
            .AsNoTracking()
            .Where(x => x.AppId == targetAppId)
            .GroupBy(x => new { x.Source, x.Service })
            .Select(x => new
            {
                x.Key.Source,
                x.Key.Service,
                LastObservedAt = x.Max(row => row.ObservedAt),
            })
            .OrderBy(x => x.Source)
            .ToListAsync(cancellationToken);
        var sources = sourceRows
            .Select(x => new RuntimeLogSourceResponse(x.Source, x.Service, x.LastObservedAt))
            .ToList();
        return Results.Ok(sources);
    }

    [HttpGet("diagnostics")]
    public async Task<IResult> GetRuntimeDiagnostics([FromQuery] string appId, [FromQuery] string? since, [FromServices] SelfrunDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken, [FromQuery] string? applicationId = null, [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null) { return ownershipError; }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), targetAppId, OrganizationAction.ReadRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var appRecord = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetAppId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        var cutoff = ParseSince(since, timeProvider.GetUtcNow()) ?? timeProvider.GetUtcNow().AddMinutes(-30);
        var diagnosticRows = await db.RuntimeLogRecords.AsNoTracking()
            .Where(x => x.AppId == targetAppId)
            .ToListAsync(cancellationToken);
        var caddy = diagnosticRows
            .Where(x => x.Source == "caddy" && x.ObservedAt >= cutoff && (x.Content.Contains("error") || x.Content.Contains("502")))
            .OrderByDescending(x => x.ObservedAt)
            .Take(20)
            .ToList();
        var services = diagnosticRows
            .Where(x => x.Source.StartsWith("service:", StringComparison.Ordinal) && x.ObservedAt >= cutoff && x.Stream == "stderr")
            .OrderByDescending(x => x.ObservedAt)
            .Take(50)
            .ToList();
        var status = await LatestRuntimeStatusAsync(db, targetAppId, timeProvider.GetUtcNow(), cancellationToken);
        var latestDeployments = await db.Deployments.AsNoTracking()
            .Where(x => x.AppId == targetAppId)
            .ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: true, take: 1, cancellationToken);
        var deploymentUrl = latestDeployments.FirstOrDefault()?.WebsiteUrl;

        return Results.Ok(new RuntimeDiagnosticBundleResponse(
            caddy.OrderBy(x => x.ObservedAt).Select(ToRuntimeLogResponse).ToArray(),
            services.OrderBy(x => x.ObservedAt).Select(ToRuntimeLogResponse).ToArray(),
            status,
            deploymentUrl));
    }

    static Selfrun.Common.Validation.ValidationResult ValidateRuntimeLogRequest(RuntimeLogIngestRequest request, DateTimeOffset now)
    {
        var validation = new Selfrun.Common.Validation.ValidationResult();
        if (request.Records.Count > RuntimeIngestionLimits.MaxBatchRecords)
        {
            validation.Add("records", $"At most {RuntimeIngestionLimits.MaxBatchRecords} log records can be ingested per batch.");
        }

        for (var i = 0; i < request.Records.Count; i++)
        {
            var record = request.Records[i];
            if (string.IsNullOrWhiteSpace(record.Source) || record.Source.Length > RuntimeIngestionLimits.MaxSourceLength)
            {
                validation.Add($"records[{i}].source", "Source is required and must fit within the source length limit.");
            }
            if (string.IsNullOrWhiteSpace(record.Cursor) || record.Cursor.Length > RuntimeIngestionLimits.MaxCursorLength)
            {
                validation.Add($"records[{i}].cursor", "Cursor is required and must fit within the cursor length limit.");
            }
            if (record.Message.Length > RuntimeIngestionLimits.MaxLineLength)
            {
                validation.Add($"records[{i}].message", $"Log lines must be {RuntimeIngestionLimits.MaxLineLength} characters or fewer.");
            }
            if (record.ObservedAt > now.Add(RuntimeIngestionLimits.MaxFutureSkew))
            {
                validation.Add($"records[{i}].observedAt", "Observed timestamp is too far in the future.");
            }
            if (!RuntimeSourceHasValidShape(record.Source, record.Service))
            {
                validation.Add($"records[{i}].source", "Source must be caddy, docker-compose, selfrun-agent, or service:<name>.");
            }
        }

        return validation;
    }

    static bool RuntimeSourceHasValidShape(string source, string? service)
    {
        if (source is "caddy" or "docker-compose" or "selfrun-agent")
        {
            return string.IsNullOrWhiteSpace(service);
        }

        if (!source.StartsWith("service:", StringComparison.Ordinal))
        {
            return false;
        }

        var serviceName = source["service:".Length..];
        return !string.IsNullOrWhiteSpace(serviceName) &&
            serviceName.All(character => char.IsLetterOrDigit(character) || character is '-' or '_') &&
            (string.IsNullOrWhiteSpace(service) || string.Equals(service, serviceName, StringComparison.Ordinal));
    }

    static bool RuntimeSourceAllowedForServices(string source, string? service, ISet<string> deployedServices)
    {
        if (!source.StartsWith("service:", StringComparison.Ordinal) || deployedServices.Count == 0)
        {
            return true;
        }

        var serviceName = service ?? source["service:".Length..];
        return deployedServices.Contains(serviceName);
    }

    static string NormalizeApplyStatus(string status) =>
        string.Equals(status, DomainBindingService.ApplyFailed, StringComparison.OrdinalIgnoreCase)
            ? DomainBindingService.ApplyFailed
            : DomainBindingService.ApplyApplied;

    static string NormalizeSslStatus(string status) =>
        string.Equals(status, DomainBindingService.SslReady, StringComparison.OrdinalIgnoreCase)
            ? DomainBindingService.SslReady
            : DomainBindingService.SslPending;

    static async Task<ISet<string>> LatestDeploymentServicesAsync(SelfrunDbContext db, string appId, CancellationToken cancellationToken)
    {
        var deployments = await db.Deployments
            .AsNoTracking()
            .Where(x => x.AppId == appId)
            .ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: true, take: 1, cancellationToken);
        var deploymentId = deployments.FirstOrDefault()?.Id;

        if (deploymentId is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var services = await db.DeploymentServices
            .AsNoTracking()
            .Where(x => x.DeploymentId == deploymentId)
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);

        return services.ToHashSet(StringComparer.Ordinal);
    }


    static DateTimeOffset? ParseSince(string? since, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(since))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(since, out var absolute))
        {
            return absolute;
        }

        var unit = since[^1];
        var numberText = since[..^1];
        return int.TryParse(numberText, out var value)
            ? unit switch
            {
                'm' => now.AddMinutes(-value),
                'h' => now.AddHours(-value),
                'd' => now.AddDays(-value),
                _ => null
            }
            : null;
    }

    static async Task PruneRuntimeLogsAsync(SelfrunDbContext db, string appId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now.Subtract(RuntimeIngestionLimits.LogRetention);
        if (db.Database.IsSqlite())
        {
            var expired = await db.RuntimeLogRecords
                .Where(x => x.AppId == appId)
                .ToListAsync(cancellationToken);
            db.RuntimeLogRecords.RemoveRange(expired.Where(x => x.ObservedAt < cutoff));
        }
        else
        {
            await db.RuntimeLogRecords
                .Where(x => x.AppId == appId && x.ObservedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }

        var retainedRows = await db.RuntimeLogRecords
            .Where(x => x.AppId == appId)
            .ToDateTimeOffsetOrderedListAsync(db, x => x.ObservedAt, x => x.ObservedAt, descending: true, take: null, cancellationToken);
        var excessIds = retainedRows
            .Skip(RuntimeIngestionLimits.PerAppRetentionRows)
            .Select(x => x.Id)
            .ToList();

        if (excessIds.Count > 0)
        {
            await db.RuntimeLogRecords.Where(x => excessIds.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    static RuntimeLogResponse ToRuntimeLogResponse(RuntimeLogRecord record) =>
        new(record.Id, record.RuntimeServerId, record.Source, record.Service, record.Stream, record.Content, record.ObservedAt, record.ReceivedAt);

    static async Task<RuntimeStatusResponse?> LatestRuntimeStatusAsync(SelfrunDbContext db, string appId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var servers = await db.RuntimeServers
            .AsNoTracking()
            .Where(x => x.AppId == appId)
            .ToListAsync(cancellationToken);
        var server = servers
            .OrderByDescending(x => x.LastSeenAt ?? x.CreatedAt)
            .FirstOrDefault();
        if (server is null)
        {
            return null;
        }

        var snapshots = await db.RuntimeStatusSnapshots
            .AsNoTracking()
            .Where(x => x.RuntimeServerId == server.Id)
            .ToListAsync(cancellationToken);
        var snapshot = snapshots
            .OrderByDescending(x => x.ObservedAt)
            .FirstOrDefault();
        var stored = snapshot is null
            ? null
            : JsonSerializer.Deserialize<StoredRuntimeStatus>(snapshot.StatusJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var online = server.LastSeenAt is not null && server.LastSeenAt > now.AddMinutes(-2);

        return new RuntimeStatusResponse(
            server.Id,
            server.AgentVersion ?? stored?.AgentVersion,
            server.LastSeenAt,
            AgentInstalled: true,
            AgentOnline: online,
            stored?.ObservedAt ?? snapshot?.ObservedAt,
            stored?.Services ?? [],
            stored?.Caddy);
    }

}
