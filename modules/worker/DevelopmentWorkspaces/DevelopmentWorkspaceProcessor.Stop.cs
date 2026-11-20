using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessor
{
    private async Task StopStepAsync(IDevelopmentWorkspaceProvider cloud, DevelopmentWorkspace workspace,
        DevelopmentWorkspaceVm vm, DevelopmentWorkspaceOperation op, Evidence evidence, CancellationToken ct)
    {
        switch (op.Step)
        {
            case "pending":
            case "wait-for-tasks":
            case "wait-for-save":
                if (vm.Phase == "blocked")
                    await SaveAsync(op, evidence, op.Step, (_, fresh, _) =>
                        fresh.Phase = op.Step == "wait-for-save" ? "awaiting_git_save" : "draining", ct, release: false);
                if (vm.ProviderServerId is null)
                {
                    var found = await cloud.FindWorkspaceServersAsync(ComputeLabels(workspace, vm), ct);
                    if (found.Count != 1)
                        throw new InvalidOperationException("An interrupted allocation needs explicit ownership reconciliation before releasing its reservation.");
                    var discovered = found[0];
                    await SaveAsync(op, evidence, op.Step, (_, fresh, _) =>
                    { fresh.ProviderServerId = discovered.Id; fresh.ProviderIpId = discovered.PrimaryIpId; fresh.IpAddress = discovered.Ipv4; }, ct);
                    break;
                }
                var publishRoute = !await db.DevelopmentWorkspaceGatewayRoutes.AnyAsync(x =>
                    x.VmId == vm.Id && x.Generation == vm.Generation && x.Service == "t3" &&
                    x.RevokedAt != null, ct);
                await RefreshRuntimeAsync(workspace, vm, op.Id, ct, publishRoute: publishRoute);
                vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync(x => x.Id == vm.Id, ct);
                if (await lifecycle.HasUnresolvedTasksAsync(vm, ct) || !await IsRuntimeIdleAsync(vm, op.Id, ct))
                {
                    await SaveAsync(op, evidence, "wait-for-tasks", (_, fresh, _) =>
                    { fresh.Phase = "draining"; fresh.GatewayReady = publishRoute; fresh.LastHeartbeatAt = Now; }, ct);
                    break;
                }
                var discard = vm.DiscardGeneration == vm.Generation && vm.DiscardOperationId == op.Id &&
                    vm.DiscardRequestedAt is not null && vm.DiscardRequestedBy is not null;
                if (vm.Kind == "ephemeral" && !discard)
                {
                    try { await RefreshRepositoryCredentialAsync(vm, ct, force: true); }
                    catch (DevelopmentWorkspaceException error) when (error.Code == "workspace_git_access_denied")
                    {
                        await AwaitGitAsync(op, evidence, ct); break;
                    }
                    var candidate = await CallStopRuntimeAsync(vm, op, evidence, "git-check", null, ct);
                    if (!candidate.TryGetProperty("ok", out var check) || check.ValueKind != JsonValueKind.True)
                    {
                        await AwaitGitAsync(op, evidence, ct); break;
                    }
                }
                evidence.GatewayRevokedAt = Now;
                await SaveAsync(op, evidence, "gateway-drain", (_, fresh, _) =>
                {
                    fresh.GatewayReady = false; fresh.Phase = "draining"; fresh.LastHeartbeatAt = Now;
                    foreach (var route in db.DevelopmentWorkspaceGatewayRoutes.Where(x => x.VmId == fresh.Id && x.Generation == fresh.Generation))
                        route.RevokedAt ??= Now;
                    foreach (var session in db.DevelopmentWorkspaceGatewaySessions.Where(x => x.VmId == fresh.Id && x.Generation == fresh.Generation))
                        session.RevokedAt ??= Now;
                }, ct); break;
            case "gateway-drain":
                if (evidence.GatewayRevokedAt is null) throw new InvalidOperationException("Gateway admission fence has no durable receipt.");
                if (Now - evidence.GatewayRevokedAt.Value < TimeSpan.FromSeconds(Math.Max(75, options.Value.GatewayDrainSeconds)))
                { await SaveAsync(op, evidence, op.Step, null, ct); break; }
                await SaveAsync(op, evidence, "quiesce", null, ct); break;
            case "quiesce":
                var status = Result(await CallStopRuntimeAsync(vm, op, evidence, "status", null, ct));
                if (status.GetProperty("phase").GetString() == "Ready")
                {
                    await RefreshRuntimeAsync(workspace, vm, op.Id, ct, publishRoute: false);
                    vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync(x => x.Id == vm.Id, ct);
                    if (await lifecycle.HasUnresolvedTasksAsync(vm, ct) || !await IsRuntimeIdleAsync(vm, op.Id, ct))
                    {
                        await ReopenAfterFailedFreezeAsync(workspace, vm, op, evidence, false, ct);
                        break;
                    }
                    var port = await tunnels.EnsureAsync(Target(vm), ct);
                    using var client = T3Client(port, await SecretAsync(vm.RuntimeCredentialRef!, ct));
                    var snapshot = await GetJsonAsync(client, "/api/orchestration/shell", ct);
                    if (!T3WorkspaceEvidence.IsIdle(snapshot)) throw new InvalidOperationException("T3 activity is not quiescent.");
                    evidence.T3SnapshotSequence = snapshot.GetProperty("snapshotSequence").GetInt64();
                    await SaveAsync(op, evidence, op.Step, null, ct, release: false);
                }
                var authorizedDiscard = vm.DiscardGeneration == vm.Generation && vm.DiscardOperationId == op.Id &&
                    vm.DiscardRequestedBy is not null && vm.DiscardRequestedAt is not null;
                var action = vm.Kind == "primary" ? "quiesce" : authorizedDiscard ? "discard" : "git-save";
                if (action == "git-save")
                {
                    try { await RefreshRepositoryCredentialAsync(vm, ct, force: true); }
                    catch (DevelopmentWorkspaceException error) when (error.Code == "workspace_git_access_denied")
                    {
                        await ReopenAfterFailedFreezeAsync(workspace, vm, op, evidence, true, ct); break;
                    }
                }
                var reply = await CallStopRuntimeAsync(vm, op, evidence, action, new
                {
                    gatewayAdmissionsFenced = true, unobservableToolsAbsent = true,
                    t3SnapshotSequence = evidence.T3SnapshotSequence,
                    authorizedDiscard, acknowledgeLostIgnoredAndRootData = authorizedDiscard
                }, ct);
                if (!reply.TryGetProperty("ok", out var success) || success.ValueKind != JsonValueKind.True)
                {
                    var awaitingGit = reply.TryGetProperty("state", out var state) && state.GetString() == "AwaitingGitSave";
                    await ReopenAfterFailedFreezeAsync(workspace, vm, op, evidence, awaitingGit, ct);
                    break;
                }
                var receipt = Result(reply);
                ValidateReceipt(receipt, vm, op, workspace, authorizedDiscard);
                evidence.ReceiptId = receipt.GetProperty("receiptId").GetString();
                await SaveAsync(op, evidence, vm.Kind == "primary" ? "detach" : "delete-intent", (_, fresh, _) =>
                {
                    fresh.QuiescedAt = Now; fresh.QuiescedGeneration = fresh.Generation;
                    fresh.QuiescedOperationId = op.Id; fresh.QuiescenceReceipt = receipt.GetRawText();
                    fresh.LastHeartbeatAt = Now; fresh.GatewayReady = false;
                    if (fresh.Kind == "ephemeral" && receipt.GetProperty("type").GetString() == "GitSaved")
                    {
                        var repository = receipt.GetProperty("repositories").EnumerateArray().Single(x =>
                            x.GetProperty("path").GetString() == "/workspace/repos/project");
                        fresh.GitSaveReceipt = receipt.GetRawText(); fresh.GitSavedGeneration = fresh.Generation;
                        fresh.GitSavedOperationId = op.Id; fresh.GitSavedAt = Now; fresh.GitStatus = "saved";
                        fresh.FinalHead = repository.GetProperty("headSha").GetString();
                        fresh.VerifiedRemoteCommit = fresh.FinalHead;
                        fresh.VerifiedRemoteRef = repository.GetProperty("remoteRef").GetString();
                    }
                }, ct); break;
            case "detach":
                Result(await CallStopRuntimeAsync(vm, op, evidence, "receipt-validate", new { receiptId = evidence.ReceiptId }, ct));
                var detached = await cloud.DetachWorkspaceVolumeAsync(workspace.ProviderVolumeId!, vm.ProviderServerId!, VolumeLabels(workspace), ct);
                if (detached is not null) evidence.Actions.Add(detached);
                await SaveAsync(op, evidence, "detached", (_, fresh, _) =>
                { fresh.LastHeartbeatAt = Now; fresh.Phase = "detaching"; }, ct); break;
            case "detached":
                if (!await WaitActionsAsync(cloud, evidence, ct)) { await SaveAsync(op, evidence, op.Step, null, ct); break; }
                var retained = await cloud.GetWorkspaceVolumeAsync(workspace.ProviderVolumeId!, ct)
                    ?? throw new InvalidOperationException("The retained volume disappeared.");
                WorkspaceOwnership.Require(retained.Labels, VolumeLabels(workspace));
                if (retained.ServerId is not null) throw new InvalidOperationException("The primary writer has not been detached.");
                await SaveAsync(op, evidence, "delete-intent", (_, _, fresh) =>
                { fresh.VolumeAttachedVmId = null; fresh.VolumeStatus = "retained"; }, ct); break;
            case "delete-intent":
                if (vm.Kind == "ephemeral" && vm.GitSavedOperationId == op.Id)
                    await RefreshRepositoryCredentialAsync(vm, ct, force: true);
                Result(await CallStopRuntimeAsync(vm, op, evidence, "receipt-validate", new { receiptId = evidence.ReceiptId }, ct));
                await RevokeRepositoryCredentialAsync(vm, ct);
                evidence.RepositoryCredentialRevoked = true;
                await SaveAsync(op, evidence, "commit-delete", null, ct);
                break;
            case "commit-delete":
                if (!evidence.RepositoryCredentialRevoked)
                    throw new InvalidOperationException("Repository credential retirement has no durable confirmation.");
                await SaveAsync(op, evidence, op.Step, (_, fresh, _) => fresh.LastHeartbeatAt = Now, ct, release: false);
                db.ChangeTracker.Clear();
                var guardedVm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync(x => x.Id == vm.Id, ct);
                var guardedOp = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == op.Id, ct);
                if (!await lifecycle.CanDeleteComputeAsync(guardedVm, guardedOp, ct))
                    throw new InvalidOperationException("The generation-bound deletion gate refused compute destruction.");
                evidence.DeleteCommitted = true;
                await SaveAsync(op, evidence, "delete", (_, fresh, _) => fresh.Phase = "deleting", ct);
                break;
            case "delete":
                if (!evidence.DeleteCommitted || !evidence.RepositoryCredentialRevoked ||
                    vm.QuiescedOperationId != op.Id || vm.QuiescedGeneration != vm.Generation ||
                    string.IsNullOrEmpty(vm.QuiescenceReceipt))
                    throw new InvalidOperationException("There is no durable deletion intent and quiescence proof for this generation.");
                var server = await cloud.GetWorkspaceServerAsync(vm.ProviderServerId!, ct);
                if (server is not null)
                {
                    WorkspaceOwnership.Require(server.Labels, ComputeLabels(workspace, vm));
                    if (server.Status != "deleting")
                    {
                        var deletion = await cloud.DeleteWorkspaceServerAsync(server.Id, ComputeLabels(workspace, vm), ct);
                        if (deletion is not null) evidence.Actions.Add(deletion);
                    }
                    await SaveAsync(op, evidence, "delete-confirm", null, ct); break;
                }
                await SaveAsync(op, evidence, "cleanup", null, ct); break;
            case "delete-confirm":
                if (!await WaitActionsAsync(cloud, evidence, ct)) { await SaveAsync(op, evidence, op.Step, null, ct); break; }
                await SaveAsync(op, evidence, "delete", null, ct); break;
            case "cleanup":
                if (await cloud.GetWorkspaceServerAsync(vm.ProviderServerId!, ct) is not null)
                    throw new InvalidOperationException("Compute absence is not confirmed.");
                if (vm.Kind == "primary")
                {
                    var preserved = await cloud.GetWorkspaceVolumeAsync(workspace.ProviderVolumeId!, ct)
                        ?? throw new InvalidOperationException("Retained volume absence must never be treated as a successful stop.");
                    WorkspaceOwnership.Require(preserved.Labels, VolumeLabels(workspace));
                    if (preserved.ServerId is not null) throw new InvalidOperationException("The retained volume still has an attached writer.");
                }
                if (vm.ProviderIpId is not null) await cloud.DeleteWorkspacePrimaryIpAsync(vm.ProviderIpId, ComputeLabels(workspace, vm), ct);
                if (vm.ProviderFirewallId is not null) await cloud.DeleteWorkspaceFirewallAsync(vm.ProviderFirewallId, ComputeLabels(workspace, vm), ct);
                await tunnels.RemoveAsync(vm.Id, vm.Generation, ct);
                RepositoryRenewals.TryRemove((vm.Id, vm.Generation), out _);
                RepositoryCredentialLocks.TryRemove((vm.Id, vm.Generation), out _);
                await SaveAsync(op, evidence, "stopped", (fresh, machine, parent) =>
                {
                    machine.Phase = "stopped"; machine.ComputeReserved = false; machine.GatewayReady = false;
                    machine.ProviderServerId = null; machine.ProviderIpId = null; machine.ProviderFirewallId = null;
                    machine.ProviderActionId = null; machine.IpAddress = null;
                    machine.RuntimeCredentialRef = null; machine.RuntimeTokenExpiresAt = null;
                    if (machine.Kind == "primary") { parent.VolumeAttachedVmId = null; parent.VolumeStatus = "retained"; }
                    fresh.Status = "completed"; fresh.LeaseToken = null; fresh.LeaseOwner = null; fresh.LeaseExpiresAt = null;
                }, ct);
                if (vm.Kind == DevelopmentWorkspaceVmKinds.Ephemeral && vm.DiscardGeneration == vm.Generation && vm.DiscardOperationId == op.Id)
                    await DeleteResourceHistoryAsync(vm.WorkspaceId, vm.Id, ct);
                break;
            default: throw new InvalidOperationException("Unknown workspace stop stage.");
        }
    }

    private Task AwaitGitAsync(DevelopmentWorkspaceOperation op, Evidence evidence, CancellationToken ct) =>
        SaveAsync(op, evidence, "wait-for-save", (_, fresh, _) =>
        {
            fresh.Phase = "awaiting_git_save"; fresh.GatewayReady = true; fresh.LastHeartbeatAt = Now;
            fresh.GitStatus = "awaiting_save"; fresh.ErrorCode = "workspace_git_save_required";
            fresh.ErrorMessage = "Finish work, commit and explicitly push the authorized work branch, then retry; dirty, untracked, unpushed or unavailable remote state blocks deletion. Explicit discard does not bypass task safety.";
        }, ct);

    private async Task ReopenAfterFailedFreezeAsync(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm,
        DevelopmentWorkspaceOperation op, Evidence evidence, bool awaitingGit, CancellationToken ct)
    {
        var state = Result(await CallStopRuntimeAsync(vm, op, evidence, "status", null, ct, recovery: true)).GetProperty("phase").GetString();
        if (state == "Frozen")
            Result(await CallStopRuntimeAsync(vm, op, evidence, "resume", new { deletionNotCommitted = true }, ct, recovery: true));
        else if (state == "Fenced")
            Result(await CallStopRuntimeAsync(vm, op, evidence, "cancel-drain", null, ct, recovery: true));
        else if (state != "Ready") throw new InvalidOperationException("The frozen workspace state cannot safely reopen admission.");
        if (evidence.DeleteCommitted) throw new InvalidOperationException("A committed deletion cannot reopen the old VM.");
        evidence.ReceiptId = null; evidence.T3SnapshotSequence = null; evidence.GatewayRevokedAt = null;
        await SaveAsync(op, evidence, awaitingGit ? "wait-for-save" : "wait-for-tasks", (_, fresh, _) =>
        {
            fresh.QuiescedAt = null; fresh.QuiescedGeneration = null; fresh.QuiescedOperationId = null; fresh.QuiescenceReceipt = null;
            fresh.GitSaveReceipt = null; fresh.GitSavedAt = null; fresh.GitSavedGeneration = null; fresh.GitSavedOperationId = null;
            fresh.FinalHead = null; fresh.VerifiedRemoteCommit = null; fresh.VerifiedRemoteRef = null;
            fresh.Phase = awaitingGit ? "awaiting_git_save" : "draining"; fresh.GatewayReady = false;
        }, ct, release: false);
        await RefreshRuntimeAsync(workspace, vm, op.Id, ct, allowRevive: true);
        if (awaitingGit) await AwaitGitAsync(op, evidence, ct);
        else await SaveAsync(op, evidence, "wait-for-tasks", (_, fresh, _) => fresh.GatewayReady = true, ct);
    }

    private async Task<JsonElement> CallStopRuntimeAsync(DevelopmentWorkspaceVm vm, DevelopmentWorkspaceOperation op,
        Evidence evidence, string action, object? arguments, CancellationToken ct, bool recovery = false)
    {
        var response = await runtime.CallAsync(Target(vm), op.Id, action, arguments, ct);
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("ok", out var ok) &&
            ok.ValueKind == JsonValueKind.True) return response;
        var rejection = WorkspaceRuntimeRejection.Capture(action, response, Now);
        if (recovery) evidence.LastRuntimeRecoveryRejection = rejection;
        else evidence.LastRuntimeRejection = rejection;
        logger?.LogWarning("Workspace operation {OperationId} runtime rejection: {RuntimeRejection}",
            op.Id, JsonSerializer.Serialize(rejection, Json));
        // Checkpoint before a recovery call can fail or clear the runtime's frozen state.
        await SaveAsync(op, evidence, op.Step, null, ct, release: false);
        return response;
    }

    private static void ValidateReceipt(JsonElement receipt, DevelopmentWorkspaceVm vm, DevelopmentWorkspaceOperation op,
        DevelopmentWorkspace workspace, bool discard)
    {
        if (receipt.GetProperty("schemaVersion").GetInt32() != 1 || receipt.GetProperty("vmId").GetString() != vm.Id ||
            receipt.GetProperty("generation").GetInt64() != vm.Generation || receipt.GetProperty("kind").GetString() != vm.Kind ||
            receipt.GetProperty("operationId").GetString() != op.Id || string.IsNullOrEmpty(receipt.GetProperty("receiptId").GetString()))
            throw new InvalidOperationException("Runtime receipt has stale identity or generation.");
        var type = receipt.GetProperty("type").GetString();
        if (vm.Kind == "primary" && (type != "PrimaryQuiesced" || !receipt.GetProperty("unmounted").GetBoolean() ||
            receipt.GetProperty("filesystemUuid").GetString() != workspace.VolumeFilesystemUuid))
            throw new InvalidOperationException("The primary has no exact-volume unmount proof.");
        if (vm.Kind == "ephemeral" && type != (discard ? "DiscardAuthorized" : "GitSaved"))
            throw new InvalidOperationException("The ephemeral has neither verified Git proof nor authorized discard.");
    }
}
