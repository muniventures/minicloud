// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;

namespace Minicloud.Tests;

public sealed class CliSshAccessTests
{
    private static readonly string Ed25519Key = "ssh-ed25519 " + Convert.ToBase64String(Blob("ssh-ed25519", new byte[32]));

    [Theory]
    [InlineData(new[] { "--ssh-key", "key_1", "--ssh-public-key-file", "id.pub" }, "either")]
    [InlineData(new[] { "--ssh-key", "key_1", "--ssh-key-name", "laptop" }, "requires --ssh-public-key-file")]
    [InlineData(new[] { "--ssh-key-name", "laptop" }, "requires --ssh-public-key-file")]
    [InlineData(new[] { "--ssh-key" }, "requires a Minicloud SSH key ID")]
    [InlineData(new[] { "--ssh-key", "--plan" }, "requires a Minicloud SSH key ID")]
    [InlineData(new[] { "--ssh-key=a", "--ssh-key=b" }, "only once")]
    public void Ssh_options_enforce_mutual_exclusion_and_name_pairing(string[] args, string message)
    {
        var ex = Assert.Throws<CliCommandException>(() => CliApplication.ParseSshAccessOptions(args));
        Assert.Equal(CliExitCodes.ValidationError, ex.ExitCode);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Omitted_ssh_options_produce_no_selection_and_existing_key_is_a_reference()
    {
        Assert.Null(CliApplication.ParseSshAccessOptions(["--plan", "small"]));
        var existing = CliApplication.ParseSshAccessOptions(["--ssh-key=key_1"])!;
        Assert.Equal(("existing", "key_1", null, null, null), (existing.Mode, existing.KeyId, existing.Name, existing.PublicKey, existing.ExpectedRevision));
    }

    [Fact]
    public void Public_key_file_sends_only_normalized_public_material_with_derived_or_explicit_name()
    {
        using var dir = new TempDir();
        var path = dir.Write("id_ed25519.pub", $"  {Ed25519Key}   me@laptop  \n\n");
        var named = CliApplication.ParseSshAccessOptions(["--ssh-public-key-file", path, "--ssh-key-name", " Work "])!;
        Assert.Equal(("new", null, "Work", Ed25519Key), (named.Mode, named.KeyId, named.Name, named.PublicKey));
        Assert.Equal("me@laptop", CliApplication.ParseSshAccessOptions(["--ssh-public-key-file", path])!.Name);
        var bare = dir.Write("deploy.pub", Ed25519Key);
        Assert.Equal("deploy", CliApplication.ParseSshAccessOptions(["--ssh-public-key-file", bare])!.Name);
    }

    [Theory]
    [InlineData("private", "private key")]
    [InlineData("openssh-private", "private key")]
    [InlineData("multiple-lines", "more than one line")]
    [InlineData("multiple-inline", "more than one key")]
    [InlineData("options", "authorized_keys options")]
    [InlineData("unsupported", "unsupported key type")]
    [InlineData("mismatch", "does not match")]
    [InlineData("small-rsa", "shorter than 3072")]
    [InlineData("oversize", "larger than 16 KiB")]
    [InlineData("empty", "is empty")]
    [InlineData("missing", "was not found")]
    public void Public_key_file_validation_rejects_unsafe_or_invalid_input(string kind, string message)
    {
        using var dir = new TempDir();
        var content = kind switch
        {
            "private" => "-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----\n",
            "openssh-private" => "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----\n",
            "multiple-lines" => Ed25519Key + " a\n" + Ed25519Key + " b\n",
            "multiple-inline" => Ed25519Key + " " + Ed25519Key,
            "options" => "no-pty,command=\"/bin/true\" " + Ed25519Key,
            "unsupported" => "ssh-dss AAAAB3NzaC1kc3M=",
            "mismatch" => "ssh-rsa " + Ed25519Key.Split(' ')[1],
            "small-rsa" => RsaKey(2048),
            "oversize" => Ed25519Key + " " + new string('x', 17 * 1024),
            "empty" => "\n  \n",
            _ => null
        };
        var path = content is null ? Path.Combine(dir.Path, "absent.pub") : dir.Write("key.pub", content);
        var ex = Assert.Throws<CliCommandException>(() => CliApplication.ParseSshAccessOptions(["--ssh-public-key-file", path]));
        Assert.Equal(CliExitCodes.ValidationError, ex.ExitCode);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MIIabc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rsa_3072_public_key_is_accepted()
    {
        var key = RsaKey(3072);
        Assert.Equal(key, CliApplication.ParseSshPublicKey(key + " ops", "key.pub").PublicKey);
    }

    [Fact]
    public void Create_application_request_includes_initial_environment_ssh_only_when_selected()
    {
        var omitted = Serialize(CliApplication.BuildCreateApplicationRequest("org", "demo", "small", "sqlite", null));
        Assert.Equal(JsonValueKind.Null, omitted.GetProperty("initialEnvironment").GetProperty("sshAccess").ValueKind);
        var selected = Serialize(CliApplication.BuildCreateApplicationRequest("org", "demo", "small", "sqlite", new("existing", KeyId: "key_1")));
        var ssh = selected.GetProperty("initialEnvironment").GetProperty("sshAccess");
        Assert.Equal("existing", ssh.GetProperty("mode").GetString());
        Assert.Equal("key_1", ssh.GetProperty("keyId").GetString());
        Assert.Equal(JsonValueKind.Null, ssh.GetProperty("expectedRevision").ValueKind);
    }

    [Fact]
    public async Task Environment_create_without_ssh_options_sends_no_selection()
    {
        using var fixture = new Fixture();
        Assert.Equal(CliExitCodes.Success, await fixture.RunAsync("environments", "create", "staging", "--plan", "small"));
        var body = fixture.Handler.Body("POST /api/apps/app_main/environments");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sshAccess").ValueKind);
        Assert.DoesNotContain("SSH access", fixture.Console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_create_with_public_key_file_sends_new_selection_and_explains_deferred_registration()
    {
        using var fixture = new Fixture();
        var path = fixture.Dir.Write("laptop.pub", Ed25519Key + " me@laptop");
        Assert.Equal(CliExitCodes.Success, await fixture.RunAsync("environments", "create", "staging", "--plan", "small", "--ssh-public-key-file", path, "--ssh-key-name", "Laptop"));
        var ssh = fixture.Handler.Body("POST /api/apps/app_main/environments").GetProperty("sshAccess");
        Assert.Equal("new", ssh.GetProperty("mode").GetString());
        Assert.Equal("Laptop", ssh.GetProperty("name").GetString());
        Assert.Equal(Ed25519Key, ssh.GetProperty("publicKey").GetString());
        Assert.Equal(JsonValueKind.Null, ssh.GetProperty("keyId").ValueKind);
        Assert.Equal(JsonValueKind.Null, ssh.GetProperty("expectedRevision").ValueKind);
        Assert.Contains("first provisions", fixture.Console.Output, StringComparison.Ordinal);
        Assert.Contains("immutable once server allocation starts", fixture.Console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_ssh_options_fail_before_any_api_request()
    {
        using var fixture = new Fixture();
        var path = fixture.Dir.Write("id_ed25519", "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----");
        Assert.Equal(CliExitCodes.ValidationError, await fixture.RunAsync("environments", "create", "staging", "--plan", "small", "--ssh-public-key-file", path));
        Assert.Equal(CliExitCodes.ValidationError, await fixture.RunAsync("init", "--ssh-key-name", "x"));
        Assert.Equal(CliExitCodes.ValidationError, await fixture.RunAsync("environments", "rename", "production", "prod", "--ssh-key", "key_1"));
        Assert.Empty(fixture.Handler.Requests);
        Assert.Contains("private key", fixture.Console.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_update_sends_existing_selection_with_current_revision()
    {
        using var fixture = new Fixture { Revision = 4 };
        Assert.Equal(CliExitCodes.Success, await fixture.RunAsync("environments", "update", "staging", "--ssh-key", "key_1"));
        Assert.Contains("GET /api/environments/env_staging", fixture.Handler.Requests);
        var body = fixture.Handler.Body("PATCH /api/apps/app_main/environments/env_staging");
        var ssh = body.GetProperty("sshAccess");
        Assert.Equal("existing", ssh.GetProperty("mode").GetString());
        Assert.Equal("key_1", ssh.GetProperty("keyId").GetString());
        Assert.Equal(4, ssh.GetProperty("expectedRevision").GetInt64());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("name").ValueKind);
        Assert.Contains("SHA256:fixture", fixture.Console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_update_without_ssh_options_is_rejected_and_rename_preserves_selection()
    {
        using var fixture = new Fixture();
        Assert.Equal(CliExitCodes.ValidationError, await fixture.RunAsync("environments", "update", "staging"));
        Assert.Contains("keeps the current SSH selection", fixture.Console.Errors, StringComparison.Ordinal);
        Assert.Equal(CliExitCodes.Success, await fixture.RunAsync("environments", "rename", "staging", "qa"));
        var body = fixture.Handler.Body("PATCH /api/apps/app_main/environments/env_staging");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sshAccess").ValueKind);
    }

    [Fact]
    public async Task Environment_update_refuses_locked_selection_before_patch()
    {
        using var fixture = new Fixture { Locked = true };
        Assert.Equal(CliExitCodes.ValidationError, await fixture.RunAsync("environments", "update", "staging", "--ssh-key", "key_1"));
        Assert.DoesNotContain(fixture.Handler.Requests, x => x.StartsWith("PATCH", StringComparison.Ordinal));
        Assert.Contains("immutable after allocation", fixture.Console.Errors, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, "ssh_key_invalid", CliExitCodes.ValidationError, "SSH key rejected")]
    [InlineData(404, "ssh_key_not_found", CliExitCodes.ValidationError, "providers ssh-keys --plan")]
    [InlineData(403, "forbidden", CliExitCodes.AuthError, "providers:read")]
    [InlineData(409, "ssh_selection_locked", CliExitCodes.ValidationError, "immutable after allocation")]
    [InlineData(409, "ssh_selection_revision_conflict", CliExitCodes.ValidationError, "latest revision")]
    [InlineData(409, "ssh_key_changed", CliExitCodes.ValidationError, "Refresh the key inventory")]
    [InlineData(503, "ssh_inventory_unavailable", CliExitCodes.NetworkOrApiUnavailable, "temporarily unavailable")]
    [InlineData(502, "ssh_registration_failed", CliExitCodes.NetworkOrApiUnavailable, "rejected the SSH key registration")]
    public async Task Ssh_api_errors_map_to_actionable_messages(int status, string code, int exitCode, string message)
    {
        using var fixture = new Fixture { PatchError = (status, code) };
        Assert.Equal(exitCode, await fixture.RunAsync("environments", "update", "staging", "--ssh-key", "key_1"));
        Assert.Contains(message, fixture.Console.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_new_key_selection_names_required_provider_write_scope()
    {
        using var fixture = new Fixture { CreateError = (403, "forbidden") };
        var path = fixture.Dir.Write("k.pub", Ed25519Key);
        Assert.Equal(CliExitCodes.AuthError, await fixture.RunAsync("environments", "create", "staging", "--plan", "small", "--ssh-public-key-file", path));
        Assert.Contains("providers:write", fixture.Console.Errors, StringComparison.Ordinal);
        Assert.Contains("apps:write", fixture.Console.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_ssh_keys_resolves_plan_account_and_lists_inventory()
    {
        using var fixture = new Fixture();
        Assert.Equal(CliExitCodes.Success, await fixture.RunAsync("providers", "ssh-keys", "--plan", "small"));
        Assert.Contains("GET /api/providers/acct_1/ssh-keys?organizationId=org", fixture.Handler.Requests);
        Assert.Contains("key_1\tLaptop\ted25519\tSHA256:fixture\tready", fixture.Console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apps_inspect_shows_ssh_selection_and_verified_connection()
    {
        using var fixture = new Fixture();
        Assert.Equal(CliExitCodes.Success, await fixture.RunAsync("apps", "inspect", "demo", "--environment", "staging"));
        Assert.Contains("SSH access: existing key Laptop ed25519 SHA256:fixture  state=ready  revision=0", fixture.Console.Output, StringComparison.Ordinal);
        Assert.Contains("SSH: ssh deploy@203.0.113.10", fixture.Console.Output, StringComparison.Ordinal);
    }

    private static JsonElement Serialize(object value) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static byte[] Blob(params object[] fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            var bytes = field as byte[] ?? System.Text.Encoding.ASCII.GetBytes((string)field);
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private static string RsaKey(int bits)
    {
        using var rsa = RSA.Create(bits);
        var parameters = rsa.ExportParameters(false);
        static byte[] Mpint(byte[] value) => (value[0] & 0x80) != 0 ? [0, .. value] : value;
        return "ssh-rsa " + Convert.ToBase64String(Blob("ssh-rsa", Mpint(parameters.Exponent!), Mpint(parameters.Modulus!)));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("minicloud-ssh-cli-").FullName;
        public string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class Fixture : IDisposable
    {
        public TempDir Dir { get; } = new();
        public TestConsole Console { get; } = new();
        public Handler Handler { get; } = new();
        public long Revision { set => Handler.Revision = value; }
        public bool Locked { set => Handler.Locked = value; }
        public (int Status, string Code)? PatchError { set => Handler.PatchError = value; }
        public (int Status, string Code)? CreateError { set => Handler.CreateError = value; }

        public async Task<int> RunAsync(params string[] args)
        {
            var configPath = System.IO.Path.Combine(Dir.Path, "minicloud.yml");
            if (!File.Exists(configPath))
                await File.WriteAllTextAsync(configPath, MinicloudConfigWriter.Write(new MinicloudConfig("demo", "sqlite", null,
                    new Dictionary<string, MinicloudServiceConfig> { ["web"] = new(null, null, "example/web:latest", 8080, true, "/", "/health") }) { AppId = "app_main" }));
            var environment = CliEnvironment.ForTests("https://api.example", Dir.Path);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var application = new CliApplication(Console, environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(Handler)));
            var withConfig = args[0] is "environments" ? [.. args, "--config", configPath] : args;
            return await application.RunAsync(withConfig, default);
        }

        public void Dispose() => Dir.Dispose();
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        private readonly Dictionary<string, string> _bodies = [];
        public long Revision;
        public bool Locked;
        public (int Status, string Code)? PatchError, CreateError;

        public JsonElement Body(string request) => JsonDocument.Parse(_bodies[request]).RootElement;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            Requests.Add(key);
            if (request.Content is not null)
                _bodies[key] = await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri.AbsolutePath;
            var access = new { mode = "existing", providerAccountId = "acct_1", keyId = "key_1", name = "Laptop", fingerprint = "SHA256:fixture", keyType = "ed25519", revision = Revision, registrationState = "ready", selectionLocked = Locked };
            object? response = (request.Method.Method, path) switch
            {
                ("GET", "/api/me") => new { id = "user", email = "test@example.com", organizations = new[] { new { id = "org", name = "Test", slug = "test", role = "owner" } } },
                ("GET", "/api/apps") => new[] { App() },
                ("GET", "/api/apps/app_main") => App(),
                ("GET", "/api/plans") => new[] { new { id = "plan_1", organizationId = "org", providerAccountId = "acct_1", name = "small", providerName = "Hetzner", providerType = "hetzner", region = "nbg1", sku = "cx23", image = (string?)null, buildPlatform = "linux/amd64", enabled = true, accountEnabled = true, revision = 0, provisioningLocked = false } },
                ("GET", "/api/providers/acct_1/ssh-keys") => new[] { new { id = "key_1", providerAccountId = "acct_1", name = "Laptop", fingerprint = "SHA256:fixture", keyType = "ed25519", registrationState = "ready" } },
                ("GET", "/api/environments/env_staging") => new
                {
                    id = "env_staging", applicationId = "app_main", organizationId = "org", name = "Demo", slug = "demo", plan = "small", database = "sqlite",
                    environmentName = "staging", branches = Array.Empty<object>(), sshAccess = access, sshConnection = new { address = "203.0.113.10", username = "deploy" }
                },
                ("GET", "/api/domains/services") => Array.Empty<object>(),
                ("POST", "/api/apps/app_main/environments") when CreateError is { } error => Error(error),
                ("POST", "/api/apps/app_main/environments") => Environment(access),
                ("PATCH", "/api/apps/app_main/environments/env_staging") when PatchError is { } error => Error(error),
                ("PATCH", "/api/apps/app_main/environments/env_staging") => Environment(access),
                _ => throw new InvalidOperationException(key)
            };
            return response as HttpResponseMessage ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }

        private static HttpResponseMessage Error((int Status, string Code) error) =>
            new((HttpStatusCode)error.Status) { Content = JsonContent.Create(new { error = new { code = error.Code, message = "Fixture message.", details = Array.Empty<object>() } }) };

        private static object App() => new
        {
            id = "app_main", organizationId = "org", name = "Demo", slug = "demo",
            environments = new[] { CliApiFixtures.EnvironmentSummary("env_prod", "production"), new { id = "env_staging", name = "staging", kind = "persistent", plan = "small", database = "sqlite", deletionStatus = (string?)null } }
        };

        private static object Environment(object access) => new
        {
            id = "env_staging", applicationId = "app_main", organizationId = "org", name = "staging", kind = "persistent", plan = "small", database = "sqlite", sshAccess = access
        };
    }

    private sealed class TestConsole : IConsole
    {
        public bool SupportsAnsi => false;
        public string Output { get; private set; } = "";
        public string Errors { get; private set; } = "";
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + System.Environment.NewLine;
        public void WriteError(string message) => Errors += message + System.Environment.NewLine;
        public string? ReadLine() => null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
