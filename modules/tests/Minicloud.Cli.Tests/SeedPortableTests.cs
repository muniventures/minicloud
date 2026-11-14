// SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;
using Minicloud.Cli.Seed;

namespace Minicloud.Tests;

[Collection("SeedAgentEnvironment")]
public sealed class SeedPortableTests : IDisposable
{
    private readonly string home = Path.GetFullPath(Path.Combine(".local", "artifacts", "seed-portable-tests-" + Guid.NewGuid().ToString("N")));
    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, true); }
    private static SeedPortableConfig Config() => new()
    {
        SchemaVersion = 1, SeedId = new('a', 32), Provider = "digitalocean", ProviderAccount = "DigitalOcean account synthetic",
        Region = "region", Size = "size", Domain = "cloud.example.com", AppDomain = "apps.example.com",
        ServerVersion = "server-v1.2.3", InitialCreationIntent = SeedPortableConfig.CreationIntent
    };
    private static SeedState State()
    {
        var state = Config().State();
        state.Plan = state.Plan with
        {
            ReleaseDigest = new('b', 64), ReleaseSource = new('c', 40),
            Quote = state.Plan.Quote with { Image = "42", Costs = [new("VPS", 12m, .02m, "USD", "monthly", "tax", "synthetic")] }
        };
        return state;
    }

    [Theory]
    [InlineData("\"schemaVersion\":1,\"schemaVersion\":2")]
    [InlineData("\"schemaVersion\":1,\"SchemaVersion\":2")]
    [InlineData("\"schemaVersion\":1,\"target\":{\"serverId\":\"one\",\"serverId\":\"two\"}")]
    public void Duplicate_configuration_fields_fail_before_any_action(string fields)
        => Assert.Throws<CliCommandException>(() => SeedPortableConfig.Parse("{" + fields + "}"));

    [Fact]
    public void Unknown_configuration_fields_and_future_schemas_fail_closed()
    {
        var json = JsonSerializer.Serialize(Config(), SeedJournal.Json);
        Assert.Throws<JsonException>(() => SeedPortableConfig.Parse(json.Replace("\"schemaVersion\": 1", "\"unrecognized\": 1")));
        Assert.Throws<CliCommandException>(() => SeedPortableConfig.Parse(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")));
        Assert.Throws<CliCommandException>(() => (Config() with { InitialCreationIntent = "retry" }).Validate());
    }

    [Theory]
    [InlineData("absent", "unknown", null, "new_seed")]
    [InlineData("empty", "pending", null, "continue_partial")]
    [InlineData("preparing", "prepared", null, "continue_partial")]
    [InlineData("installed", "initializing", "server-v1.2.3", "continue_partial")]
    [InlineData("installed", "ready", "server-v1.2.3", "current")]
    [InlineData("installed", "awaiting_operator", "server-v1.2.3", "current")]
    [InlineData("installed", "ready", "server-v1.2.2", "upgrade_required")]
    [InlineData("reconciliation_required", "unknown", null, "reconciliation_required")]
    [InlineData("unavailable", "unknown", null, "reconciliation_required")]
    public void Classification_discovers_installed_release_before_comparison(string observed, string phase, string? release, string expected)
        => Assert.Equal(expected, SeedPortable.Classify(new(observed, phase, Release: release), "server-v1.2.3"));

    [Theory]
    [InlineData("installed", "ready", "server-v1.2.3")]
    [InlineData("installed", "awaiting_operator", "server-v1.2.3")]
    [InlineData("installed", "ready", "server-v1.2.2")]
    [InlineData("reconciliation_required", "unknown", null)]
    [InlineData("unavailable", "unknown", null)]
    public async Task Current_upgrade_and_uncertain_hosts_are_never_prepared_installed_or_repaired(string observed, string phase, string? release)
    {
        var host = new Host();
        var observation = new SeedObservation(observed, phase, Release: release, Health: "unavailable", OpenBao: "unavailable");
        Assert.Same(observation, await SeedPortable.ContinueAsync(State(), "unused", host, host, observation, default));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Machine_B_continues_original_partial_installer_with_no_provider_or_journal()
    {
        var state = State();
        state.Server = new("original-server", state.Id, "region", "size", "42", "203.0.113.1");
        var observation = new SeedObservation("installed", "initialized", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            "server-v1.2.3", new('c', 40), Prepared: true);
        var host = new Host { Observation = observation with { Progress = "awaiting_operator" } };
        var report = new SeedPortableReport { SeedId = state.Id, Domain = Config().Domain, AppDomain = Config().AppDomain,
            RequestedRelease = Config().ServerVersion };
        SeedPortable.Observe(report, state, observation);
        var installation = state.InstallationId;
        var operation = state.InstallerOperationId;
        var resource = state.Server;
        var result = await SeedPortable.ContinueAsync(state, home, host, host, observation, default);
        SeedPortable.Observe(report, state, result);
        Assert.Equal(["dns", "install", "inspect"], host.Calls);
        Assert.Equal(installation, state.InstallationId);
        Assert.Equal(operation, state.InstallerOperationId);
        Assert.Same(resource, state.Server);
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public async Task Partial_preparation_waits_for_DNS_without_installer_or_hidden_key_generation()
    {
        var host = new Host { Dns = false };
        var result = await SeedPortable.ContinueAsync(State(), home, host, host, new("preparing", "pending"), default);
        Assert.Equal(["prepare", "dns"], host.Calls);
        Assert.Equal("awaiting_dns", result.Progress);
        Assert.True(result.Prepared);
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public async Task Explicit_local_source_plan_can_stage_before_DNS_without_claiming_ready()
    {
        var host = new Host { Dns = false, Observation = new("installed", "awaiting_dns_tls", Release: "server-v1.2.3") };
        var state = State();
        state.Plan = state.Plan with { ArtifactTrust = SeedLocalSource.Trust };
        var result = await SeedPortable.ContinueAsync(state, home, host, host, new("preparing", "pending"), default);
        Assert.Equal(["prepare", "dns", "install", "inspect"], host.Calls);
        Assert.Equal("awaiting_dns_tls", result.Progress);
        var config = Config();
        Assert.NotEqual(SeedPortable.Approval(config, State(), "new_seed", "key", true),
            SeedPortable.Approval(config, state, "new_seed", "key", true));
    }

    [Fact]
    public void Approval_survives_credential_rotation_but_binds_cost_identity_key_classification_and_resolved_target()
    {
        var config = Config() with { ProviderCredential = "synthetic-old-credential" };
        var state = State();
        string Digest(SeedPortableConfig c, SeedState s, string key = "public-key", string classification = "new_seed")
            => SeedPortable.Approval(c, s, classification, key, true);
        var approval = Digest(config, state);
        Assert.Equal(approval, Digest(config with { ProviderCredential = "synthetic-rotated-credential" }, state));
        state.Plan = state.Plan with { Quote = state.Plan.Quote with { RetrievedAt = DateTimeOffset.UtcNow } };
        Assert.Equal(approval, Digest(config, state));
        Assert.NotEqual(approval, Digest(config with { ProviderAccount = "different authorized account" }, state));
        Assert.NotEqual(approval, Digest(config with { SeedId = new('d', 32) }, state));
        Assert.NotEqual(approval, Digest(config, state, "different-public-key"));
        Assert.NotEqual(approval, Digest(config, state, classification: "continue_partial"));
        state.Server = new("resolved", state.Id, "region", "size", "42", "203.0.113.1");
        Assert.NotEqual(approval, Digest(config, state));
        state.Server = null;
        state.Plan = state.Plan with { Quote = state.Plan.Quote with { Costs = [state.Plan.Quote.Costs[0] with { Monthly = 13 }] } };
        Assert.NotEqual(approval, Digest(config, state));
    }

    [Fact]
    public async Task Lost_create_response_never_retries_and_existing_operation_key_blocks_even_with_zero_inventory()
    {
        var config = Config();
        var state = State();
        var provider = new Provider { LoseResponse = true };
        var approval = SeedPortable.Approval(config, state, "new_seed", "actual-user-public-key", true);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            SeedPortable.CreateAsync(config, state, provider, "actual-user-public-key", true, approval, "new_seed", default));
        Assert.Equal(1, provider.Creates);
        Assert.Equal("actual-user-public-key", provider.UploadedKey);
        Assert.True(state.ServerCreateStarted);
        Assert.Null(state.Server);
        var machineB = State();
        await Assert.ThrowsAsync<CliCommandException>(() =>
            SeedPortable.CreateAsync(config, machineB, provider, "actual-user-public-key", true, approval, "new_seed", default));
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.KeyCreates);
        Assert.Equal(0, provider.Deletes);
    }

    [Theory]
    [InlineData(false, "new_seed")]
    [InlineData(true, "reconciliation_required")]
    public async Task Stale_report_approval_never_replaces_explicit_initial_creation_intent(bool newSeed, string classification)
    {
        var state = State();
        var provider = new Provider();
        var approval = SeedPortable.Approval(Config(), state, "new_seed", "key", true);
        await Assert.ThrowsAsync<CliCommandException>(() =>
            SeedPortable.CreateAsync(Config(), state, provider, "key", newSeed, approval, classification, default));
        Assert.Equal(0, provider.Creates + provider.KeyCreates);
    }

    [Fact]
    public async Task Existing_user_provider_key_is_reused_without_a_hidden_key_or_modification()
    {
        var config = Config();
        var state = State();
        var provider = new Provider { ExistingUserKey = "original-user-key" };
        var approval = SeedPortable.Approval(config, state, "new_seed", "user-public-key", true);
        await SeedPortable.CreateAsync(config, state, provider, "user-public-key", true, approval, "new_seed", default);
        Assert.Equal("original-user-key", state.SshKeyId);
        Assert.False(state.KeyCreateStarted);
        Assert.Equal(0, provider.KeyCreates + provider.Deletes);
        Assert.Equal(1, provider.Creates);
    }

    [Theory]
    [InlineData(0, "preparing")]
    [InlineData(0, "reconciliation_required")]
    [InlineData(1, "unavailable")]
    public async Task SSH_inspection_has_one_observational_request_even_when_host_is_partial_or_failing(int exit, string observed)
    {
        var state = State();
        state.Server = new("original", state.Id, "region", "size", "42", "203.0.113.1");
        var calls = 0;
        var host = new SeedHost("explicit-user-key", false, (executable, arguments, input, ct, agent) =>
        {
            calls++;
            var args = arguments.ToArray();
            Assert.Equal("ssh", executable);
            Assert.Null(input);
            Assert.False(agent);
            Assert.Contains("ForwardAgent=no", args);
            Assert.Contains("explicit-user-key", args);
            Assert.DoesNotContain("id_ed25519", string.Join(' ', args));
            Assert.Contains("python3 -B -c", args[^1]);
            Assert.Contains(SeedHost.InspectionScript.Replace("'", "'\\''"), args[^1]);
            return Task.FromResult(new SeedProcessResult(exit, JsonSerializer.Serialize(new { state = observed, progress = "unknown" })));
        });
        Assert.Equal(observed, (await host.InspectAsync(state, home, default)).State);
        Assert.Equal(1, calls);
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public async Task Mutation_requests_revalidate_host_under_the_shared_lock_before_writing()
    {
        if (OperatingSystem.IsWindows()) return;
        var state = State();
        state.Server = new("original", state.Id, "region", "size", "42", "203.0.113.1");
        var commands = new List<string>();
        var host = new SeedHost("explicit-user-key", false, (executable, arguments, input, ct, agent) =>
        {
            commands.Add(arguments.Last());
            return Task.FromResult(new SeedProcessResult(0, ""));
        });
        await host.AddOperatorKeysAsync(state, home, default);
        var command = Assert.Single(commands);
        var result = await SeedProcess.RunAsync("python3", ["-B", "-c",
            "import shlex,sys; args=shlex.split(sys.stdin.read()); code=args[args.index('-c')+1]; compile(code,'mutation-guard','exec'); assert code.index('LOCK_EX') < code.index('acquire_lock=False') < code.index('sys.stdin')"],
            Encoding.UTF8.GetBytes(command), default);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("preparing")]
    [InlineData("installed")]
    public async Task Fresh_machine_check_uses_only_pinned_observation_without_provider_or_installer(string observed)
    {
        if (OperatingSystem.IsWindows()) return;
        SeedFiles.Directory(home);
        var key = Path.Combine(home, "user-key");
        await SeedProcess.RequireAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", key], null, default);
        var hostKey = SeedOperatorKeysTests.PublicKey();
        var fingerprint = SeedHost.Fingerprint("203.0.113.1 " + hostKey, "203.0.113.1");
        var config = Config() with
        {
            Target = new("original", "203.0.113.1", fingerprint, "42"),
            InitialCreationIntent = null
        };
        var configPath = Path.Combine(home, "config.json");
        var reportPath = Path.Combine(home, "report.json");
        SeedFiles.WriteNew(configPath, JsonSerializer.SerializeToUtf8Bytes(config, SeedJournal.Json));
        var observation = JsonSerializer.Serialize(new
        {
            state = observed, progress = observed == "installed" ? "initialized" : "pending",
            release = "server-v1.2.3", source = new string('c', 40), health = "unavailable", openBao = "unavailable",
            configuration = new { consoleUrl = "https://" + config.Domain, apiUrl = "https://api." + config.Domain,
                appDomain = config.AppDomain }, publicAddresses = new[] { "203.0.113.1" }
        });
        var log = Path.Combine(home, "ssh-observations");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        foreach (var (name, script) in new[]
        {
            ("ssh-keyscan", "#!/bin/sh\nprintf '%s\\n' '203.0.113.1 " + hostKey + "'\n"),
            ("ssh", "#!/bin/sh\nprintf '%s\\n' \"$*\" >> '" + log + "'\nprintf '%s\\n' '" + observation + "'\n")
        })
        {
            var path = Path.Combine(home, name);
            SeedFiles.WriteNew(path, Encoding.UTF8.GetBytes(script));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try
        {
            Environment.SetEnvironmentVariable("PATH", home + Path.PathSeparator + oldPath);
            Assert.Equal(10, await Application().RunAsync(["seed", "check", "--config", configPath,
                "--report", reportPath, "--ssh-key", key], default));
            var commands = File.ReadAllText(log);
            Assert.Contains("python3 -B -c", commands);
            Assert.DoesNotContain("install-server.sh", commands);
            Assert.DoesNotContain("apt-get", commands);
            Assert.DoesNotContain("statusOnly", commands);
            Assert.DoesNotContain("LOCK_EX", commands);
            Assert.DoesNotContain("O_CREAT", commands);
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal(fingerprint, report.RootElement.GetProperty("verifiedHostFingerprint").GetString());
            Assert.Equal("unavailable", report.RootElement.GetProperty("result").GetString());
            Assert.False(Directory.Exists(Path.Combine(home, "unused-cli-home")));
            Assert.Empty(Directory.GetDirectories(home));
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
    }

    [Fact]
    public async Task Supplied_user_key_needs_no_public_sidecar_or_hidden_client_identity_and_is_not_changed()
    {
        SeedFiles.Directory(home);
        var key = Path.Combine(home, "user-key");
        await SeedProcess.RequireAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", key], null, default);
        var original = File.ReadAllBytes(key);
        File.Delete(key + ".pub");
        var publicKeyA = await SeedUserKey.ReadAsync(key, false, default);
        var publicKeyB = await SeedUserKey.ReadAsync(key, false, default);
        Assert.Equal(publicKeyA, publicKeyB);
        Assert.Equal(original, File.ReadAllBytes(key));
        Assert.Equal([key], Directory.GetFiles(home));
    }

    [Fact]
    public async Task Encrypted_user_identity_is_preserved_and_requires_explicit_agent_opt_in()
    {
        SeedFiles.Directory(home);
        var key = Path.Combine(home, "encrypted-user-key");
        var publicKey = SeedOperatorKeysTests.PublicKey();
        static byte[] Field(byte[] value) => [0, 0, 0, checked((byte)value.Length), .. value];
        byte[] blob = [.. "openssh-key-v1\0"u8, .. Field("aes256-ctr"u8.ToArray()), .. Field("bcrypt"u8.ToArray()),
            .. Field([]), 0, 0, 0, 1, .. Field(Convert.FromBase64String(publicKey.Split(' ')[1]))];
        var pem = "-----BEGIN OPENSSH PRIVATE KEY-----\n" + Convert.ToBase64String(blob) + "\n-----END OPENSSH PRIVATE KEY-----";
        SeedFiles.WriteNew(key, Encoding.ASCII.GetBytes(pem));
        Assert.Equal(publicKey, SeedUserKey.PublicKey(pem));
        var error = await Assert.ThrowsAsync<CliCommandException>(() => SeedUserKey.ReadAsync(key, false, default));
        Assert.Contains("--use-ssh-agent", error.Message);
        Assert.Equal(pem, File.ReadAllText(key));
        Assert.Equal([key], Directory.GetFiles(home));
    }

    [Fact]
    public async Task Agent_socket_is_omitted_by_default_and_explicit_matching_encrypted_identity_is_accepted()
    {
        if (OperatingSystem.IsWindows()) return;
        SeedFiles.Directory(home);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldSocket = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        var publicKey = SeedOperatorKeysTests.PublicKey();
        static byte[] Field(byte[] value) => [0, 0, 0, checked((byte)value.Length), .. value];
        byte[] blob = [.. "openssh-key-v1\0"u8, .. Field("aes256-ctr"u8.ToArray()), .. Field("bcrypt"u8.ToArray()),
            .. Field([]), 0, 0, 0, 1, .. Field(Convert.FromBase64String(publicKey.Split(' ')[1]))];
        var pem = "-----BEGIN OPENSSH PRIVATE KEY-----\n" + Convert.ToBase64String(blob) + "\n-----END OPENSSH PRIVATE KEY-----";
        var key = Path.Combine(home, "encrypted-user-key");
        SeedFiles.WriteNew(key, Encoding.ASCII.GetBytes(pem));
        var agent = Path.Combine(home, "ssh-add");
        SeedFiles.WriteNew(agent, Encoding.UTF8.GetBytes("#!/bin/sh\nprintf '%s\\n' '" + publicKey + "'\n"));
        File.SetUnixFileMode(agent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Environment.SetEnvironmentVariable("PATH", home + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", "synthetic-explicit-agent-socket");
            var defaultProcess = await SeedProcess.RunAsync("python3",
                ["-B", "-c", "import os; print(os.environ.get('SSH_AUTH_SOCK','absent'))"], null, default);
            Assert.Equal("absent", defaultProcess.Output.Trim());
            var explicitProcess = await SeedProcess.RunAsync("python3",
                ["-B", "-c", "import os; print(os.environ.get('SSH_AUTH_SOCK','absent'))"], null, default, true);
            Assert.Equal("synthetic-explicit-agent-socket", explicitProcess.Output.Trim());
            Assert.Equal(publicKey, await SeedUserKey.ReadAsync(key, true, default));
            Assert.Equal(pem, File.ReadAllText(key));
            File.WriteAllText(agent, "#!/bin/sh\nprintf '%s\\n' '" + SeedOperatorKeysTests.PublicKey(8) + "'\n");
            await Assert.ThrowsAsync<CliCommandException>(() => SeedUserKey.ReadAsync(key, true, default));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", oldSocket);
        }
    }

    [Fact]
    public async Task Private_handoff_reads_original_installer_material_into_protected_separate_outputs_without_rotation()
    {
        SeedFiles.Directory(home);
        var state = State();
        state.Server = new("original", state.Id, "region", "size", "42", "203.0.113.1");
        var id = Guid.NewGuid().ToString();
        var operation = Guid.NewGuid().ToString();
        var metadata = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, product = "minicloud", installationId = id, operationId = operation,
            releaseTag = state.Plan.Selection.ServerVersion, sourceRevision = state.Plan.ReleaseSource,
            root = "/opt/minicloud", recoveryDir = "/var/lib/minicloud-seed/" + state.Id + "/recovery",
            publicAddresses = new[] { state.Server.Address },
            configuration = new { consoleUrl = "https://" + Config().Domain, apiUrl = "https://api." + Config().Domain,
                appDomain = Config().AppDomain }, setupTokenFile = "setup-token-initial"
        });
        var host = new SeedHost("explicit-user-key", false, (executable, arguments, input, ct, agent) =>
        {
            var command = arguments.Last();
            Assert.Null(input);
            Assert.DoesNotContain("rotate-token", command);
            Assert.DoesNotContain("InstallAsync", command);
            var output = command.Contains("binding=pathlib.Path", StringComparison.Ordinal) ? metadata
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(command.EndsWith("'/opt/minicloud/installation.json'", StringComparison.Ordinal)
                    ? metadata : "synthetic-private-original-material"));
            return Task.FromResult(new SeedProcessResult(0, output));
        });
        var destination = Path.Combine(home, "private-recovery");
        await host.ExportAsync(state, home, destination, true, default);
        Assert.Equal(id, state.InstallationId);
        Assert.Equal(operation, state.InstallerOperationId);
        Assert.Equal("synthetic-private-original-material", File.ReadAllText(Path.Combine(destination, "setup-token-initial")));
        foreach (var file in Directory.GetFiles(destination)) SeedFiles.Check(file);
        SeedFiles.Check(destination, true);
        var original = File.ReadAllBytes(Path.Combine(destination, "openbao-recovery.json"));
        await host.ExportAsync(state, home, destination, true, default);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(destination, "openbao-recovery.json")));
        // A fresh runner has no RecoveryDirectory journal; existing protected
        // bytes must still reconcile without replacing original material.
        var machineB = State();
        machineB.Server = state.Server;
        await host.ExportAsync(machineB, home, destination, true, default);
        Assert.Equal(id, machineB.InstallationId);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(destination, "openbao-recovery.json")));
        File.WriteAllText(Path.Combine(destination, "openbao-recovery.json"), "conflicting-independent-material");
        await Assert.ThrowsAsync<CliCommandException>(() => host.ExportAsync(state, home, destination, true, default));
        Assert.Equal("conflicting-independent-material", File.ReadAllText(Path.Combine(destination, "openbao-recovery.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clean_runner_with_missing_or_stale_report_has_no_creation_authority_or_client_config(bool staleReport)
    {
        SeedFiles.Directory(home);
        var configPath = Path.Combine(home, "config.json");
        SeedFiles.WriteNew(configPath, JsonSerializer.SerializeToUtf8Bytes(Config(), SeedJournal.Json));
        var reportPath = Path.Combine(home, "report.json");
        if (staleReport)
            SeedFiles.WriteNew(reportPath, Encoding.UTF8.GetBytes(
                $$"""{"schemaVersion":1,"kind":"minicloud-seed-report","seedId":"{{Config().SeedId}}","classification":"new_seed","approval":"stale"}"""));
        var console = new ConsoleFixture();
        var emptyHome = Path.Combine(home, "machine-b-empty-config");
        var environment = CliEnvironment.ForTests("", emptyHome);
        var token = new TokenStore(environment);
        var application = new CliApplication(console, environment, token,
            new MinicloudApiClient(environment, token, new HttpClient(new NoRequests())));
        Assert.Equal(12, await application.RunAsync(["seed", "check", "--config", configPath, "--report", reportPath], default));
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal("reconciliation_required", report.RootElement.GetProperty("classification").GetString());
        Assert.False(Directory.Exists(emptyHome));
        Assert.DoesNotContain("stale", File.ReadAllText(reportPath));
        Assert.Empty(Directory.GetDirectories(home));
    }

    [CollectionDefinition("SeedAgentEnvironment", DisableParallelization = true)]
    public sealed class SeedAgentEnvironmentCollection;

    [Fact]
    public void Report_does_not_expose_config_credentials_or_installer_secrets_or_treat_stale_ready_as_live_health()
    {
        var state = State();
        var report = new SeedPortableReport { SeedId = state.Id, Domain = Config().Domain,
            AppDomain = Config().AppDomain, RequestedRelease = Config().ServerVersion };
        state.Server = new("original", state.Id, "region", "size", "42", "203.0.113.1", "original-ip");
        SeedPortable.Observe(report, state, new("installed", "ready", Release: Config().ServerVersion,
            Health: "unavailable", OpenBao: "unavailable"));
        Assert.Equal("current", report.Classification);
        Assert.Equal("unavailable", report.Result);
        Assert.Equal(10, SeedPortable.ExitCode(report));
        Assert.Equal(["server:original", "ipv4:original-ip"], report.RetainedBillableResources);
        var text = JsonSerializer.Serialize(report, SeedJournal.Json);
        foreach (var forbidden in new[] { "providerCredential", "privateKey", "setupToken", "recoverySecret", "openbao-root-token" })
            Assert.DoesNotContain(forbidden, text);
    }

    [Theory]
    [InlineData("--generate-ssh-key", false)]
    [InlineData("--generate-ssh-key", true)]
    [InlineData("--ssh-key", false)]
    [InlineData("--ssh-key", true)]
    public async Task Reports_never_replace_private_or_public_keys_even_on_invalid_invocation(string option, bool publicSidecar)
    {
        SeedFiles.Directory(home);
        var configPath = Path.Combine(home, "config.json");
        SeedFiles.WriteNew(configPath, JsonSerializer.SerializeToUtf8Bytes(Config(), SeedJournal.Json));
        var keyPath = Path.Combine(home, "user-key");
        var reportPath = keyPath + (publicSidecar ? ".pub" : "");
        SeedFiles.WriteNew(reportPath, "original-user-key-material"u8.ToArray());
        var application = Application();
        Assert.Equal(2, await application.RunAsync(["seed", "plan", "--config", configPath,
            "--report", reportPath, option, keyPath], default));
        Assert.Equal("original-user-key-material", File.ReadAllText(reportPath));
        Assert.Equal(2, Directory.GetFiles(home).Length);
    }

    [Fact]
    public async Task Another_seeds_report_is_not_replaced_by_a_failed_check()
    {
        SeedFiles.Directory(home);
        var configPath = Path.Combine(home, "config.json");
        SeedFiles.WriteNew(configPath, JsonSerializer.SerializeToUtf8Bytes(Config(), SeedJournal.Json));
        var reportPath = Path.Combine(home, "report.json");
        var original = $$"""{"schemaVersion":1,"kind":"minicloud-seed-report","seedId":"{{new string('b', 32)}}"}""";
        SeedFiles.WriteNew(reportPath, Encoding.UTF8.GetBytes(original));
        Assert.Equal(2, await Application().RunAsync(["seed", "check", "--config", configPath, "--report", reportPath], default));
        Assert.Equal(original, File.ReadAllText(reportPath));
    }

    private CliApplication Application()
    {
        var environment = CliEnvironment.ForTests("", Path.Combine(home, "unused-cli-home"));
        var token = new TokenStore(environment);
        return new(new ConsoleFixture(), environment, token,
            new MinicloudApiClient(environment, token, new HttpClient(new NoRequests())));
    }

    [Fact]
    public async Task Python_inspector_observes_real_metadata_without_modifying_any_fixture_bytes()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        Assert.NotNull(root);
        SeedFiles.Directory(home);
        var result = await SeedProcess.RunAsync("python3", ["-B",
            Path.Combine(root.FullName, "modules/cli/Seed/tests/test_inspect_host.py"), home], null, default);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private sealed class Host : ISeedHost, ISeedInspector
    {
        public readonly List<string> Calls = [];
        public bool Dns = true;
        public SeedObservation Observation = new("installed", "awaiting_operator", Release: "server-v1.2.3");
        public Task CheckPrerequisitesAsync(CancellationToken ct) => throw new InvalidOperationException("No provisioning prerequisites on observation.");
        public Task GenerateKeyAsync(string path, CancellationToken ct) => throw new InvalidOperationException("No hidden key.");
        public Task TrustAsync(SeedState state, string path, string? fingerprint, CancellationToken ct) => throw new InvalidOperationException("Trust must already be independent.");
        public Task PrepareAsync(SeedState state, string path, CancellationToken ct) { Calls.Add("prepare"); return Task.CompletedTask; }
        public Task AddOperatorKeysAsync(SeedState state, string path, CancellationToken ct) { Calls.Add("operator-keys"); return Task.CompletedTask; }
        public Task<bool> DnsReadyAsync(SeedState state, CancellationToken ct) { Calls.Add("dns"); return Task.FromResult(Dns); }
        public Task<string> InstallAsync(SeedState state, string path, bool statusOnly, CancellationToken ct)
        { Assert.False(statusOnly); Calls.Add("install"); return Task.FromResult("awaiting_operator"); }
        public Task<SeedObservation> InspectAsync(SeedState state, string path, CancellationToken ct)
        { Calls.Add("inspect"); return Task.FromResult(Observation); }
        public Task ExportAsync(SeedState state, string path, string destination, bool token, CancellationToken ct) => throw new InvalidOperationException();
        public Task VerifyHttpsAsync(SeedState state, CancellationToken ct) => throw new InvalidOperationException();
    }

    private sealed class Provider : ISeedProvider
    {
        public int Creates, KeyCreates, Deletes;
        public bool LoseResponse;
        public string? UploadedKey;
        public string? ExistingUserKey;
        public string CredentialScope(string operation) => throw new InvalidOperationException("Never bind ownership to credential bytes.");
        public Task<string> GetAccountIdentityAsync(CancellationToken ct) => Task.FromResult(Config().ProviderAccount);
        public Task<SeedQuote> QuoteAsync(SeedSelection s, string operation, CancellationToken ct) => Task.FromResult(State().Plan.Quote);
        public Task<string?> FindKeyAsync(string operation, string key, CancellationToken ct) => Task.FromResult(UploadedKey is null ? null : "key-id");
        public Task<string?> FindUserKeyAsync(string key, CancellationToken ct) => Task.FromResult(ExistingUserKey);
        public Task<string> CreateKeyAsync(string operation, string key, CancellationToken ct)
        { KeyCreates++; UploadedKey = key; return Task.FromResult("key-id"); }
        public Task<SeedResource?> FindServerAsync(string operation, CancellationToken ct) => Task.FromResult<SeedResource?>(null);
        public Task<SeedResource> CreateServerAsync(SeedState state, CancellationToken ct)
        {
            Creates++;
            if (LoseResponse) throw new HttpRequestException("synthetic-secret-provider-response");
            return Task.FromResult(new SeedResource("server", state.Id, "region", "size", "42", "203.0.113.1"));
        }
        public Task<bool> DeleteServerAsync(SeedState state, CancellationToken ct) { Deletes++; throw new InvalidOperationException(); }
        public Task DeleteKeyAsync(string id, string operation, string key, CancellationToken ct) { Deletes++; throw new InvalidOperationException(); }
        public void Dispose() { }
    }

    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("No instance API calls.");
    }
    private sealed class ConsoleFixture : IConsole
    {
        public bool SupportsAnsi => false;
        public void Write(string message) { }
        public void WriteLine(string message = "") { }
        public void WriteError(string message) { }
        public string? ReadLine() => throw new InvalidOperationException("No prompts.");
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException("No prompts.");
    }
}
