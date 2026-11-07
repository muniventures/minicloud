using System.Security.Cryptography;
using System.Text.Json;
using Minicloud.Common.Contracts;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Services;

public sealed record RegistryBlobDescriptor(string MediaType, string Digest, long Size);
public sealed record VerifiedRegistryImage(
    string ManifestDigest,
    string Platform,
    long SizeBytes,
    RegistryBlobDescriptor Config,
    IReadOnlyList<RegistryBlobDescriptor> Layers);

public sealed class RegistryImageValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class RegistryImageValidator
{
    public const string OciManifest = "application/vnd.oci.image.manifest.v1+json";
    public const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    private const string OciConfig = "application/vnd.oci.image.config.v1+json";
    private const string DockerConfig = "application/vnd.docker.container.image.v1+json";
    private static readonly HashSet<string> LayerTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.oci.image.layer.v1.tar",
        "application/vnd.oci.image.layer.v1.tar+gzip",
        "application/vnd.oci.image.layer.v1.tar+zstd",
        "application/vnd.docker.image.rootfs.diff.tar.gzip"
    };

    // Network callers must enforce these same limits while reading, before allocating the complete body.
    public static VerifiedRegistryImage Verify(
        ReadOnlyMemory<byte> manifest,
        ReadOnlyMemory<byte> config,
        string expectedDigest,
        string targetPlatform)
    {
        if (manifest.Length == 0 || manifest.Length > ImagePublicationValidation.MaxManifestBytes || config.Length > ImagePublicationValidation.MaxConfigBytes)
        {
            throw Invalid(ImagePublicationErrors.Unsupported, "Image document exceeds its byte limit or is empty.");
        }
        if (!ImagePublicationValidation.IsPlatform(targetPlatform))
        {
            throw Invalid(ImagePublicationErrors.PlatformUnknown, "Target platform is not supported.");
        }
        if (!ImagePublicationValidation.IsDigest(expectedDigest) || Digest(manifest.Span) != expectedDigest)
        {
            throw Invalid(ImagePublicationErrors.Incomplete, "Manifest digest does not match its content.");
        }
        using var manifestDocument = Parse(manifest, ImagePublicationValidation.MaxManifestBytes);
        var root = manifestDocument.RootElement;
        var mediaType = String(root, "mediaType");
        if (mediaType is not (OciManifest or DockerManifest) ||
            !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 2 ||
            root.TryGetProperty("subject", out _) || root.TryGetProperty("artifactType", out _))
        {
            throw Invalid(ImagePublicationErrors.Unsupported, "Only a single runnable OCI or Docker schema-2 image is supported.");
        }
        if (!root.TryGetProperty("config", out var configElement))
        {
            throw Invalid(ImagePublicationErrors.Incomplete, "Image config descriptor is missing.");
        }
        var configDescriptor = Descriptor(configElement);
        if (configDescriptor.MediaType != (mediaType == OciManifest ? OciConfig : DockerConfig))
        {
            throw Invalid(ImagePublicationErrors.Unsupported, "Image config media type is not supported.");
        }
        if (configDescriptor.Size > ImagePublicationValidation.MaxConfigBytes ||
            configDescriptor.Size != config.Length || configDescriptor.Digest != Digest(config.Span))
        {
            throw Invalid(ImagePublicationErrors.Incomplete, "Image config size or digest does not match its content.");
        }
        if (!root.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array ||
            layersElement.GetArrayLength() > ImagePublicationValidation.MaxLayers)
        {
            throw Invalid(ImagePublicationErrors.Unsupported, "Image layers are missing or exceed the layer limit.");
        }
        var layers = new List<RegistryBlobDescriptor>();
        var unique = new Dictionary<string, long>(StringComparer.Ordinal) { [configDescriptor.Digest] = configDescriptor.Size };
        long totalBytes = manifest.Length + configDescriptor.Size;
        foreach (var element in layersElement.EnumerateArray())
        {
            var layer = Descriptor(element);
            if (!LayerTypes.Contains(layer.MediaType))
            {
                throw Invalid(ImagePublicationErrors.Unsupported, "Foreign or unsupported image layers are not allowed.");
            }
            if (unique.TryGetValue(layer.Digest, out var existing))
            {
                if (existing != layer.Size)
                {
                    throw Invalid(ImagePublicationErrors.Incomplete, "Conflicting sizes for the same image blob.");
                }
            }
            else
            {
                if (layer.Size > long.MaxValue - totalBytes)
                { throw Invalid(ImagePublicationErrors.Unsupported, "Image byte count exceeds the supported numeric range."); }
                totalBytes += layer.Size;
                unique.Add(layer.Digest, layer.Size);
            }
            layers.Add(layer);
        }
        using var configDocument = Parse(config, ImagePublicationValidation.MaxConfigBytes);
        var imageConfig = configDocument.RootElement;
        var platform = $"{String(imageConfig, "os")}/{String(imageConfig, "architecture")}";
        if (!ImagePublicationValidation.IsPlatform(platform) || platform != targetPlatform ||
            (imageConfig.TryGetProperty("variant", out var variant) &&
             !(platform == "linux/arm64" && variant.ValueKind == JsonValueKind.String && variant.GetString() == "v8")))
        {
            throw Invalid(ImagePublicationErrors.PlatformMismatch, "Image config does not match the target platform.");
        }
        if (!imageConfig.TryGetProperty("rootfs", out var rootfs) || rootfs.ValueKind != JsonValueKind.Object ||
            String(rootfs, "type") != "layers" || !rootfs.TryGetProperty("diff_ids", out var diffIds) ||
            diffIds.ValueKind != JsonValueKind.Array || diffIds.GetArrayLength() != layers.Count ||
            diffIds.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || !ImagePublicationValidation.IsDigest(x.GetString())))
        {
            throw Invalid(ImagePublicationErrors.Unsupported, "Image root filesystem does not match its layer descriptors.");
        }
        return new VerifiedRegistryImage(expectedDigest, platform, totalBytes, configDescriptor, layers.AsReadOnly());
    }

    public static string Digest(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static RegistryBlobDescriptor Descriptor(JsonElement element)
    {
        var mediaType = String(element, "mediaType");
        var digest = String(element, "digest");
        if (element.TryGetProperty("urls", out _) || element.TryGetProperty("data", out _) ||
            element.TryGetProperty("artifactType", out _) || !ImagePublicationValidation.IsDigest(digest) ||
            !element.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out var size) ||
            size < 0)
        {
            throw Invalid(ImagePublicationErrors.Unsupported, "Image descriptor is invalid or references external content.");
        }
        return new RegistryBlobDescriptor(mediaType, digest, size);
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> bytes, int limit)
    {
        if (bytes.Length == 0 || bytes.Length > limit)
        {
            throw Invalid(ImagePublicationErrors.Unsupported, "Image document exceeds its byte limit or is empty.");
        }
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            ValidateNames(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(ImagePublicationErrors.Unsupported, "Image document must be an object.");
            }
            return document;
        }
        catch (JsonException)
        {
            document?.Dispose();
            throw Invalid(ImagePublicationErrors.Unsupported, "Image document is not valid bounded JSON.");
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    private static void ValidateNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid(ImagePublicationErrors.Unsupported, "Duplicate image document properties are not allowed.");
                }
                ValidateNames(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) { ValidateNames(item); }
        }
    }

    private static string String(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(ImagePublicationErrors.Unsupported, "Required image document property is invalid.");
        }
        return value.GetString()!;
    }

    private static RegistryImageValidationException Invalid(string code, string message) => new(code, message);
}
