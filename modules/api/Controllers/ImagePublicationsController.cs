using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Registry;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/environments/{appId}")]
public sealed class ImagePublicationsController(MinicloudDbContext db, ControlPlaneAuthorization authorization, ImagePublicationService publications, ImageBuildTargetService targets, TimeProvider clock) : ControllerBase
{
    [HttpGet("image-build-target")]
    public Task<IResult> Target(string appId, CancellationToken ct) => Handle(async () =>
    {
        var (app, error) = await App(appId, OrganizationAction.CreateDeployments, ct);
        if (error is not null) { return error; }
        publications.RequireEnabled();
        return Results.Ok(await targets.ResolveAsync(app!, ct));
    });

    [HttpPost("image-publications")]
    public Task<IResult> Create(string appId, CreateImagePublicationRequest request, CancellationToken ct) => Handle(async () =>
    {
        var (app, error) = await App(appId, OrganizationAction.CreateDeployments, ct);
        if (error is not null) { return error; }
        var publication = await publications.CreateAsync(app!, request, EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), ct);
        return Results.Created($"/api/environments/{appId}/image-publications/{publication.Id}", ImagePublicationService.Response(publication));
    });

    [HttpGet("image-publications/{id}")]
    public Task<IResult> Read(string appId, string id, CancellationToken ct) => Handle(async () =>
    {
        var (_, error) = await App(appId, OrganizationAction.ReadDeployments, ct);
        if (error is not null) { return error; }
        var publication = await db.ImagePublications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.AppId == appId, ct);
        return publication is null ? EndpointResults.NotFound("image_publication_not_found", "Image publication was not found.") : Results.Ok(ImagePublicationService.Response(publication));
    });

    [HttpPost("image-publications/{id}/credentials")]
    public Task<IResult> Credentials(string appId, string id, CancellationToken ct) => Handle(async () =>
    {
        var (_, error) = await App(appId, OrganizationAction.CreateDeployments, ct);
        if (error is not null) { return error; }
        var publication = await db.ImagePublications.SingleOrDefaultAsync(x => x.Id == id && x.AppId == appId, ct);
        if (publication is null) { return EndpointResults.NotFound("image_publication_not_found", "Image publication was not found."); }
        Response.Headers.CacheControl = "no-store";
        return Results.Ok(await publications.CredentialsAsync(publication, EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), ct));
    });

    [HttpPost("image-publications/{id}/complete")]
    public Task<IResult> Complete(string appId, string id, CompleteImagePublicationRequest request, CancellationToken ct) => Handle(async () =>
    {
        var (_, error) = await App(appId, OrganizationAction.CreateDeployments, ct);
        if (error is not null) { return error; }
        var publication = await db.ImagePublications.SingleOrDefaultAsync(x => x.Id == id && x.AppId == appId, ct);
        if (publication is null) { return EndpointResults.NotFound("image_publication_not_found", "Image publication was not found."); }
        return Results.Ok(ImagePublicationService.Response(await publications.CompleteAsync(publication, request.ManifestDigest, EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), ct)));
    });

    [HttpDelete("image-publications/{id}")]
    public Task<IResult> Cancel(string appId, string id, CancellationToken ct) => Handle(async () =>
    {
        var (_, error) = await App(appId, OrganizationAction.CreateDeployments, ct);
        if (error is not null) { return error; }
        return await publications.LockedAsync<IResult>(async () =>
        {
            var publication = await db.ImagePublications.SingleOrDefaultAsync(x => x.Id == id && x.AppId == appId, ct);
            if (publication is null) { return EndpointResults.NotFound("image_publication_not_found", "Image publication was not found."); }
            if (await db.DeploymentServices.AnyAsync(x => x.ImagePublicationId == id, ct))
            { return EndpointResults.Conflict("image_publication_in_use", "Image publication is referenced by a deployment."); }
            publication.Status = ImagePublicationStatus.Canceled;
            publication.UpdatedAt = clock.GetUtcNow();
            publication.Revision++;
            foreach (var session in await db.RegistrySessions.Where(x => x.ImagePublicationId == id && x.RevokedAt == null).ToListAsync(ct))
            { session.RevokedAt = clock.GetUtcNow(); session.Revision++; }
            return Results.NoContent();
        }, ct);
    });

    private async Task<(MinicloudApp?, IResult?)> App(string id, OrganizationAction action, CancellationToken ct)
    {
        var applicationId = Request.Query["applicationId"].FirstOrDefault();
        var environmentId = Request.Query["environmentId"].FirstOrDefault();
        var ownershipError = await EndpointResults.VerifyTargetOwnershipAsync(db, id, applicationId, environmentId, ct);
        if (ownershipError is not null) { return (null, ownershipError); }
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (app is null) { return (null, EndpointResults.NotFound("app_not_found", "App was not found.")); }
        return await authorization.CanAccessAppAsync(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), id, action, ct)
            ? (app, null) : (null, EndpointResults.Forbidden());
    }

    internal static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (RegistryImageValidationException ex)
        {
            var code = ex.Code is ImagePublicationErrors.Unavailable or ImagePublicationErrors.RegistryUnavailable or ImagePublicationErrors.Maintenance ? 503 :
                ex.Code == ImagePublicationErrors.QuotaExceeded ? 429 : ex.Code == ImagePublicationErrors.PlatformChanged ? 409 : 400;
            return EndpointResults.Problem(ex.Code, ex.Message, code);
        }
        catch (HttpRequestException) { return EndpointResults.Problem(ImagePublicationErrors.RegistryUnavailable, "Registry is temporarily unavailable.", 503); }
    }
}
