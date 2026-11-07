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
using Minicloud.Common.Redaction;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1/[controller]")]
public sealed class RuntimeController : ControllerBase
{
    [HttpPost("heartbeat")]
    [AllowAnonymous]
    public async Task<IResult> Heartbeat([FromBody] RuntimeHeartbeatRequest request, [FromServices] MinicloudDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
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
    public async Task<IResult> IngestLogs([FromBody] RuntimeLogIngestRequest request, [FromServices] MinicloudDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
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
        var accepted = 0;
        var rejected = 0;
        var cursors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var record in request.Records)
        {
            if (!RuntimeSourceAllowedForServices(record.Source, record.Service, deployedServices))
            {
                rejected++;
                continue;
            }

            var exists = await db.RuntimeLogRecords.AnyAsync(x =>
                x.RuntimeServerId == auth.Server.Id &&
                x.Source == record.Source &&
                x.Cursor == record.Cursor &&
                x.ObservedAt == record.ObservedAt,
                cancellationToken);
            if (exists)
            {
                cursors[record.Source] = record.Cursor;
                continue;
            }

            db.RuntimeLogRecords.Add(new RuntimeLogRecord
            {
                Id = ids.NewId("rtlog"),
                OrganizationId = auth.Server.OrganizationId,
                AppId = auth.Server.AppId,
                RuntimeServerId = auth.Server.Id,
                Source = record.Source,
                Service = record.Service,
                Stream = NormalizeStream(record.Stream),
                Cursor = record.Cursor,
                Content = LogRedactor.Redact(record.Message),
                ObservedAt = record.ObservedAt,
                ReceivedAt = now
            });
            accepted++;
            cursors[record.Source] = record.Cursor;
        }

        await PruneRuntimeLogsAsync(db, auth.Server.AppId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new RuntimeLogIngestResponse(accepted, rejected, cursors));
    }

    [HttpPost("ingest/status")]
    [AllowAnonymous]
    public async Task<IResult> IngestStatus([FromBody] RuntimeStatusIngestRequest request, [FromServices] MinicloudDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
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
        auth.Server.AgentVersion = request.AgentVersion ?? auth.Server.AgentVersion;
        auth.Server.LastSeenAt = now;
        auth.Server.UpdatedAt = now;
        var stored = new StoredRuntimeStatus(request.AgentVersion, request.ObservedAt, request.ComposeProject, request.Services, request.Caddy);
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
        return Results.Ok(new { accepted = true, serverId = auth.Server.Id });
    }

