using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1/[controller]")]
public sealed class ArtifactsController : ControllerBase
{
    [HttpGet]
    public async Task<IResult> ListArtifacts(
        [FromQuery] string appId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = EndpointResults.RequirePrincipal(HttpContext);
        if (string.IsNullOrWhiteSpace(appId))
        {
            var validation = new ValidationResult();
            validation.Add("appId", "App is required.");
            return EndpointResults.ValidationProblem("Deployment artifact request is invalid.", validation);
        }

        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var artifacts = await db.DeploymentArtifacts
            .Where(x => x.OrganizationId == app.OrganizationId && x.AppId == app.Id)
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        var changed = false;
        foreach (var artifact in artifacts)
        {
            changed |= TryMarkExpired(artifact, timeProvider.GetUtcNow());
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(artifacts.Select(ToResponse));
    }

    [HttpPost]
    public async Task<IResult> CreateArtifact(
        [FromBody] CreateDeploymentArtifactRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] IIdGenerator ids,
        [FromServices] TimeProvider timeProvider,
        [FromServices] DeploymentArtifactStorageService storage,
        CancellationToken cancellationToken)
    {
        var principal = EndpointResults.RequirePrincipal(HttpContext);
        var validation = ValidateCreateArtifact(request, storage.MaxArtifactBytes);
        if (!validation.IsValid)
        {
            return EndpointResults.ValidationProblem("Deployment artifact request is invalid.", validation);
        }

        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.AppId, cancellationToken);
        if (app is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), app.Id, OrganizationAction.CreateDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var now = timeProvider.GetUtcNow();
        var artifact = new DeploymentArtifact
        {
            Id = ids.NewId("art"),
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            ServiceName = request.ServiceName,
            FileName = request.FileName,
            ContentType = request.ContentType,
            ExpectedSizeBytes = request.SizeBytes,
            ExpectedSha256 = request.Sha256.ToLowerInvariant(),
            ManifestJson = JsonSerializer.Serialize(request.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Status = DeploymentArtifactStatus.Created,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.Add(storage.Retention)
        };
        artifact.StoragePath = storage.CreateStoragePath(artifact.OrganizationId, artifact.AppId, artifact.Id);
        db.DeploymentArtifacts.Add(artifact);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/v1/artifacts/{artifact.Id}", ToCreateResponse(artifact));
    }

    [HttpPut("{artifactId}/file")]
    public async Task<IResult> UploadContent(
        [FromRoute] string artifactId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] DeploymentArtifactStorageService storage,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = EndpointResults.RequirePrincipal(HttpContext);
        var artifact = await db.DeploymentArtifacts.SingleOrDefaultAsync(x => x.Id == artifactId, cancellationToken);
        if (artifact is null)
        {
            return EndpointResults.NotFound("artifact_not_found", "Deployment artifact was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), artifact.AppId, OrganizationAction.CreateDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        if (TryMarkExpired(artifact, timeProvider.GetUtcNow()))
        {
            await db.SaveChangesAsync(cancellationToken);
            return EndpointResults.Problem("artifact_expired", "Deployment artifact has expired.", StatusCodes.Status410Gone);
        }

        if (artifact.Status != DeploymentArtifactStatus.Created)
        {
            return EndpointResults.Conflict("artifact_not_mutable", "Deployment artifact content cannot be changed.");
        }

        var declaredSha = Request.Headers["X-Minicloud-Artifact-Sha256"].ToString();
        var declaredSizeHeader = Request.Headers["X-Minicloud-Artifact-Size"].ToString();
        if (string.IsNullOrWhiteSpace(declaredSha) || !long.TryParse(declaredSizeHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var declaredSize))
        {
            return EndpointResults.Problem("artifact_headers_missing", "Artifact checksum and size headers are required.", StatusCodes.Status400BadRequest);
        }

        artifact.Status = DeploymentArtifactStatus.Uploading;
        artifact.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        var result = await storage.StoreAsync(artifact, Request.Body, declaredSize, declaredSha, cancellationToken);
        artifact.UpdatedAt = timeProvider.GetUtcNow();
        if (result.IsReady)
        {
            artifact.Status = DeploymentArtifactStatus.Ready;
            artifact.SizeBytes = result.SizeBytes;
            artifact.Sha256 = result.Sha256;
            artifact.UploadedAt = result.UploadedAt;
        }
        else
        {
            artifact.Status = DeploymentArtifactStatus.Failed;
            artifact.FailureCode = result.FailureCode;
            artifact.FailureMessage = result.FailureMessage;
        }

        await db.SaveChangesAsync(cancellationToken);
        return result.IsReady
            ? Results.Ok(ToResponse(artifact))
            : EndpointResults.Problem(result.FailureCode ?? "artifact_upload_failed", result.FailureMessage ?? "Artifact upload failed.", StatusCodes.Status400BadRequest);
    }

    [HttpGet("{artifactId}/file")]
    public async Task<IResult> DownloadContent(
        [FromRoute] string artifactId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] DeploymentArtifactStorageService storage,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = EndpointResults.RequirePrincipal(HttpContext);
        var artifact = await db.DeploymentArtifacts.SingleOrDefaultAsync(x => x.Id == artifactId, cancellationToken);
        if (artifact is null)
        {
            return EndpointResults.NotFound("artifact_not_found", "Deployment artifact was not found.");
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), artifact.OrganizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        if (TryMarkExpired(artifact, timeProvider.GetUtcNow()))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (artifact.Status != DeploymentArtifactStatus.Ready)
        {
            return EndpointResults.Problem("artifact_unavailable", "Deployment artifact is not available for download.", StatusCodes.Status410Gone);
        }

        var path = storage.AbsolutePath(artifact);
        if (!System.IO.File.Exists(path))
        {
            return EndpointResults.Problem("artifact_file_missing", "Deployment artifact file is missing.", StatusCodes.Status410Gone);
        }

        return Results.File(System.IO.File.OpenRead(path), artifact.ContentType, artifact.FileName);
    }

