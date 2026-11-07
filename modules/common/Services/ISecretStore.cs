namespace Minicloud.Common.Services;

public interface ISecretStore
{
    Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken);
    Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken);
    Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken);
}

public sealed record SecretStoreWriteRequest(
    string OrganizationId,
    string AppId,
    string ServiceName,
    string Name,
    string Value);

public sealed record StoredSecretReference(
    string Provider,
    string Path,
    string? Version);

public sealed class SecretStoreUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
