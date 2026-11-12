// SPDX-License-Identifier: MPL-2.0
using System.Text;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Seed;

namespace Minicloud.Tests;

public sealed class SeedOperatorKeysTests
{
    internal static string PublicKey(byte value = 7) => "ssh-ed25519 " + Convert.ToBase64String(
        [0, 0, 0, 11, .. Encoding.ASCII.GetBytes("ssh-ed25519"), 0, 0, 0, 32, .. Enumerable.Repeat(value, 32)]);

    [Fact]
    public void Public_key_comments_are_removed_but_key_options_private_keys_and_extra_lines_are_rejected()
    {
        var key = PublicKey();
        Assert.Equal(key, SeedOperatorKeys.Normalize(key + " operator@example.invalid\n"));
        foreach (var invalid in new[] { "restrict " + key, key + "\n" + PublicKey(8),
            "-----BEGIN OPENSSH PRIVATE KEY-----", "ssh-rsa " + key.Split(' ')[1], "ssh-ed25519 not-base64" })
            Assert.Throws<CliCommandException>(() => SeedOperatorKeys.Normalize(invalid));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("yes", true)]
    [InlineData("N", false)]
    public void Passphrase_choice_defaults_to_protection_and_keeps_secret_entry_inside_keygen(string answer, bool protect)
    {
        Assert.Equal(protect, SeedOperatorKeys.ParseProtectionAnswer(answer));
        var args = SeedOperatorKeys.GenerationArguments("operator key", protect);
        Assert.Equal(protect ? -1 : args.Length - 2, Array.IndexOf(args, "-N"));
        if (!protect) Assert.Equal("", args[^1]);
        Assert.Throws<CliCommandException>(() => SeedOperatorKeys.ParseProtectionAnswer("maybe"));
    }

    [Fact]
    public void Empty_keygen_passphrase_is_detected_before_the_public_key_is_selected()
    {
        static string Pem(string cipher)
        {
            var value = Encoding.ASCII.GetBytes(cipher);
            byte[] data = [.. "openssh-key-v1\0"u8, 0, 0, 0, (byte)value.Length, .. value];
            return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + Convert.ToBase64String(data)
                + "\n-----END OPENSSH PRIVATE KEY-----";
        }
        Assert.False(SeedOperatorKeys.IsEncrypted(Pem("none")));
        Assert.True(SeedOperatorKeys.IsEncrypted(Pem("aes256-ctr")));
    }
}
