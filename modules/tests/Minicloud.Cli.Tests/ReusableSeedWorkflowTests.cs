// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Minicloud.Cli.Tests;

public sealed class ReusableSeedWorkflowTests : IDisposable
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherDigest = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string ProviderSecret = "synthetic-provider-token-7f3c9a";
    private static readonly string Root = FindRoot();
    private static readonly YamlMappingNode Workflow = Load();
    private readonly string home = Path.GetFullPath(Path.Combine(".local", "artifacts", "seed-workflow-tests-" + Guid.NewGuid().ToString("N")));

    public ReusableSeedWorkflowTests()
    {
        Directory.CreateDirectory(home);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => Directory.Delete(home, true);

    [Fact]
    public void Workflow_is_call_only_with_minimal_permissions_and_pinned_actions()
    {
        var on = Map(Workflow, "on");
        Assert.Equal(["workflow_call"], on.Children.Keys.Select(Text));
        var permissions = Map(Workflow, "permissions");
        Assert.Equal("contents=read", string.Join(",", permissions.Children.Select(entry => $"{Text(entry.Key)}={Text(entry.Value)}")));
        Assert.False(Job.Children.ContainsKey(new YamlScalarNode("permissions")));
        var uses = Steps.Where(step => step.Children.ContainsKey(new YamlScalarNode("uses"))).Select(step => Text(step["uses"])).ToList();
        Assert.NotEmpty(uses);
        Assert.All(uses, value => Assert.Matches("^[a-z0-9-]+/[a-z0-9-]+@[0-9a-f]{40}$", value));
        Assert.DoesNotContain(Steps, step => step.Children.ContainsKey(new YamlScalarNode("uses"))
            && Text(step["uses"]).StartsWith("actions/checkout@", StringComparison.Ordinal));
    }

    [Fact]
    public void Environment_concurrency_and_secrets_are_bound_inside_the_reusable_job()
    {
        Assert.Equal("${{ inputs.environment }}", Text(Job["environment"]));
        var inputs = Map(Map(Map(Workflow, "on"), "workflow_call"), "inputs");
        foreach (var name in new[] { "operation", "environment", "minicloud_version" })
            Assert.Equal("true", Text(Map(inputs, name)["required"]));
        Assert.Equal("boolean", Text(Map(inputs, "new_seed")["type"]));
        Assert.Equal("false", Text(Map(inputs, "new_seed")["default"]));
        Assert.False(Map(inputs, "minicloud_version").Children.ContainsKey(new YamlScalarNode("default")));

        var secrets = Map(Map(Map(Workflow, "on"), "workflow_call"), "secrets");
        Assert.Equal(["MINICLOUD_SEED_CONFIG", "MINICLOUD_SEED_SSH_KEY", "MINICLOUD_SEED_PROVIDER_CREDENTIAL"], secrets.Children.Keys.Select(Text));
        Assert.Equal("true", Text(Map(secrets, "MINICLOUD_SEED_CONFIG")["required"]));

        var concurrency = Map(Job, "concurrency");
        Assert.Contains("inputs.environment", Text(concurrency["group"]));
        Assert.Equal("false", Text(concurrency["cancel-in-progress"]));

        var text = File.ReadAllText(WorkflowPath);
        Assert.DoesNotContain("inherit", text);
        var secretSteps = Steps.Where(step => Yaml(step).Contains("${{ secrets.", StringComparison.Ordinal)).Select(Name).ToList();
        Assert.Equal(["Write protected seed inputs"], secretSteps);
    }

    [Fact]
    public void Scripts_take_inputs_from_env_and_never_trace_or_dump_secrets()
    {
        foreach (var step in Steps.Where(step => step.Children.ContainsKey(new YamlScalarNode("run"))))
        {
            var script = Text(step["run"]);
            Assert.DoesNotContain("${{", script);
            Assert.DoesNotMatch(@"set\s+-[a-z]*x|xtrace|\benv\b\s*$|printenv|SEED_CONFIG\s*\|", script);
            if (!OperatingSystem.IsWindows())
            {
                var path = Path.Combine(home, "syntax.sh");
                File.WriteAllText(path, script);
                Assert.Equal(0, Execute("bash", ["-n", path], new Dictionary<string, string>()).ExitCode);
            }
        }
        var cleanup = Step("Remove protected inputs");
        Assert.Equal("always()", Text(cleanup["if"]));
    }

    [Fact]
    public void Documentation_caller_passes_environment_secrets_by_name_and_dispatches_apply()
    {
        var docs = File.ReadAllText(Path.Combine(Root, "docs", "github-actions.md"));
        var section = docs[docs.IndexOf("## Seed a control plane", StringComparison.Ordinal)..];
        var yaml = Regex.Match(section, "```yaml\n(.*?)```", RegexOptions.Singleline).Groups[1].Value;
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var caller = (YamlMappingNode)stream.Documents[0].RootNode;
        Assert.Equal(["workflow_dispatch"], Map(caller, "on").Children.Keys.Select(Text));
        var job = Map(Map(caller, "jobs"), "seed");
        Assert.StartsWith("muniventures/minicloud/.github/workflows/seed.yml@", Text(job["uses"]));
        Assert.False(job.Children.ContainsKey(new YamlScalarNode("environment")));
        var secrets = Map(job, "secrets");
        var declared = Map(Map(Map(Workflow, "on"), "workflow_call"), "secrets");
        var references = Map(Step("Write protected seed inputs"), "env").Children.Values.Select(Text).ToList();
        Assert.Equal(declared.Children.Keys.Select(Text), secrets.Children.Keys.Select(Text));
        foreach (var name in secrets.Children.Keys.Select(Text))
        {
            var reference = "${{ secrets." + name + " }}";
            Assert.Equal(reference, Text(secrets[name]));
            Assert.Contains(reference, references);
            Assert.Contains("| `" + name + "` |", section);
        }
        Assert.Equal("${{ inputs.approve }}", Text(Map(job, "with")["approve"]));
        Assert.DoesNotContain("inherit", yaml);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("plan")]
    [InlineData("apply")]
    public void Synthetic_environment_secret_names_feed_the_protected_input_step(string operation)
    {
        if (OperatingSystem.IsWindows()) return;
        // Resolve only the declared secret references locally, not GitHub's hosted Environment behavior.
        var selectedEnvironment = new Dictionary<string, string>
        {
            ["MINICLOUD_SEED_CONFIG"] = Config(),
            ["MINICLOUD_SEED_SSH_KEY"] = operation == "check" ? "" : NewKey(),
            ["MINICLOUD_SEED_PROVIDER_CREDENTIAL"] = "synthetic-separate-provider-token",
        };
        var environment = new Dictionary<string, string> { ["OPERATION"] = operation, ["SAFE_BASE"] = home };
        foreach (var (name, value) in Map(Step("Write protected seed inputs"), "env").Children)
        {
            var match = Regex.Match(Text(value), @"^\$\{\{ secrets\.([A-Za-z0-9_]+) \}\}$");
            if (match.Success)
            {
                Assert.True(selectedEnvironment.TryGetValue(match.Groups[1].Value, out var secret),
                    "Secret reference must match the documented Environment secret name.");
                environment[Text(name)] = secret!;
            }
        }
        var result = RunStep("Write protected seed inputs", environment);
        Assert.True(result.ExitCode == 0, result.Output);
        var directory = result.Outputs["dir"];
        Assert.Equal(Config(), File.ReadAllText(Path.Combine(directory, "seed-config.json")));
        Assert.Equal(selectedEnvironment["MINICLOUD_SEED_PROVIDER_CREDENTIAL"],
            File.ReadAllText(Path.Combine(directory, "provider-credential")));
        Assert.Equal(operation != "check", File.Exists(Path.Combine(directory, "ssh-key")));
    }

    [Theory]
    [InlineData("apply", "", "false", "workflow_dispatch", "v1.2.3", "requires approve")]
    [InlineData("apply", "ABC", "false", "workflow_dispatch", "v1.2.3", "requires approve")]
    [InlineData("plan", Digest, "false", "workflow_dispatch", "v1.2.3", "only accepted with operation apply")]
    [InlineData("check", "", "true", "workflow_dispatch", "v1.2.3", "new_seed is only accepted")]
    [InlineData("apply", Digest, "false", "push", "v1.2.3", "not allowed from 'push'")]
    [InlineData("plan", "", "false", "pull_request_target", "v1.2.3", "not allowed")]
    [InlineData("plan", "", "false", "schedule", "v1.2.3", "not allowed")]
    [InlineData("destroy", "", "false", "workflow_dispatch", "v1.2.3", "must be check, plan or apply")]
    [InlineData("check", "", "false", "workflow_dispatch", "latest", "exact signed CLI tag")]
    [InlineData("check", "", "false", "workflow_dispatch", "server-v1.0.0", "exact signed CLI tag")]
    [InlineData("check", "", "false", "workflow_dispatch", "main", "exact signed CLI tag")]
    public void Input_validation_rejects_unsafe_requests(string operation, string approve, string newSeed, string eventName, string version, string message)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = RunStep("Validate inputs", ValidateEnv(operation, approve, newSeed, eventName, version));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(message, result.Output);
    }

    [Theory]
    [InlineData("check", "", "schedule", "v1.2.3", "binary")]
    [InlineData("plan", "", "workflow_dispatch", "v0.4.0-rc.1", "binary")]
    [InlineData("apply", Digest, "workflow_dispatch", "0123456789abcdef0123456789abcdef01234567", "source")]
    public void Input_validation_selects_exact_install_mode_and_a_protected_base(string operation, string approve, string eventName, string version, string mode)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = RunStep("Validate inputs", ValidateEnv(operation, approve, "false", eventName, version));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(mode, result.Outputs["install_mode"]);
        Assert.Equal(Path.Combine(home, "runner-temp"), result.Outputs["safe_base"]);
    }

    [Fact]
    public void Input_validation_avoids_group_writable_runner_temp()
    {
        if (OperatingSystem.IsWindows()) return;
        var environment = ValidateEnv("check", "", "false", "workflow_dispatch", "v1.2.3");
        File.SetUnixFileMode(environment["RUNNER_TEMP"], (UnixFileMode)0b111_111_111);
        var result = RunStep("Validate inputs", environment);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(environment["HOME"], result.Outputs["safe_base"]);

        File.SetUnixFileMode(environment["HOME"], (UnixFileMode)0b111_111_111);
        result = RunStep("Validate inputs", environment);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("group/world-writable", result.Output);
    }

    [Theory]
    [InlineData("plan", "", "KEY", "MINICLOUD_SEED_CONFIG")]
    [InlineData("plan", "CONFIG", "  \n", "MINICLOUD_SEED_SSH_KEY")]
    [InlineData("apply", " ", "", "MINICLOUD_SEED_CONFIG, MINICLOUD_SEED_SSH_KEY")]
    public void Empty_or_unpassed_required_secrets_fail_before_any_cli_run(string operation, string config, string key, string missing)
    {
        if (OperatingSystem.IsWindows()) return;
        var environment = PrepareEnv(operation, config == "CONFIG" ? Config() : config, key == "KEY" ? NewKey() : key);
        var result = RunStep("Write protected seed inputs", environment);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Required secret(s) are empty or were not passed: " + missing, result.Output);
    }

    [Theory]
    [InlineData("{\"schemaVersion\": 1, \"seedId\": \"x\"", "not valid JSON")]
    [InlineData("[1]", "schemaVersion 1")]
    [InlineData("{\"schemaVersion\": 2}", "schemaVersion 1")]
    public void Malformed_config_fails_without_echoing_contents(string config, string message)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = RunStep("Write protected seed inputs", PrepareEnv("check", config, ""));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(message, result.Output);
        Assert.DoesNotContain("seedId", result.Output);
    }

    [Fact]
    public void Encrypted_ssh_keys_are_rejected_for_unattended_runs()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = RunStep("Write protected seed inputs", PrepareEnv("plan", Config(), NewKey("synthetic-passphrase")));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be an unencrypted OpenSSH private key", result.Output);
    }

    [Fact]
    public void Protected_inputs_are_owner_only_normalized_and_masked()
    {
        if (OperatingSystem.IsWindows()) return;
        var key = NewKey().Replace("\n", "\r\n").TrimEnd();
        var environment = PrepareEnv("plan", Config(), key);
        environment["PROVIDER_CREDENTIAL"] = "separate-credential-value\n";
        var result = RunStep("Write protected seed inputs", environment);
        Assert.Equal(0, result.ExitCode);
        var directory = result.Outputs["dir"];
        Assert.StartsWith(environment["SAFE_BASE"], directory);
        Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(directory));
        foreach (var name in new[] { "seed-config.json", "ssh-key", "provider-credential" })
            Assert.Equal((UnixFileMode)0b110_000_000, File.GetUnixFileMode(Path.Combine(directory, name)));
        Assert.Equal(Config(), File.ReadAllText(Path.Combine(directory, "seed-config.json")));
        Assert.Equal("separate-credential-value", File.ReadAllText(Path.Combine(directory, "provider-credential")));
        Assert.DoesNotContain("\r", File.ReadAllText(Path.Combine(directory, "ssh-key")));
        Assert.EndsWith("\n", File.ReadAllText(Path.Combine(directory, "ssh-key")));
        Assert.StartsWith("SHA256:", result.Outputs["ssh_fingerprint"]);
        Assert.Contains("::add-mask::" + ProviderSecret, result.Output);
        Assert.Contains("::add-mask::separate-credential-value", result.Output);
        Assert.DoesNotContain("::add-mask::-----", result.Output);
        Assert.DoesNotContain("example.invalid", result.Output);
    }

    [Fact]
    public void Apply_passes_exact_approval_and_files_without_secret_arguments()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Prepared("apply", credential: "separate-credential-value");
        var run = RunCli("apply", directory, 10, Report("continue_partial"), newSeed: "true");
        Assert.Equal("10", run.Outputs["exit_code"]);
        var arguments = File.ReadAllLines(Path.Combine(home, "cli-args"));
        Assert.Equal(["seed", "apply", "--config", Path.Combine(directory, "seed-config.json"),
            "--report", Path.Combine(directory, "report", "seed-report.json"),
            "--ssh-key", Path.Combine(directory, "ssh-key"),
            "--credential-file", Path.Combine(directory, "provider-credential"),
            "--new-seed", "--approve", Digest], arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains(ProviderSecret) || argument.Contains("separate-credential-value"));
        Assert.Matches(@"::stop-commands::([0-9a-f]{32})[\s\S]*::add-mask::injected[\s\S]*::\1::", run.Output);
    }

    [Fact]
    public void Check_is_read_only_and_omits_unsupplied_key_and_new_seed()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Prepared("check", key: "");
        var run = RunCli("check", directory, 0, Report("current"));
        Assert.Equal("0", run.Outputs["exit_code"]);
        Assert.Equal(["seed", "check", "--config", Path.Combine(directory, "seed-config.json"),
            "--report", Path.Combine(directory, "report", "seed-report.json")], File.ReadAllLines(Path.Combine(home, "cli-args")));
    }

    [Fact]
    public void Plan_summary_stages_report_and_digest_for_review()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Prepared("plan");
        RunCli("plan", directory, 0, Report("new_seed", Digest), cliDigest: OtherDigest);
        var result = RunStep("Summarize and stage the non-secret report", ReportEnv("plan", directory, "0"));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("success", result.Outputs["result"]);
        Assert.Equal("new_seed", result.Outputs["classification"]);
        Assert.Equal(Digest, result.Outputs["approval_digest"]);
        var staged = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Outputs["path"], "seed-report.json")));
        Assert.Equal("new_seed", staged.RootElement.GetProperty("classification").GetString());
        Assert.Equal(Digest, staged.RootElement.GetProperty("approval").GetString());
        Assert.Contains("| Approval digest | `" + Digest + "` |", result.Summary);
        Assert.DoesNotContain(OtherDigest, result.Summary);
        Assert.Contains("dispatch `apply` with this digest", result.Summary);
        Assert.Contains("### Reasons", result.Summary);
        Assert.Contains("No provider resource matches seedId", result.Summary);
        Assert.Contains("Add target.hostFingerprint from the provider console", result.Summary);
    }

    [Fact]
    public void Plan_digest_falls_back_to_cli_output()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Prepared("plan");
        RunCli("plan", directory, 0, Report("new_seed"));
        var result = RunStep("Summarize and stage the non-secret report", ReportEnv("plan", directory, "0"));
        Assert.Equal(Digest, result.Outputs["approval_digest"]);
    }

    [Theory]
    [InlineData("10", "handoff_pending", 0)]
    [InlineData("11", "upgrade_required", 1)]
    [InlineData("12", "reconciliation_required", 1)]
    [InlineData("3", "error", 1)]
    public void Exit_codes_map_to_summary_results_and_job_status(string code, string expected, int jobExit)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Prepared("apply");
        RunCli("apply", directory, int.Parse(code), Report("current"));
        var result = RunStep("Summarize and stage the non-secret report", ReportEnv("apply", directory, code));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.Outputs["result"]);
        Assert.Contains("| Result | `" + expected + "` |", result.Summary);
        if (expected == "upgrade_required") Assert.Contains("never upgrades", result.Summary);
        var outcome = RunStep("Report seed outcome", new Dictionary<string, string> { ["EXIT_CODE"] = code });
        Assert.Equal(jobExit, outcome.ExitCode);
    }

    [Fact]
    public void Reports_containing_supplied_secrets_are_not_staged_and_inputs_are_removed()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Prepared("plan");
        var keyLine = File.ReadAllLines(Path.Combine(directory, "ssh-key"))[1];
        RunCli("plan", directory, 0, Report("new_seed", Digest, note: keyLine));
        var result = RunStep("Summarize and stage the non-secret report", ReportEnv("plan", directory, "0"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Outputs.ContainsKey("path"));
        Assert.DoesNotContain(keyLine, result.Summary);
        Assert.Contains("was not uploaded", result.Summary);

        var cleanup = RunStep("Remove protected inputs", new Dictionary<string, string> { ["SEED_DIR"] = directory, ["CLI_DIR"] = "" });
        Assert.Equal(0, cleanup.ExitCode);
        Assert.False(Directory.Exists(directory));
    }

    private string Prepared(string operation, string key = "KEY", string credential = "")
    {
        var environment = PrepareEnv(operation, Config(), key == "KEY" ? NewKey() : key);
        environment["PROVIDER_CREDENTIAL"] = credential;
        var result = RunStep("Write protected seed inputs", environment);
        Assert.True(result.ExitCode == 0, result.Output);
        return result.Outputs["dir"];
    }

    private StepResult RunCli(string operation, string directory, int exitCode, string report, string newSeed = "false", string cliDigest = Digest)
    {
        var bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(bin);
        var fake = Path.Combine(bin, "minicloud");
        File.WriteAllText(fake, """
            #!/usr/bin/env bash
            printf '%s\n' "$@" > "$FAKE_ARGS"
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == --report ]]; then printf '%s' "$FAKE_REPORT" > "$2"; fi
              shift
            done
            echo "::add-mask::injected"
            echo "Approval digest: $FAKE_DIGEST"
            exit "$FAKE_EXIT"
            """);
        File.SetUnixFileMode(fake, (UnixFileMode)0b111_000_000);
        var result = RunStep("Run minicloud seed", new Dictionary<string, string>
        {
            ["OPERATION"] = operation, ["APPROVE"] = operation == "apply" ? Digest : "", ["NEW_SEED"] = newSeed,
            ["SEED_DIR"] = directory, ["FAKE_ARGS"] = Path.Combine(home, "cli-args"), ["FAKE_REPORT"] = report,
            ["FAKE_DIGEST"] = cliDigest, ["FAKE_EXIT"] = exitCode.ToString(),
        }, bin);
        Assert.True(result.ExitCode == 0, result.Output);
        return result;
    }

    // Mirrors the camelCase serialization of the CLI's SeedReport record.
    private static string Report(string classification, string? approval = null, string note = "Non-secret snapshot.") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["kind"] = "minicloud-seed-report",
            ["generatedAt"] = "2026-11-12T00:00:00+00:00",
            ["command"] = "plan",
            ["seedId"] = "0123456789abcdef0123456789abcdef",
            ["provider"] = "hetzner",
            ["region"] = "nbg1",
            ["size"] = "cx32",
            ["release"] = new { requested = "server-v0.0.1", installed = (string?)null, sourceRevision = (string?)null, digest = (string?)null },
            ["classification"] = classification,
            ["reasons"] = new[] { "No provider resource matches seedId." },
            ["observed"] = new
            {
                account = "synthetic", serverId = (string?)null, ipv4 = (string?)null, hostVerification = "pending",
                hostFingerprint = (string?)null, sshUser = "root", rootKeyFingerprint = "SHA256:synthetic", checkpoint = (string?)null,
                installerPhase = (string?)null, installationId = (string?)null, maintenancePending = Array.Empty<string>(), https = (string?)null,
            },
            ["domains"] = new { dashboard = "cloud.example.invalid", api = "api.cloud.example.invalid", app = "apps.example.invalid" },
            ["dnsRecords"] = Array.Empty<object>(),
            ["approval"] = approval,
            ["costs"] = Array.Empty<object>(),
            ["nextActions"] = new[] { "Add target.hostFingerprint from the provider console", "Rerun apply" },
            ["note"] = note,
        });

    [Theory]
    [InlineData(2, "minicloud-seed-report")]
    [InlineData(1, "other-report")]
    public void Unsupported_report_schema_is_not_trusted_or_staged(int schemaVersion, string kind)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Prepared("plan");
        var report = JsonNode.Parse(Report("new_seed", Digest))!.AsObject();
        report["schemaVersion"] = schemaVersion;
        report["kind"] = kind;
        RunCli("plan", directory, 0, report.ToJsonString(), cliDigest: OtherDigest);
        var result = RunStep("Summarize and stage the non-secret report", ReportEnv("plan", directory, "0"));
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Outputs.ContainsKey("path"));
        Assert.Equal("", result.Outputs["classification"]);
        Assert.Equal(OtherDigest, result.Outputs["approval_digest"]);
        Assert.Contains("not a supported minicloud-seed-report", result.Summary);
    }

    private Dictionary<string, string> ValidateEnv(string operation, string approve, string newSeed, string eventName, string version)
    {
        var temp = Path.Combine(home, "runner-temp");
        var user = Path.Combine(home, "runner-home");
        foreach (var path in new[] { temp, user })
        {
            Directory.CreateDirectory(path);
            File.SetUnixFileMode(path, (UnixFileMode)0b111_000_000);
        }
        return new Dictionary<string, string>
        {
            ["OPERATION"] = operation, ["ENVIRONMENT_NAME"] = "minicloud-seed", ["MINICLOUD_VERSION"] = version,
            ["APPROVE"] = approve, ["NEW_SEED"] = newSeed, ["REPORT_ARTIFACT_NAME"] = "minicloud-seed-report",
            ["EVENT_NAME"] = eventName, ["RUNNER_TEMP"] = temp, ["HOME"] = user,
        };
    }

    private Dictionary<string, string> PrepareEnv(string operation, string config, string key) => new()
    {
        ["OPERATION"] = operation, ["SAFE_BASE"] = home, ["SEED_CONFIG"] = config, ["SSH_PRIVATE_KEY"] = key, ["PROVIDER_CREDENTIAL"] = "",
    };

    private Dictionary<string, string> ReportEnv(string operation, string directory, string exitCode) => new()
    {
        ["OPERATION"] = operation, ["ENVIRONMENT_NAME"] = "minicloud-seed", ["MINICLOUD_VERSION"] = "v1.2.3", ["NEW_SEED"] = "false",
        ["REPORT_ARTIFACT_NAME"] = "minicloud-seed-report", ["EXIT_CODE"] = exitCode, ["SSH_FINGERPRINT"] = "SHA256:synthetic",
        ["SEED_DIR"] = directory, ["RUNNER_TEMP"] = home, ["GITHUB_RUN_ID"] = "1", ["GITHUB_RUN_ATTEMPT"] = "1",
        ["GITHUB_JOB"] = Guid.NewGuid().ToString("N"),
    };

    private static string Config() => $$"""
        {"schemaVersion": 1, "seedId": "0123456789abcdef0123456789abcdef", "provider": "hetzner", "region": "nbg1", "size": "cx32",
         "domain": "cloud.example.invalid", "appDomain": "apps.example.invalid", "serverVersion": "server-v0.0.1",
         "providerCredential": "{{ProviderSecret}}"}
        """;

    private string NewKey(string passphrase = "")
    {
        var path = Path.Combine(home, "key-" + Guid.NewGuid().ToString("N"));
        var result = Execute("ssh-keygen", ["-q", "-t", "ed25519", "-N", passphrase, "-C", "synthetic", "-f", path], new Dictionary<string, string>());
        Assert.True(result.ExitCode == 0, result.Output);
        return File.ReadAllText(path);
    }

    private StepResult RunStep(string name, Dictionary<string, string> environment, string? extraPath = null)
    {
        var script = Path.Combine(home, "step-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(script, Text(Step(name)["run"]));
        var output = Path.Combine(home, "output-" + Guid.NewGuid().ToString("N"));
        var summary = Path.Combine(home, "summary-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(output, "");
        File.WriteAllText(summary, "");
        var variables = new Dictionary<string, string>(environment)
        {
            ["GITHUB_OUTPUT"] = output, ["GITHUB_STEP_SUMMARY"] = summary,
            ["PATH"] = (extraPath is null ? "" : extraPath + ":") + Environment.GetEnvironmentVariable("PATH"),
        };
        variables.TryAdd("HOME", home);
        var result = Execute("bash", ["--noprofile", "--norc", "-eo", "pipefail", script], variables);
        var outputs = File.ReadAllLines(output).Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
        return new StepResult(result.ExitCode, result.Output, outputs, File.ReadAllText(summary));
    }

    private static (int ExitCode, string Output) Execute(string file, string[] arguments, Dictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private sealed record StepResult(int ExitCode, string Output, Dictionary<string, string> Outputs, string Summary);

    private static string WorkflowPath => Path.Combine(Root, ".github", "workflows", "seed.yml");
    private static YamlMappingNode Job => Map(Map(Workflow, "jobs"), "seed");
    private static IEnumerable<YamlMappingNode> Steps => ((YamlSequenceNode)Job["steps"]).Children.Cast<YamlMappingNode>();
    private static YamlMappingNode Step(string name) => Steps.Single(step => Name(step) == name);
    private static string Name(YamlMappingNode step) => step.Children.TryGetValue(new YamlScalarNode("name"), out var name) ? Text(name) : "";
    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node[key];
    private static string Text(YamlNode node) => ((YamlScalarNode)node).Value ?? "";

    private static string Yaml(YamlNode node)
    {
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter();
        stream.Save(writer, false);
        return writer.ToString();
    }

    private static YamlMappingNode Load()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(WorkflowPath)));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Minicloud.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
