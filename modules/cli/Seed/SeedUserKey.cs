// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Text;

namespace Minicloud.Cli.Seed;

internal static class SeedUserKey
{
    internal static string PublicKey(string privateKey)
    {
        const string begin = "-----BEGIN OPENSSH PRIVATE KEY-----";
        const string end = "-----END OPENSSH PRIVATE KEY-----";
        var encoded = privateKey.Trim();
        SeedGuard.Require(encoded.StartsWith(begin, StringComparison.Ordinal) && encoded.EndsWith(end, StringComparison.Ordinal),
            "Supply an OpenSSH private key, never a passphrase in arguments.");
        var blob = Convert.FromBase64String(encoded[begin.Length..^end.Length]);
        var magic = "openssh-key-v1\0"u8;
        SeedGuard.Require(blob.AsSpan().StartsWith(magic), "Invalid OpenSSH identity.");
        var offset = magic.Length;
        byte[] Field()
        {
            SeedGuard.Require(offset + 4 <= blob.Length, "Invalid OpenSSH identity.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset));
            offset += 4;
            SeedGuard.Require(length <= blob.Length - offset, "Invalid OpenSSH identity.");
            var value = blob.AsSpan(offset, (int)length).ToArray();
            offset += (int)length;
            return value;
        }
        _ = Field(); _ = Field(); _ = Field();
        SeedGuard.Require(offset + 4 <= blob.Length && BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset)) == 1,
            "Select one user-owned SSH identity.");
        offset += 4;
        var key = Field();
        SeedGuard.Require(key.Length > 4, "Invalid public identity.");
        var typeLength = BinaryPrimitives.ReadUInt32BigEndian(key);
        SeedGuard.Require(typeLength <= key.Length - 4, "Invalid public identity.");
        var type = Encoding.ASCII.GetString(key, 4, (int)typeLength);
        return SeedOperatorKeys.Normalize(type + " " + Convert.ToBase64String(key));
    }

    public static async Task<string> ReadAsync(string path, bool useAgent, CancellationToken ct)
    {
        SeedFiles.Check(path);
        SeedGuard.Require(new FileInfo(path).Length is > 0 and < 1048576, "Invalid SSH identity file size.");
        var privateKey = await File.ReadAllTextAsync(path, ct);
        var key = PublicKey(privateKey);
        var encrypted = SeedOperatorKeys.IsEncrypted(privateKey);
        SeedGuard.Require(!encrypted || useAgent,
            "Encrypted SSH identity requires explicit --use-ssh-agent and an independently unlocked matching key. Never remove its passphrase.");
        if (useAgent)
        {
            SeedGuard.Require(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_AUTH_SOCK")),
                "--use-ssh-agent requires SSH_AUTH_SOCK. The agent is never forwarded.");
            var agent = await SeedProcess.RunAsync("ssh-add", ["-L"], null, ct, true);
            SeedGuard.Require(agent.ExitCode == 0 && agent.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)) == key),
                "Explicit SSH agent does not contain the supplied user identity.");
        }
        else
        {
            var derived = await SeedProcess.RequireAsync("ssh-keygen", ["-y", "-P", "", "-f", path], null, ct);
            SeedGuard.Require(SeedOperatorKeys.Normalize(derived.Trim()) == key, "User SSH identity is inconsistent.");
        }
        return key;
    }
}
