// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Minicloud.Cli.Seed;

// Deliberately separate from the server's EF/OpenBao-backed provider factory.
internal sealed partial class SeedProvider : ISeedProvider
{
    private readonly string provider;
    private readonly string credential;
    private readonly HttpClient http;
    private readonly Uri origin;
    private string Servers => provider switch { "hetzner" => "servers", "digitalocean" => "droplets", _ => "instances" };
    private string Keys => provider switch { "hetzner" => "ssh_keys", "digitalocean" => "account/keys", _ => "ssh-keys" };
    private string ServerObject => provider switch { "hetzner" => "server", "digitalocean" => "droplet", _ => "instance" };
    private string KeyArray => "ssh_keys";
    private string KeyObject => "ssh_key";
    private static string Name(string operation) => "mc-seed-" + operation;
    public string CredentialScope(string operation) => SeedGuard.Scope(credential, operation);

    private string AccountIdentity(JsonElement account) => provider switch
    {
        "hetzner" => "Hetzner project scoped by the supplied credential (scope fingerprint binds approval)",
        "digitalocean" => "DigitalOcean account " + Text(account, "uuid"),
        _ => "Vultr account " + SeedGuard.Hash(Text(account, "email"))
    };

    public async Task<string> GetAccountIdentityAsync(CancellationToken ct)
    {
        if (provider == "hetzner")
        {
            // No account endpoint: authenticate the original project-scoped token without checking creation capacity.
            await SendAsync(HttpMethod.Get, "servers?per_page=1", null, ct);
            return AccountIdentity(default);
        }
        return AccountIdentity((await SendAsync(HttpMethod.Get, "account", null, ct)).GetProperty("account"));
    }

