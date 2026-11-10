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

[ApiController, Authorize, Route("v1/database-repositories")]
public sealed class DatabaseRepositoriesController(MinicloudDbContext db, DatabaseManagementService service, ProviderCatalogService catalog, IIdGenerator ids, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public Task<IResult> List(string organizationId, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        await service.RequireAsync(RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, false, ct);
        return Results.Ok(await db.Apps.Where(x => x.OrganizationId == organizationId && x.DeletionStatus == null && x.Slug.StartsWith("dbrepo-")).Select(x => new { x.Id, x.Name }).ToListAsync(ct));
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
        var app = new MinicloudApp { Id = appId, OrganizationId = request.OrganizationId, Name = request.Name, Slug = "dbrepo-" + appId[4..], Database = DatabaseMode.None, PlanId = plan.Id, EnvironmentName = "production", EnvironmentKey = appId + ":production", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        db.Apps.Add(app);
        var operation = service.Queue(app.OrganizationId, null, null, "create-repository", new { appId, sizeGb = request.SizeGb }, actor);
        await db.SaveChangesAsync(ct);
        return Results.Accepted("/v1/database-repositories", new { app.Id, app.Name, OperationId = operation.Id, operation.Status });
    });
}

public sealed record CreateDatabaseRepositoryRequest(string OrganizationId, string Name, string Plan, int SizeGb = 20);
