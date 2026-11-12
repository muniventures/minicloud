// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Config;
using Minicloud.Cli.Seed;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private static readonly HashSet<string> SeedFlags = ["--setup-token", "--rotate-setup-token", "--register-provider", "--use-existing-auth",
        "--ssh-key-passphrase", "--no-ssh-key-passphrase", "--no-operator-ssh-key"];
    private static readonly HashSet<string> SeedValues = ["--provider", "--region", "--size", "--domain", "--app-domain", "--server-version",
        "--credential-file", "--approve", "--confirm-console-fingerprint", "--confirm-cleanup", "--recovery-dir", "--ack-recovery",
        "--instance-name", "--organization", "--provider-name", "--confirm-provider-import", "--confirm-provider-account",
        "--ssh-public-key", "--generate-ssh-key"];

    private async Task<int> RunSeedAsync(string[] args, CancellationToken ct)
    {
        string? operation = null;
        try
        {
            var action = args.FirstOrDefault() is "status" or "resume" or "cleanup" or "export" or "register-provider" ? args[0] : "plan";
            var offset = action == "plan" ? 0 : 2;
            if (action != "plan") operation = args.ElementAtOrDefault(1);
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var publicKeyPaths = new List<string>();
            for (var i = offset; i < args.Length; i++)
            {
                var name = args[i];
                SeedGuard.Require(SeedFlags.Contains(name) || SeedValues.Contains(name),
                    "Unknown seed option. Credentials are accepted only by hidden prompt or --credential-file; see minicloud help seed.");
                var value = "true";
                if (!SeedFlags.Contains(name))
                {
                    SeedGuard.Require(i + 1 < args.Length && !args[i + 1].StartsWith("--"), "Seed option requires a value.");
                    value = args[++i];
                }
                if (name == "--ssh-public-key")
                {
                    publicKeyPaths.Add(value);
                    SeedGuard.Require(publicKeyPaths.Count <= 16, "At most 16 operator SSH keys are supported.");
                    options[name] = value;
                }
                else SeedGuard.Require(options.TryAdd(name, value), "Duplicate seed options are not allowed.");
            }
            HashSet<string> allowed = action switch
            {
                "plan" => ["--provider", "--region", "--size", "--domain", "--app-domain", "--server-version", "--credential-file",
                    "--ssh-public-key", "--generate-ssh-key", "--ssh-key-passphrase", "--no-ssh-key-passphrase", "--no-operator-ssh-key"],
                "resume" => ["--approve", "--credential-file", "--confirm-console-fingerprint", "--instance-name"],
                "cleanup" => ["--credential-file", "--confirm-cleanup"],
                "export" => ["--recovery-dir", "--setup-token", "--rotate-setup-token", "--ack-recovery"],
                "register-provider" => ["--register-provider", "--organization", "--provider-name", "--use-existing-auth", "--credential-file", "--confirm-provider-import", "--confirm-provider-account"],
                _ => []
            };
            SeedGuard.Require(options.Keys.All(allowed.Contains), "Option is not valid for this seed action. Existing operation selections are immutable; see help seed.");
            string? Option(string name) => options.GetValueOrDefault(name);
            if (action == "plan")
            {
                var selection = new SeedSelection(Option("--provider") ?? "", Option("--region") ?? "", Option("--size") ?? "",
                    Option("--domain") ?? "", Option("--app-domain") ?? "", Option("--server-version") ?? "");
                selection.Validate();
                SeedGuard.Require(Option("--approve") is null, "First create a read-only plan, then approve its exact digest with seed resume.");
                SeedGuard.Require(!(options.ContainsKey("--ssh-key-passphrase") && options.ContainsKey("--no-ssh-key-passphrase")),
                    "Choose either protected or unprotected operator key generation, not both.");
                operation = Guid.NewGuid().ToString("N");
                using var journal = new SeedJournal(_environment.ConfigHome, operation, true);
                _console.WriteLine("Local seed operation: " + operation);
                var release = await new SeedRelease().VerifyAsync(selection.ServerVersion, Path.Combine(journal.DirectoryPath, "release"), ct);
                await new SeedHost().CheckPrerequisitesAsync(ct);
                var credential = SeedFiles.Credential(Option("--credential-file"), _console);
                using var provider = new SeedProvider(selection.Provider, credential);
                var quote = await provider.QuoteAsync(selection, operation, ct);
                var operatorKeys = await SeedOperatorKeys.SelectAsync(publicKeyPaths, Option("--generate-ssh-key"),
                    options.ContainsKey("--ssh-key-passphrase") ? true : options.ContainsKey("--no-ssh-key-passphrase") ? false : null,
                    options.ContainsKey("--no-operator-ssh-key"), _console, ct);
                var state = new SeedState { Id = operation, Plan = new(selection, quote, release.Digest, release.Source)
                    { OperatorPublicKeys = operatorKeys } };
                journal.Save(state);
                _console.WriteLine(JsonSerializer.Serialize(state.Plan, SeedJournal.Json));
                _console.WriteLine("Approval digest: " + state.Plan.Approval);
                _console.WriteLine($"No provider mutations. Review all costs/restrictions, then: minicloud seed resume {operation} --approve {state.Plan.Approval} --credential-file <protected-path>");
                return CliExitCodes.Success;
            }
            SeedGuard.Require(operation is not null, "Supply the recorded operation ID.");
            using (var journal = new SeedJournal(_environment.ConfigHome, operation!))
            {
                var state = journal.Load();
                if (action == "status")
                {
                    SeedGuard.Require(options.Count == 0, "seed status accepts only the operation ID and never reads credentials or contacts providers.");
                    _console.WriteLine(JsonSerializer.Serialize(state, SeedJournal.Json));
                    _console.WriteLine("Local journal only, not a live health check. Retained resources may remain billable.");
                    return CliExitCodes.Success;
                }
                var host = new SeedHost();
                if (action == "export")
                {
                    SeedGuard.Require(state.HostFingerprint is not null && state.InstallStarted, "Only an installation with pinned SSH identity can export recovery.");
                    await host.TrustAsync(state, journal.DirectoryPath, null, ct);
                    var destination = Option("--recovery-dir") ?? state.RecoveryDirectory;
                    SeedGuard.Require(destination is not null, "Select a new protected workstation directory using --recovery-dir.");
                    if (state.RecoveryDirectory is null)
                    {
                        SeedGuard.Require(!Directory.Exists(destination) && !File.Exists(destination), "Recovery export requires a new directory.");
                        state.RecoveryDirectory = Path.GetFullPath(destination!);
                        journal.Save(state);
                    }
                    SeedGuard.Require(state.PendingTokenOutput is null || Option("--rotate-setup-token") is not null,
                        "A setup-token rotation is pending. Preserve its output name and resume export with --setup-token --rotate-setup-token.");
                    if (Option("--rotate-setup-token") is not null)
                    {
                        SeedGuard.Require(Option("--setup-token") is not null, "Explicit --setup-token is also required for rotation/export.");
                        state.PendingTokenOutput ??= "seed-token-" + Guid.NewGuid().ToString("N");
                        journal.Save(state);
                        var rotationRelease = await new SeedRelease().VerifyAsync(state.Plan.Selection.ServerVersion, Path.Combine(journal.DirectoryPath, "release"), ct);
                        SeedGuard.Require(rotationRelease.Digest == state.Plan.ReleaseDigest, "Release identity changed; no token rotation allowed.");
                        await host.RotateTokenAsync(state, journal.DirectoryPath, state.PendingTokenOutput, ct);
                    }
                    await host.ExportAsync(state, journal.DirectoryPath, destination!, Option("--setup-token") is not null, ct);
                    state.PendingTokenOutput = null;
                    if (Option("--ack-recovery") is { } acknowledgement)
                    {
                        SeedGuard.Require(acknowledgement == state.InstallationId, "Acknowledge independent recoverability with the exact installation ID.");
                        state.RecoveryAcknowledged = true;
                    }
                    journal.Save(state);
                    _console.WriteLine("Protected recovery export: " + state.RecoveryDirectory);
                    if (Option("--setup-token") is not null) _console.WriteLine("Protected setup token file: " + state.SetupTokenExportPath + ". Enter it manually at the HTTPS /setup URL; no token was printed.");
                    _console.WriteLine($"Verify independent custody/recoverability, then repeat export with --ack-recovery {state.InstallationId}; resume after browser setup.");
                    return CliExitCodes.Success;
                }
                if (action == "register-provider")
                    return await RegisterSeedProviderAsync(state, journal, options, ct);
                if (action == "cleanup")
                {
                    var credential = SeedFiles.Credential(Option("--credential-file"), _console);
                    using var provider = new SeedProvider(state.Plan.Selection.Provider, credential);
                    await new SeedLifecycle(provider, host, journal.Save, _console.WriteLine).CleanupAsync(state, journal.DirectoryPath, Option("--confirm-cleanup"), ct);
                    return CliExitCodes.Success;
                }
                var release = await new SeedRelease().VerifyAsync(state.Plan.Selection.ServerVersion, Path.Combine(journal.DirectoryPath, "release"), ct);
                SeedGuard.Require(release.Digest == state.Plan.ReleaseDigest && release.Source == state.Plan.ReleaseSource, "Exact release changed; do not approve or resume another release in this operation.");
                var secret = SeedFiles.Credential(Option("--credential-file"), _console);
                using (var provider = new SeedProvider(state.Plan.Selection.Provider, secret))
                    await new SeedLifecycle(provider, host, journal.Save, _console.WriteLine).ResumeAsync(state, journal.DirectoryPath,
                        Option("--approve"), Option("--confirm-console-fingerprint"), ct);
                if (Option("--instance-name") is { } name)
                    await SaveSeedInstanceAsync(state, journal, name, host, ct);
                return state.Phase == "complete" ? CliExitCodes.Success : CliExitCodes.ValidationError;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _console.WriteError($"Seed interrupted. Operation {operation ?? "(not yet planned)"} and all provider resources are retained; billing continues. Use status/resume; never delete the lock.");
            return CliExitCodes.ValidationError;
        }
        catch (ApiException)
        {
            _console.WriteError("Optional provider registration unavailable. Healthy control plane retained; verify normal authentication, workspace permissions and read-only account reconciliation. No API response body logged.");
            return CliExitCodes.AuthError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException or HttpRequestException or TaskCanceledException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _console.WriteError($"Seed could not establish a safe result. Operation {operation ?? "(none)"} retained. Check protected inputs, tools, exact release, DNS/TLS and host state; no automatic create retry or rollback. Secret-bearing diagnostics suppressed.");
            return CliExitCodes.ValidationError;
        }
        catch (CliCommandException ex)
        {
            _console.WriteError(ex.Message);
            if (operation is not null) _console.WriteError($"Operation {operation} retained. Billable resources, if created, are not rolled back; inspect seed status.");
            return ex.ExitCode;
        }
    }

    private async Task SaveSeedInstanceAsync(SeedState state, SeedJournal journal, string name, ISeedHost host, CancellationToken ct)
    {
        SeedGuard.Require(state.InstallerState is "ready" or "awaiting_operator", "Only a verified HTTPS installation may be saved.");
        SeedGuard.Require(!string.IsNullOrWhiteSpace(name) && !name.StartsWith('-') && name.Length <= 100 && !name.Any(char.IsControl), "Choose a nonempty instance name.");
        await host.VerifyHttpsAsync(state, ct);
        var profile = new InstanceProfile(name, "https://" + state.Plan.Selection.Domain, state.Plan.Selection.AppDomain);
        _environment.Instances.AddWithoutSelecting(profile);
        state.InstanceName = name; journal.Save(state);
        _console.WriteLine($"Saved instance {name} without changing active selection/authentication. Select it explicitly: minicloud instance use {name}");
    }

    private async Task<int> RegisterSeedProviderAsync(SeedState state, SeedJournal journal, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        SeedGuard.Require(state.Phase == "complete", "Complete verified browser setup and independent recovery handoff first.");
        SeedGuard.Require(options.ContainsKey("--register-provider"), "Provider registration is optional and defaults to no import. Explicitly supply --register-provider to opt in.");
        SeedGuard.Require(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CliEnvironment.TokenEnvironmentVariable)),
            "Unset MINICLOUD_TOKEN. Seed never silently reuses an environment token; select normal instance-scoped authentication explicitly.");
        await new SeedHost().VerifyHttpsAsync(state, ct);
        var environment = CliEnvironment.ForInstance(new(state.InstanceName ?? state.Id, "https://" + state.Plan.Selection.Domain, state.Plan.Selection.AppDomain), _environment.ConfigHome);
        var store = new TokenStore(environment);
        SeedFiles.Directory(Path.GetDirectoryName(environment.TokenFilePath)!);
        if (File.Exists(environment.TokenFilePath)) SeedFiles.Check(environment.TokenFilePath);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new MinicloudApiClient(environment, store, http);
        if (!options.ContainsKey("--use-existing-auth"))
        {
            var app = new CliApplication(_console, environment, store, client);
            var result = await app.RunAsync(["login", "--no-browser"], ct);
            if (result != CliExitCodes.Success) return result;
        }
        var organization = options.GetValueOrDefault("--organization");
        var name = options.GetValueOrDefault("--provider-name");
        SeedGuard.Require(!string.IsNullOrWhiteSpace(organization) && !string.IsNullOrWhiteSpace(name), "Explicit --organization and --provider-name are required.");
        SeedGuard.Require(state.ImportOrganization is null || state.ImportOrganization == organization && state.ImportName == name,
            "Resume provider registration in its originally selected workspace/name. No duplicate or credential replacement.");
        var me = await client.GetMeAsync(ct);
        SeedGuard.Require(me.Organizations.Any(x => x.Id == organization), "Selected workspace is not available to the authenticated actor.");
        var permissions = await client.GetProviderPermissionsAsync(organization!, ct);
        if (!permissions.CanRead || !permissions.CanManage)
        {
            _console.WriteLine("Optional registration pending: normal browser-consent tokens do not include providers:read/write. Use the existing dashboard Provider accounts flow, or an explicitly selected operator API key with both scopes for this workspace. No scopes were broadened or credential sent.");
            return CliExitCodes.AuthError;
        }
        var accounts = await client.GetProvidersAsync(organization!, ct);
        var matches = accounts.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        SeedGuard.Require(matches.Length <= 1, "Conflicting provider accounts; reconcile in dashboard.");
        var type = state.Plan.Selection.Provider == "digitalocean" ? "digital-ocean" : state.Plan.Selection.Provider;
        if (state.ProviderAccountId is not null || matches.Length == 1)
        {
            var existing = state.ProviderAccountId is not null ? accounts.SingleOrDefault(x => x.Id == state.ProviderAccountId) : matches.Single();
            SeedGuard.Require(existing is not null && existing.OrganizationId == organization && existing.Name == name && existing.ProviderType == type,
                "Provider account name/ID/workspace/type conflict. No credential replacement is allowed.");
            SeedGuard.Require(state.ProviderAccountId == existing!.Id || options.GetValueOrDefault("--confirm-provider-account") == existing.Id,
                "Existing or ambiguous imported account requires independent verification and --confirm-provider-account <exact ID>; no duplicate or replacement will be created.");
            state.ProviderAccountId = existing.Id; journal.Save(state);
            _console.WriteLine("Existing provider account confirmed: " + existing.Id + ". No credential replaced, VPS adopted, plan or app created.");
            return CliExitCodes.Success;
        }
        SeedGuard.Require(!state.ImportStarted, "Provider create outcome uncertain; reconcile with authenticated dashboard/support before any retry.");
        var credential = SeedFiles.Credential(options.GetValueOrDefault("--credential-file"), _console);
        var digest = SeedGuard.Hash(JsonSerializer.Serialize(new { api = environment.ApiBaseUrl, organization, name, type, scope = SeedGuard.Scope(credential, state.Id) }));
        _console.WriteLine($"Optional registration grants {environment.ApiBaseUrl} authority to provision BILLABLE app resources in workspace {organization}, provider {type}, name {name}. A different scoped key may be supplied. Approval: {digest}");
        if (options.GetValueOrDefault("--confirm-provider-import") != digest) { _console.WriteLine("No credential sent. Repeat with --confirm-provider-import <exact digest> only after reviewing permissions."); return CliExitCodes.Success; }
        state.ImportStarted = true; state.ImportOrganization = organization; state.ImportName = name; journal.Save(state);
        try
        {
            var account = await client.CreateProviderAsync(new(organization!, name!, type, credential), ct);
            state.ProviderAccountId = account.Id; journal.Save(state);
            _console.WriteLine("Provider registered: " + account.Id + ". No app resources created or control-plane VPS adopted.");
            return CliExitCodes.Success;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            _console.WriteError("Optional provider registration pending/uncertain. Healthy control plane retained; reconcile by exact workspace/name/ID before retry. Error body suppressed.");
            return CliExitCodes.ValidationError;
        }
    }

    private void PrintSeedHelp()
    {
        _console.WriteLine("Standalone seed (no instance/login): --provider hetzner|digitalocean|vultr --region ID --size ID --domain HOST --app-domain SUFFIX --server-version server-vX.Y.Z [--credential-file PATH]");
        _console.WriteLine("Operator access: [--ssh-public-key PUBLIC_FILE ...] [--generate-ssh-key NEW_PRIVATE_PATH [--ssh-key-passphrase | --no-ssh-key-passphrase]] or --no-operator-ssh-key.");
        _console.WriteLine("Interactive planning offers public-key input or generation and asks about passphrase protection (default yes). Only public keys reach the VPS; the bootstrap identity stays separate.");
        _console.WriteLine("minicloud seed status OP | resume OP [--approve DIGEST] [--confirm-console-fingerprint SHA256:...] [--instance-name NAME] [--credential-file PATH]");
        _console.WriteLine("minicloud seed cleanup OP [--confirm-cleanup DIGEST] [--credential-file PATH] (forbidden after installation starts)");
        _console.WriteLine("minicloud seed export OP --recovery-dir NEW_LOCAL_PATH [--setup-token [--rotate-setup-token]] [--ack-recovery INSTALLATION_ID]");
        _console.WriteLine("minicloud seed register-provider OP --register-provider --organization ID --provider-name NAME [--use-existing-auth] [--credential-file PATH] [--confirm-provider-import DIGEST | --confirm-provider-account ID]");
        _console.WriteLine("Exact signed published server required; current candidate unpublished/unqualified. Public HTTPS and independent console SSH fingerprint required. IP-only access deferred. See docs/seed.md.");
    }
}
