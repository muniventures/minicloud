namespace Minicloud.Common.Models;

public sealed class CustomDomainRegistration
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string? AppId { get; set; }
    public MinicloudApp? App { get; set; }
    public string Hostname { get; set; } = default!;
    public string VerificationToken { get; set; } = default!;
    public string Status { get; set; } = "pending";
    public string? FailureMessage { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
