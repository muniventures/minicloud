using System.Numerics;
using System.Security.Cryptography;

namespace Minicloud.Common.Validation;

public sealed record ValidatedSshPublicKey(string PublicKey, string Fingerprint, string KeyType);

public static class SshPublicKeyParser
{
    public const int MaximumInputLength = 16 * 1024;
    private const int MinimumRsaBits = 3072;

    public static bool TryParse(string? value, out ValidatedSshPublicKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumInputLength)
            return false;

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1 || lines[0].Contains('\r') ||
            lines[0].Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is not ("ssh-ed25519" or "ssh-rsa"))
            return false;

        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { return false; }

        var reader = new SshBlobReader(blob);
        if (!reader.TryReadString(out var algorithm) ||
            !string.Equals(System.Text.Encoding.ASCII.GetString(algorithm), parts[0], StringComparison.Ordinal))
            return false;

        var valid = parts[0] switch
        {
            "ssh-ed25519" => reader.TryReadString(out var keyBytes) && keyBytes.Length == 32 && reader.AtEnd,
            "ssh-rsa" => TryValidateRsa(ref reader),
            _ => false
        };
        if (!valid)
            return false;

        var normalized = string.Join(' ', parts);
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        key = new ValidatedSshPublicKey(normalized, fingerprint, parts[0] == "ssh-ed25519" ? "ed25519" : "rsa");
        return true;
    }

    private static bool TryValidateRsa(ref SshBlobReader reader)
    {
        if (!reader.TryReadString(out var exponentBytes) || !reader.TryReadString(out var modulusBytes) || !reader.AtEnd)
            return false;
        if (!IsCanonicalPositiveMpint(exponentBytes) || !IsCanonicalPositiveMpint(modulusBytes))
            return false;
        var exponent = new BigInteger(exponentBytes, isUnsigned: true, isBigEndian: true);
        var modulus = new BigInteger(modulusBytes, isUnsigned: true, isBigEndian: true);
        return exponent > BigInteger.One && !exponent.IsEven && !modulus.IsEven && modulus.GetBitLength() >= MinimumRsaBits;
    }

    private static bool IsCanonicalPositiveMpint(byte[] value) =>
        (value.Length > 0 && value[0] != 0 && (value[0] & 0x80) == 0) ||
        (value.Length > 1 && value[0] == 0 && (value[1] & 0x80) != 0);

    private ref struct SshBlobReader(ReadOnlySpan<byte> blob)
    {
        private readonly ReadOnlySpan<byte> _blob = blob;
        private int _position;

        public bool AtEnd => _position == _blob.Length;

        public bool TryReadString(out byte[] value)
        {
            value = [];
            if (_position + sizeof(uint) > _blob.Length)
                return false;
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(_blob[_position..]);
            _position += sizeof(uint);
            if (length < 0 || length > _blob.Length - _position)
                return false;
            value = _blob.Slice(_position, length).ToArray();
            _position += length;
            return true;
        }
    }
}
