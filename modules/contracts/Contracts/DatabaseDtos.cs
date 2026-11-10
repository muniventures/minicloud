namespace Minicloud.Common.Contracts;

public sealed record CreateDatabaseServerRequest(string OrganizationId, string Name, string Mode, string Plan, string Engine = "postgres", string EngineVersion = "16");
public sealed record CreateManagedDatabaseRequest(string OrganizationId, string Name, string? ServerId = null, string? AppId = null, BackupPolicyRequest? Backup = null);
public sealed record BackupPolicyRequest(bool Enabled, string? RepositoryAppId = null, int FrequencyHours = 24, int RetentionDays = 30, int MinimumCopies = 2, bool KeepLastCopies = false);
public sealed record DatabaseOperationRequest(string Kind, string? BackupArtifactId = null, string? TargetServerId = null, string? TargetName = null);
public sealed record DatabaseAccessRequest(string Mode, IReadOnlyList<string>? AllowedCidrs = null, string? NetworkId = null);
