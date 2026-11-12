// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using Minicloud.Common.Validation;

namespace Minicloud.Cli.Seed;

internal static class SeedOperatorKeys
{
    internal static string Normalize(string value)
    {
        SeedGuard.Require(SshPublicKeyParser.TryParse(value, out var key),
            "Supply one OpenSSH public key per file: ed25519 or RSA of at least 3072 bits; no private keys or authorized_keys options.");
        return string.Join(' ', key!.PublicKey.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2));
    }

    internal static string[] Read(IEnumerable<string> paths) => paths.Select(path =>
    {
        SeedFiles.NoLinks(path);
        SeedGuard.Require(new FileInfo(path).Length is > 0 and <= SshPublicKeyParser.MaximumInputLength,
            "Public-key file is empty or too large.");
        return Normalize(File.ReadAllText(path));
    }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static void Validate(IEnumerable<string> keys)
    {
        var values = keys.ToArray();
        SeedGuard.Require(values.Length <= 16 && values.All(key => Normalize(key) == key)
            && values.Distinct(StringComparer.Ordinal).Count() == values.Length,
            "Operator public keys must be distinct canonical OpenSSH keys (maximum 16).");
    }

    public static async Task<string[]> SelectAsync(List<string> paths, string? generatePath, bool? protect,
        bool skip, IConsole console, CancellationToken ct)
    {
        SeedGuard.Require(!(skip && (paths.Count > 0 || generatePath is not null || protect is not null))
            && (protect is null || generatePath is not null), "Conflicting operator SSH-key options.");
        if (!skip && paths.Count == 0 && generatePath is null && !Console.IsInputRedirected)
        {
            console.Write("Add operator SSH access: [p]ublic key, [g]enerate a key, [s]kip (default): ");
            var choice = (console.ReadLine() ?? "").Trim().ToLowerInvariant();
            SeedGuard.Require(choice is "" or "s" or "p" or "g", "Choose public key, generate or skip.");
            if (choice == "p")
            {
                do
                {
                    console.Write("Public-key file path (blank to finish): ");
                    var path = console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(path)) break;
                    paths.Add(path);
                    SeedGuard.Require(paths.Count <= 16, "At most 16 operator SSH keys are supported.");
                } while (true);
                SeedGuard.Require(paths.Count > 0, "Select a public-key file or explicitly skip operator SSH access.");
            }
            if (choice == "g")
            {
                console.Write("New local private-key path (will not overwrite): ");
                generatePath = console.ReadLine()?.Trim();
                SeedGuard.Require(!string.IsNullOrWhiteSpace(generatePath), "A new local key path is required.");
            }
        }
        if (generatePath is not null)
        {
            if (protect is null)
            {
                SeedGuard.Require(!Console.IsInputRedirected,
                    "Select --ssh-key-passphrase or --no-ssh-key-passphrase explicitly for noninteractive generation.");
                console.Write("Protect the new operator private key with a passphrase? [Y/n]: ");
                protect = ParseProtectionAnswer(console.ReadLine());
            }
            await GenerateAsync(generatePath, protect.Value, console, ct);
            paths.Add(generatePath + ".pub");
        }
        var keys = Read(paths);
        Validate(keys);
        return keys;
    }

    internal static bool ParseProtectionAnswer(string? answer) => (answer ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "y" or "yes" => true,
        "n" or "no" => false,
        _ => throw new Commands.CliCommandException(CliExitCodes.ValidationError, "Choose yes or no for passphrase protection.")
    };

    internal static string[] GenerationArguments(string path, bool protect) =>
        protect ? ["-t", "ed25519", "-C", "minicloud-operator", "-f", path]
                : ["-t", "ed25519", "-C", "minicloud-operator", "-f", path, "-N", ""];

    private static async Task GenerateAsync(string path, bool protect, IConsole console, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        SeedFiles.Check(Path.GetDirectoryName(full)!, true);
        SeedGuard.Require(!File.Exists(full) && !Directory.Exists(full)
            && !File.Exists(full + ".pub") && !Directory.Exists(full + ".pub"),
            "Operator key paths already exist; never overwrite or replace an existing key.");
        SeedGuard.Require(!protect || !Console.IsInputRedirected && !Console.IsOutputRedirected,
            "Passphrase-protected generation requires an interactive terminal for ssh-keygen's hidden prompts.");
        console.WriteLine(protect
            ? "ssh-keygen will ask for the passphrase twice. Enter a nonempty passphrase; it is never handled or stored by Minicloud."
            : "Generating an unencrypted operator key by explicit choice. Protect its private file.");
        var start = new ProcessStartInfo("ssh-keygen") { UseShellExecute = false };
        foreach (var arg in GenerationArguments(full, protect)) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new Commands.CliCommandException(CliExitCodes.ValidationError,
                "ssh-keygen is unavailable. Install OpenSSH or supply an existing --ssh-public-key file.");
        }
        using var registration = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(ct);
        SeedGuard.Require(process.ExitCode == 0, "Operator key generation failed; preserve any partial files and select a fresh path.");
        SeedFiles.Check(full);
        SeedGuard.Require(!protect || IsEncrypted(File.ReadAllText(full)),
            "An empty passphrase was entered. Key retained locally but not selected; generate a new protected key or explicitly choose unprotected generation.");
        _ = Read([full + ".pub"]);
        console.WriteLine("Operator private key retained locally: " + full);
    }

    internal static bool IsEncrypted(string privateKey)
    {
        const string begin = "-----BEGIN OPENSSH PRIVATE KEY-----";
        const string end = "-----END OPENSSH PRIVATE KEY-----";
        var encoded = privateKey.Trim();
        SeedGuard.Require(encoded.StartsWith(begin, StringComparison.Ordinal) && encoded.EndsWith(end, StringComparison.Ordinal),
            "Expected an OpenSSH private key from ssh-keygen.");
        var blob = Convert.FromBase64String(encoded[begin.Length..^end.Length]);
        var magic = "openssh-key-v1\0"u8;
        SeedGuard.Require(blob.AsSpan().StartsWith(magic) && blob.Length >= magic.Length + 4, "Invalid generated private-key header.");
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(magic.Length));
        SeedGuard.Require(length > 0 && length <= blob.Length - magic.Length - 4, "Invalid generated cipher header.");
        return !blob.AsSpan(magic.Length + 4, (int)length).SequenceEqual("none"u8);
    }
}
