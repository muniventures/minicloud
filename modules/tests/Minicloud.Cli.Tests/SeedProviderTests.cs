// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Seed;

namespace Minicloud.Tests;

public sealed class SeedProviderTests
{
    private const string Operation = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Credential = "synthetic-provider-credential-never-log";
    private static SeedSelection Selection(string provider) => new(provider, "region", "size", "cloud.example.com", "apps.example.com", "server-v1.2.3");

    [Theory]
    [InlineData("hetzner")]
    [InlineData("digitalocean")]
    [InlineData("vultr")]
    public async Task Account_identity_matches_the_quote_without_reading_creation_catalog_or_quota(string name)
    {
        var fixture = new Fixture(name);
        using var provider = fixture.CreateProvider();
        var quote = await provider.QuoteAsync(Selection(name), Operation, default);
        var path = name == "hetzner" ? "servers" : "account";
        var identity = fixture.Bodies[path];
        fixture.Bodies.Clear();
        fixture.Bodies[path] = identity;
        fixture.Requests.Clear();

        Assert.Equal(quote.Account, await provider.GetAccountIdentityAsync(default));
        Assert.Equal("GET", Assert.Single(fixture.Requests).Method);
    }

    [Theory]
    [InlineData("hetzner", 2)]
    [InlineData("digitalocean", 1)]
    [InlineData("vultr", 1)]
    public async Task All_provider_adapters_resolve_authoritative_catalog_prices_then_send_no_secret_to_host(string name, int costs)
    {
        var fixture = new Fixture(name);
        using var provider = new SeedProvider(name, Credential, fixture);
        var quote = await provider.QuoteAsync(Selection(name), Operation, default);
        Assert.All(fixture.Requests, x => Assert.Equal("GET", x.Method));
        Assert.Equal(costs, quote.Costs.Length);
        Assert.All(quote.Costs, x => Assert.Equal("USD", x.Currency));
        Assert.Equal(name == "hetzner" ? 11m : 12m, quote.Costs[0].Monthly);
        Assert.Equal("42", quote.Image);
        Assert.Equal("root", quote.SshUser);
        Assert.DoesNotContain(Credential, JsonSerializer.Serialize(quote));
        var key = await provider.CreateKeyAsync(Operation, "ssh-ed25519 synthetic-fixture", default);
        var state = new SeedState { Id = Operation, Plan = new(Selection(name), quote, "digest", "source"), SshKeyId = key };
        var server = await provider.CreateServerAsync(state, default);
        SeedLifecycle.Match(state, server);
        foreach (var request in fixture.Requests.Where(x => x.Method == "POST"))
        {
            Assert.DoesNotContain(Credential, request.Body);
            Assert.DoesNotContain("user_data", request.Body);
            Assert.DoesNotContain("password", request.Body);
        }
        Assert.Equal("server", server.Id);
        Assert.Equal(2, fixture.Requests.Count(x => x.Method == "POST"));
        if (name == "digitalocean")
        {
            using var request = JsonDocument.Parse(fixture.Requests.Last().Body);
            Assert.Equal(quote.NetworkId, request.RootElement.GetProperty("vpc_uuid").GetString());
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("region", false)]
    [InlineData("other-region", true)]
    public async Task DigitalOcean_refuses_implicit_creation_of_a_default_shared_network(string region, bool isDefault)
    {
        var fixture = new Fixture("digitalocean");
        fixture.Bodies["vpcs"] = new { vpcs = region.Length == 0 ? Array.Empty<object>() : new[] { new { id = "unusable-network", region, @default = isDefault } } };
        using var provider = new SeedProvider("digitalocean", Credential, fixture);
        await Assert.ThrowsAsync<CliCommandException>(() => provider.QuoteAsync(Selection("digitalocean"), Operation, default));
        Assert.All(fixture.Requests, x => Assert.Equal("GET", x.Method));
    }

    [Theory]
    [InlineData("hetzner")]
    [InlineData("digitalocean")]
    [InlineData("vultr")]
    public async Task Api_error_bodies_never_enter_errors_or_retry_mutations(string name)
    {
        var fixture = new Fixture(name) { Deny = true };
        using var provider = new SeedProvider(name, Credential, fixture);
        var error = await Assert.ThrowsAsync<CliCommandException>(() => provider.QuoteAsync(Selection(name), Operation, default));
        Assert.DoesNotContain(Credential, error.Message);
        Assert.Single(fixture.Requests);
        Assert.Equal("GET", fixture.Requests[0].Method);
    }

    [Fact]
    public async Task Pagination_cannot_send_bearer_credential_to_another_origin()
    {
        var fixture = new Fixture("digitalocean");
        fixture.Bodies["droplets"] = new { droplets = Array.Empty<object>(), links = new { pages = new { next = "https://unrelated.invalid/v2/droplets" } } };
        using var provider = new SeedProvider("digitalocean", Credential, fixture);
        await Assert.ThrowsAsync<CliCommandException>(() => provider.QuoteAsync(Selection("digitalocean"), Operation, default));
        Assert.Equal(2, fixture.Requests.Count);
        Assert.All(fixture.Requests, x => Assert.Equal("api.digitalocean.com", x.Host));
    }

    [Theory]
    [InlineData("hetzner")]
    [InlineData("digitalocean")]
    [InlineData("vultr")]
    public async Task Name_alone_is_never_server_ownership(string name)
    {
        var fixture = new Fixture(name);
        var server = fixture.Server(false);
        var field = name switch { "hetzner" => "servers", "digitalocean" => "droplets", _ => "instances" };
        fixture.Bodies[field] = new Dictionary<string, object> { [field] = new[] { server } };
        using var provider = new SeedProvider(name, Credential, fixture);
        Assert.Null(await provider.FindServerAsync(Operation, default));
        Assert.Single(fixture.Requests);
    }

    internal sealed class Fixture : HttpMessageHandler
    {
        private readonly string provider;
        public readonly Dictionary<string, object> Bodies = [];
        public readonly List<(string Method, string Host, string Body)> Requests = [];
        public bool Deny;
        internal SeedProvider CreateProvider() => new(provider, Credential, this);
        public Fixture(string provider)
        {
            this.provider = provider;
            Bodies["servers"] = new { servers = Array.Empty<object>() };
            Bodies["instances"] = new { instances = Array.Empty<object>() };
            Bodies["droplets"] = new { droplets = Array.Empty<object>() };
            Bodies["ssh_keys"] = Bodies["account/keys"] = Bodies["ssh-keys"] = new { ssh_keys = Array.Empty<object>() };
            Bodies["locations"] = new { locations = new[] { new { id = 1, name = "region" } } };
            Bodies["server_types"] = new { server_types = new[] { new { id = 1, name = "size", architecture = "x86", cores = 2,
                memory = 2, disk = 40, locations = new[] { new { name = "region", available = true } } } } };
            Bodies["images"] = new { images = new[] { new { id = 42, os_flavor = "ubuntu", os_version = "24.04", architecture = "x86",
                type = "system", status = "available", disk_size = 10 } } };
            Bodies["pricing"] = new { pricing = new { currency = "USD",
                server_types = new[] { new { id = 1, prices = new[] { new { location = "region", price_monthly = new { net = "11" },
                    price_hourly = new { net = "0.02" }, included_traffic = 1000000000, price_per_tb_traffic = new { net = "1" } } } } },
                primary_ips = new[] { new { type = "ipv4", prices = new[] { new { location = "region", price_monthly = new { net = "1" }, price_hourly = new { net = "0.001" } } } } } } };
            Bodies["account"] = provider == "digitalocean"
                ? (object)new { account = new { uuid = "synthetic-account", status = "active", email_verified = true, droplet_limit = 5 } }
                : new { account = new { email = "synthetic@example.invalid", acls = new[] { "provision" } } };
            Bodies["regions"] = provider == "digitalocean"
                ? (object)new { regions = new[] { new { slug = "region", available = true } } }
                : new { regions = new[] { new { id = "region" } } };
            Bodies["sizes"] = new { sizes = new[] { new { slug = "size", available = true, regions = new[] { "region" }, disk = 40,
                vcpus = 2, memory = 2048, transfer = 1, price_monthly = 12, price_hourly = 0.02m } } };
            Bodies["vpcs"] = new { vpcs = new[] { new { id = "synthetic-existing-network", region = "region", @default = true } } };
            Bodies["images/ubuntu-24-04-x64"] = new { image = new { id = 42, distribution = "Ubuntu", slug = "ubuntu-24-04-x64",
                @public = true, regions = new[] { "region" }, min_disk_size = 10 } };
            Bodies["plans"] = new { plans = new[] { new { id = "size", type = "vc2", locations = new[] { "region" }, monthly_cost = 12,
                bandwidth = 1000, vcpu_count = 2, ram = 2048, disk = 40 } } };
            Bodies["regions/region/availability"] = new { available_plans = new[] { "size" } };
            Bodies["os"] = new { os = new[] { new { id = 42, family = "ubuntu", arch = "x64", name = "Ubuntu 24.04 x64" } } };
        }

        public object Server(bool owned) => provider switch
        {
            "hetzner" => new { id = "server", name = "mc-seed-" + Operation, location = new { name = "region" },
                server_type = new { name = "size" }, image = new { id = 42 },
                labels = new Dictionary<string, string> { ["minicloud-seed"] = owned ? Operation : "different" },
                public_net = new { ipv4 = new { id = 99, ip = "203.0.113.1" } } },
            "digitalocean" => new { id = "server", name = "mc-seed-" + Operation, region = new { slug = "region" }, size_slug = "size",
                image = new { id = 42 }, tags = new[] { owned ? "mc-seed-" + Operation : "unrelated" },
                networks = new { v4 = new[] { new { type = "public", ip_address = "203.0.113.1" } } } },
            _ => (object)new { id = "server", label = "mc-seed-" + Operation, region = "region", plan = "size", os_id = 42,
                tags = new[] { owned ? "mc-seed-" + Operation : "unrelated" }, main_ip = "203.0.113.1" }
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(Credential, request.Headers.Authorization?.Parameter);
            Requests.Add((request.Method.Method, request.RequestUri!.Host, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (Deny) return new(HttpStatusCode.Forbidden) { Content = new StringContent(Credential) };
            var path = string.Join("/", request.RequestUri.AbsolutePath.Split('/').Skip(2));
            if (request.Method == HttpMethod.Post)
            {
                var serverObject = provider switch { "hetzner" => "server", "digitalocean" => "droplet", _ => "instance" };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(path is "ssh_keys" or "ssh-keys" or "account/keys"
                    ? (object)new { ssh_key = new { id = 7 } } : new Dictionary<string, object> { [serverObject] = Server(true) }) };
            }
            Assert.True(Bodies.ContainsKey(path), "Unexpected synthetic endpoint: " + path);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Bodies[path]) };
        }
    }
}
