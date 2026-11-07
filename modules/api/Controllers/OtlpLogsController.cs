using Microsoft.AspNetCore.Mvc;
using Minicloud.Common.Contracts;

namespace Minicloud.Api.Controllers;

/// <summary>
/// T35: Public OTLP ingestion on the Control API is retired.
/// Ingestion is served directly by the dedicated telemetry service at telemetry.cloud.muni.dev.
/// </summary>
[Route("otlp/v1/logs")]
public sealed class OtlpLogsController : ControllerBase
{
    [HttpPost]
    public IActionResult Export()
    {
        return StatusCode(StatusCodes.Status410Gone, new ErrorResponse(new ErrorBody(
            "endpoint_retired",
            "Public OTLP ingestion has moved to the dedicated telemetry service at telemetry.cloud.muni.dev.",
            [])));
    }
}
