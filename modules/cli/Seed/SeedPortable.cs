// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Minicloud.Cli.Seed;

internal sealed record SeedPortableConfig
{
    public int SchemaVersion { get; init; }
    public string SeedId { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Region { get; init; } = "";
    public string Size { get; init; } = "";
    public string Domain { get; init; } = "";
    public string AppDomain { get; init; } = "";
    public string ServerVersion { get; init; } = "";
    [JsonIgnore]
    public SeedSelection Selection => new(Provider, Region, Size, Domain, AppDomain, ServerVersion);
    public string ProviderAccount { get; init; } = "";
    public string? ProviderCredential { get; init; }
    public SeedPortableTarget? Target { get; init; }
    public string Architecture { get; init; } = "linux/amd64";
    public string SshUser { get; init; } = "root";
    [JsonIgnore]
    public string? HostFingerprint => Target?.HostFingerprint;
    public string? InitialCreationIntent { get; init; }
    public string[] OperatorPublicKeys { get; init; } = [];

    internal const string CreationIntent = "one-authorized-creator-no-unresolved-create";

    public static SeedPortableConfig Parse(string json)
    {
        using var document = SeedPortable.ReadDocument(json);
        var config = document.RootElement.Deserialize<SeedPortableConfig>(SeedJournal.Json)
            ?? throw new JsonException("Expected a seed configuration object.");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        SeedGuard.Require(SchemaVersion == 1 && Regex.IsMatch(SeedId, "^[0-9a-f]{32}$"),
            "Use schemaVersion 1 and a caller-held 32-character lowercase hex seedId. Generate it once with seed generate-id.");
        Selection.Validate();
        SeedOperatorKeys.Validate(OperatorPublicKeys);
        SeedGuard.Require(!string.IsNullOrWhiteSpace(ProviderAccount) && ProviderAccount.Length <= 200
            && !ProviderAccount.Any(char.IsControl), "An explicit stable providerAccount scope is required, not credential bytes.");
        SeedGuard.Require(SshUser == "root" && Architecture is "linux/amd64" or "linux/arm64",
            "Seed currently supports root SSH on linux/amd64 or linux/arm64.");
        SeedGuard.Require(InitialCreationIntent is null or CreationIntent,
            "Unknown initialCreationIntent; explicit independent reconciliation is required.");
        SeedGuard.Require(ProviderCredential is null || ProviderCredential.Length is > 0 and <= 16384
            && !ProviderCredential.Any(char.IsWhiteSpace), "Provider credential must be one bounded value.");
        SeedGuard.Require(HostFingerprint is null || Regex.IsMatch(HostFingerprint, @"^SHA256:[A-Za-z0-9+/]{43}$"),
            "Supply an independently verified ed25519 hostFingerprint.");
        if (Target is { } target)
        {
            SeedGuard.Require(Regex.IsMatch(target.ServerId, "^[A-Za-z0-9-]{1,100}$")
                && (target.Image is null || Regex.IsMatch(target.Image, "^[A-Za-z0-9._-]{1,100}$"))
                && (target.Ipv4 is null || System.Net.IPAddress.TryParse(target.Ipv4, out var address)
                    && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork),
                "Explicit target identity/region/size/image/IPv4 conflicts with configuration; reconcile.");
        }
    }

    public SeedState State() => new()
    {
        Id = SeedId, Server = Target is null ? null : new(Target.ServerId, SeedId, Region, Size, Target.Image ?? "",
            Target.Ipv4, Target.PrimaryIpId), HostFingerprint = HostFingerprint, ProviderAccountId = ProviderAccount,
        Plan = new(Selection, new(ProviderAccount, "authorized-account-access", Target?.Image ?? "", Architecture,
            SshUser, 0, 0, 0, [], DateTimeOffset.UnixEpoch, "Explicit target; no creation authority implied."), "", "")
            { OperatorPublicKeys = OperatorPublicKeys }
    };
}

internal sealed record SeedPortableTarget(string ServerId, string? Ipv4 = null, string? HostFingerprint = null,
    string? Image = null, string? PrimaryIpId = null);

internal sealed record SeedObservation(string State, string Progress, string? InstallationId = null,
    string? OperationId = null, string? Release = null, string? Source = null, string Health = "unknown",
    string OpenBao = "unknown", string Browser = "unknown", bool Prepared = false);

internal interface ISeedInspector
{
    Task<SeedObservation> InspectAsync(SeedState state, string directory, CancellationToken ct);
}

internal sealed record SeedPortableReport
{
    public int SchemaVersion { get; init; } = 1;
    public string Kind { get; init; } = "minicloud-seed-report";
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public required string SeedId { get; init; }
    public string Provider { get; init; } = "";
    public string ProviderAccount { get; init; } = "";
    public string Region { get; init; } = "";
    public string Size { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Classification { get; set; } = "reconciliation_required";
    public string Result { get; set; } = "unknown";
    public string? Approval { get; set; }
    public SeedPlan? Plan { get; set; }
    public SeedResource? Target { get; set; }
    public string? ProviderSshKeyId { get; set; }
    public string SshUser { get; init; } = "root";
    public string? VerifiedHostFingerprint { get; set; }
    public string? RootKeyFingerprint { get; set; }
    public string IndependentRecoveryCustody { get; init; } = "unknown";
    public required string Domain { get; init; }
    public required string AppDomain { get; init; }
    public required string RequestedRelease { get; init; }
    public SeedObservation Observation { get; set; } = new("unknown", "unknown");
    public string[] Dns { get; set; } = [];
    public string[] Reasons { get; set; } = [];
    public string[] NextActions { get; set; } = [];
    public string[] RetainedBillableResources { get; set; } = [];
}

internal static class SeedPortable
{
    internal static JsonDocument ReadDocument(string json)
    {
        var document = JsonDocument.Parse(json);
        try
        {
            Check(document.RootElement);
            return document;
        }
        catch { document.Dispose(); throw; }

        static void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    SeedGuard.Require(names.Add(property.Name), "Duplicate seed JSON property.");
                    Check(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Check(item);
        }
    }

