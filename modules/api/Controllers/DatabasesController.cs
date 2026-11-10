using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/databases")]
public sealed class DatabasesController(MinicloudDbContext db, DatabaseManagementService service) : ControllerBase
{
    [HttpGet]
    public Task<IResult> List(string organizationId, CancellationToken ct) => Handle(async () =>
    {
        await service.RequireAsync(RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, false, ct);
        return Results.Ok(await db.ManagedDatabases.AsNoTracking().Where(x => x.OrganizationId == organizationId && (x.Status != "deleted" || db.DatabaseBackupArtifacts.Any(b => b.DatabaseId == x.Id && b.OrganizationId == organizationId && b.Status == "complete") || db.DatabaseBackupPolicies.Any(p => p.DatabaseId == x.Id && p.OrganizationId == organizationId && p.EncryptedKeyMaterialJson != null))).OrderBy(x => x.Name).ToListAsync(ct));
    });

    [HttpGet("repositories")]
    public Task<IResult> Repositories(string organizationId, CancellationToken ct) => Handle(async () =>
    {
        await service.RequireAsync(RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, false, ct);
        return Results.Ok(await db.Apps.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.Database == Minicloud.Common.Models.DatabaseMode.None && x.Slug.StartsWith("dbrepo-") && x.DeletionStatus == null).Select(x => new { x.Id, x.Name }).ToListAsync(ct));
    });

    [HttpPost]
    public Task<IResult> Create(CreateManagedDatabaseRequest request, CancellationToken ct) => Handle(async () =>
    {
        var database = await service.CreateDatabaseAsync(request, RequirePrincipal(HttpContext).ToActorIdentity(), ct);
        return Results.Created($"/api/databases/{database.Id}", database);
    });

    [HttpGet("{id}")]
    public Task<IResult> Get(string id, CancellationToken ct) => Handle(async () => Results.Ok(await service.GetAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), false, ct)));

    [HttpGet("{id}/capabilities")]
    public Task<IResult> Capabilities(string id, CancellationToken ct) => Handle(async () =>
    {
        var database = await service.GetAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), false, ct);
        var appId = database.AppId ?? database.RecoverySourceAppId;
        if (appId is null && database.ServerId is not null)
        { appId = await db.ManagedDatabaseServers.Where(x => x.Id == database.ServerId && x.OrganizationId == database.OrganizationId).Select(x => x.RuntimeAppId).SingleOrDefaultAsync(ct); }
        var host = await db.ServerInstances.AsNoTracking().SingleOrDefaultAsync(x => x.AppId == appId && x.OrganizationId == database.OrganizationId, ct);
        var native = database.Engine == "postgres" && string.Equals(host?.Provider, "hetzner", StringComparison.OrdinalIgnoreCase) && host?.ProviderAccountId is not null;
        var zone = native ? await db.ManagedDatabaseNetworks.Where(x => x.OrganizationId == database.OrganizationId && x.ProviderAccountId == host!.ProviderAccountId && x.Region == host!.Region && x.Status == "ready").Select(x => x.NetworkZone).FirstOrDefaultAsync(ct) : null;
        return Results.Ok(new { accessModes = database.Engine == "sqlite" ? Array.Empty<string>() : native ? new[] { "internal", "public", "private" } : new[] { "internal", "public" }, providerAccountId = host?.ProviderAccountId, region = host?.Region, networkZone = zone });
    });

    [HttpGet("{id}/backup-policy")]
    public Task<IResult> Policy(string id, CancellationToken ct) => Handle(async () =>
    {
        await service.GetAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), false, ct);
        return Results.Ok(await db.DatabaseBackupPolicies.AsNoTracking().SingleAsync(x => x.DatabaseId == id, ct));
    });

    [HttpPut("{id}/backup-policy")]
    public Task<IResult> SetPolicy(string id, BackupPolicyRequest request, CancellationToken ct) => Handle(async () => Results.Ok(await service.SetPolicyAsync(id, request, RequirePrincipal(HttpContext).ToActorIdentity(), ct)));

    [HttpGet("{id}/backups")]
    public Task<IResult> Backups(string id, CancellationToken ct) => Handle(async () =>
    {
        await service.GetAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), false, ct);
        return Results.Ok(await db.DatabaseBackupArtifacts.AsNoTracking().Where(x => x.DatabaseId == id).ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: true, take: 200, ct));
    });

    [HttpGet("{id}/operations")]
    public Task<IResult> Operations(string id, CancellationToken ct) => Handle(async () =>
    {
        await service.GetAsync(id, RequirePrincipal(HttpContext).ToActorIdentity(), false, ct);
        var operations = await db.DatabaseOperations.AsNoTracking().Where(x => x.DatabaseId == id).ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: true, take: 100, ct);
        return Results.Ok(operations.Select(x => new { x.Id, x.Kind, x.Status, x.ErrorCode, x.ErrorMessage, x.CreatedAt, x.CompletedAt }));
    });

    [HttpPost("{id}/operations")]
    public Task<IResult> Operate(string id, DatabaseOperationRequest request, CancellationToken ct) => Handle(async () =>
    {
        var operation = await service.OperateAsync(id, request, RequirePrincipal(HttpContext).ToActorIdentity(), ct);
        return Results.Accepted($"/api/databases/{id}/operations", new { operation.Id, operation.Kind, operation.Status });
    });

    internal static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException) { return EndpointResults.Forbidden(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "invalid_database_request", message = ex.Message }); }
        catch (ProviderCatalogException ex) { return Results.BadRequest(new { code = ex.Code, message = ex.Message }); }
        catch (DbUpdateException) { return EndpointResults.Conflict("database_conflict", "The database/server name, binding or operation changed. Refresh and retry."); }
        catch (InvalidOperationException ex) { return EndpointResults.Conflict("database_unavailable", ex.Message); }
    }
}
