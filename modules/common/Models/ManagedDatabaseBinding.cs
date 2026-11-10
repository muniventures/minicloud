namespace Minicloud.Common.Models;

public sealed class ManagedDatabaseBinding
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string DatabaseId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string? Host { get; set; }
    public int Port { get; set; } = 5432;
    public string? SslMode { get; set; }
    public string Status { get; set; } = "pending";
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