    public static async Task<SeedObservation> ContinueAsync(SeedState state, string directory, ISeedHost host,
        ISeedInspector inspector, SeedObservation observation, CancellationToken ct)
    {
        var classification = Classify(observation, state.Plan.Selection.ServerVersion);
        if (classification != "continue_partial") return observation;
        if (!observation.Prepared) await host.PrepareAsync(state, directory, ct);
        state.HostPrepared = true;
        if (state.Plan.OperatorPublicKeys.Length > 0) await host.AddOperatorKeysAsync(state, directory, ct);
        if (!await host.DnsReadyAsync(state, ct))
            return observation with { Progress = "awaiting_dns", Health = "pending", Prepared = true };
        await host.InstallAsync(state, directory, false, ct);
        return await inspector.InspectAsync(state, directory, ct);
    }

    public static string Classify(SeedObservation observation, string requestedRelease) => observation.State switch
    {
        "absent" => "new_seed",
        "empty" or "preparing" or "awaiting_trust" => "continue_partial",
        "installed" or "public" when observation.Release != requestedRelease => "upgrade_required",
        "public" => "current",
        "installed" when observation.Progress is "ready" or "awaiting_operator" or "awaiting_dns_tls" => "current",
        "installed" => "continue_partial",
        _ => "reconciliation_required"
    };

    public static string Approval(SeedPortableConfig config, SeedState state, string classification, string publicKey, bool newSeed)
        => SeedGuard.Hash(JsonSerializer.Serialize(new
        {
            config.SeedId, config.ProviderAccount, config.InitialCreationIntent, NewSeed = newSeed,
            Plan = state.Plan with { Quote = state.Plan.Quote with { RetrievedAt = DateTimeOffset.UnixEpoch } },
            Classification = classification, Target = state.Server,
            config.HostFingerprint, PublicKey = publicKey
        }, SeedJournal.Json));

