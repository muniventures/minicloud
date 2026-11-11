// SPDX-License-Identifier: MPL-2.0
using System.Numerics;
using System.Security.Cryptography;
using Minicloud.Cli.Api;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    internal const string SshKeyOption = "--ssh-key";
    internal const string SshPublicKeyFileOption = "--ssh-public-key-file";
    internal const string SshKeyNameOption = "--ssh-key-name";
    internal const int MaximumSshPublicKeyBytes = 16 * 1024;
    private const int MinimumRsaBits = 3072;
    private const int MaximumSshKeyNameLength = 128;

    internal static readonly string[] SshOptionsWithValues = [SshKeyOption, SshPublicKeyFileOption, SshKeyNameOption];

    internal const string SshOptionsUsage = "[--ssh-key <key-id> | --ssh-public-key-file <path> [--ssh-key-name <name>]]";

    internal static bool HasSshAccessOptions(IReadOnlyList<string> args) =>
        args.Any(arg => SshOptionsWithValues.Any(option => arg == option || arg.StartsWith(option + "=", StringComparison.Ordinal)));

    /// <summary>
    /// Builds the environment SSH selection from CLI flags. Returns null when no SSH flag is supplied so creation
    /// keeps Minicloud-only access and updates preserve the saved selection.
    /// </summary>
    internal static SshAccessRequest? ParseSshAccessOptions(IReadOnlyList<string> args)
    {
        var keyId = ReadSshOption(args, SshKeyOption);
        var publicKeyFile = ReadSshOption(args, SshPublicKeyFileOption);
        var keyName = ReadSshOption(args, SshKeyNameOption);

        if (keyId is not null && publicKeyFile is not null)
            throw new CliCommandException(CliExitCodes.ValidationError, $"Use either {SshKeyOption} or {SshPublicKeyFileOption}, not both.");
        if (keyName is not null && publicKeyFile is null)
            throw new CliCommandException(CliExitCodes.ValidationError, $"{SshKeyNameOption} names a new key and requires {SshPublicKeyFileOption} <path>.");
        if (keyId is not null)
            return new SshAccessRequest("existing", KeyId: keyId.Trim());
        if (publicKeyFile is null)
            return null;

        var key = ReadSshPublicKeyFile(publicKeyFile);
        var name = keyName ?? key.Comment ?? DefaultSshKeyName(publicKeyFile);
        return new SshAccessRequest("new", Name: ValidateSshKeyName(name), PublicKey: key.PublicKey);
    }

    private static string? ReadSshOption(IReadOnlyList<string> args, string option)
    {
        var occurrences = args.Count(arg => arg == option || arg.StartsWith(option + "=", StringComparison.Ordinal));
        if (occurrences == 0)
            return null;
        if (occurrences > 1)
            throw new CliCommandException(CliExitCodes.ValidationError, $"{option} can be specified only once.");
        var value = GetOption(args, option);
        if (string.IsNullOrWhiteSpace(value) || (value.StartsWith("--", StringComparison.Ordinal) && !args.Contains(option + "=" + value)))
            throw new CliCommandException(CliExitCodes.ValidationError, option switch
            {
                SshKeyOption => $"{SshKeyOption} requires a Minicloud SSH key ID from the provider account's key inventory.",
                SshPublicKeyFileOption => $"{SshPublicKeyFileOption} requires the path to an OpenSSH public key file (for example ~/.ssh/id_ed25519.pub).",
                _ => $"{SshKeyNameOption} requires a name."
            });
        return value;
    }

    internal sealed record SshPublicKeyFile(string PublicKey, string? Comment);

    internal static SshPublicKeyFile ReadSshPublicKeyFile(string path)
    {
        var fullPath = ExpandHomePath(path);
        string content;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
                throw new CliCommandException(CliExitCodes.ValidationError, $"SSH public key file '{path}' was not found.");
            if (info.Length > MaximumSshPublicKeyBytes)
                throw new CliCommandException(CliExitCodes.ValidationError, $"SSH public key file '{path}' is larger than 16 KiB. Supply a single OpenSSH public key line.");
            content = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"SSH public key file '{path}' could not be read.");
        }

        return ParseSshPublicKey(content, path);
    }

    internal static SshPublicKeyFile ParseSshPublicKey(string content, string source)
    {
        if (content.Length > MaximumSshPublicKeyBytes)
            throw InvalidKey(source, "is larger than 16 KiB");
        if (content.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("-----BEGIN", StringComparison.Ordinal) ||
            content.Contains("PuTTY-User-Key-File", StringComparison.OrdinalIgnoreCase))
            throw new CliCommandException(CliExitCodes.ValidationError,
                $"'{source}' looks like a private key. Minicloud never accepts private keys; supply the matching .pub public key file instead.");

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (lines.Length == 0)
            throw InvalidKey(source, "is empty");
        if (lines.Length > 1)
            throw InvalidKey(source, "contains more than one line; supply exactly one public key");

        var parts = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts[0] is not ("ssh-ed25519" or "ssh-rsa"))
        {
            if (parts.Skip(1).Any(part => part is "ssh-ed25519" or "ssh-rsa"))
                throw InvalidKey(source, "starts with authorized_keys options; supply only the key type, key data and optional comment");
            throw InvalidKey(source, $"uses unsupported key type '{parts[0]}'; use an Ed25519 or RSA (at least 3072-bit) key");
        }
        if (parts.Length < 2)
            throw InvalidKey(source, "is missing the key data");
        if (parts.Skip(2).Any(part => part is "ssh-ed25519" or "ssh-rsa" || part.StartsWith("AAAA", StringComparison.Ordinal) && part.Length > 40))
            throw InvalidKey(source, "contains more than one key; supply exactly one public key");

        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw InvalidKey(source, "has malformed key data"); }

        if (!IsValidSshKeyBlob(parts[0], blob, out var rsaTooSmall))
            throw InvalidKey(source, rsaTooSmall ? "is an RSA key shorter than 3072 bits" : "has key data that does not match its key type");

        var comment = parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : null;
        return new SshPublicKeyFile($"{parts[0]} {parts[1]}", comment);
    }

    internal static string SshPublicKeyFingerprint(string publicKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(publicKey.Split(' ')[1]))).TrimEnd('=');

    private static CliCommandException InvalidKey(string source, string reason) =>
        new(CliExitCodes.ValidationError, $"SSH public key file '{source}' {reason}.");

    private static bool IsValidSshKeyBlob(string keyType, byte[] blob, out bool rsaTooSmall)
    {
        rsaTooSmall = false;
        var position = 0;
        if (!TryReadSshString(blob, ref position, out var algorithm) ||
            System.Text.Encoding.ASCII.GetString(algorithm) != keyType)
            return false;
        if (keyType == "ssh-ed25519")
            return TryReadSshString(blob, ref position, out var key) && key.Length == 32 && position == blob.Length;
        if (!TryReadSshString(blob, ref position, out var exponent) || !TryReadSshString(blob, ref position, out var modulus) ||
            position != blob.Length || exponent.Length == 0 || modulus.Length == 0)
            return false;
        rsaTooSmall = new BigInteger(modulus, isUnsigned: true, isBigEndian: true).GetBitLength() < MinimumRsaBits;
        return !rsaTooSmall;
    }

    private static bool TryReadSshString(byte[] blob, ref int position, out byte[] value)
    {
        value = [];
        if (position + 4 > blob.Length)
            return false;
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(position));
        position += 4;
        if (length > (uint)(blob.Length - position))
            return false;
        value = blob.AsSpan(position, (int)length).ToArray();
        position += (int)length;
        return true;
    }

    private static string ValidateSshKeyName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaximumSshKeyNameLength || trimmed.Any(char.IsControl))
            throw new CliCommandException(CliExitCodes.ValidationError, $"SSH key names must contain 1 to {MaximumSshKeyNameLength} characters without control characters. Set one with {SshKeyNameOption}.");
        return trimmed;
    }

    private static string DefaultSshKeyName(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
    }

    private static string ExpandHomePath(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : string.Empty)
            : path;

    private void WriteSshSelectionNote(SshAccessRequest request, EnvironmentSshAccessResponse? saved)
    {
        if (request.Mode == "none")
        {
            _console.WriteLine("SSH access: Minicloud only (no personal key).");
            return;
        }

        var label = request.Mode == "existing"
            ? $"existing key {saved?.Name ?? request.KeyId}"
            : $"new key '{request.Name}' ({SshPublicKeyFingerprint(request.PublicKey!)})";
        _console.WriteLine($"SSH access: {label}{(saved?.Fingerprint is { } fingerprint && request.Mode == "existing" ? $" ({fingerprint})" : string.Empty)}.");
        _console.WriteLine("The key is installed when Minicloud first provisions this environment's VPS; new keys are registered with the provider account at that time, not now.");
        _console.WriteLine("The selection becomes immutable once server allocation starts and never changes keys on a running VPS.");
    }

    private void WriteSshAccessDetails(EnvironmentSshAccessResponse? access, SshConnectionResponse? connection)
    {
        if (access is null)
            return;
        var key = access.Mode == "none"
            ? "Minicloud only"
            : $"{access.Mode} key {access.Name ?? access.KeyId ?? "-"}{(access.Fingerprint is null ? string.Empty : $" {access.KeyType} {access.Fingerprint}")}";
        _console.WriteLine($"SSH access: {key}  state={access.RegistrationState}  revision={access.Revision}{(access.SelectionLocked ? "  locked" : string.Empty)}");
        if (connection is not null)
            _console.WriteLine($"SSH: ssh {connection.Username}@{connection.Address}");
    }

    private static async Task<T> WithSshErrorsAsync<T>(SshAccessRequest? request, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (ApiException ex) when (MapSshApiError(ex, request) is { } mapped)
        {
            throw mapped;
        }
    }

    internal static CliCommandException? MapSshApiError(ApiException ex, SshAccessRequest? request) => ex.Code switch
    {
        "ssh_key_invalid" => new(CliExitCodes.ValidationError,
            $"SSH key rejected: {ex.Message} Supply one Ed25519 or RSA (at least 3072-bit) OpenSSH public key."),
        "ssh_key_not_found" => new(CliExitCodes.ValidationError,
            $"SSH key not found: {ex.Message} Use a key ID from 'minicloud providers ssh-keys --plan <plan>' for the environment plan's provider account."),
        "ssh_selection_locked" => new(CliExitCodes.ValidationError,
            "SSH access can no longer be changed: server allocation has started or a VPS exists. The selection is immutable after allocation and does not change keys on a running VPS."),
        "ssh_selection_revision_conflict" => new(CliExitCodes.ValidationError,
            "The environment's SSH selection changed since it was read. Re-run the command to apply your selection to the latest revision."),
        "ssh_key_changed" => new(CliExitCodes.ValidationError,
            $"The selected SSH key changed or is no longer available at the provider: {ex.Message} Refresh the key inventory and select it again."),
        "ssh_selection_account_mismatch" => new(CliExitCodes.ValidationError,
            $"{ex.Message} Supply {SshKeyOption} or {SshPublicKeyFileOption} for the new plan's provider account."),
        "ssh_inventory_unavailable" => new(CliExitCodes.NetworkOrApiUnavailable,
            "The provider SSH key inventory is temporarily unavailable. Try again later."),
        "ssh_registration_failed" => new(CliExitCodes.NetworkOrApiUnavailable,
            $"The provider rejected the SSH key registration: {ex.Message}"),
        _ when ex.StatusCode == 403 && request is { Mode: not "none" } => new(CliExitCodes.AuthError,
            $"Auth error: {ex.Message} Selecting an existing SSH key requires providers:read and adding a new public key requires providers:write " +
            "(organization owner or admin), in addition to apps:write. Use an API key with these scopes or omit the SSH options."),
        _ => null
    };
}