    [HttpGet("routing")]
    [AllowAnonymous]
    public async Task<IResult> GetDesiredRouting([FromServices] MinicloudDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] DeploymentServiceSetService serviceSets, CancellationToken cancellationToken)
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

        var services = await serviceSets.BuildActiveServicesAsync(db, auth.Server.AppId, cancellationToken);
        var serviceByName = services
            .Where(x => x.Public)
            .ToDictionary(x => x.Name, StringComparer.Ordinal);
        var serviceNames = serviceByName.Keys.ToArray();
        var bindings = await db.Domains.AsNoTracking()
            .Where(x =>
                x.AppId == auth.Server.AppId &&
                (x.Kind == DomainBindingService.KindMinicloudSubdomain ||
                    (x.Kind == DomainBindingService.KindCustomDomain && x.DnsStatus == DomainBindingService.DnsVerified &&
                        db.ServerInstances.Any(s => s.AppId == auth.Server.AppId && s.OrganizationId == auth.Server.OrganizationId &&
                            s.Provider == auth.Server.Provider && s.ProviderServerId == auth.Server.ProviderServerId && s.IpAddress == x.TargetIp))) &&
                x.Status != DomainBindingService.StatusDisabled &&
                serviceNames.Contains(x.ServiceName))
            .OrderBy(x => x.Hostname)
            .ToListAsync(cancellationToken);
        var routes = bindings.Select(binding =>
            new RuntimeDesiredRouteResponse(
                binding.Id,
                binding.Hostname,
                "/",
                binding.ServiceName,
                serviceByName[binding.ServiceName].Port))
            .ToArray();
        var version = bindings.Count == 0
            ? DateTimeOffset.UnixEpoch.ToString("O")
            : bindings.Max(x => x.UpdatedAt).ToString("O");

        return Results.Ok(new RuntimeDesiredRoutingResponse(version, auth.Server.AppId, $"minicloud-{auth.Server.App.Slug}", routes));
    }

    [HttpPost("routing/apply-results")]
    [AllowAnonymous]
    public async Task<IResult> IngestRoutingApplyResults([FromBody] RuntimeRoutingApplyResultsRequest request, [FromServices] MinicloudDbContext db, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
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
        return Results.Ok(new { accepted });
    }

    [HttpPost("servers")]
    public async Task<IResult> CreateRuntimeServer([FromQuery] string appId, [FromBody] CreateRuntimeServerRequest request, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var appRecord = await db.Apps.SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
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

        var now = timeProvider.GetUtcNow();
        var runtimeServer = await db.RuntimeServers
            .SingleOrDefaultAsync(x =>
                x.AppId == appId &&
                x.Provider == request.Provider &&
                x.ProviderServerId == request.ProviderServerId,
                cancellationToken);

        if (runtimeServer is null)
        {
            runtimeServer = new RuntimeServer
            {
                Id = ids.NewId("rtsrv"),
                OrganizationId = appRecord.OrganizationId,
                AppId = appRecord.Id,
                Provider = request.Provider,
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
        return Results.Created($"/v1/runtime/servers/{runtimeServer.Id}?appId={Uri.EscapeDataString(appId)}", new RuntimeServerCreateResponse(
            runtimeServer.Id,
            runtimeServer.OrganizationId,
            runtimeServer.AppId,
            token.Prefix,
            token.Token,
            runtimeServer.CreatedAt));
    }

    [HttpPost("servers/{serverId}/rotate-token")]
    public async Task<IResult> RotateRuntimeServerToken([FromQuery] string appId, [FromRoute] string serverId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IRuntimeTokenService runtimeTokens, [FromServices] IIdGenerator ids, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var server = await db.RuntimeServers.SingleOrDefaultAsync(x => x.Id == serverId && x.AppId == appId, cancellationToken);
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
    public async Task<IResult> GetLogs([FromQuery] string appId, [FromQuery] string? source, [FromQuery] string? service, [FromQuery] int? tail, [FromQuery] string? since, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.ReadRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var appRecord = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        var cutoff = ParseSince(since, timeProvider.GetUtcNow());
        var take = Math.Clamp(tail ?? 200, 1, 1000);
        var query = db.RuntimeLogRecords.AsNoTracking().Where(x => x.AppId == appId);
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
    public async Task<IResult> GetRuntimeStatus([FromQuery] string appId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.ReadRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var appRecord = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        var response = await LatestRuntimeStatusAsync(db, appId, timeProvider.GetUtcNow(), cancellationToken);
        return response is null
            ? Results.Ok(new RuntimeStatusResponse("", null, null, false, false, null, [], null))
            : Results.Ok(response);
    }

    [HttpGet("log-sources")]
    public async Task<IResult> GetLogSources([FromQuery] string appId, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.ReadRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var appRecord = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        var sourceRows = await db.RuntimeLogRecords
            .AsNoTracking()
            .Where(x => x.AppId == appId)
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
    public async Task<IResult> GetRuntimeDiagnostics([FromQuery] string appId, [FromQuery] string? since, [FromServices] MinicloudDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.ReadRuntime, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var appRecord = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (appRecord is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        var cutoff = ParseSince(since, timeProvider.GetUtcNow()) ?? timeProvider.GetUtcNow().AddMinutes(-30);
        var diagnosticRows = await db.RuntimeLogRecords.AsNoTracking()
            .Where(x => x.AppId == appId)
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
        var status = await LatestRuntimeStatusAsync(db, appId, timeProvider.GetUtcNow(), cancellationToken);
        var latestDeployments = await db.Deployments.AsNoTracking()
            .Where(x => x.AppId == appId)
            .ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: true, take: 1, cancellationToken);
        var deploymentUrl = latestDeployments.FirstOrDefault()?.WebsiteUrl;

        return Results.Ok(new RuntimeDiagnosticBundleResponse(
            caddy.OrderBy(x => x.ObservedAt).Select(ToRuntimeLogResponse).ToArray(),
            services.OrderBy(x => x.ObservedAt).Select(ToRuntimeLogResponse).ToArray(),
            status,
            deploymentUrl));
    }

    static Minicloud.Common.Validation.ValidationResult ValidateRuntimeLogRequest(RuntimeLogIngestRequest request, DateTimeOffset now)
    {
        var validation = new Minicloud.Common.Validation.ValidationResult();
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
                validation.Add($"records[{i}].source", "Source must be caddy, docker-compose, minicloud-agent, or service:<name>.");
            }
        }

        return validation;
    }

    static bool RuntimeSourceHasValidShape(string source, string? service)
    {
        if (source is "caddy" or "docker-compose" or "minicloud-agent")
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

    static string NormalizeStream(string stream) =>
        string.Equals(stream, "stderr", StringComparison.OrdinalIgnoreCase) ? "stderr" : "stdout";

    static string NormalizeApplyStatus(string status) =>
        string.Equals(status, DomainBindingService.ApplyFailed, StringComparison.OrdinalIgnoreCase)
            ? DomainBindingService.ApplyFailed
            : DomainBindingService.ApplyApplied;

    static string NormalizeSslStatus(string status) =>
        string.Equals(status, DomainBindingService.SslReady, StringComparison.OrdinalIgnoreCase)
            ? DomainBindingService.SslReady
            : DomainBindingService.SslPending;

    static async Task<ISet<string>> LatestDeploymentServicesAsync(MinicloudDbContext db, string appId, CancellationToken cancellationToken)
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

    static async Task PruneRuntimeLogsAsync(MinicloudDbContext db, string appId, DateTimeOffset now, CancellationToken cancellationToken)
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

    static async Task<RuntimeStatusResponse?> LatestRuntimeStatusAsync(MinicloudDbContext db, string appId, DateTimeOffset now, CancellationToken cancellationToken)
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