    public SeedProvider(string provider, string credential, HttpMessageHandler? handler = null)
    {
        this.provider = provider;
        this.credential = credential;
        origin = new(provider switch
        {
            "hetzner" => "https://api.hetzner.cloud/v1/",
            "digitalocean" => "https://api.digitalocean.com/v2/",
            "vultr" => "https://api.vultr.com/v2/",
            _ => throw new ArgumentException("Unsupported seed provider.")
        });
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ActivityHeadersPropagator = null })
        { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool missingOkay = false)
    {
        var uri = new Uri(origin, path);
        SeedGuard.Require(uri.Scheme == "https" && uri.Host == origin.Host && uri.AbsolutePath.StartsWith(origin.AbsolutePath, StringComparison.Ordinal),
            "Provider pagination left the authenticated API origin.");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (missingOkay && response.StatusCode == HttpStatusCode.NotFound) return default;
            SeedGuard.Require(response.IsSuccessStatusCode,
                $"{provider} API returned HTTP {(int)response.StatusCode}. Check credential scopes, account restrictions, catalog and quota. Response body suppressed; resources may remain billable.");
            if (method == HttpMethod.Delete || response.StatusCode == HttpStatusCode.NoContent) return default;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new Commands.CliCommandException(CliExitCodes.NetworkOrApiUnavailable,
                $"{provider} request outcome unavailable. No automatic mutation retry; resume to reconcile by operation identity.");
        }
    }

    private async Task<List<JsonElement>> ListAsync(string path, string field, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        string? next = path + (path.Contains('?') ? "&" : "?") + "per_page=100";
        var visited = new HashSet<string>();
        while (next is not null)
        {
            SeedGuard.Require(visited.Add(next) && visited.Count <= 1000, "Provider pagination is ambiguous; refusing an incomplete inventory.");
            var value = await SendAsync(HttpMethod.Get, next, null, ct);
            result.AddRange(value.GetProperty(field).EnumerateArray().Select(x => x.Clone()));
            next = null;
            if (provider == "hetzner" && value.TryGetProperty("meta", out var meta) && meta.TryGetProperty("pagination", out var pagination)
                && pagination.TryGetProperty("next_page", out var page) && page.ValueKind == JsonValueKind.Number)
                next = path + (path.Contains('?') ? "&" : "?") + "per_page=100&page=" + page.GetInt32();
            if (provider == "digitalocean" && value.TryGetProperty("links", out var links) && links.TryGetProperty("pages", out var pages)
                && pages.TryGetProperty("next", out var link)) next = link.GetString();
            if (provider == "vultr" && value.TryGetProperty("meta", out var vm) && vm.TryGetProperty("links", out var vl)
                && vl.TryGetProperty("next", out var cursor) && !string.IsNullOrEmpty(cursor.GetString()))
                next = path + (path.Contains('?') ? "&" : "?") + "per_page=100&cursor=" + Uri.EscapeDataString(cursor.GetString()!);
        }
        return result;
    }

    public async Task<SeedQuote> QuoteAsync(SeedSelection selection, string operation, CancellationToken ct)
    {
        selection.Validate();
        var quote = provider switch
        {
            "hetzner" => await HetznerQuoteAsync(selection, ct),
            "digitalocean" => await DigitalOceanQuoteAsync(selection, ct),
            _ => await VultrQuoteAsync(selection, ct)
        };
        SeedGuard.Require(quote.Cpus >= 2 && quote.MemoryMiB >= 2048 && quote.DiskGiB >= 20,
            "Select at least 2 CPUs, 2 GiB RAM and 20 GiB disk; these are refusal guards, not qualified capacity.");
        SeedGuard.Require(quote.Costs.Length > 0 && quote.Costs.All(x => x.Monthly > 0 && x.Currency.Length == 3 && (x.Hourly is null || x.Hourly > 0)),
            "Authoritative recurring pricing is missing or invalid; creation blocked.");
        return quote with { CredentialScope = SeedGuard.Scope(credential, operation), RetrievedAt = DateTimeOffset.UtcNow };
    }

    public async Task<string?> FindKeyAsync(string operation, string publicKey, CancellationToken ct)
    {
        var keys = (await ListAsync(Keys, KeyArray, ct)).Where(x => Text(x, "name") == Name(operation)).ToArray();
        SeedGuard.Require(keys.Length <= 1, "Multiple operation SSH keys: reconcile in provider console.");
        if (keys.Length == 0) return null;
        SeedGuard.Require(SameKey(Text(keys[0], provider == "vultr" ? "ssh_key" : "public_key"), publicKey), "Operation key name conflicts with different public key; refusing adoption.");
        if (provider == "hetzner") SeedGuard.Require(Owned(keys[0], operation), "SSH key lacks operation ownership label.");
        return Text(keys[0], "id");
    }

    public async Task<string> CreateKeyAsync(string operation, string publicKey, CancellationToken ct)
    {
        object body = provider switch
        {
            "hetzner" => new { name = Name(operation), public_key = publicKey, labels = new Dictionary<string, string> { ["minicloud-seed"] = operation } },
            "digitalocean" => new { name = Name(operation), public_key = publicKey },
            _ => (object)new { name = Name(operation), ssh_key = publicKey }
        };
        var result = await SendAsync(HttpMethod.Post, Keys, body, ct);
        return Text(result.GetProperty(KeyObject), "id");
    }

    public async Task<SeedResource?> FindServerAsync(string operation, CancellationToken ct)
    {
        var list = (await ListAsync(Servers, Servers, ct)).Where(x => Owned(x, operation)).ToArray();
        SeedGuard.Require(list.Length <= 1, "Multiple operation-owned servers; reconcile manually, never retry creation.");
        return list.Length == 0 ? null : Resource(list[0], operation);
    }

    public async Task<string?> FindUserKeyAsync(string publicKey, CancellationToken ct)
    {
        var matches = (await ListAsync(Keys, KeyArray, ct))
            .Where(x => SameKey(Text(x, provider == "vultr" ? "ssh_key" : "public_key"), publicKey)).ToArray();
        SeedGuard.Require(matches.Length <= 1, "Multiple provider keys match the user identity; reconcile before creation.");
        return matches.Length == 0 ? null : Text(matches[0], "id");
    }

    public async Task<SeedResource> CreateServerAsync(SeedState state, CancellationToken ct)
    {
        var s = state.Plan.Selection;
        var q = state.Plan.Quote;
        object body = provider switch
        {
            "hetzner" => new { name = Name(state.Id), location = s.Region, server_type = s.Size, image = long.Parse(q.Image, CultureInfo.InvariantCulture),
                ssh_keys = new[] { long.Parse(state.SshKeyId!, CultureInfo.InvariantCulture) },
                labels = new Dictionary<string, string> { ["minicloud-seed"] = state.Id },
                public_net = new { enable_ipv4 = true, enable_ipv6 = false }, start_after_create = true },
            "digitalocean" => new { name = Name(state.Id), region = s.Region, size = s.Size,
                image = long.Parse(q.Image, CultureInfo.InvariantCulture),
                ssh_keys = new[] { long.Parse(state.SshKeyId!, CultureInfo.InvariantCulture) },
                tags = new[] { Name(state.Id) }, ipv6 = false, backups = false, monitoring = false, vpc_uuid = q.NetworkId },
            _ => (object)new { label = Name(state.Id), hostname = Name(state.Id), region = s.Region, plan = s.Size,
                os_id = int.Parse(q.Image, CultureInfo.InvariantCulture), sshkey_id = new[] { state.SshKeyId! },
                tags = new[] { Name(state.Id) }, enable_ipv6 = false, disable_public_ipv4 = false, enable_vpc = false,
                backups = "disabled", ddos_protection = false, activation_email = false, user_scheme = "root" }
        };
        var value = (await SendAsync(HttpMethod.Post, Servers, body, ct)).GetProperty(ServerObject);
        SeedGuard.Require(Owned(value, state.Id), "Created server response lacks ownership; resume for reconciliation.");
        state.Server = new(Text(value, "id"), state.Id, s.Region, s.Size, q.Image, null);
        return Resource(value, state.Id);
    }

    private bool Owned(JsonElement value, string operation) => provider == "hetzner"
        ? value.TryGetProperty("labels", out var labels) && labels.TryGetProperty("minicloud-seed", out var id) && id.GetString() == operation
        : value.TryGetProperty("tags", out var tags) && tags.EnumerateArray().Any(x => x.GetString() == Name(operation));

    private SeedResource Resource(JsonElement value, string operation)
    {
        string? ip;
        string? primary = null;
        if (provider == "hetzner")
        {
            var ipv4 = value.GetProperty("public_net").GetProperty("ipv4");
            ip = Text(ipv4, "ip");
            if (ipv4.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && number > 0) primary = number.ToString(CultureInfo.InvariantCulture);
        }
        else if (provider == "digitalocean")
        {
            var addresses = value.GetProperty("networks").GetProperty("v4").EnumerateArray().Where(x => Text(x, "type") == "public").ToArray();
            SeedGuard.Require(addresses.Length <= 1, "Ambiguous public IPv4 inventory.");
            ip = addresses.Length == 0 ? null : Text(addresses[0], "ip_address");
        }
        else ip = Text(value, "main_ip");
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip == "0.0.0.0") ip = null;
        return provider switch
        {
            "hetzner" => new(Text(value, "id"), operation, value.TryGetProperty("location", out var location) ? Text(location, "name") : Text(value.GetProperty("datacenter").GetProperty("location"), "name"),
                Text(value.GetProperty("server_type"), "name"), Text(value.GetProperty("image"), "id"), ip, primary),
            "digitalocean" => new(Text(value, "id"), operation, Text(value.GetProperty("region"), "slug"),
                Text(value, "size_slug"), Text(value.GetProperty("image"), "id"), ip),
            _ => new(Text(value, "id"), operation, Text(value, "region"), Text(value, "plan"), Text(value, "os_id"), ip)
        };
    }

    public async Task<bool> DeleteServerAsync(SeedState state, CancellationToken ct)
    {
        var resource = state.Server!;
        var response = await SendAsync(HttpMethod.Get, Servers + "/" + Uri.EscapeDataString(resource.Id), null, ct, true);
        if (response.ValueKind == JsonValueKind.Undefined) return true;
        var value = response.GetProperty(ServerObject);
        SeedGuard.Require(Owned(value, state.Id), "Refusing deletion: server operation tag/label changed.");
        SeedLifecycle.Match(state, Resource(value, state.Id));
        if (provider == "hetzner")
        {
            var ip = value.GetProperty("public_net").GetProperty("ipv4");
            SeedGuard.Require(resource.PrimaryIpId is not null && Text(ip, "id") == resource.PrimaryIpId, "Primary IP identity is uncertain; reconcile before cleanup.");
            var primary = (await SendAsync(HttpMethod.Get, "primary_ips/" + resource.PrimaryIpId, null, ct)).GetProperty("primary_ip");
            SeedGuard.Require(primary.GetProperty("auto_delete").GetBoolean()
                && Text(primary, "assignee_id") == resource.Id, "Primary IP is not auto-delete/owned by this server; use normal provider reconciliation.");
        }
        await SendAsync(HttpMethod.Delete, Servers + "/" + Uri.EscapeDataString(resource.Id), null, ct, true);
        var after = await SendAsync(HttpMethod.Get, Servers + "/" + Uri.EscapeDataString(resource.Id), null, ct, true);
        return after.ValueKind == JsonValueKind.Undefined;
    }

    public async Task DeleteKeyAsync(string id, string operation, string publicKey, CancellationToken ct)
    {
        var value = await SendAsync(HttpMethod.Get, Keys + "/" + Uri.EscapeDataString(id), null, ct, true);
        if (value.ValueKind == JsonValueKind.Undefined) return;
        var key = value.GetProperty(KeyObject);
        SeedGuard.Require(Text(key, "id") == id && Text(key, "name") == Name(operation)
            && SameKey(Text(key, provider == "vultr" ? "ssh_key" : "public_key"), publicKey)
            && (provider != "hetzner" || Owned(key, operation)), "SSH key ownership changed; refusing deletion.");
        await SendAsync(HttpMethod.Delete, Keys + "/" + Uri.EscapeDataString(id), null, ct, true);
    }

    private static bool SameKey(string left, string right) => string.Join(" ", left.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2))
        == string.Join(" ", right.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
    private static string Text(JsonElement value, string key) => value.GetProperty(key).ToString();
    private static decimal Money(JsonElement value, string key) => decimal.Parse(Text(value, key), CultureInfo.InvariantCulture);
    private static JsonElement One(IEnumerable<JsonElement> values, Func<JsonElement, bool> predicate, string message)
    {
        var matches = values.Where(predicate).ToArray();
        SeedGuard.Require(matches.Length == 1, message);
        return matches[0];
    }
    public void Dispose() => http.Dispose();
}
