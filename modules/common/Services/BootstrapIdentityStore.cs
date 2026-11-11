namespace Minicloud.Common.Services;

public sealed class BootstrapException(string code, int statusCode, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    public static BootstrapException Conflict() =>
        new("setup_state_conflict", 409, "The installation requires explicit host reconciliation.");
    public static BootstrapException Unavailable() =>
        new("auth_unavailable", 503, "The identity service is temporarily unavailable.");
}

public sealed record BootstrapIdentity(string Id, string Email);

public sealed class BootstrapIdentityCreationRejectedException()
    : Exception("The identity service rejected creation without mutating an identity.");

/// <summary>Trusted identity administration, never an email-based adoption mechanism.</summary>
public interface IBootstrapIdentityStore
{
    Task<bool> HasIdentitiesAsync(CancellationToken cancellationToken);
    Task<BootstrapIdentity?> FindOperationAsync(string installationId, string operationId, CancellationToken cancellationToken);
    Task<BootstrapIdentity> CreateAsync(string installationId, string operationId, string email,
        string password, string displayName, CancellationToken cancellationToken);
    Task<BootstrapIdentity> GetAsync(string identityId, CancellationToken cancellationToken);
    Task SetPasswordAsync(string identityId, string password, CancellationToken cancellationToken);
    Task RevokeSessionsAsync(string identityId, CancellationToken cancellationToken);
}

/// <summary>The implementation must reveal secrets only to a protected file or terminal.</summary>
public interface IBootstrapTokenOutput
{
    Task WriteAsync(string token, CancellationToken cancellationToken);
}
