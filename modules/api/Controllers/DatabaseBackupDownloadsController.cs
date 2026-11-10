using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/databases/{id}/backups/{artifactId}/files/{fileName}")]
public sealed class DatabaseBackupDownloadsController(DatabaseBackupDownloadService downloads) : ControllerBase
{
    [HttpGet]
    public Task<IResult> Download(string id, string artifactId, string fileName, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return DatabasesController.Handle(async () =>
        {
            var download = await downloads.PrepareAsync(id, artifactId, fileName, RequirePrincipal(HttpContext).ToActorIdentity(), ct);
            Response.RegisterForDispose(download);
            return Results.Stream(stream => download.CopyToAsync(stream, HttpContext.RequestAborted), "application/octet-stream", download.Filename);
        });
    }
}
