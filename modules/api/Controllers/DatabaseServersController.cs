using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("v1/database-servers")]
public sealed class DatabaseServersController(MinicloudDbContext db, DatabaseManagementService service) : ControllerBase
{
    [HttpGet]
    public Task<IResult> List(string organizationId, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        await service.RequireAsync(RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, false, ct);
        return Results.Ok(await db.ManagedDatabaseServers.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.Status != "deleted").OrderBy(x => x.Name).ToListAsync(ct));
    });

    [HttpGet("{id}")]
    public Task<IResult> Get(string id, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var server = await db.ManagedDatabaseServers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        await service.RequireAsync(RequirePrincipal(HttpContext).ToActorIdentity(), server.OrganizationId, false, ct);
        return Results.Ok(server);
    });

    [HttpDelete("{id}")]
    public Task<IResult> Delete(string id, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var server = await db.ManagedDatabaseServers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        var actor = RequirePrincipal(HttpContext).ToActorIdentity();
        await service.RequireAsync(actor, server.OrganizationId, true, ct);
        if (await db.ManagedDatabases.AnyAsync(x => x.ServerId == id && x.Status != "deleted", ct) || await db.DatabaseOperations.AnyAsync(x => x.ServerId == id && (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling"), ct))
            throw new InvalidOperationException("Remove databases and finish active operations before deleting this server.");
        server.Revision++;
        server.Status = "deleting";
        var operation = service.Queue(server.OrganizationId, null, id, "delete-server", new { serverId = id }, actor);
        await db.SaveChangesAsync(ct);
        return Results.Accepted(null, new { operation.Id, operation.Status });
    });

    [HttpPost]
    public Task<IResult> Create(CreateDatabaseServerRequest request, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var server = await service.CreateServerAsync(request, RequirePrincipal(HttpContext).ToActorIdentity(), ct);
        return Results.Created($"/v1/database-servers/{server.Id}", server);
    });
}