    [HttpGet("{artifactId}")]
    public async Task<IResult> GetArtifact(
        [FromRoute] string artifactId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = EndpointResults.RequirePrincipal(HttpContext);
        var artifact = await db.DeploymentArtifacts.SingleOrDefaultAsync(x => x.Id == artifactId, cancellationToken);
        if (artifact is null)
        {
            return EndpointResults.NotFound("artifact_not_found", "Deployment artifact was not found.");
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), artifact.OrganizationId, OrganizationAction.ReadDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        if (TryMarkExpired(artifact, timeProvider.GetUtcNow()))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(ToResponse(artifact));
    }

    [HttpDelete("{artifactId}")]
    public async Task<IResult> DeleteArtifact(
        [FromRoute] string artifactId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] DeploymentArtifactStorageService storage,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var principal = EndpointResults.RequirePrincipal(HttpContext);
        var artifact = await db.DeploymentArtifacts.SingleOrDefaultAsync(x => x.Id == artifactId, cancellationToken);
        if (artifact is null)
        {
            return EndpointResults.NotFound("artifact_not_found", "Deployment artifact was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), artifact.AppId, OrganizationAction.CreateDeployments, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        if (await db.AssetReleases.AnyAsync(r => r.ArtifactId == artifact.Id, cancellationToken))
        {
            return EndpointResults.Conflict("artifact_in_use", "Artifact is retained by an immutable asset release.");
        }
        artifact.Status = DeploymentArtifactStatus.Deleted;
        artifact.DeletedAt = timeProvider.GetUtcNow();
        artifact.UpdatedAt = artifact.DeletedAt.Value;
        storage.DeleteContent(artifact);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static ValidationResult ValidateCreateArtifact(CreateDeploymentArtifactRequest request, long maxArtifactBytes)
    {
        var result = new ValidationResult();
        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            result.Add("appId", "App is required.");
        }
        if (string.IsNullOrWhiteSpace(request.ServiceName))
        {
            result.Add("serviceName", "Service name is required.");
        }
        if (string.IsNullOrWhiteSpace(request.FileName) || !request.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("fileName", "Artifact file name must end with .zip.");
        }
        if (request.ContentType != "application/zip")
        {
            result.Add("contentType", "Artifact content type must be application/zip.");
        }
        if (request.SizeBytes < 1 || request.SizeBytes > maxArtifactBytes)
        {
            result.Add("sizeBytes", $"Artifact size must be between 1 and {maxArtifactBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }
        if (string.IsNullOrWhiteSpace(request.Sha256) || request.Sha256.Length != 64 || request.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            result.Add("sha256", "Artifact SHA256 must be a 64-character hex string.");
        }

        return result;
    }

    private static bool TryMarkExpired(DeploymentArtifact artifact, DateTimeOffset now)
    {
        if (artifact.Status is DeploymentArtifactStatus.Deleted or DeploymentArtifactStatus.Expired)
        {
            return false;
        }

        if (artifact.ExpiresAt > now)
        {
            return false;
        }

        artifact.Status = DeploymentArtifactStatus.Expired;
        artifact.UpdatedAt = now;
        return true;
    }

    private static DeploymentArtifactCreateResponse ToCreateResponse(DeploymentArtifact artifact) =>
        new(
            artifact.Id,
            artifact.AppId,
            artifact.ServiceName,
            ControlPlaneValidation.ToWireValue(artifact.Status),
            $"/v1/artifacts/{artifact.Id}/file",
            artifact.ExpectedSizeBytes ?? 0,
            artifact.ExpectedSha256 ?? string.Empty,
            artifact.ExpiresAt);

    private static DeploymentArtifactResponse ToResponse(DeploymentArtifact artifact) =>
        new(
            artifact.Id,
            artifact.AppId,
            artifact.ServiceName,
            ControlPlaneValidation.ToWireValue(artifact.Status),
            artifact.SizeBytes ?? artifact.ExpectedSizeBytes ?? 0,
            artifact.Sha256 ?? artifact.ExpectedSha256 ?? string.Empty,
            artifact.CreatedAt,
            artifact.ExpiresAt);

}
