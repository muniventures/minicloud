using Microsoft.AspNetCore.Mvc;
using Minicloud.Telemetry.Services;

namespace Minicloud.Telemetry.Controllers;

[ApiController]
public sealed class HealthController(
    TelemetryAuthorityService authority,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("health")]
    public async Task<IResult> Health(CancellationToken cancellationToken)
    {
        var checkpoint = await authority.GetCheckpointAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var isFresh = checkpoint is not null && (now - checkpoint.ConfirmedAt) <= Minicloud.Common.Services.TelemetryLimits.MaxIngestionOutageAllowance;

        return Results.Ok(new
        {
            status = "healthy",
            authority = isFresh ? "fresh" : (checkpoint is null ? "uninitialized" : "stale"),
            checkpointSequence = checkpoint?.Sequence,
            checkpointAgeSeconds = checkpoint is not null ? (now - checkpoint.ConfirmedAt).TotalSeconds : (double?)null
        });
    }
}
