using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("v1/assets")]
public sealed class AssetsController : ControllerBase
{
    [HttpPost]
    public async Task<IResult> Publish(PublishAssetRequest request, [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization, [FromServices] AssetReleaseService releases, CancellationToken ct)
    {
        var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == request.AppId, ct);
        if (app is null) { return EndpointResults.NotFound("app_not_found", "App was not found."); }
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), app.Id, OrganizationAction.CreateDeployments, ct))
        { return EndpointResults.Forbidden(); }
        try
        {
            var (release, deployment) = await releases.PublishAsync(app, request, ct);
            return Results.Created($"/v1/assets/{release.Id}", AssetReleaseService.Response(release, deployment));
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "invalid_asset_release", message = ex.Message }); }
    }

    [HttpGet("{releaseId}")]
    public async Task<IResult> Get(string releaseId, [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization, CancellationToken ct)
    {
        var release = await db.AssetReleases.SingleOrDefaultAsync(x => x.Id == releaseId, ct);
        if (release is null) { return EndpointResults.NotFound("asset_not_found", "Asset release was not found."); }
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), release.AppId, OrganizationAction.ReadDeployments, ct))
        { return EndpointResults.Forbidden(); }
        var deployment = await db.Deployments.SingleOrDefaultAsync(x => x.AssetReleaseId == release.Id, ct);
        return Results.Ok(AssetReleaseService.Response(release, deployment));
    }
}
