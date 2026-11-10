using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("v1/databases/{id}/operations/{operationId}/cancel")]
public sealed class DatabaseOperationCancellationController(MinicloudDbContext db, DatabaseManagementService service, AuditEventService audit, TimeProvider clock) : ControllerBase
{
    [HttpPost]
    public Task<IResult> Cancel(string id, string operationId, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var actor = RequirePrincipal(HttpContext).ToActorIdentity();
        var database = await service.GetAsync(id, actor, true, ct);
        var operation = await db.DatabaseOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId && x.DatabaseId == id && x.OrganizationId == database.OrganizationId, ct)
            ?? throw new KeyNotFoundException();
        if (operation.Kind != "backup") { throw new InvalidOperationException("Only backup capture supports cancellation; database mutations must finish or be explicitly reconciled."); }
        var now = clock.GetUtcNow();
        await db.DatabaseOperations.Where(x => x.Id == operationId && x.Status == "queued").ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.CompletedAt, now).SetProperty(x => x.UpdatedAt, now), ct);
        await db.DatabaseOperations.Where(x => x.Id == operationId && x.Status == "running").ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "cancelling").SetProperty(x => x.UpdatedAt, now), ct);
        await audit.AppendAsync(database.OrganizationId, database.AppId, actor.Describe(), "database.backup.cancellation_requested", System.Text.Json.JsonSerializer.Serialize(new { operationId, databaseId = id }), ct);
        return Results.Accepted($"/v1/databases/{id}/operations", new { operationId, cancellationRequested = true });
    });
}
