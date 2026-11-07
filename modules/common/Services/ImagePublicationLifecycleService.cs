using System.Text;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Services;

public sealed class ImagePublicationLifecycleService(IIdGenerator ids, TimeProvider timeProvider)
{
    public ImagePublication Create(MinicloudApp app, CreateImagePublicationRequest request, ImageBuildTarget currentTarget)
    {
        if (!ImagePublicationValidation.Validate(request).IsValid)
        {
            throw new ArgumentException("Image publication request is invalid.");
        }
        if (request.Target != currentTarget)
        {
            throw new RegistryImageValidationException(ImagePublicationErrors.PlatformChanged, "Build target changed; resolve it again before building.");
        }
        if (app.DeletionStatus is not null)
        {
            throw new InvalidOperationException("Cannot publish an image for a deleting app.");
        }
        var now = timeProvider.GetUtcNow();
        var id = ids.NewId("img");
        return new ImagePublication
        {
            Id = id,
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            ServiceName = request.ServiceName,
            IdempotencyKey = Guid.Parse(request.IdempotencyKey).ToString("D"),
            Repository = ImagePublicationValidation.Repository(app.OrganizationId, app.Id, request.ServiceName),
            CandidateTag = id,
            TargetVersion = currentTarget.Version,
            Platform = currentTarget.Platform,
            InputSha256 = request.InputSha256,
            Status = ImagePublicationStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
    }

    public void BeginUpload(ImagePublication publication)
    {
        RequireWritable(publication);
        if (publication.Status == ImagePublicationStatus.Uploading) { return; }
        publication.Status = ImagePublicationStatus.Uploading;
        Touch(publication);
    }

    public VerifiedRegistryImage Complete(ImagePublication publication, ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> config, string expectedDigest)
    {
        if (publication.Status == ImagePublicationStatus.Ready)
        {
            if (publication.ManifestDigest != expectedDigest)
            {
                throw new RegistryImageValidationException(ImagePublicationErrors.Incomplete, "A ready image publication cannot change its digest.");
            }
            return RegistryImageValidator.Verify(manifest, config, expectedDigest, publication.Platform);
        }
        RequireWritable(publication);
        var image = RegistryImageValidator.Verify(manifest, config, expectedDigest, publication.Platform);
        publication.ManifestDigest = image.ManifestDigest;
        publication.ManifestJson = Encoding.UTF8.GetString(manifest.Span);
        publication.SizeBytes = image.SizeBytes;
        publication.Status = ImagePublicationStatus.Ready;
        publication.ReadyAt = timeProvider.GetUtcNow();
        Touch(publication);
        return image;
    }

    public ValidationResult ValidateBinding(ImagePublication publication, MinicloudApp app, string serviceName, ImageBuildTarget target, bool previouslyBound = false)
    {
        var result = new ValidationResult();
        if (publication.OrganizationId != app.OrganizationId || publication.AppId != app.Id || publication.ServiceName != serviceName)
        {
            result.Add("imagePublicationId", "Image publication does not belong to this app and service.");
        }
        if (app.DeletionStatus is not null || publication.Status != ImagePublicationStatus.Ready ||
            publication.RetiredAt is not null || (!previouslyBound && publication.ExpiresAt <= timeProvider.GetUtcNow()) ||
            !ImagePublicationValidation.IsDigest(publication.ManifestDigest) || publication.SizeBytes is null or <= 0)
        {
            result.Add("imagePublicationId", "Image publication is not available for new deployment admission.");
        }
        if (publication.TargetVersion != target.Version || publication.Platform != target.Platform)
        {
            result.Add("imagePublicationId", "Image publication does not match the current build target.");
        }
        return result;
    }

    private void RequireWritable(ImagePublication publication)
    {
        if (publication.ExpiresAt <= timeProvider.GetUtcNow())
        {
            throw new RegistryImageValidationException(ImagePublicationErrors.Expired, "Image publication has expired.");
        }
        if (publication.Status is not (ImagePublicationStatus.Pending or ImagePublicationStatus.Uploading) || publication.RetiredAt is not null)
        {
            throw new InvalidOperationException("Image publication is no longer writable.");
        }
    }

    private void Touch(ImagePublication publication)
    {
        publication.UpdatedAt = timeProvider.GetUtcNow();
        publication.Revision++;
    }
}
