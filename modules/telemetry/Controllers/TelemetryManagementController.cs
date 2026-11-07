using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using Minicloud.Telemetry.Services;

namespace Minicloud.Telemetry.Controllers;

[ApiController]
[Route("internal/v1/telemetry")]
public sealed class TelemetryManagementController(
    TelemetryAuthorityService authority,
    TelemetryStatusReporter reporter,
    TelemetryCleanupService cleanup,
    GrafanaSessionService sessions,
    TimeProvider timeProvider,
    IOptions<TelemetryOptions> options,
    ILogger<TelemetryManagementController> logger) : ControllerBase
{
    private const string ManagementKeyHeader = "X-Telemetry-Management-Key";

    private bool IsAuthorized()
    {
        var provided = Request.Headers[ManagementKeyHeader].ToString();
        var expected = options.Value.ManagementApiKey;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        return string.Equals(provided, expected, StringComparison.Ordinal);
    }

    [HttpPost("sync")]
    public async Task<IResult> Sync(
        [FromBody] TelemetrySyncBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
        {
            return Results.Unauthorized();
        }

        var response = await authority.ApplySyncBatchAsync(request, cancellationToken);
        if (!response.Success)
        {
            return Results.BadRequest(response);
        }

        // Trigger cleanups if any tombstones were included
        if (request.Tombstones.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await cleanup.ProcessPendingCleanupsAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Background cleanup processing failed after sync.");
                }
            }, CancellationToken.None);
        }

        return Results.Ok(response);
    }

    [HttpGet("status")]
    public IResult Status()
    {
        if (!IsAuthorized())
        {
            return Results.Unauthorized();
        }

        var report = reporter.SnapshotAndClear(timeProvider);
        return Results.Ok(report);
    }

    [HttpPost("launch-code")]
    public async Task<IResult> IssueLaunchCode(
        [FromBody] TelemetryLaunchCodeIssueRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
        {
            return Results.Unauthorized();
        }

        var response = await sessions.IssueLaunchCodeAsync(request, cancellationToken);
        return Results.Ok(response);
    }
}
