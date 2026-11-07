using Minicloud.Common.Redaction;

namespace Minicloud.Tests;

public sealed class CommonRedactionTests
{
    [Fact]
    public void RedactBearerCredential()
    {
        var redacted = LogRedactor.Redact("call with Authorization: Bearer abcDEF123 ok");
        Assert.Contains("Authorization: Bearer [REDACTED]", redacted);
        Assert.DoesNotContain("abcDEF123", redacted);
    }

    [Theory]
    [InlineData("token mcl_ingest_xyz123")]
    [InlineData("token mc_live_abc456")]
    [InlineData("token mrt_def789")]
    public void RedactServiceTokens(string message)
    {
        var redacted = LogRedactor.Redact($"ingest {message} done");
        Assert.DoesNotContain("mcl_ingest_xyz123", redacted);
        Assert.DoesNotContain("mc_live_abc456", redacted);
        Assert.DoesNotContain("mrt_def789", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void RedactSecretAssignmentKeepsKeyName()
    {
        var redacted = LogRedactor.Redact("connect password=hunter2 now");
        Assert.Contains("password=[REDACTED]", redacted);
        Assert.DoesNotContain("hunter2", redacted);
    }

    [Fact]
    public void RedactJwt()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        Assert.DoesNotContain(jwt, LogRedactor.Redact($"bearer {jwt}"));
    }

    [Fact]
    public void SecretFieldNamesRedactWholesale()
    {
        Assert.True(LogRedactor.IsSecretFieldName("api_key"));
        Assert.True(LogRedactor.IsSecretFieldName("client_secret"));
        Assert.False(LogRedactor.IsSecretFieldName("service_name"));
        Assert.Equal("[REDACTED]", LogRedactor.RedactAttributeValue("password", "hunter2"));
        Assert.Equal("plain", LogRedactor.RedactAttributeValue("service", "plain"));
    }
}
