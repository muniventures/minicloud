namespace Minicloud.Common.Contracts;

// Account creation/credential import is separate. This manifest assigns existing
// resource ownership explicitly and never contains provider credentials.
public sealed record ProviderLegacyImportRequest(
    string OrganizationId,
    string ProviderAccountId,
    IReadOnlyList<ProviderLegacyAppBinding> AppBindings,
    IReadOnlyList<string> RetainedVolumeIds);

public sealed record ProviderLegacyAppBinding(string AppId, string? PlanName = null, string? BuildPlatform = null);

public sealed record ProviderLegacyImportResult(
    int CreatedPlans,
    int BoundApps,
    int BoundServers,
    int BoundRuntimes,
    int BoundVolumes);
