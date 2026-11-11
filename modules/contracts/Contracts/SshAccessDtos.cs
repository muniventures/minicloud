using System.Text.Json.Serialization;

namespace Minicloud.Common.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SshAccessRequest(
    string Mode,
    string? KeyId = null,
    string? Name = null,
    string? PublicKey = null,
    long? ExpectedRevision = null);

public sealed record ProviderSshKeyResponse(
    string Id,
    string ProviderAccountId,
    string Name,
    string Fingerprint,
    string KeyType,
    string RegistrationState);

public sealed record EnvironmentSshAccessResponse(
    string Mode,
    string? ProviderAccountId,
    string? KeyId,
    string? Name,
    string? Fingerprint,
    string? KeyType,
    long Revision,
    string RegistrationState,
    bool SelectionLocked);

public sealed record SshConnectionResponse(string Address, string Username);