    public static void Observe(SeedPortableReport report, SeedState state, SeedObservation observation)
    {
        report.Target = state.Server;
        report.VerifiedHostFingerprint = state.HostFingerprint;
        report.Observation = observation;
        report.Classification = Classify(observation, report.RequestedRelease);
        report.Result = observation.State is "unavailable" || observation.Health == "unavailable"
            || observation.OpenBao == "unavailable" || observation.Browser == "unavailable" ? "unavailable"
            : report.Classification == "reconciliation_required" ? "failed"
            : observation.Health == "ready" && observation.OpenBao == "ready" && observation.Browser == "ready" ? "ready" : "pending";
        report.RetainedBillableResources = state.Server is null ? [] :
            new[] { "server:" + state.Server.Id }.Concat(state.Server.PrimaryIpId is { } ip ? new[] { "ipv4:" + ip } : []).ToArray();
        report.Dns = state.Server?.Address is { } address
            ? [$"{report.Domain}. A {address}", $"api.{report.Domain}. A {address}"] : [];
        report.Reasons = [report.Classification switch
        {
            "new_seed" => "Inventory absence is not evidence that an earlier create failed.",
            "continue_partial" => "Continue only this verified target, preserving original installation and operation identities.",
            "current" => "Requested release is installed; live health and browser/recovery handoffs remain separate observations.",
            "upgrade_required" => "Installed release discovered before comparison. Seed never performs upgrades.",
            _ => "Identity, metadata, maintenance or host observations are unsafe or unavailable."
        }];
        report.NextActions = [report.Classification switch
        {
            "new_seed" => "Only an initial reviewed --new-seed plan and one authorized creator may create. Reconcile any unresolved request with provider support.",
            "continue_partial" => "Review plan, then apply its exact approval. DNS waits retain billing; never create a replacement.",
            "current" => "Check live health; export recovery independently and complete normal browser setup. Never reinitialize sealed OpenBao.",
            "upgrade_required" => "Use existing signed coordinated maintenance with explicit upgrade approval, previous verified assets, backup inputs, compatibility and fencing/drain.",
            _ => "Inspect provider console and protected host metadata. Resolve maintenance/restore or identity conflicts; do not reset host trust or reinstall."
        }];
        state.InstallationId = observation.InstallationId;
        state.InstallerOperationId = observation.OperationId;
        state.HostPrepared = observation.Prepared;
        state.InstallerState = observation.Progress;
    }

    public static int ExitCode(SeedPortableReport report) => report.Classification switch
    {
        "upgrade_required" => 11,
        "reconciliation_required" => 12,
        _ => report.Result == "ready" ? 0 : 10
    };

    public static async Task CreateAsync(SeedPortableConfig config, SeedState state, ISeedProvider provider,
        string publicKey, bool newSeed, string? approval, string classification, CancellationToken ct)
    {
        SeedGuard.Require(newSeed && config.Target is null && classification == "new_seed"
            && config.InitialCreationIntent == SeedPortableConfig.CreationIntent,
            "Creation requires explicit initialCreationIntent, --new-seed, no unresolved prior request, and one authorized creator.");
        SeedGuard.Require(approval == Approval(config, state, classification, publicKey, newSeed),
            "Review a fresh exact plan before apply. Approval must bind identity, account, cost, release, key and target.");
        SeedGuard.Require(await provider.FindServerAsync(state.Id, ct) is null,
            "Target appeared after planning. Replan and reconcile; no creation.");
        var key = await provider.FindKeyAsync(state.Id, publicKey, ct);
        // An existing operation key can be evidence of an interrupted create before inventory became visible.
        SeedGuard.Require(key is null, "Operation key already exists. Reconcile any prior create; no automatic retry.");
        state.SshKeyId = await provider.FindUserKeyAsync(publicKey, ct);
        if (state.SshKeyId is null)
        {
            state.KeyCreateStarted = true;
            state.SshKeyId = await provider.CreateKeyAsync(state.Id, publicKey, ct);
        }
        state.ServerCreateStarted = true;
        state.Server = await provider.CreateServerAsync(state, ct);
        SeedLifecycle.Match(state, state.Server);
    }
}
