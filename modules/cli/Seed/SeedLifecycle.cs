// SPDX-License-Identifier: MPL-2.0
namespace Minicloud.Cli.Seed;

internal sealed class SeedLifecycle(ISeedProvider provider, ISeedHost host, Action<SeedState> save, Action<string> write)
{
    public static void Match(SeedState state, SeedResource resource)
    {
        SeedGuard.Require(resource.Operation == state.Id
            && resource.Region == state.Plan.Selection.Region && resource.Size == state.Plan.Selection.Size
            && resource.Image == state.Plan.Quote.Image && (state.Server is null || state.Server.Id == resource.Id),
            "Provider resource does not match the recorded operation/account/region/size/image. Reconciliation required.");
        if (state.Server?.Address is { } address)
            SeedGuard.Require(resource.Address == address, "Public address changed; do not adopt/reconfigure an established installation.");
        if (state.Server?.PrimaryIpId is { } primary)
            SeedGuard.Require(resource.PrimaryIpId == primary, "Original primary IPv4 resource identity changed; refusing adoption/cleanup.");
    }

    public async Task ResumeAsync(SeedState state, string directory, string? approval, string? fingerprint, CancellationToken ct)
    {
        try { await ResumeCoreAsync(state, directory, approval, fingerprint, ct); }
        catch
        {
            if (state.Phase is not ("complete" or "cleaned"))
            {
                state.LastFailurePhase = state.Phase;
                state.Phase = state.ServerCreateStarted && state.Server is null ? "reconciliation_required" : "failed";
                save(state);
            }
            throw;
        }
    }

    private async Task ResumeCoreAsync(SeedState state, string directory, string? approval, string? fingerprint, CancellationToken ct)
    {
        SeedGuard.Require(state.Phase != "cleaned", "This operation was explicitly cleaned up.");
        if (state.Phase == "complete") { write("Seed already recorded complete (not a live health check). Use normal server maintenance, not seed rollback."); return; }
        var fresh = await provider.QuoteAsync(state.Plan.Selection, state.Id, ct);
        SeedGuard.Require(fresh.CredentialScope == state.Plan.Quote.CredentialScope && fresh.Account == state.Plan.Quote.Account,
            "Resume requires the original provider credential/account scope. Credential rotation needs explicit operator reconciliation.");
        if (!state.ServerCreateStarted)
        {
            var updated = state.Plan with { Quote = fresh };
            if (updated.Approval != state.Plan.Approval)
            {
                state.Plan = updated;
                state.Phase = "planned";
                save(state);
            }
            write(System.Text.Json.JsonSerializer.Serialize(state.Plan, SeedJournal.Json));
            write("Approval digest: " + state.Plan.Approval);
            if (approval != state.Plan.Approval)
            {
                write("No resources created. Review and resume with --approve <exact digest>. DNS waits and interruptions remain billable after creation.");
                return;
            }
            await host.CheckPrerequisitesAsync(ct);
            await host.GenerateKeyAsync(directory, ct);
            var publicKey = File.ReadAllText(Path.Combine(directory, "id_ed25519.pub")).Trim();
            var key = await provider.FindKeyAsync(state.Id, publicKey, ct);
            if (state.SshKeyId is not null) SeedGuard.Require(key == state.SshKeyId, "Recorded operation SSH key is missing or changed.");
            if (key is null)
            {
                SeedGuard.Require(!state.KeyCreateStarted, "SSH key creation outcome is uncertain; reconcile with provider before any retry.");
                state.KeyCreateStarted = true;
                save(state);
                key = await provider.CreateKeyAsync(state.Id, publicKey, ct);
            }
            state.SshKeyId = key;
            save(state);
            SeedGuard.Require(await provider.FindServerAsync(state.Id, ct) is null, "Operation server already exists before creation intent; reconciliation required.");
            state.ServerCreateStarted = true;
            state.Phase = "creating";
            save(state);
            // The intent is durable before dispatch; even cancellation cannot permit an implicit retry.
            state.Server = await provider.CreateServerAsync(state, ct);
            save(state);
        }
        var server = await provider.FindServerAsync(state.Id, ct);
        if (server is null)
        {
            state.Phase = "reconciliation_required"; save(state);
            write("Create outcome uncertain or server missing. No second create will be sent. Inspect provider console/support using operation " + state.Id + ".");
            return;
        }
        Match(state, server);
        state.Server = server;
        save(state);
        if (server.Address is null) { write("VPS is still allocating its public IPv4. Resume later; billing continues."); return; }
        write($"Retained/billable {state.Plan.Selection.Provider} server {server.Id}, IPv4 {server.Address}, SSH key {state.SshKeyId}.");
        state.Phase = "awaiting_ssh_trust"; save(state);
        if (state.HostFingerprint is null && fingerprint is null)
        {
            write("Pause: independently obtain the ed25519 host fingerprint in the provider's authenticated console, then resume with --confirm-console-fingerprint SHA256:...");
            return;
        }
        await host.TrustAsync(state, directory, fingerprint, ct);
        save(state);
        if (!state.HostPrepared)
        {
            state.Phase = "preparing_host"; save(state);
            await host.PrepareAsync(state, directory, ct);
            state.HostPrepared = true; save(state);
        }
        state.Phase = "awaiting_dns"; save(state);
        var domain = state.Plan.Selection.Domain;
        write($"Manual DNS: {domain}. A {server.Address}\nManual DNS: api.{domain}. A {server.Address}");
        write($"Do NOT point *.{state.Plan.Selection.AppDomain} at this control plane. Configure app routing only when app VPSs exist. Remove stale/conflicting A/AAAA records.");
        if (!await host.DnsReadyAsync(state, ct)) { write("DNS not ready. Correct records and resume; billing continues."); return; }
        state.Phase = "installing"; save(state);
        // Persist the irreversible install boundary before any installer request is sent.
        state.InstallStarted = true; save(state);
        var result = await host.InstallAsync(state, directory, true, ct);
        if (result == "incomplete")
        {
            result = await host.InstallAsync(state, directory, false, ct);
        }
        state.InstallerState = result;
        state.Phase = result == "awaiting_dns_tls" ? "awaiting_dns" : "awaiting_browser_setup";
        save(state);
        if (result is "awaiting_operator" or "ready")
        {
            await host.VerifyHttpsAsync(state, ct);
            write($"Verified setup origin: https://{domain}/setup (no token in URL).");
            write("Use seed export <operation> --recovery-dir <new protected local directory> [--setup-token] to take independent custody. Then complete ordinary browser onboarding.");
        }
        if (result == "ready" && state.RecoveryExported && state.RecoveryAcknowledged)
        {
            state.Phase = "complete"; save(state);
            write("Seed infrastructure and browser setup complete; independent recovery acknowledged. Login/provider registration/app deployment are separate actions.");
            write($"Optional, default NO import: minicloud seed register-provider {state.Id} --register-provider --organization <workspace-id> --provider-name <name>. This requires ordinary login/permissions and separate approval of billable app-provisioning authority.");
        }
    }

