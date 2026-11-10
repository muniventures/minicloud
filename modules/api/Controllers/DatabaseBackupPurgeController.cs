using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("v1/databases/{id}/backups")]
public sealed class DatabaseBackupPurgeController(DatabaseBackupPurgeService purge) : ControllerBase
{
    [HttpDelete]
    public Task<IResult> Purge(string id, [FromBody] DatabaseBackupPurgeRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return DatabasesController.Handle(async () =>
        {
            var operation = await purge.QueueAsync(id, request, RequirePrincipal(HttpContext).ToActorIdentity(), ct);
            return Results.Accepted($"/v1/databases/{id}/operations", new { operation.Id, operation.Kind, operation.Status });
        });
    }
}
