using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Api.Auth;

public enum MaintenanceAdmissionClass
{
    /// <summary>Not a management write (reads, auth/session flows, runtime agent ingest, health).</summary>
    Exempt,
    /// <summary>Completes or cancels already-accepted work; allowed while fenced but not on a revoked authority.</summary>
    Continuation,
    /// <summary>Originates new management state or work; refused while fenced.</summary>
    Governed
}

/// <summary>Classifies controller actions for maintenance admission.</summary>
public static class MaintenanceAdmissionPolicy
{
    private static readonly HashSet<string> ExemptControllers = new(StringComparer.Ordinal)
    {
        "Account", "AccountFlows", "CliLoginSessions", "GrafanaSession", "GrafanaProxy", "GrafanaQueryGateway",
        "OtlpLogs", "Health", "Instance", "DatabaseConnections", "DatabaseBackupDownloads", "TelemetryLogs", "Me"
    };

    private static readonly HashSet<(string Controller, string Action)> ExemptActions =
    [
        ("Runtime", "Heartbeat"), ("Runtime", "IngestLogs"), ("Runtime", "IngestStatus"), ("Runtime", "IngestRoutingApplyResults"),
        ("Deployments", "ValidateDeploymentSecrets"), ("Deployments", "RefreshDeployment"), ("Deployments", "ResolveDeploymentSecrets"),
        ("Telemetry", "IssueReadToken")
    ];

    private static readonly HashSet<(string Controller, string Action)> ContinuationActions =
    [
        ("Deployments", "CancelDeployment"),
        ("ImagePublications", "Credentials"), ("ImagePublications", "Complete"), ("ImagePublications", "Cancel"),
        ("RegistryGateway", "Gateway")
    ];

    private static readonly HashSet<string> ContinuationControllers = new(StringComparer.Ordinal)
    {
        "DatabaseOperationCancellation"
    };

    public static MaintenanceAdmissionClass Classify(string method, Endpoint? endpoint)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
            return MaintenanceAdmissionClass.Exempt;
        var action = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action is null)
            // Non-controller endpoints are health checks / OpenAPI; unrouted requests fall through to 404.
            return MaintenanceAdmissionClass.Exempt;
        return Classify(action.ControllerName, action.ActionName);
    }

    public static MaintenanceAdmissionClass Classify(string controller, string action)
    {
        if (ExemptControllers.Contains(controller) || ExemptActions.Contains((controller, action)))
            return MaintenanceAdmissionClass.Exempt;
        if (ContinuationControllers.Contains(controller) || ContinuationActions.Contains((controller, action)))
            return MaintenanceAdmissionClass.Continuation;
        return MaintenanceAdmissionClass.Governed;
    }
}

/// <summary>
/// Admits management writes against the durable maintenance authority. A governed write records a
/// durable activity in the same transaction that observes the authority as open, and holds it until
/// the response completes, so fence + drain status never miss a write admitted before the fence.
/// </summary>
public sealed class MaintenanceAdmissionMiddleware(RequestDelegate next, MaintenanceParticipant participant, ILogger<MaintenanceAdmissionMiddleware> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var admission = MaintenanceAdmissionPolicy.Classify(context.Request.Method, endpoint);
        if (admission == MaintenanceAdmissionClass.Exempt)
        {
            await next(context);
            return;
        }
        var action = endpoint!.Metadata.GetMetadata<ControllerActionDescriptor>()!;
        MaintenanceAdmissionDecision decision;
        MaintenanceActivityLease? lease;
        try
        {
            (decision, lease) = await participant.TryBeginActivityAsync(
                "api_write", $"{context.Request.Method} {action.ControllerName}.{action.ActionName}",
                requireOpen: admission == MaintenanceAdmissionClass.Governed, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Maintenance admission check failed ({FailureType}); refusing the write.", ex.GetType().Name);
            await RefuseAsync(context, "maintenance_admission_unavailable", "Write admission is temporarily unavailable. Retry later.", 5);
            return;
        }
        if (!decision.IsAdmitted)
        {
            await RefuseAsync(context, decision.Code, decision.Message,
                decision.Outcome == MaintenanceAdmissionOutcome.Fenced ? 30 : decision.IsRevoked ? null : 5);
            return;
        }
        await using (lease)
        {
            await next(context);
        }
    }

    private static async Task RefuseAsync(HttpContext context, string code, string message, int? retryAfterSeconds)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        if (retryAfterSeconds is { } seconds)
            context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await JsonSerializer.SerializeAsync(context.Response.Body, new ErrorResponse(new ErrorBody(code, message, [])), Json, context.RequestAborted);
    }
}
