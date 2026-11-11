using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Minicloud.Tests;

// Static exposure and wiring checks for the packaged self-hosting Compose bundle.
public sealed class SelfHostingComposeBundleTests
{
    private static readonly string Bundle = Path.Combine(FindRepository(), "packaging", "server", "bundle");
    private static readonly YamlMappingNode Compose = Load("compose.yml");
    private static readonly YamlMappingNode Services = (YamlMappingNode)Compose["services"];

    [Fact]
    public void Only_caddy_publishes_host_ports_and_no_service_escapes_isolation()
    {
        foreach (var (name, service) in AllServices())
        {
            Assert.False(service.Children.ContainsKey("ports") && name != "caddy", $"{name} publishes ports");
            Assert.False(service.Children.ContainsKey("expose"), $"{name} exposes ports");
            foreach (var key in new[] { "build", "privileged", "network_mode", "pid", "ipc", "cap_add", "devices" })
                Assert.False(service.Children.ContainsKey(key), $"{name} sets {key}");
            Assert.Equal(["private"], Sequence(service, "networks"));
            Assert.Matches(@"^\$\{MINICLOUD_[A-Z]+_IMAGE:\?set MINICLOUD_[A-Z]+_IMAGE\}$", Scalar(service, "image"));
        }

        Assert.Equal(["80:80", "443:443", "443:443/udp"], Sequence(Service("caddy"), "ports"));
        var caddyfile = File.ReadAllText(Path.Combine(Bundle, "Caddyfile"));
        Assert.Equal(["api:8080", "dashboard:8080"],
            Regex.Matches(caddyfile, @"reverse_proxy\s+(\S+)").Select(match => match.Groups[1].Value).Order());
        Assert.Equal(["maintenance"], Sequence(Service("openbao-configure"), "profiles"));
    }

    [Fact]
    public void Referenced_secrets_bind_sources_and_configuration_files_exist_and_are_mounted()
    {
        var definedSecrets = (YamlMappingNode)Compose["secrets"];
        foreach (var (name, definition) in definedSecrets.Children.Select(x => (Text(x.Key), (YamlMappingNode)x.Value)))
            Assert.StartsWith("${MINICLOUD_SECRETS_DIR:?set MINICLOUD_SECRETS_DIR}/", Scalar(definition, "file"));

        foreach (var (name, service) in AllServices())
        {
            var secrets = Sequence(service, "secrets");
            Assert.All(secrets, secret => Assert.True(definedSecrets.Children.ContainsKey(secret), $"{name}: undefined secret {secret}"));
            var mounts = Mounts(service);
            foreach (var (source, target, readOnly) in mounts.Where(m => m.Source.StartsWith("./", StringComparison.Ordinal)))
            {
                Assert.True(File.Exists(Path.Combine(Bundle, source[2..])) || source == "./server-release.json",
                    $"{name}: missing bundle file {source}");
                Assert.True(readOnly, $"{name}: bundle file {source} must be read-only");
            }

            // Every /run/secrets path used by environment or mounted configuration must be granted.
            var text = string.Join('\n', Environment(service).Values) + '\n' + string.Join('\n', mounts
                .Where(m => m.Source.StartsWith("./", StringComparison.Ordinal) && File.Exists(Path.Combine(Bundle, m.Source[2..])))
                .Where(m => !m.Source.EndsWith(".sh", StringComparison.Ordinal))
                .Select(m => File.ReadAllText(Path.Combine(Bundle, m.Source[2..]))));
            foreach (Match match in Regex.Matches(text, @"/run/secrets/([A-Za-z0-9._-]+)"))
                Assert.True(secrets.Contains(match.Groups[1].Value), $"{name}: references ungranted secret {match.Groups[1].Value}");
        }

        Assert.Equal(["app-db-password"], Sequence(Service("api-migrate"), "secrets"));
        Assert.Empty(Mounts(Service("api-migrate")));
        Assert.DoesNotContain("SSL_CERT_FILE", File.ReadAllText(Path.Combine(Bundle, "compose.yml")));
    }

