using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;

namespace Minicloud.Api.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class HealthController : ControllerBase
{
    [HttpGet("ready")]
    public async Task<IResult> Ready([FromServices] MinicloudDbContext db, CancellationToken cancellationToken)
    {
        var canConnect = await db.Database.CanConnectAsync(cancellationToken);
        return canConnect ? Results.Ok(new { status = "ready" }) : Results.Problem("Database is not reachable.", statusCode: 503);
    }
}
