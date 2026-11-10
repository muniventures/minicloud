using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/database-repositories")]
public sealed class DatabaseRepositoriesController(MinicloudDbContext db, DatabaseManagementService service, ProviderCatalogService catalog, IIdGenerator ids, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public Task<IResult> List(string organizationId, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        await service.RequireAsync(RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, false, ct);
        return Results.Ok(await db.Apps.Where(x => x.OrganizationId == organizationId && x.EnvironmentKind == EnvironmentKinds.Internal && x.ApplicationId == null && x.Database == DatabaseMode.None && x.Slug.StartsWith("dbrepo-")).Select(x => new { x.Id, x.OrganizationId, x.Name, x.Slug, x.DeletionStatus }).ToListAsync(ct));
    });

    [HttpGet("{id}")]
    public Task<IResult> Get(string id, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
        await service.RequireAsync(RequirePrincipal(HttpContext).ToActorIdentity(), app.OrganizationId, false, ct);
        if (app.EnvironmentKind != EnvironmentKinds.Internal || app.ApplicationId is not null ||
            app.Database != DatabaseMode.None || !app.Slug.StartsWith("dbrepo-", StringComparison.Ordinal))
            throw new KeyNotFoundException();
        return Results.Ok(new { app.Id, app.OrganizationId, app.Name, app.Slug, app.DeletionStatus });
    });

    [HttpPost]
    public Task<IResult> Create(CreateDatabaseRepositoryRequest request, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var actor = RequirePrincipal(HttpContext).ToActorIdentity();
        await service.RequireAsync(actor, request.OrganizationId, true, ct);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 63 || request.SizeGb is < 10 or > 10000) { throw new ArgumentException("Repository name or size is invalid."); }
        var plan = await catalog.ResolveAsync(request.OrganizationId, request.Plan, ct);
        if (plan.ProviderAccount.ProviderType != Minicloud.Common.Providers.CloudProvider.Hetzner) { throw new ArgumentException("Managed repository volumes currently require a Hetzner plan."); }
        var appId = ids.NewId("app");
        var app = new MinicloudApp { Id = appId, OrganizationId = request.OrganizationId, Name = request.Name, Slug = "dbrepo-" + appId[4..], Database = DatabaseMode.None, PlanId = plan.Id, EnvironmentName = "production", EnvironmentKind = EnvironmentKinds.Internal, EnvironmentKey = appId + ":production", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        db.Apps.Add(app);
        var operation = service.Queue(app.OrganizationId, null, null, "create-repository", new { appId, sizeGb = request.SizeGb }, actor);
        await db.SaveChangesAsync(ct);
        return Results.Accepted("/api/database-repositories", new { app.Id, app.Name, OperationId = operation.Id, operation.Status });
    });

    [HttpDelete("{id}")]
    public Task<IResult> Delete(string id, DeleteDatabaseRepositoryRequest request, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var repositories = db.Apps.Where(x => x.Id == id);
            if (db.Database.IsNpgsql())
            {
                repositories = db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE id = {id} FOR UPDATE");
            }
            var app = await repositories.SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
            await db.Entry(app).ReloadAsync(ct);
            var actor = RequirePrincipal(HttpContext).ToActorIdentity();
            await service.RequireAsync(actor, app.OrganizationId, true, ct);
            if (app.EnvironmentKind != EnvironmentKinds.Internal || app.ApplicationId is not null ||
                app.Database != DatabaseMode.None || !app.Slug.StartsWith("dbrepo-", StringComparison.Ordinal))
                throw new KeyNotFoundException("Backup repository was not found.");
            if (!string.Equals(app.Name, request.Confirmation, StringComparison.Ordinal))
                throw new ArgumentException("Type the exact repository name to confirm deletion.");
            if (app.DeletionStatus == "queued") { return Results.Accepted(); }
            var blocker = await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, app.Id, ct);
            if (blocker is not null) { throw new InvalidOperationException(blocker); }
            if (await db.Deployments.AnyAsync(x => x.AppId == app.Id &&
                (x.OwnershipToken != null || (x.Status != DeploymentStatus.Succeeded && x.Status != DeploymentStatus.Failed && x.Status != DeploymentStatus.Canceled)), ct))
                throw new InvalidOperationException("Wait for active repository deployments before requesting deletion.");
            app.DeletionStatus = "queued";
            app.DeletionError = null;
            app.UpdatedAt = clock.GetUtcNow();
            db.AuditEvents.Add(new AuditEvent { Id = ids.NewId("audit"), OrganizationId = app.OrganizationId, AppId = app.Id,
                Actor = actor.Describe(), Action = "database.repository.delete_requested", Details = JsonSerializer.Serialize(new { repositoryId = app.Id }), CreatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Accepted();
        });
    });
}

public sealed record CreateDatabaseRepositoryRequest(string OrganizationId, string Name, string Plan, int SizeGb = 20);
public sealed record DeleteDatabaseRepositoryRequest(string Confirmation);
