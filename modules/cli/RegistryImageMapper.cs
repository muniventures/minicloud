namespace Municloud.Cli;

public sealed class RegistryImageMapper
{
    private readonly CliEnvironment _environment;

    public RegistryImageMapper(CliEnvironment environment)
    {
        _environment = environment;
    }

    public string RuntimeImageForDeployment(string image, string organizationSlug)
    {
        if (!UsesMunicloudRegistry(image))
        {
            return image;
        }

        var imageWithoutHost = image[(_environment.RegistryHost.Length + 1)..];
        var tagSeparator = imageWithoutHost.LastIndexOf(':');
        var tag = tagSeparator >= 0 ? imageWithoutHost[tagSeparator..] : "";
        var repository = tagSeparator >= 0 ? imageWithoutHost[..tagSeparator] : imageWithoutHost;
        var upstreamRepository = string.Join(
            "-",
            repository
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeImageSegment));

        return $"{_environment.RuntimeRegistryPrefix}/{NormalizeImageSegment(organizationSlug)}-{upstreamRepository}{tag}";
    }

    public bool UsesMunicloudRegistry(string? image) =>
        !string.IsNullOrWhiteSpace(image) &&
        image.StartsWith(_environment.RegistryHost + "/", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeImageSegment(string segment)
    {
        var normalized = string.Concat(segment.ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-')).Trim('-', '_', '.');
        return string.IsNullOrWhiteSpace(normalized) ? "image" : normalized;
    }
}
