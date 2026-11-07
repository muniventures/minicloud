// SPDX-License-Identifier: MPL-2.0

using System.Text.RegularExpressions;
using Minicloud.Common.Contracts;

namespace Minicloud.Common.Validation;

public static partial class ImagePublicationValidation
{
    public const int MaxManifestBytes = 1024 * 1024;
    public const int MaxConfigBytes = 1024 * 1024;
    public const int MaxLayers = 128;

    public static bool IsPlatform(string? value) => value is "linux/amd64" or "linux/arm64";
    public static bool IsDigest(string? value) => value is not null && DigestPattern().IsMatch(value);

    public static ValidationResult Validate(CreateImagePublicationRequest request)
    {
        var result = new ValidationResult();
        if (!IsRepositorySegment(request.ServiceName))
        {
            result.Add("serviceName", "Service name must be a lowercase repository segment of at most 128 characters.");
        }
        if (request.InputSha256 is null || !ShaPattern().IsMatch(request.InputSha256))
        {
            result.Add("inputSha256", "Input identity must be a lowercase SHA-256 hexadecimal value.");
        }
        if (request.Target is null || !IsPlatform(request.Target.Platform))
        {
            result.Add("target.platform", "Target platform must be linux/amd64 or linux/arm64.");
        }
        if (request.Target?.Version is null || !ShaPattern().IsMatch(request.Target.Version))
        {
            result.Add("target.version", "A server-issued target version is required.");
        }
        if (request.IdempotencyKey is null || !Guid.TryParseExact(request.IdempotencyKey, "D", out _))
        {
            result.Add("idempotencyKey", "Idempotency key must be a UUID.");
        }
        return result;
    }

    public static string Repository(string organizationId, string appId, string serviceName)
    {
        foreach (var segment in new[] { organizationId, appId, serviceName })
        {
            if (!IsRepositorySegment(segment))
            {
                throw new ArgumentException("Repository identity contains an invalid segment.");
            }
        }
        return $"org/{organizationId}/app/{appId}/service/{serviceName}";
    }

    private static bool IsRepositorySegment(string? value) => value is { Length: > 0 and <= 128 } && SegmentPattern().IsMatch(value);

    [GeneratedRegex("\\A[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*\\z", RegexOptions.NonBacktracking)]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("\\A[a-f0-9]{64}\\z", RegexOptions.NonBacktracking)]
    private static partial Regex ShaPattern();

    [GeneratedRegex("\\Asha256:[a-f0-9]{64}\\z", RegexOptions.NonBacktracking)]
    private static partial Regex DigestPattern();
}
