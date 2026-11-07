using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using Minicloud.Common.Contracts;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1/apps/{appId}/storage")]
public sealed class AppStorageController : ControllerBase
{
    // Registration is not a live provider/mount health check. Include retained volumes.
    [HttpGet]
    public async Task<IResult> List(string appId, [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization, CancellationToken ct)
    {
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), appId, OrganizationAction.ReadApps, ct))
        { return EndpointResults.Forbidden(); }
        var volumes = await db.AppStorageVolumes.AsNoTracking().Where(x => x.AppId == appId).OrderBy(x => x.Name).ToListAsync(ct);
        return Results.Ok(volumes.Select(x => new AppStorageVolumeResponse(x.Name, "hetzner", x.ProviderVolumeId,
            x.Region, x.SizeGb, x.Uid, x.Gid,
            x.ProviderVolumeId is not null ? "registered" : x.CreationAttempted ? "creation_unresolved" : "pending")));
    }
}