    public async Task CleanupAsync(SeedState state, string directory, string? confirmation, CancellationToken ct)
    {
        SeedGuard.Require(!state.InstallStarted && state.InstallationId is null && state.Phase != "complete",
            "Installation may have started. Seed cleanup is forbidden; use normal maintenance/decommission, never rollback.");
        SeedGuard.Require(provider.CredentialScope(state.Id) == state.Plan.Quote.CredentialScope, "Cleanup requires the original provider account scope.");
        var server = await provider.FindServerAsync(state.Id, ct);
        if (server is not null) { Match(state, server); state.Server = server; save(state); }
        SeedGuard.Require(!state.ServerCreateStarted || state.Server is not null,
            "Creation outcome is uncertain. Absence from inventory does not prove no billable server; reconcile with provider support.");
        write($"Destructive cleanup: server={state.Server?.Id ?? "(none)"}, auto-owned IPv4={state.Server?.PrimaryIpId ?? "(included/none)"}, generated SSH key={state.SshKeyId ?? "(reconcile key)"}. No other resources.");
        var digest = SeedGuard.Hash(System.Text.Json.JsonSerializer.Serialize(new { state.Id, state.Server, state.SshKeyId }, SeedJournal.Json));
        write("Cleanup approval: " + digest);
        if (confirmation != digest) { write("No deletion. Repeat with --confirm-cleanup <exact digest>."); return; }
        if (state.Server is not null && !state.ServerDeleted)
        {
            if (!await provider.DeleteServerAsync(state, ct))
            {
                state.Phase = "cleanup_pending"; save(state);
                write("Deletion accepted but provider still reports the VPS. Repeat owned cleanup to confirm absence; billing may continue. Key retained.");
                return;
            }
            state.ServerDeleted = true; save(state);
        }
        if (state.KeyCreateStarted && !state.KeyDeleted)
        {
            SeedFiles.NoLinks(Path.Combine(directory, "id_ed25519.pub"));
            var publicKey = File.ReadAllText(Path.Combine(directory, "id_ed25519.pub"));
            var key = await provider.FindKeyAsync(state.Id, publicKey, ct);
            SeedGuard.Require(key is not null || state.SshKeyId is not null, "SSH key create outcome uncertain; reconcile independently before marking cleanup complete.");
            if (key is not null)
            {
                SeedGuard.Require(state.SshKeyId is null || key == state.SshKeyId, "SSH key identity changed.");
                state.SshKeyId = key; save(state);
                await provider.DeleteKeyAsync(key, state.Id, publicKey, ct);
            }
            state.KeyDeleted = true; save(state);
        }
        state.Phase = "cleaned"; save(state);
        write("Owned cleanup requests completed. Confirm provider billing/resource disappearance; local recovery/key files are retained.");
    }
}
