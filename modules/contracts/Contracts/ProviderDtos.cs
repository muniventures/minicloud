namespace Minicloud.Common.Contracts;

public sealed record ProviderTypeResponse(string Type, string Name, bool SupportsStorage, string? DefaultImage, int? DefaultOsId = null);
public sealed record CreateProviderRequest(string OrganizationId, string Name, string ProviderType, string Credential,
    string? ProjectId = null, int? DefaultOsId = null);
public sealed record UpdateProviderRequest(long Revision, string? Name = null, bool? Enabled = null);
public sealed record ReplaceProviderCredentialRequest(string Credential, long Revision);
public sealed record ProviderAccountResponse(string Id, string OrganizationId, string Name, string ProviderType,
    bool Enabled, string? ProjectId, int? DefaultOsId, string ValidationStatus, DateTimeOffset? LastValidatedAt,
    long Revision, int PlanCount);
public sealed record CreateProviderPlanRequest(string OrganizationId, string ProviderAccountId, string Region, string Sku,
    string? Name = null, string? Image = null, string BuildPlatform = "linux/amd64", bool Enabled = true);
public sealed record UpdateProviderPlanRequest(long Revision, string? Name = null, string? Region = null, string? Sku = null,
    string? Image = null, string? BuildPlatform = null, bool? Enabled = null);
public sealed record ProviderPlanResponse(string Id, string OrganizationId, string ProviderAccountId, string Name,
    string ProviderName, string ProviderType, string Region, string Sku, string? Image, string BuildPlatform,
    bool Enabled, bool AccountEnabled, long Revision, bool ProvisioningLocked);
public sealed record ProviderValidationResponse(string Status, DateTimeOffset CheckedAt);
