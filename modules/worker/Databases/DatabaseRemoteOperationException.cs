namespace Minicloud.Worker.Databases;

internal sealed class DatabaseRemoteOperationException(
    string code, string script = "remote", string? stage = null, string? runtimeErrorType = null) : Exception(code)
{
    public string Code { get; } = code;
    public string Stage { get; } = stage is "access-tls" or "access-rules" or "access-listener" or "access-readiness" or "access-tls-verification"
        ? stage : script is "dispatch.py" or "operation.py" or "restore.py" or "repository-control.py" or
            "private-network.py" or "native-network.py" or "migration-fence.py" ? script : "remote";
    public string RuntimeErrorType { get; } = runtimeErrorType is "CalledProcessError" or "TimeoutExpired" or "TimeoutError" or
        "ValueError" or "RuntimeError" or "OSError" or "FileNotFoundError" or "PermissionError" or "BackupCapacityError" or
        "AccessRollbackUnverifiedError" or "AccessChangeRolledBackError" ? runtimeErrorType : "unknown";
}
