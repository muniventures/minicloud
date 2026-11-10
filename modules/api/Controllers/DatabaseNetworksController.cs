using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/database-networks")]
public sealed class DatabaseNetworksController(MinicloudDbContext db, DatabaseManagementService databases, DatabaseNetworkService networks, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public Task<IResult> List(string organizationId, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        await databases.RequireAsync(RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, false, ct);
        return Results.Ok(await db.ManagedDatabaseNetworks.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.Status != "deleted").OrderBy(x => x.Name).ToListAsync(ct));
    });

    [HttpPost]
    public Task<IResult> Create(CreateDatabaseNetworkRequest request, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var network = await networks.CreateAsync(request, RequirePrincipal(HttpContext).ToActorIdentity(), ct);
        return Results.Accepted($"/api/database-networks/{network.Id}", network);
    });

    [HttpGet("{id}")]
    public Task<IResult> Get(string id, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var network = await db.ManagedDatabaseNetworks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        await databases.RequireAsync(RequirePrincipal(HttpContext).ToActorIdentity(), network.OrganizationId, false, ct);
        return Results.Ok(network);
    });

    [HttpDelete("{id}")]
    public Task<IResult> Delete(string id, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var network = await db.ManagedDatabaseNetworks.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        var actor = RequirePrincipal(HttpContext).ToActorIdentity();
        await databases.RequireAsync(actor, network.OrganizationId, true, ct);
        if (network.Status == "deleted") { return Results.NoContent(); }
        if (await db.DatabaseOperations.AnyAsync(x =>
                (x.Kind == "create-network" || x.Kind == "delete-network") && x.RequestJson.Contains(network.Id) &&
                (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling"), ct))
        { throw new InvalidOperationException("Wait for active network operations before requesting deletion."); }
        if (await db.ManagedDatabases.AnyAsync(x => x.NetworkId == id && x.Status != "deleted", ct))
        { throw new InvalidOperationException("Remove database access references before deleting this network."); }
        network.Status = "deleting";
        network.Revision++;
        network.UpdatedAt = clock.GetUtcNow();
        var operation = databases.Queue(network.OrganizationId, null, null, "delete-network", new { networkId = network.Id }, actor);
        await db.SaveChangesAsync(ct);
        return Results.Accepted(null, new { operation.Id, operation.Status });
    });
}
