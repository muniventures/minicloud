namespace Minicloud.Worker.Databases;

internal sealed class DatabaseRemoteOperationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