    [Fact]
    public void Openbao_token_and_tls_wiring_is_consistent()
    {
        var agentConfig = File.ReadAllText(Path.Combine(Bundle, "openbao-agent.hcl"));
        var sink = Regex.Match(agentConfig, "sink \"file\"[^}]*path = \"([^\"]+)\"", RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal("/run/minicloud-openbao/token", sink);
        Assert.Contains(Mounts(Service("openbao-agent")), m => m == ("openbao-agent-token", "/run/minicloud-openbao", false));
        foreach (var consumer in new[] { "api", "worker" })
        {
            var environment = Environment(Service(consumer));
            Assert.Equal(sink, environment["OpenBao__TokenFile"]);
            Assert.Equal("https://openbao:8200", environment["OpenBao__Address"]);
            Assert.Equal("/run/secrets/openbao-ca.crt", environment["OpenBao__CaCertificateFile"]);
            Assert.False(environment.ContainsKey("OpenBao__Token"));
            Assert.Contains(Mounts(Service(consumer)), m => m == ("openbao-agent-token", "/run/minicloud-openbao", true));
            Assert.Equal("service_healthy", DependencyCondition(consumer, "openbao-agent"));
        }

        var tokenVolume = (YamlMappingNode)((YamlMappingNode)Compose["volumes"])["openbao-agent-token"];
        Assert.Equal("tmpfs", Scalar((YamlMappingNode)tokenVolume["driver_opts"], "type"));
        foreach (var name in new[] { "openbao", "openbao-agent" })
            Assert.Equal("true", Environment(Service(name))["BAO_SKIP_DROP_ROOT"]);
        Assert.Contains("https://openbao:8200", agentConfig);
        Assert.Contains("/run/secrets/openbao-role-id", agentConfig);
    }

    [Fact]
    public void Identity_dashboard_and_startup_order_match_runtime_expectations()
    {
        var kratos = Load("kratos.yml");
        var publicBaseUrl = Text(((YamlMappingNode)((YamlMappingNode)kratos["serve"])["public"])["base_url"]);
        var api = Environment(Service("api"));
        Assert.Equal(api["Kratos__PublicBaseUrl"], publicBaseUrl);
        Assert.Equal("http://kratos:4434/", api["Kratos__AdminBaseUrl"]);
        Assert.Equal("false", api["Database__RunMigrationsOnStartup"]);
        Assert.DoesNotContain("kratos\", \"healthcheck", string.Join("\", \"", Sequence((YamlMappingNode)Service("kratos")["healthcheck"], "test")));
        var kratosEnvironment = Environment(Service("kratos"));
        Assert.Equal("${CONSOLE_URL:?set CONSOLE_URL}/sign-in", kratosEnvironment["SELFSERVICE_FLOWS_LOGIN_UI_URL"]);
        Assert.Equal(kratosEnvironment, Environment(Service("kratos-migrate")));
        Assert.DoesNotMatch("(?m)^(SERVE|SELFSERVICE)_", File.ReadAllText(Path.Combine(Bundle, "secrets.env.example")));

        var dashboard = Service("dashboard");
        Assert.Equal(["MINICLOUD_API_URL", "MINICLOUD_APP_DOMAIN", "MINICLOUD_SOURCE_URL"], Environment(dashboard).Keys.Order());
        Assert.False(dashboard.Children.ContainsKey("secrets") || dashboard.Children.ContainsKey("volumes") || dashboard.Children.ContainsKey("env_file"));

        Assert.Equal("service_healthy", DependencyCondition("database-initialize", "postgres"));
        Assert.Equal("service_completed_successfully", DependencyCondition("api-migrate", "database-initialize"));
        Assert.Equal("service_completed_successfully", DependencyCondition("kratos-migrate", "database-initialize"));
        Assert.Equal("service_completed_successfully", DependencyCondition("kratos", "kratos-migrate"));
        Assert.Equal("service_completed_successfully", DependencyCondition("api", "api-migrate"));
        Assert.Equal("service_healthy", DependencyCondition("api", "kratos"));
        Assert.Equal("service_healthy", DependencyCondition("worker", "api"));
        Assert.Equal("service_healthy", DependencyCondition("caddy", "dashboard"));
    }

    [Fact]
    public void Interpolated_settings_are_the_documented_operator_inputs()
    {
        var variables = Regex.Matches(File.ReadAllText(Path.Combine(Bundle, "compose.yml")), @"\$\{([A-Z_]+)")
            .Select(match => match.Groups[1].Value).Where(name => !Regex.IsMatch(name, "^MINICLOUD_[A-Z]+_IMAGE$")).ToHashSet();
        Assert.Equal(
            ["API_HOSTNAME", "API_URL", "APP_DOMAIN", "CONSOLE_HOSTNAME", "CONSOLE_URL", "MANAGE_DNS_RECORDS",
             "MINICLOUD_SECRETS_DIR", "MINICLOUD_SOURCE_URL", "OPENBAO_ROOT_TOKEN_FILE"],
            variables.Order());
        var guide = File.ReadAllText(Path.Combine(FindRepository(), "docs", "self-hosting.md"));
        Assert.All(variables, variable => Assert.Contains(variable, guide));
    }

    private static IEnumerable<(string Name, YamlMappingNode Service)> AllServices() =>
        Services.Children.Select(x => (Text(x.Key), (YamlMappingNode)x.Value));

    private static YamlMappingNode Service(string name) => (YamlMappingNode)Services[name];

    private static string? DependencyCondition(string service, string dependency) =>
        Service(service).Children.TryGetValue("depends_on", out var node) &&
        ((YamlMappingNode)node).Children.TryGetValue(dependency, out var value)
            ? Scalar((YamlMappingNode)value, "condition")
            : null;

    private static Dictionary<string, string> Environment(YamlMappingNode service)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (service.Children.TryGetValue("environment", out var node)) Merge((YamlMappingNode)node, result);
        return result;
    }

    private static void Merge(YamlMappingNode mapping, Dictionary<string, string> result)
    {
        if (mapping.Children.TryGetValue("<<", out var parent)) Merge((YamlMappingNode)parent, result);
        foreach (var (key, value) in mapping.Children.Where(x => Text(x.Key) != "<<"))
            result[Text(key)] = Text(value);
    }

    private static List<(string Source, string Target, bool ReadOnly)> Mounts(YamlMappingNode service) =>
        !service.Children.TryGetValue("volumes", out var node) ? [] : ((YamlSequenceNode)node).Children.Select(volume =>
        {
            if (volume is YamlMappingNode mapping)
                return (Scalar(mapping, "source"), Scalar(mapping, "target"),
                    mapping.Children.TryGetValue("read_only", out var ro) && Text(ro) == "true");
            var parts = Text(volume).Split(':');
            return (parts[0], parts[1], parts.Length > 2 && parts[2] == "ro");
        }).ToList();

    private static List<string> Sequence(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(key, out var value) ? ((YamlSequenceNode)value).Children.Select(Text).ToList() : [];

    private static string Scalar(YamlMappingNode node, string key) => Text(node[key]);

    private static string Text(YamlNode node) => ((YamlScalarNode)node).Value ?? "";

    private static YamlMappingNode Load(string file)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(Path.Combine(Bundle, file));
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Minicloud.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
