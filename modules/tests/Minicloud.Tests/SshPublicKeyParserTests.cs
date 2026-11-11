using System.Buffers.Binary;
using System.Text;
using Minicloud.Common.Validation;

namespace Minicloud.Tests;

public sealed class SshPublicKeyParserTests
{
    [Fact]
    public void TryParse_NormalizesEd25519KeyAndDerivesFingerprint()
    {
        var blob = Blob("ssh-ed25519", Enumerable.Range(0, 32).Select(x => (byte)x).ToArray());
        var input = "  ssh-ed25519   " + Convert.ToBase64String(blob) + "  work laptop\r\n";

        Assert.True(SshPublicKeyParser.TryParse(input, out var key));

        Assert.NotNull(key);
        Assert.Equal($"ssh-ed25519 {Convert.ToBase64String(blob)} work laptop", key.PublicKey);
        Assert.StartsWith("SHA256:", key.Fingerprint);
        Assert.Equal("ed25519", key.KeyType);
    }

    [Fact]
    public void TryParse_AcceptsRsaAtMinimumStrength()
    {
        var modulus = new byte[384];
        modulus[0] = 0x80;
        modulus[^1] = 1;
        var blob = Blob("ssh-rsa", [1, 0, 1], [0, .. modulus]);

        Assert.True(SshPublicKeyParser.TryParse($"ssh-rsa {Convert.ToBase64String(blob)}", out var key));
        Assert.Equal("rsa", key!.KeyType);
    }

    [Theory]
    [InlineData("command=\"touch /tmp/x\" ssh-ed25519 AAAA")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("ssh-ed25519 AAAA\nssh-ed25519 AAAA")]
    [InlineData("ssh-ed25519 !!!")]
    [InlineData("ssh-rsa AQAB")]
    public void TryParse_RejectsUnsupportedOrMalformedMaterial(string input)
    {
        Assert.False(SshPublicKeyParser.TryParse(input, out _));
    }

    private static byte[] Blob(string algorithm, params byte[][] fields)
    {
        using var stream = new MemoryStream();
        WriteString(stream, Encoding.ASCII.GetBytes(algorithm));
        foreach (var field in fields)
            WriteString(stream, field);
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
