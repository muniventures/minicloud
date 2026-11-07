namespace Minicloud.Common.Providers.Models;

public record CreateServerRequest(
    string Name,
    string Plan,
    string Region,
    string SshKeyIdOrPublicKey,
    string UserDataCloudInit,
    IReadOnlyDictionary<string, string>? Labels = null,
    IReadOnlyList<string>? Tags = null,
    string? Hostname = null,
    string? Image = null);
