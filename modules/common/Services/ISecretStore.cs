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
    string Value)
{
    // Organization infrastructure credentials use their own namespace, never a synthetic app.
    public string? ProviderAccountId { get; init; }
    public string? CredentialGeneration { get; init; }
}

public sealed record StoredSecretReference(
    string Provider,
    string Path,
    string? Version);

public sealed class SecretStoreUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
