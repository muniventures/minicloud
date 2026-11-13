// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Minicloud.Cli.Seed;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunPortableSeedAsync(string[] args, CancellationToken ct)
    {
        if (args is ["generate-id"] or ["new-id"])
        {
            _console.WriteLine(Guid.NewGuid().ToString("N"));
            return CliExitCodes.Success;
        }
        SeedPortableReport? report = null;
        SeedState? state = null;
        string? reportPath = null, scratch = null, credential = null, embeddedCredential = null;
        try
        {
            var action = args.FirstOrDefault();
            SeedGuard.Require(action is "plan" or "apply" or "check" or "export",
                "Use seed plan|apply|check|export --config <protected.json> --report <report.json>.");
            HashSet<string> flags = ["--new-seed", "--use-ssh-agent", "--setup-token", "--ssh-key-passphrase", "--no-ssh-key-passphrase"];
            HashSet<string> values = ["--config", "--report", "--ssh-key", "--credential-file", "--approve", "--recovery-dir", "--generate-ssh-key"];
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                var name = args[i];
                SeedGuard.Require(flags.Contains(name) || values.Contains(name), "Unknown stateless seed option. See help seed.");
                var value = "true";
                if (values.Contains(name))
                {
                    SeedGuard.Require(i + 1 < args.Length && !args[i + 1].StartsWith("--"), "Seed option requires a value.");
                    value = args[++i];
                }
                SeedGuard.Require(options.TryAdd(name, value), "Duplicate seed option.");
            }
            string? Option(string name) => options.GetValueOrDefault(name);
            SeedGuard.Require(Option("--config") is not null && Option("--report") is not null, "--config and --report are required.");
            var configPath = Path.GetFullPath(Option("--config")!);
            SeedFiles.Check(configPath);
            SeedGuard.Require(new FileInfo(configPath).Length is > 0 and < 1048576, "Invalid configuration size.");
            var config = SeedPortableConfig.Parse(await File.ReadAllTextAsync(configPath, ct));
            credential = config.ProviderCredential;
            embeddedCredential = config.ProviderCredential;
            reportPath = Path.GetFullPath(Option("--report")!);
            SeedGuard.Require(reportPath != configPath && reportPath != Path.GetFullPath(Option("--ssh-key") ?? configPath)
                && reportPath != Path.GetFullPath(Option("--credential-file") ?? configPath), "Report must be separate from protected inputs.");
            if (Option("--generate-ssh-key") is { } generatedPath)
            {
                var fullKeyPath = Path.GetFullPath(generatedPath);
                SeedGuard.Require(reportPath != fullKeyPath && reportPath != fullKeyPath + ".pub",
                    "Report must never replace a generated private or public key.");
            }
            if (Option("--ssh-key") is { } suppliedPath)
                SeedGuard.Require(reportPath != Path.GetFullPath(suppliedPath) + ".pub",
                    "Report must never replace a user-owned public key.");
            SeedFiles.Check(Path.GetDirectoryName(reportPath)!, true);
            CheckPortableReportDestination(reportPath, config.SeedId);
            report = new() { SeedId = config.SeedId, Domain = config.Selection.Domain, AppDomain = config.Selection.AppDomain,
                RequestedRelease = config.Selection.ServerVersion, Provider = config.Provider, ProviderAccount = config.ProviderAccount,
                Region = config.Region, Size = config.Size, Architecture = config.Architecture };
            state = config.State();
            SeedGuard.Require(action is "plan" or "apply" || !options.ContainsKey("--new-seed"), "--new-seed is valid only with plan/apply.");
            SeedGuard.Require(action == "apply" || Option("--approve") is null, "--approve is valid only with apply.");
            SeedGuard.Require(action == "export" || Option("--recovery-dir") is null && !options.ContainsKey("--setup-token"),
                "Private handoff options require export.");
            SeedGuard.Require(!(options.ContainsKey("--ssh-key-passphrase") && options.ContainsKey("--no-ssh-key-passphrase")),
                "Choose one passphrase protection option.");
            var keyPath = Option("--ssh-key");
            if (Option("--generate-ssh-key") is { } generate)
            {
                SeedGuard.Require(action == "plan" && keyPath is null, "Generate a user-owned key explicitly during plan, before resource creation.");
                await SeedOperatorKeys.SelectAsync([], generate,
                    options.ContainsKey("--ssh-key-passphrase") ? true : options.ContainsKey("--no-ssh-key-passphrase") ? false : null,
                    false, _console, ct);
                keyPath = generate;
            }
            else SeedGuard.Require(!options.ContainsKey("--ssh-key-passphrase") && !options.ContainsKey("--no-ssh-key-passphrase"),
                "Passphrase protection options require --generate-ssh-key.");
            SeedGuard.Require(keyPath is not null || action == "check", "Supply the separate user-owned --ssh-key (encrypted keys need explicit --use-ssh-agent).");
            if (keyPath is not null) keyPath = Path.GetFullPath(keyPath);
            SeedGuard.Require(keyPath is null || reportPath != keyPath && reportPath != keyPath + ".pub",
                "Report must never replace a user-owned private or public key.");
            var useAgent = options.ContainsKey("--use-ssh-agent");
            SeedGuard.Require(!useAgent || keyPath is not null, "--use-ssh-agent requires the explicit user-owned --ssh-key.");
            var publicKey = keyPath is null ? "" : await SeedUserKey.ReadAsync(keyPath, useAgent, ct);
            if (keyPath is not null)
                report.RootKeyFingerprint = "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                    Convert.FromBase64String(publicKey.Split(' ')[1]))).TrimEnd('=');
            scratch = Path.Combine(Path.GetDirectoryName(reportPath)!, ".seed-session-" + Guid.NewGuid().ToString("N"));
            SeedFiles.Directory(scratch);
            var host = new SeedHost(keyPath, useAgent);
            credential = Option("--credential-file") is { } credentialPath ? SeedFiles.Credential(credentialPath, _console) : config.ProviderCredential;
            using var provider = string.IsNullOrEmpty(credential) ? null : new SeedProvider(config.Selection.Provider, credential);
            if (provider is not null)
            {
                var account = await provider.GetAccountIdentityAsync(ct);
                SeedGuard.Require(config.Selection.Provider == "hetzner"
                    ? config.ProviderAccount.StartsWith("hetzner-project:", StringComparison.Ordinal)
                    : account == config.ProviderAccount, "Authorized provider account differs from explicit providerAccount scope.");
                var found = await provider.FindServerAsync(state.Id, ct);
                if (found is not null)
                {
                    if (state.Server is null || string.IsNullOrEmpty(state.Server.Image))
                    {
                        if (state.Server is not null) state.Server = state.Server with { Image = found.Image };
                        state.Plan = state.Plan with { Quote = state.Plan.Quote with { Image = found.Image } };
                    }
                    SeedLifecycle.Match(state, found);
                    state.Server = found;
                }
                else SeedGuard.Require(state.Server is null, "Explicit resource is absent from authorized provider inventory. Reconcile; never replace it.");
            }
            var observation = state.Server is null ? new SeedObservation("absent", "unknown")
                : keyPath is null ? await SeedHost.InspectPublicTargetAsync(state, ct)
                : await ObservePortableHostAsync(host, state, scratch, ct);
            SeedPortable.Observe(report, state, observation);
            if (keyPath is null)
            {
                report.VerifiedHostFingerprint = null;
                report.Reasons = ["Public HTTPS observation only. Protected host identity, preparation and OpenBao state remain unknown; no repair attempted."];
            }
            if (observation.State == "awaiting_trust")
            {
                report.VerifiedHostFingerprint = null;
                report.NextActions = ["Retain the explicit server ID/IPv4, obtain its host fingerprint independently through provider console, and add target.hostFingerprint before replanning. Never repeat initial creation."];
                return 10;
            }
            if (state.Server is null && (!options.ContainsKey("--new-seed") || provider is null
                || config.InitialCreationIntent != SeedPortableConfig.CreationIntent))
            {
                report.Classification = "reconciliation_required";
                report.Result = "unknown";
                report.NextActions = ["Supply the explicit existing target and independently verified host fingerprint. Missing reports/inventory never authorize creation."];
                return SeedPortable.ExitCode(report);
            }
            if (report.Classification == "reconciliation_required") return SeedPortable.ExitCode(report);
            if (action == "check") return SeedPortable.ExitCode(report);
            if (action == "export")
            {
                SeedGuard.Require(observation.State == "installed" && observation.Release == config.Selection.ServerVersion,
                    "Export requires this exact installed target; inspect installed identity/release first.");
                state.Plan = state.Plan with { ReleaseSource = observation.Source! };
                var destination = Option("--recovery-dir");
                var reportDirectory = Path.GetDirectoryName(reportPath)! + Path.DirectorySeparatorChar;
                var recoveryDirectory = destination is null ? "" : Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                SeedGuard.Require(destination is not null && !recoveryDirectory.StartsWith(reportDirectory, StringComparison.Ordinal)
                    && !reportDirectory.StartsWith(recoveryDirectory, StringComparison.Ordinal),
                    "Select a separate protected --recovery-dir, never the public report directory.");
                await host.ExportAsync(state, scratch, destination!, options.ContainsKey("--setup-token"), ct);
                report.NextActions = ["Private recovery files exported separately. Verify independent encrypted custody; enter any setup token manually at the verified HTTPS setup origin."];
                return CliExitCodes.Success;
            }
            // Observation precedes release comparison/verification. Never run the installer to discover installed identity.
            var release = await new SeedRelease().VerifyAsync(config.Selection.ServerVersion, Path.Combine(scratch, "release"), ct);
            SeedGuard.Require(observation.State is not ("empty" or "preparing") || observation.Prepared
                || !string.IsNullOrEmpty(state.Plan.Quote.Image),
                "For initial host-only preparation include target.image from the creation report/provider console, or supply authorized read-only provider inventory access.");
            var quote = state.Server is null
                ? await provider!.QuoteAsync(config.Selection, state.Id, ct)
                : state.Plan.Quote;
            if (state.Server is null)
                SeedGuard.Require(quote.Architecture == config.Architecture, "Approved architecture differs from provider image.");
            quote = quote with { Account = config.ProviderAccount, CredentialScope = "authorized-account-access" };
            state.Plan = new(config.Selection, quote, release.Digest, release.Source) { OperatorPublicKeys = config.OperatorPublicKeys };
            if (observation.State == "installed" && observation.Release == config.Selection.ServerVersion)
                SeedGuard.Require(observation.Source == release.Source, "Installed source differs from exact signed release; reconcile.");
            report.Plan = state.Plan;
            var newSeed = options.ContainsKey("--new-seed");
            report.Approval = SeedPortable.Approval(config, state, report.Classification, publicKey, newSeed);
            _console.WriteLine("Classification: " + report.Classification);
            var displayedPlan = JsonSerializer.Serialize(state.Plan, SeedJournal.Json);
            SeedGuard.Require(string.IsNullOrEmpty(credential) || !displayedPlan.Contains(credential, StringComparison.Ordinal),
                "Plan withheld because it contained supplied secret material.");
            SeedGuard.Require(string.IsNullOrEmpty(embeddedCredential) || !displayedPlan.Contains(embeddedCredential, StringComparison.Ordinal),
                "Plan withheld because it contained supplied secret material.");
            _console.WriteLine(displayedPlan);
            _console.WriteLine("Approval digest: " + report.Approval);
            if (action == "plan") return CliExitCodes.Success;
            SeedGuard.Require(Option("--approve") == report.Approval, "Apply requires the exact fresh reviewed plan approval.");
            if (report.Classification == "upgrade_required") return SeedPortable.ExitCode(report);
            if (report.Classification == "current") return SeedPortable.ExitCode(report);
            if (report.Classification == "new_seed")
            {
                await host.CheckPrerequisitesAsync(ct);
                await SeedPortable.CreateAsync(config, state, provider!, publicKey, newSeed, Option("--approve"), report.Classification, ct);
                SeedPortable.Observe(report, state, new("awaiting_trust", "awaiting_independent_ssh_trust"));
                report.NextActions = ["Retain this resource ID in explicit target configuration. Obtain its host fingerprint independently through provider console, replan and continue; never repeat initial creation."];
                return 10;
            }
            // Host-only continuation needs no provider token or creation permissions.
            SeedPortable.Observe(report, state, await SeedPortable.ContinueAsync(state, scratch, host, host, observation, ct));
            return SeedPortable.ExitCode(report);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (report is not null)
            {
                report.Result = ex is OperationCanceledException ? "unknown" : "failed";
                report.Classification = "reconciliation_required";
                report.Reasons = ["Operation did not establish a safe verified result. Secret-bearing diagnostics suppressed; no automatic create retry or rollback."];
                report.NextActions = [state?.KeyCreateStarted == true || state?.ServerCreateStarted == true
                    ? "Creation may be uncertain even if inventory is empty. Retain the same seedId; reconcile provider console/support before any new initial-create assertion."
                    : "Check protected inputs, host trust, identity and metadata. Resolve incomplete maintenance without reinstalling."];
            }
            _console.WriteError("Seed could not establish a safe result. Resources retained; billing may continue. Inspect the non-secret report and independently reconcile. No secret-bearing diagnostics logged.");
            return report is null ? CliExitCodes.ValidationError : SeedPortable.ExitCode(report);
        }
        finally
        {
            try
            {
                if (report is not null && reportPath is not null)
                {
                    if (state?.Server is { } retained)
                    {
                        report.Target = retained;
                        report.RetainedBillableResources = new[] { "server:" + retained.Id }
                            .Concat(retained.PrimaryIpId is { } ip ? new[] { "ipv4:" + ip } : []).ToArray();
                    }
                    else if (state?.KeyCreateStarted == true || state?.ServerCreateStarted == true)
                        report.RetainedBillableResources = state.ServerCreateStarted
                            ? ["provider-ssh-key:" + (state.SshKeyId ?? "unknown"), "server:create-outcome-unknown"]
                            : ["provider-ssh-key:" + (state.SshKeyId ?? "unknown")];
                    report.ProviderSshKeyId = state?.SshKeyId;
                    WritePortableSeedReport(reportPath, report, credential, embeddedCredential);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                throw new CliCommandException(CliExitCodes.ValidationError,
                    "Could not safely write the seed report. Preserve the same seedId and reconcile any uncertain creation; diagnostics suppressed.");
            }
            finally { if (scratch is not null && Directory.Exists(scratch)) Directory.Delete(scratch, true); }
        }
    }

    private static async Task<SeedObservation> ObservePortableHostAsync(SeedHost host, SeedState state, string scratch, CancellationToken ct)
    {
        if (state.Server?.Address is null || state.HostFingerprint is null) return new("awaiting_trust", "awaiting_independent_ssh_trust");
        await host.TrustAsync(state, scratch, state.HostFingerprint, ct);
        return await host.InspectAsync(state, scratch, ct);
    }

    private static void WritePortableSeedReport(string path, SeedPortableReport report, string? credential, string? embeddedCredential)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, SeedJournal.Json);
        SeedGuard.Require(string.IsNullOrEmpty(credential) || !System.Text.Encoding.UTF8.GetString(bytes).Contains(credential, StringComparison.Ordinal),
            "Report withheld because it contained supplied secret material.");
        SeedGuard.Require(string.IsNullOrEmpty(embeddedCredential) || !System.Text.Encoding.UTF8.GetString(bytes).Contains(embeddedCredential, StringComparison.Ordinal),
            "Report withheld because it contained supplied secret material.");
        CheckPortableReportDestination(path, report.SeedId);
        var pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        SeedFiles.WriteNew(pending, bytes);
        File.Move(pending, path, true);
    }

    private static void CheckPortableReportDestination(string path, string seedId)
    {
        if (!File.Exists(path)) return;
        SeedFiles.Check(path);
        SeedGuard.Require(new FileInfo(path).Length < 1048576, "Existing report is too large; choose a fresh report path.");
        using var previous = SeedPortable.ReadDocument(File.ReadAllText(path));
        SeedGuard.Require(previous.RootElement.GetProperty("kind").GetString() == "minicloud-seed-report"
            && previous.RootElement.GetProperty("schemaVersion").GetInt32() == 1
            && previous.RootElement.GetProperty("seedId").GetString() == seedId,
            "Never overwrite keys, journals, private exports, another seed's report or unrelated files.");
    }
}
