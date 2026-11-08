using Minicloud.Common.Providers;

namespace Minicloud.Common.Models;

public sealed class ProviderAccount
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string Name { get; set; } = default!;
    public CloudProvider ProviderType { get; set; }
    public bool Enabled { get; set; } = true;
    public string? ProjectId { get; set; }
    public int? DefaultOsId { get; set; }
    public string CredentialStoreProvider { get; set; } = "openbao";
    public string CredentialPath { get; set; } = default!;
    public string? CredentialVersion { get; set; }
    public string CredentialGeneration { get; set; } = default!;
    public string ValidationStatus { get; set; } = "unchecked";
    public DateTimeOffset? LastValidatedAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ProviderPlan> Plans { get; set; } = [];
}

public sealed class ProviderPlan
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    public ProviderAccount ProviderAccount { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string NormalizedName { get; set; } = default!;
    public string Region { get; set; } = default!;
    public string Sku { get; set; } = default!;
    public string? Image { get; set; }
    public string BuildPlatform { get; set; } = "linux/amd64";
    public bool Enabled { get; set; } = true;
    public long Revision { get; set; } = 1;
    public DateTimeOffset? ProvisioningLockedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// Retained after rename/deletion: stale YAML must never select a different plan.
public sealed class ProviderPlanName
{
    public string OrganizationId { get; set; } = default!;
    public string NormalizedName { get; set; } = default!;
    public string PlanId { get; set; } = default!;
    public bool Retired { get; set; }
}

// OpenBao cleanup intent survives crashes between a database commit and secret deletion.
public sealed class ProviderCredentialCleanup
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    public string StoreProvider { get; set; } = "openbao";
    public string Path { get; set; } = default!;
    public string? Version { get; set; }
    public DateTimeOffset NotBefore { get; set; }
}
