using System.Security.Cryptography;
using System.Text;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ManagedServerNameTests
{
    [Theory]
    [InlineData("muni", "example-suite")]
    [InlineData("short", "app")]
    public void ExistingNamesArePreserved(string organization, string app)
    {
        Assert.Equal($"minicloud-{organization}-production-{app}", ManagedServerName.ForApp(organization, app));
        Assert.Equal($"minicloud-{organization}-{app}", ManagedServerName.ForHostname(organization, app));
    }

    [Theory]
    [InlineData("db-0123456789abcdef01234567")]
    [InlineData("dbrepo-0123456789abcdef01234567")]
    public void GeneratedDatabaseNamesUseStableFullIdentityHash(string app)
    {
        const string organization = "minicloud-staging";
        var canonical = $"minicloud-{organization}-production-{app}";
        var expectedSuffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..20];
        var name = ManagedServerName.ForApp(organization, app);
        Assert.InRange(name.Length, 1, 63);
        Assert.Matches("^[a-z0-9][a-z0-9-]*[a-z0-9]$", name);
        Assert.EndsWith("-" + expectedSuffix, name);
        Assert.Equal(name, ManagedServerName.ForApp(organization, app));
        Assert.Equal(name, ManagedServerName.ForApp(organization.ToUpperInvariant(), app.ToUpperInvariant()));
    }

    [Fact]
    public void SharedReadablePrefixesDoNotCollapseDifferentOrganizationsOrApps()
    {
        var organization = new string('o', 55);
        var first = ManagedServerName.ForApp(organization + "a", "db-one");
        var second = ManagedServerName.ForApp(organization + "b", "db-one");
        var third = ManagedServerName.ForApp(organization + "a", "db-two");
        Assert.Equal(first[..42], second[..42]);
        Assert.Equal(first[..42], third[..42]);
        Assert.Equal(3, new[] { first, second, third }.Distinct().Count());
        Assert.InRange(ManagedServerName.ForHostname(organization, "dbrepo-0123456789abcdef01234567").Length, 1, 63);
    }
}
