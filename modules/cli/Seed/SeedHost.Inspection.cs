// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minicloud.Cli.Seed;

internal sealed partial class SeedHost : ISeedInspector
{
    internal static string InspectionScript
    {
        get
        {
            using var stream = typeof(SeedHost).Assembly.GetManifestResourceStream("Minicloud.Cli.Seed.inspect-host.py")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public async Task<SeedObservation> InspectAsync(SeedState state, string directory, CancellationToken ct)
    {
        var result = await SshAsync(state, directory, "python3 -B -c " + Quote(InspectionScript) + " " + Quote(state.Id), null, ct);
        if (result.ExitCode != 0) return new("unavailable", "unknown");
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        var observation = root.Deserialize<SeedObservation>(new JsonSerializerOptions(SeedJournal.Json)
            { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip })!;
        if (root.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.Object)
        {
            if (identity.GetProperty("provider").GetString() != state.Plan.Selection.Provider
                || identity.GetProperty("providerAccount").GetString() != state.ProviderAccountId
                || identity.GetProperty("serverId").GetString() != state.Server!.Id
                || identity.GetProperty("region").GetString() != state.Plan.Selection.Region
                || identity.GetProperty("size").GetString() != state.Plan.Selection.Size
                || identity.GetProperty("architecture").GetString() != state.Plan.Quote.Architecture
                || !string.IsNullOrEmpty(state.Plan.Quote.Image) && identity.GetProperty("image").GetString() != state.Plan.Quote.Image)
                return new("reconciliation_required", "unknown");
            if (string.IsNullOrEmpty(state.Plan.Quote.Image))
                state.Plan = state.Plan with { Quote = state.Plan.Quote with { Image = identity.GetProperty("image").GetString()! } };
        }
        if (observation.State != "installed") return observation;
        var configuration = root.GetProperty("configuration");
        var selection = state.Plan.Selection;
        if (configuration.GetProperty("consoleUrl").GetString() != "https://" + selection.Domain
            || configuration.GetProperty("apiUrl").GetString() != "https://api." + selection.Domain
            || configuration.GetProperty("appDomain").GetString() != selection.AppDomain
            || !root.GetProperty("publicAddresses").EnumerateArray().Select(x => x.GetString()).SequenceEqual([state.Server!.Address]))
            return new("reconciliation_required", "unknown");
        if (observation.Health != "pending") return observation;
        return await InspectPublicAsync(state, observation, ct);
    }

    internal static async Task<SeedObservation> InspectPublicAsync(SeedState state, SeedObservation observation, CancellationToken ct)
    {
        try
        {
            if (!await new SeedHost().DnsReadyAsync(state, ct)) return observation with { Health = "pending", Browser = "unknown" };
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
            var api = "https://api." + state.Plan.Selection.Domain;
            using var response = await http.GetAsync(api + "/api/instance", ct);
            if (response.StatusCode != HttpStatusCode.OK) return observation with { Health = "unavailable" };
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var value = json.RootElement;
            if (value.GetProperty("product").GetString() != "minicloud" || value.GetProperty("protocolVersion").GetInt32() != 1
                || "server-v" + value.GetProperty("serverVersion").GetString() != observation.Release
                || value.GetProperty("sourceRevision").GetString() != observation.Source
                || value.GetProperty("appDomain").GetString() != state.Plan.Selection.AppDomain)
                return observation with { State = "reconciliation_required", Health = "failed" };
            SeedRelease.RequireCompatible(value.GetProperty("cliCompatibility"));
            using var health = await http.GetAsync(api + "/health", ct);
            if (health.StatusCode != HttpStatusCode.OK || await health.Content.ReadAsStringAsync(ct) != "Healthy")
                return observation with { Health = "unavailable" };
            using var setup = await http.GetAsync(api + "/api/account/setup", ct);
            if (setup.StatusCode != HttpStatusCode.OK) return observation with { Health = "pending", Browser = "unavailable" };
            using var browser = JsonDocument.Parse(await setup.Content.ReadAsStringAsync(ct));
            var phase = browser.RootElement.GetProperty("state").GetString();
            using var runtime = await http.GetAsync("https://" + state.Plan.Selection.Domain + "/runtime-config.json", ct);
            if (runtime.StatusCode != HttpStatusCode.OK) return observation with { Health = "pending", Browser = "unavailable" };
            using var runtimeJson = JsonDocument.Parse(await runtime.Content.ReadAsStringAsync(ct));
            if (runtimeJson.RootElement.GetProperty("apiBaseUrl").GetString() != api
                || runtimeJson.RootElement.GetProperty("appDomain").GetString() != state.Plan.Selection.AppDomain)
                return observation with { State = "reconciliation_required", Health = "failed" };
            using var page = await http.GetAsync("https://" + state.Plan.Selection.Domain + "/setup", ct);
            if (page.StatusCode != HttpStatusCode.OK || page.Content.Headers.ContentType?.MediaType != "text/html")
                return observation with { Health = "pending", Browser = "unavailable" };
            return observation with { Health = "ready", Browser = phase == "ready" ? "ready"
                : phase is "required" or "in_progress" ? "pending" : "failed" };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
            or Commands.CliCommandException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return observation with { Health = "unavailable", Browser = "unknown" };
        }

    }

    internal static async Task<SeedObservation> InspectPublicTargetAsync(SeedState state, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
            using var response = await http.GetAsync("https://api." + state.Plan.Selection.Domain + "/api/instance", ct);
            if (response.StatusCode != HttpStatusCode.OK) return new("unavailable", "unknown");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var value = json.RootElement;
            var observation = new SeedObservation("public", "unknown",
                Release: "server-v" + value.GetProperty("serverVersion").GetString(),
                Source: value.GetProperty("sourceRevision").GetString());
            if (!Regex.IsMatch(observation.Release!, @"^server-v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$")
                || !Regex.IsMatch(observation.Source ?? "", "^[a-f0-9]{40}$"))
                return new("reconciliation_required", "unknown");
            return await InspectPublicAsync(state, observation, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new("unavailable", "unknown");
        }
    }
}
