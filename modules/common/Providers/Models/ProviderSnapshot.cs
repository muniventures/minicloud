namespace Selfrun.Common.Providers.Models;

public sealed record ProviderSnapshot(
    string Id,
    string Description,
    bool IsAvailable,
    decimal? SizeGb,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Regions,
    IReadOnlyDictionary<string, string> Labels);
