using System.ComponentModel.DataAnnotations;

namespace Minicloud.Common.Models;

/// <summary>
/// Minimal shared audit foundation (T11). Append-only; never stores tokens, log bodies, or secrets.
/// </summary>
public sealed class AuditEvent
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization? Organization { get; set; }
    public string? AppId { get; set; }
    public MinicloudApp? App { get; set; }
    public string Actor { get; set; } = default!;
    public string Action { get; set; } = default!;
    public string? Details { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
