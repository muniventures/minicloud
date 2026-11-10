using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/databases/{id}/bindings")]
public sealed class DatabaseBindingsController(MinicloudDbContext db, DatabaseManagementService management, DatabaseBindingService bindings) : ControllerBase
{
    [HttpGet]
    public Task<IResult> List(string id, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var database = await management.GetAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), false, ct);
        return Results.Ok(await db.ManagedDatabaseBindings.AsNoTracking().Where(x => x.DatabaseId == id && x.OrganizationId == database.OrganizationId).ToListAsync(ct));
    });

    [HttpPost]
    public Task<IResult> Bind(string id, DatabaseBindingRequest request, CancellationToken ct) => DatabasesController.Handle(async () =>
        Results.Accepted($"/api/databases/{id}/bindings", await bindings.BindAsync(id, request, RequirePrincipal(HttpContext).ToActorIdentity(), ct)));
    [HttpDelete("{bindingId}")]
    public Task<IResult> Unbind(string id, string bindingId, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        await bindings.UnbindAsync(id, bindingId, RequirePrincipal(HttpContext).ToActorIdentity(), ct);
        return Results.Accepted($"/api/databases/{id}/bindings");
    });
}
