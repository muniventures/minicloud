using System.ComponentModel.DataAnnotations;

namespace Minicloud.Common.Models;

public sealed class RuntimeServer
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string Provider { get; set; } = default!;
    public string? ProviderServerId { get; set; }
    public string? Hostname { get; set; }
    public string? AgentVersion { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<RuntimeToken> Tokens { get; set; } = [];
}

public sealed class RuntimeToken
{
    [Key]
    public string Id { get; set; } = default!;
    public string RuntimeServerId { get; set; } = default!;
    public RuntimeServer RuntimeServer { get; set; } = default!;
    public string TokenPrefix { get; set; } = default!;
    public string TokenHash { get; set; } = default!;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RuntimeLogRecord
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string RuntimeServerId { get; set; } = default!;
    public string Source { get; set; } = default!;
    public string? Service { get; set; }
    public string Stream { get; set; } = default!;
    public string Cursor { get; set; } = default!;
    public string Content { get; set; } = default!;
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class RuntimeStatusSnapshot
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string RuntimeServerId { get; set; } = default!;
    public string? AgentVersion { get; set; }
    public string StatusJson { get; set; } = default!;
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
