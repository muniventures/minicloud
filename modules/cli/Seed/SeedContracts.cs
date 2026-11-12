// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Minicloud.Cli.Commands;

namespace Minicloud.Cli.Seed;

internal sealed record SeedSelection(string Provider, string Region, string Size, string Domain, string AppDomain, string ServerVersion)
{
    public void Validate()
    {
        SeedGuard.Require(Provider is "hetzner" or "digitalocean" or "vultr", "Choose hetzner, digitalocean or vultr.");
        foreach (var value in new[] { Region, Size })
            SeedGuard.Require(Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,100}$"), "Explicit region and size IDs are required.");
        foreach (var host in new[] { Domain, AppDomain })
            SeedGuard.Require(host.Length <= 253 && host.Contains('.') && host == host.ToLowerInvariant()
                && host.Split('.').All(x => Regex.IsMatch(x, @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$"))
                && Uri.CheckHostName(host) == UriHostNameType.Dns
                && !new[] { "localhost", "local", "internal", "invalid", "test", "example" }.Any(x => host.EndsWith("." + x)),
                "Use lowercase public DNS hostnames, not IPs, URLs, wildcard or local domains.");
        var api = "api." + Domain;
        SeedGuard.Require(api.Length <= 253 && !Overlap(Domain, AppDomain) && !Overlap(api, AppDomain),
            "The application suffix must not overlap dashboard/API hostnames.");
        SeedGuard.Require(Regex.IsMatch(ServerVersion, @"^server-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$"),
            "Select an exact server-vMAJOR.MINOR.PATCH[-prerelease], never latest or a CLI v* tag.");
    }

    private static bool Overlap(string a, string b) => a == b || a.EndsWith("." + b) || b.EndsWith("." + a);
}

internal sealed record SeedCost(string Resource, decimal Monthly, decimal? Hourly, string Currency, string Billing, string Exclusions, string Source);
internal sealed record SeedQuote(string Account, string CredentialScope, string Image, string Architecture, string SshUser,
    int Cpus, int MemoryMiB, int DiskGiB, SeedCost[] Costs, DateTimeOffset RetrievedAt, string Restrictions, string? NetworkId = null);
internal sealed record SeedPlan(SeedSelection Selection, SeedQuote Quote, string ReleaseDigest, string ReleaseSource)
{
    [JsonIgnore]
    public string Approval => SeedGuard.Hash(JsonSerializer.Serialize(new
    {
        Selection, Quote = Quote with { RetrievedAt = DateTimeOffset.UnixEpoch }, ReleaseDigest, ReleaseSource
    }, SeedJournal.Json));
}

internal sealed record SeedResource(string Id, string Operation, string Region, string Size, string Image, string? Address, string? PrimaryIpId = null);
internal sealed class SeedState
{
    public int Schema { get; set; } = 1;
    public required string Id { get; set; }
    public required SeedPlan Plan { get; set; }
    public string Phase { get; set; } = "planned";
    public string? SshKeyId { get; set; }
    public bool KeyCreateStarted { get; set; }
    public bool ServerCreateStarted { get; set; }
    public SeedResource? Server { get; set; }
    public string? HostFingerprint { get; set; }
    public bool HostPrepared { get; set; }
    public bool InstallStarted { get; set; }
    public string? InstallationId { get; set; }
    public string? InstallerOperationId { get; set; }
    public string? InstallerState { get; set; }
    public string? PendingTokenOutput { get; set; }
    public string? LastFailurePhase { get; set; }
    public bool RecoveryExported { get; set; }
    public bool RecoveryAcknowledged { get; set; }
    public string? RecoveryDirectory { get; set; }
    public string? SetupTokenExportPath { get; set; }
    public string? ProviderAccountId { get; set; }
    public string? ImportOrganization { get; set; }
    public string? ImportName { get; set; }
    public bool ImportStarted { get; set; }
    public string? InstanceName { get; set; }
    public bool ServerDeleted { get; set; }
    public bool KeyDeleted { get; set; }
}

internal interface ISeedProvider : IDisposable
{
    string CredentialScope(string operation);
    Task<string> GetAccountIdentityAsync(CancellationToken ct);
    Task<SeedQuote> QuoteAsync(SeedSelection selection, string operation, CancellationToken ct);
    Task<string?> FindKeyAsync(string operation, string publicKey, CancellationToken ct);
    Task<string> CreateKeyAsync(string operation, string publicKey, CancellationToken ct);
    Task<SeedResource?> FindServerAsync(string operation, CancellationToken ct);
    Task<SeedResource> CreateServerAsync(SeedState state, CancellationToken ct);
    Task<bool> DeleteServerAsync(SeedState state, CancellationToken ct);
    Task DeleteKeyAsync(string id, string operation, string publicKey, CancellationToken ct);
}

internal interface ISeedHost
{
    Task CheckPrerequisitesAsync(CancellationToken ct);
    Task GenerateKeyAsync(string directory, CancellationToken ct);
    Task TrustAsync(SeedState state, string directory, string? independentFingerprint, CancellationToken ct);
    Task PrepareAsync(SeedState state, string directory, CancellationToken ct);
    Task<bool> DnsReadyAsync(SeedState state, CancellationToken ct);
    Task<string> InstallAsync(SeedState state, string directory, bool statusOnly, CancellationToken ct);
    Task ExportAsync(SeedState state, string directory, string destination, bool setupToken, CancellationToken ct);
    Task VerifyHttpsAsync(SeedState state, CancellationToken ct);
}

internal static class SeedGuard
{
    public static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new CliCommandException(CliExitCodes.ValidationError, message);
    }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Scope(string token, string operation) => Hash("minicloud-seed-scope:" + operation + ":" + token);
}
