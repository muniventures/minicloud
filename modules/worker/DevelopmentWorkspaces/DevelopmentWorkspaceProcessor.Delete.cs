using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessor
{
    private async Task DeleteStepAsync(IDevelopmentWorkspaceProvider cloud, DevelopmentWorkspace workspace,
        DevelopmentWorkspaceVm vm, DevelopmentWorkspaceOperation op, CancellationToken ct)
    {
        var labels = VolumeLabels(workspace);
        if (op.ProviderResourceId is not { } id)
        {
            if (vm.Generation != 0 || (await cloud.FindWorkspaceVolumesAsync(labels, ct)).Count != 0)
                throw DeleteBlocked("workspace_delete_volume_unrecorded", "The workspace may have storage but has no recorded volume ID. Contact an operator to reconcile the retained volume.");
        }
        else
        {
            var volume = await cloud.GetWorkspaceVolumeAsync(id, ct);
            if (volume is not null)
            {
                if (volume.Id != id || volume.Region != workspace.Region
                    || labels.Any(pair => !volume.Labels.TryGetValue(pair.Key, out var value) || value != pair.Value))
                    throw DeleteBlocked("workspace_delete_volume_mismatch", "The provider volume does not match the recorded workspace ownership or region. Contact an operator; do not relabel an unrelated volume.");
                if (volume.ServerId is not null)
                    throw DeleteBlocked("workspace_delete_volume_attached", "The provider says the retained volume is still attached. Reconcile and safely detach it before retrying.");
                if (op.Step == "pending")
                {
                    if ((await cloud.FindWorkspaceVolumesAsync(labels, ct)).Any(x => x.Id != id))
                        throw DeleteBlocked("workspace_delete_volume_unexpected", "Provider inventory contains more than one candidate volume. Contact an operator to reconcile it.");
                    await lifecycle.UpdateClaimedOperationAsync(op.Id, op.LeaseToken!, (fresh, _, _) =>
                    {
                        fresh.Step = "delete-verified-volume";
                        Release(fresh);
                    }, ct);
                    return;
                }
                await lifecycle.RenewOperationLeaseAsync(op.Id, op.LeaseToken!, ct);
                await cloud.DeleteWorkspaceVolumeAsync(id, labels, ct);
                if (await cloud.GetWorkspaceVolumeAsync(id, ct) is not null)
                    throw new InvalidOperationException("Volume deletion has not yet been confirmed.");
            }
            else if (op.Step != "delete-verified-volume")
                throw DeleteBlocked("workspace_delete_volume_unverified", "The recorded volume is missing before ownership could be verified. Restore access to the original provider account and retry.");

            if ((await cloud.FindWorkspaceVolumesAsync(labels, ct)).Count != 0)
                throw DeleteBlocked("workspace_delete_volume_unexpected", "Provider inventory still contains workspace-labelled storage. Contact an operator to reconcile it.");
        }

        await lifecycle.RenewOperationLeaseAsync(op.Id, op.LeaseToken!, ct);
        await DeleteWorkspaceCredentialsAsync(workspace, ct);
        await lifecycle.CompleteDeletionAsync(op.Id, op.LeaseToken!, ct);
        await DeleteResourceHistoryAsync(workspace.Id, null, ct);
    }

    // After the lifecycle commit; any rows left by a failure here still expire through retention pruning.
    private async Task DeleteResourceHistoryAsync(string workspaceId, string? vmId, CancellationToken ct)
    {
        try { await lifecycle.DeleteResourceHistoryAsync(workspaceId, vmId, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("Dev environment resource history cleanup failed ({ErrorType}); retention expiry will remove it.", error.GetType().Name);
        }
    }

    private async Task DeleteWorkspaceCredentialsAsync(DevelopmentWorkspace workspace, CancellationToken ct)
    {
        var references = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking()
            .Where(x => x.WorkspaceId == workspace.Id && x.OrganizationId == workspace.OrganizationId && x.T3CredentialReference != null)
            .Select(x => x.T3CredentialReference!).ToListAsync(ct);
        var vms = await db.DevelopmentWorkspaceVms.AsNoTracking().Where(x => x.WorkspaceId == workspace.Id).ToListAsync(ct);
        references.AddRange(vms.Select(x => x.SshBootstrapSecretRef).OfType<string>());
        var stored = references.Select(x => JsonSerializer.Deserialize<StoredSecretReference>(x, Json)
            ?? throw new InvalidOperationException("Cannot reconcile a workspace secret reference.")).ToList();
        if (secrets is OpenBaoSecretStore openBao)
        {
            // Older start generations may have cleared their references; the paths are generation-scoped.
            var generations = await db.DevelopmentWorkspaceOperations.AsNoTracking()
                .Where(x => x.WorkspaceId == workspace.Id && x.Kind == "start").Select(x => new { x.VmId, x.Generation }).Distinct().ToListAsync(ct);
            foreach (var generation in generations)
                foreach (var name in new[] { "bootstrap", "t3" })
                    stored.Add(openBao.GetIntegrationReference(workspace.OrganizationId, "workspace-vm-" + generation.VmId,
                        WorkspaceBootstrap.Generation(generation.Generation) + "-" + name));
        }
        foreach (var reference in stored.DistinctBy(x => (x.Provider, x.Path)))
            await secrets.DeleteAsync(reference, ct);
        foreach (var machine in vms)
            await tunnels.RemoveAsync(machine.Id, machine.Generation, ct);
    }

    private void RecordDeleteFailure(DevelopmentWorkspaceOperation op, DevelopmentWorkspaceVm vm, Exception error)
    {
        var blocked = error as DevelopmentWorkspaceException;
        op.ErrorCode = vm.ErrorCode = blocked?.Code ?? "workspace_delete_reconciliation_required";
        op.ErrorMessage = vm.ErrorMessage = blocked?.Message ??
            "Deletion could not be confirmed. Check provider and secret-store access, then retry permanent deletion. The workspace remains locked.";
        Release(op);
        if (blocked is null && op.Attempts < 5)
        {
            op.LeaseExpiresAt = Now.AddSeconds(5);
            vm.Phase = "deleting";
        }
        else
        {
            op.Status = "failed";
            vm.Phase = "delete_failed";
        }
    }

    private static DevelopmentWorkspaceException DeleteBlocked(string code, string message) => new(code, message, conflict: true);
}
