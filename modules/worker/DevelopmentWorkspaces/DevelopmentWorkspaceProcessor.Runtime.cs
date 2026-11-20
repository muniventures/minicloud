using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessor
{
    internal static double? ActivityTimestamp(JsonElement status) =>
        status.TryGetProperty("activityAt", out var useful) && useful.ValueKind != JsonValueKind.Null &&
        useful.TryGetDouble(out var seconds) ? seconds : null;

    private async Task RefreshRuntimeAsync(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm, string operationId, CancellationToken ct, bool allowRevive = false, bool publishRoute = true)
    {
        if (!policy.Value.Images.TryGetValue(vm.ImageId, out var image) || !image.RuntimeQualified ||
            image.ProviderAccountId != vm.ProviderAccountId || image.ProviderImageId != vm.ProviderImage ||
            !string.Equals(image.ManifestSha256, vm.ImageManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !image.HasSupportedT3Protocol())
            throw new InvalidOperationException("The reserved workspace image has no matching qualified T3 registration.");
        var status = Result(await runtime.CallAsync(Target(vm), operationId, "status", null, ct));
        if (status.GetProperty("vmId").GetString() != vm.Id || status.GetProperty("generation").GetInt64() != vm.Generation ||
            status.GetProperty("kind").GetString() != vm.Kind || status.GetProperty("phase").GetString() != "Ready")
            throw new InvalidOperationException("Runtime identity or readiness differs from its reserved generation.");
        var port = await tunnels.EnsureAsync(Target(vm), ct);
        string credential;
        var credentialRef = vm.RuntimeCredentialRef;
        DateTimeOffset expires;
        if (credentialRef is null || vm.RuntimeTokenExpiresAt < Now.AddMinutes(2) || vm.RuntimeTokenExpiresAt is null)
        {
            credential = await runtime.IssueT3SessionAsync(Target(vm), operationId, Math.Clamp(options.Value.SessionLifetimeSeconds, 300, 3600), ct);
            if (credential.Length is < 32 or > 8192 || credential.Any(char.IsWhiteSpace))
                throw new InvalidOperationException("Headless T3 session issuance did not return a bounded opaque bearer.");
            var stored = await StoreAsync(vm, "t3", credential, ct);
            credentialRef = JsonSerializer.Serialize(stored, Json);
        }
        else credential = await SecretAsync(credentialRef, ct);
        using var http = T3Client(port, credential);
        var descriptor = await GetJsonAsync(http, "/.well-known/t3/environment", ct);
        if (descriptor.GetProperty("serverVersion").GetString() != image.T3Version ||
            descriptor.GetProperty("orchestrationProtocolVersion").GetInt32() != image.T3EnvironmentProtocol ||
            !descriptor.TryGetProperty("environmentId", out var environment) || string.IsNullOrEmpty(environment.GetString()))
            throw new InvalidOperationException("The actual T3 descriptor differs from the qualified image protocol.");
        var session = await GetJsonAsync(http, "/api/auth/session", ct);
        if (!session.GetProperty("authenticated").GetBoolean() ||
            !DateTimeOffset.TryParse(session.GetProperty("expiresAt").GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out expires) || expires <= Now.AddSeconds(60))
            throw new InvalidOperationException("The actual T3 bearer session is not ready.");
        await VerifyWebSocketAsync(http, port, ct);
        var previews = publishRoute ? await ProbePreviewsAsync(vm, ct) : new Dictionary<string, int>();
        string? connectCredentialRef = null;
        if (previews.ContainsKey("connect"))
        {
            connectCredentialRef = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking()
                .Where(x => x.VmId == vm.Id && x.Generation == vm.Generation && x.Service == "connect")
                .Select(x => x.T3CredentialReference).SingleOrDefaultAsync(ct);
            if (connectCredentialRef is null)
            {
                try
                {
                    var connectCredential = await runtime.ReadConnectCredentialAsync(Target(vm), ct);
                    if (connectCredential.Length is < 32 or > 256 || connectCredential.Any(char.IsWhiteSpace))
                        throw new InvalidOperationException("Connect management credential is not a bounded opaque bearer.");
                    connectCredentialRef = JsonSerializer.Serialize(await StoreAsync(vm, "connect", connectCredential, ct), Json);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // The optional receiver can disappear between admission and export; never hide usable T3.
                    previews.Remove("connect");
                }
            }
        }
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await maintenance.EnsureCommitAuthorityAsync(db, ct);
        var fresh = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id, ct);
        if (fresh.Generation != vm.Generation || fresh.OperationId != vm.OperationId ||
            (fresh.OperationId ?? fresh.AllocationOperationId) != operationId ||
            (publishRoute && fresh.DesiredState != "running" && fresh.Phase is not ("draining" or "awaiting_git_save")))
            throw new InvalidOperationException("Runtime route publication lost its generation authority.");
        fresh.RuntimeCredentialRef = credentialRef; fresh.RuntimeTokenExpiresAt = expires;
        fresh.LastHeartbeatAt = Now; fresh.Revision++; fresh.UpdatedAt = Now;
        // Best-effort telemetry: an old image or an invalid sample only clears the metrics.
        fresh.RuntimeCapabilities = DevelopmentWorkspaceService.NormalizeCapabilities(status);
        fresh.ResourceSampleJson = DevelopmentWorkspaceService.NormalizeResourceSample(status, fresh.Id, fresh.Generation, Now);
        fresh.ResourceSampleGeneration = fresh.Generation; fresh.ResourceSampleReceivedAt = Now;
        if (fresh.Kind == "primary" && status.TryGetProperty("backup", out var backup))
        {
            var owner = await db.DevelopmentWorkspaces.SingleAsync(x => x.Id == fresh.WorkspaceId, ct);
            var previous = (owner.LastBackupAt, owner.BackupStatus);
            var state = backup.GetProperty("state").GetString();
            if (state is "Fresh" or "Stale" && backup.TryGetProperty("verifiedAt", out var verified) &&
                verified.TryGetDouble(out var timestamp) && timestamp > 0)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(timestamp * 1000)));
                if (at <= Now && (owner.LastBackupAt is null || at >= owner.LastBackupAt))
                {
                    owner.LastBackupAt = at; owner.BackupStatus = state;
                }
            }
            else if (state is "Failed" or "AwaitingOffHostAcknowledgement" || owner.LastBackupAt is null)
                owner.BackupStatus = state;
            if (owner.LastBackupAt is { } last && last < Now.AddDays(-1) && owner.BackupStatus == "Fresh")
                owner.BackupStatus = "Stale";
            if (previous != (owner.LastBackupAt, owner.BackupStatus)) { owner.Revision++; owner.UpdatedAt = Now; }
        }
        if (ActivityTimestamp(status) is { } seconds && seconds > 0)
        {
            var activity = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(seconds * 1000)));
            if (activity <= Now && (fresh.LastActivityAt is null || activity > fresh.LastActivityAt)) fresh.LastActivityAt = activity;
        }
        var history = DevelopmentWorkspaceService.ResourceHistorySample(fresh.ResourceSampleJson, fresh.WorkspaceId, fresh.Id, fresh.Generation);
        if (!publishRoute)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await RecordResourceHistoryAsync(history, ct);
            return;
        }
        var route = await db.DevelopmentWorkspaceGatewayRoutes.SingleOrDefaultAsync(
            x => x.VmId == vm.Id && x.Generation == vm.Generation && x.Service == "t3", ct);
        if (route is null)
        {
            route = new() { Id = ids.NewId("workspaceroute"), OrganizationId = workspace.OrganizationId,
                WorkspaceId = workspace.Id, VmId = vm.Id, Generation = vm.Generation, Service = "t3", RegisteredAt = Now };
            db.DevelopmentWorkspaceGatewayRoutes.Add(route);
        }
        if (route.RevokedAt is not null)
        {
            var stop = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == operationId, ct);
            if (!allowRevive || fresh.QuiescedGeneration is not null || stop.Kind != "stop" ||
                stop.Generation != fresh.Generation || stop.Step is not ("wait-for-save" or "wait-for-tasks"))
                throw new InvalidOperationException("A fenced workspace route cannot be revived by monitoring.");
            route.RevokedAt = null;
        }
        route.LoopbackPort = port; route.T3CredentialReference = credentialRef;
        route.ExpiresAt = Now.AddSeconds(90) < expires ? Now.AddSeconds(90) : expires;
        var previewRoutes = await db.DevelopmentWorkspaceGatewayRoutes
            .Where(x => x.VmId == vm.Id && x.Generation == vm.Generation && x.Service != "t3").ToListAsync(ct);
        foreach (var registered in previewRoutes.Where(x => !previews.ContainsKey(x.Service)))
            registered.RevokedAt = Now;
        foreach (var (service, previewPort) in previews)
        {
            var preview = previewRoutes.SingleOrDefault(x => x.Service == service);
            if (preview is null)
            {
                preview = new() { Id = ids.NewId("workspaceroute"), OrganizationId = workspace.OrganizationId,
                    WorkspaceId = workspace.Id, VmId = vm.Id, Generation = vm.Generation,
                    Service = service, RegisteredAt = Now };
                db.DevelopmentWorkspaceGatewayRoutes.Add(preview);
            }
            preview.LoopbackPort = previewPort;
            preview.T3CredentialReference = service == "connect" ? connectCredentialRef : null;
            preview.ExpiresAt = route.ExpiresAt; preview.RevokedAt = null;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await RecordResourceHistoryAsync(history, ct);
    }

    // Runs only after the heartbeat commit: history is best-effort and must never affect status or route publication.
    private async Task RecordResourceHistoryAsync(DevelopmentWorkspaceVmResourceSample? sample, CancellationToken ct)
    {
        if (sample is null) return;
        try { await lifecycle.RecordResourceSampleAsync(sample, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("Dev environment resource history write failed ({ErrorType}); latest status is unaffected.", error.GetType().Name);
        }
    }

    private async Task<Dictionary<string, int>> ProbePreviewsAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        var healthy = new Dictionary<string, int>();
        foreach (var (service, registration) in options.Value.PreviewServices)
        {
            if (service == "connect")
            {
                if (registration.Port != 774 || registration.SupervisedUnit is not null)
                    throw new InvalidOperationException("Connect is reserved for the managed primary enrollment service.");
                if (vm.Kind != DevelopmentWorkspaceVmKinds.Primary || vm.DesiredState != "running") continue;
                // HTTP readiness cannot attest who owns this privileged credential receiver.
                if (!await runtime.CheckConnectAdmissionAsync(Target(vm), ct)) continue;
            }
            if (service == "t3" || !System.Text.RegularExpressions.Regex.IsMatch(service, "^[a-z][a-z0-9-]{0,19}$") ||
                service != "connect" && (registration.Port is < 1024 or > 65535 or 3773))
                throw new InvalidOperationException("Preview routes require explicit named, unprivileged services separate from T3.");
            try
            {
                var port = await tunnels.EnsureAsync(Target(vm), ct, registration.Port, service);
                using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
                using var probe = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                using var response = await probe.GetAsync($"http://{options.Value.RelayBindAddress}:{port}/",
                    HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 200 and < 400) healthy.Add(service, port);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // An unavailable preview must not keep an old route alive or hide a usable T3 workspace.
            }
        }
        return healthy;
    }

    private HttpClient T3Client(int port, string credential)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"http://{options.Value.RelayBindAddress}:{port}"), Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        client.DefaultRequestHeaders.Add("x-t3-orchestration-protocol", "2");
        return client;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        using var value = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return value.RootElement.Clone();
    }

    private async Task VerifyWebSocketAsync(HttpClient client, int port, CancellationToken ct)
    {
        using var response = await client.PostAsync("/api/auth/websocket-ticket", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var ticket = document.RootElement.GetProperty("ticket").GetString();
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 8192) throw new InvalidOperationException("T3 did not issue a bounded WebSocket ticket.");
        using var socket = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(new Uri($"ws://{options.Value.RelayBindAddress}:{port}/ws?wsTicket={Uri.EscapeDataString(ticket)}&orchestrationProtocol=2"), timeout.Token);
        if (socket.State != WebSocketState.Open) throw new InvalidOperationException("T3 WebSocket readiness was not established.");
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
    }

    private async Task<bool> IsRuntimeIdleAsync(DevelopmentWorkspaceVm vm, string operationId, CancellationToken ct)
    {
        var status = Result(await runtime.CallAsync(Target(vm), operationId, "status", new { includeWorkInventory = true }, ct));
        if (status.GetProperty("phase").GetString() != "Ready" ||
            status.GetProperty("tasks").EnumerateObject().Any(task => task.Value.GetProperty("state").GetString() != "Completed") ||
            !status.TryGetProperty("workInventory", out var inventory) ||
            inventory.GetProperty("state").GetString() != "IdleCandidate")
            return false;
        var port = await tunnels.EnsureAsync(Target(vm), ct);
        using var client = T3Client(port, await SecretAsync(vm.RuntimeCredentialRef!, ct));
        return T3WorkspaceEvidence.IsIdle(await GetJsonAsync(client, "/api/orchestration/shell", ct));
    }

    public async Task MonitorNextAsync(CancellationToken ct)
    {
        await using (var admission = await db.Database.BeginTransactionAsync(ct))
        {
            if (!(await maintenance.CheckAsync(db, requireOpen: true, ct)).IsAdmitted) return;
            await admission.CommitAsync(ct);
        }
        var vm = await db.DevelopmentWorkspaceVms.AsNoTracking()
            .Where(x => x.Phase == "ready" && x.DesiredState == "running")
            .OrderBy(x => x.LastHeartbeatAt).FirstOrDefaultAsync(ct);
        if (vm is not null) await MonitorVmAsync(vm, ct);
    }

    internal async Task MonitorVmAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        var workspace = await db.DevelopmentWorkspaces.AsNoTracking().SingleAsync(x => x.Id == vm.WorkspaceId, ct);
        try
        {
            var operationId = vm.OperationId ?? vm.AllocationOperationId
                ?? throw new InvalidOperationException("Ready VM has no runtime operation identity.");
            await RefreshRuntimeAsync(workspace, vm, operationId, ct);
            try { await RefreshRepositoryCredentialAsync(vm, ct); }
            catch (DevelopmentWorkspaceException)
            {
                // Keep recovery access while expired/revoked Git authority blocks ordinary Git deletion.
            }
            db.ChangeTracker.Clear();
            vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync(x => x.Id == vm.Id, ct);
            if (vm.Phase != "ready" || vm.DesiredState != "running") return;
            if (!await IsRuntimeIdleAsync(vm, vm.OperationId ?? vm.AllocationOperationId ?? operationId, ct))
            {
                await lifecycle.ActivityAsync(vm.WorkspaceId, vm.OrganizationId, vm.Id, vm.Generation, ct);
                return;
            }
            if (vm.ShutdownDeadline is null && await lifecycle.IsIdleAsync(vm, ct))
                await lifecycle.ScheduleIdleStopAsync(vm.WorkspaceId, vm.OrganizationId, vm.Id, vm.Generation,
                    Now.AddSeconds(Math.Clamp(options.Value.IdleShutdownDelaySeconds, 30, 600)), ct);
        }
        catch (DevelopmentWorkspaceException) { }
        catch (Exception)
        {
            // Missing evidence never advances activity or authorizes destruction. Receipt/heartbeat TTLs fail closed.
        }
    }
}
