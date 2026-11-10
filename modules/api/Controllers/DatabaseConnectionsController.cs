using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Minicloud.Api.Auth;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/databases/{id}/connection")]
public sealed class DatabaseConnectionsController(DatabaseConnectionService service) : ControllerBase
{
    [HttpPost("reveal")]
    public Task<IResult> Reveal(string id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
        return DatabasesController.Handle(async () => Results.Ok(await service.RevealAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), ct)));
    }
    [HttpPost("/api/databases/{id}/backup-recovery-material")]
    public Task<IResult> ExportRecoveryMaterial(string id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
        return DatabasesController.Handle(async () => Results.Ok(await service.ExportRecoveryMaterialAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), ct)));
    }
}
