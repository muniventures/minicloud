using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/development-workspaces")]
public sealed class DevelopmentWorkspacesController(
    ControlPlaneAuthorization authorization, DevelopmentWorkspaceService workspaces) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List([FromQuery] string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadApps, ct)) return EndpointResults.Forbidden();
        return Results.Ok((await workspaces.ListAsync(organizationId, ct)).Select(workspaces.ToResponse));
    }

    [HttpGet("selections")]
    public async Task<IResult> Selections([FromQuery] string organizationId, [FromQuery] string? workspaceId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadApps, ct)) return EndpointResults.Forbidden();
        Response.Headers.CacheControl = "no-store";
        try { return Results.Ok(await workspaces.GetSelectionsAsync(organizationId, workspaceId, ct)); }
        catch (DevelopmentWorkspaceException error) { return Error(error); }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            return Results.Json(new ErrorResponse(new ErrorBody("workspace_selections_unavailable",
                "Workspace choices could not be loaded. Try again later.", [])), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpGet("{id}")]
    public async Task<IResult> Get(string id, [FromQuery] string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadApps, ct)) return EndpointResults.Forbidden();
        var workspace = await workspaces.GetAsync(id, organizationId, ct);
        return workspace is null ? EndpointResults.NotFound("workspace_not_found", "Workspace was not found.")
            : Results.Ok(await workspaces.ToDetailResponseAsync(workspace, ct));
    }

    [HttpGet("{id}/vms")]
    public async Task<IResult> ListVms(string id, [FromQuery] string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadApps, ct)) return EndpointResults.Forbidden();
        var workspace = await workspaces.GetAsync(id, organizationId, ct);
        return workspace is null ? EndpointResults.NotFound("workspace_not_found", "Workspace was not found.")
            : Results.Ok(workspace.Vms.Select(workspaces.ToVmResponse));
    }

    // Reads stored last-hour history only; never contacts the VM.
    [HttpGet("{id}/vms/{vmId}/resource-history")]
    public async Task<IResult> ResourceHistory(string id, string vmId, [FromQuery] string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadApps, ct)) return EndpointResults.Forbidden();
        Response.Headers.CacheControl = "no-store";
        try { return Results.Ok(await workspaces.GetResourceHistoryAsync(id, organizationId, vmId, ct)); }
        catch (DevelopmentWorkspaceException error) { return Error(error); }
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] CreateDevelopmentWorkspaceRequest request, CancellationToken ct)
    {
        if (!await CanAsync(request.OrganizationId, OrganizationAction.WriteApps, ct)) return EndpointResults.Forbidden();
        try
        {
            var workspace = await workspaces.CreateAsync(request, ActorId(), ct);
            return Results.Created($"/api/development-workspaces/{workspace.Id}?organizationId={Uri.EscapeDataString(workspace.OrganizationId)}",
                workspaces.ToResponse(workspace));
        }
        catch (DevelopmentWorkspaceException error) { return Error(error); }
    }

    [HttpPut("{id}/idle-timeout")]
    public Task<IResult> UpdateIdleTimeout(string id, [FromQuery] string organizationId,
        [FromBody] UpdateDevelopmentWorkspaceIdleTimeoutRequest request, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.UpdateIdleTimeoutAsync(id, organizationId, request.IdleTimeoutMinutes, request.KeepRunning, ct), ct);

    [HttpPost("{id}/start")]
    public Task<IResult> Start(string id, [FromQuery] string organizationId, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.StartAsync(id, organizationId, ct), ct);

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id, [FromQuery] string organizationId,
        [FromQuery] bool confirmPermanentDeletion, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.WriteApps, ct)) return EndpointResults.Forbidden();
        try
        {
            var response = workspaces.ToResponse(await workspaces.DeleteAsync(id, organizationId, confirmPermanentDeletion, ActorId(), ct));
            return response.Phase == "deleted" ? Results.Ok(response)
                : Results.Accepted($"/api/development-workspaces/{Uri.EscapeDataString(id)}?organizationId={Uri.EscapeDataString(organizationId)}", response);
        }
        catch (DevelopmentWorkspaceException error) { return Error(error); }
    }

    [HttpPost("{id}/stop")]
    public Task<IResult> Stop(string id, [FromQuery] string organizationId, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.StopAsync(id, organizationId, ct), ct);

    [HttpPost("{id}/stop-all")]
    public Task<IResult> StopAll(string id, [FromQuery] string organizationId, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.StopAllAsync(id, organizationId, ct), ct);

    [HttpPost("{id}/cancel-idle-stop")]
    public Task<IResult> CancelIdleStop(string id, [FromQuery] string organizationId, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.CancelIdleStopAsync(id, organizationId, ct), ct);

    [HttpPost("{id}/resize")]
    public Task<IResult> Resize(string id, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceResizeRequest request, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.ResizeAsync(id, organizationId, null, request, ct), ct);

    [HttpPost("{id}/vms/ephemeral")]
    public Task<IResult> LaunchEphemeral(string id, [FromQuery] string organizationId,
        [FromBody] LaunchEphemeralDevelopmentWorkspaceVmRequest request, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.LaunchEphemeralAsync(id, organizationId, request, ActorId(), ct), ct);

    [HttpPost("{id}/vms/{vmId}/start")]
    public Task<IResult> StartVm(string id, string vmId, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceGenerationRequest request, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.StartVmAsync(id, organizationId, vmId, request.Generation, ct), ct);

    [HttpPost("{id}/vms/{vmId}/stop")]
    public Task<IResult> StopVm(string id, string vmId, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceGenerationRequest request, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.StopVmAsync(id, organizationId, vmId, request.Generation, ct), ct);

    [HttpPost("{id}/vms/{vmId}/cancel-idle-stop")]
    public Task<IResult> CancelIdleStopVm(string id, string vmId, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceGenerationRequest request, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.CancelIdleStopVmAsync(id, organizationId, vmId, request.Generation, ct), ct);

    [HttpPost("{id}/vms/{vmId}/discard")]
    public Task<IResult> Discard(string id, string vmId, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceDiscardRequest request, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.DiscardAsync(id, organizationId, vmId, request, ActorId(), ct), ct);

    [HttpPost("{id}/vms/{vmId}/resize")]
    public Task<IResult> ResizeVm(string id, string vmId, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceResizeRequest request, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.ResizeAsync(id, organizationId, vmId, request, ct), ct);

    [HttpPost("{id}/vms/{vmId}/activity")]
    public Task<IResult> Activity(string id, string vmId, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceGenerationRequest request, CancellationToken ct) =>
        MutateAsync(organizationId, () => workspaces.ActivityAsync(id, organizationId, vmId, request.Generation, ct), ct);

    [HttpPut("{id}/vms/{vmId}/tasks/{taskId}/lease")]
    public Task<IResult> RenewTaskLease(string id, string vmId, string taskId, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceGenerationRequest request, CancellationToken ct) =>
        TaskMutationAsync(organizationId, () => workspaces.RenewTaskLeaseAsync(id, organizationId, vmId, request.Generation, taskId, ct), ct);

    [HttpPost("{id}/vms/{vmId}/tasks/{taskId}/complete")]
    public Task<IResult> CompleteTask(string id, string vmId, string taskId, [FromQuery] string organizationId,
        [FromBody] CompleteDevelopmentWorkspaceTaskRequest request, CancellationToken ct) =>
        TaskMutationAsync(organizationId, () => workspaces.CompleteTaskAsync(id, organizationId, vmId, request.Generation, taskId, request.Cancelled, ct), ct);

    // Folder sizes and deletion expose and change dev environment contents, so every call requires write access.
    [HttpPost("{id}/vms/{vmId}/disk-usage/scans")]
    public Task<IResult> StartDiskUsageScan(string id, string vmId, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceDiskUsageScanRequest request, CancellationToken ct) =>
        FilesystemJobAsync(organizationId, () => workspaces.StartDiskUsageScanAsync(id, organizationId, vmId, request, ActorId(), ct),
            StatusCodes.Status202Accepted, ct);

    [HttpPost("{id}/vms/{vmId}/directory-deletions")]
    public Task<IResult> StartDirectoryDeletion(string id, string vmId, [FromQuery] string organizationId,
        [FromBody] DevelopmentWorkspaceDirectoryDeletionRequest request, CancellationToken ct) =>
        FilesystemJobAsync(organizationId, () => workspaces.StartDirectoryDeletionAsync(id, organizationId, vmId, request, ActorId(), ct),
            StatusCodes.Status202Accepted, ct);

    [HttpGet("{id}/vms/{vmId}/filesystem-jobs/{jobId}")]
    public Task<IResult> GetFilesystemJob(string id, string vmId, string jobId, [FromQuery] string organizationId, CancellationToken ct) =>
        FilesystemJobAsync(organizationId, () => workspaces.GetFilesystemJobAsync(id, organizationId, vmId, jobId, ct),
            StatusCodes.Status200OK, ct);

    [HttpPost("{id}/vms/{vmId}/filesystem-jobs/{jobId}/cancel")]
    public Task<IResult> CancelFilesystemJob(string id, string vmId, string jobId, [FromQuery] string organizationId, CancellationToken ct) =>
        FilesystemJobAsync(organizationId, () => workspaces.CancelFilesystemJobAsync(id, organizationId, vmId, jobId, ct),
            StatusCodes.Status200OK, ct);

    private async Task<IResult> FilesystemJobAsync(string organizationId, Func<Task<DevelopmentWorkspaceFilesystemJob>> action,
        int status, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.WriteApps, ct)) return EndpointResults.Forbidden();
        Response.Headers.CacheControl = "no-store";
        try { return Results.Json(workspaces.ToFilesystemJobResponse(await action()), statusCode: status); }
        catch (DevelopmentWorkspaceException error) { return Error(error); }
    }

    private async Task<IResult> MutateAsync(string organizationId, Func<Task<DevelopmentWorkspace>> mutate, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.WriteApps, ct)) return EndpointResults.Forbidden();
        try { return Results.Ok(workspaces.ToResponse(await mutate())); }
        catch (DevelopmentWorkspaceException error) { return Error(error); }
    }

    private async Task<IResult> TaskMutationAsync(string organizationId, Func<Task<DevelopmentWorkspaceTaskLease>> mutate, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.WriteApps, ct)) return EndpointResults.Forbidden();
        try
        {
            var lease = await mutate();
            return Results.Ok(new DevelopmentWorkspaceTaskLeaseResponse(lease.VmId, lease.TaskId, lease.Generation, lease.Status, lease.ExpiresAt));
        }
        catch (DevelopmentWorkspaceException error) { return Error(error); }
    }

    private string ActorId()
    {
        var actor = EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity();
        return actor.UserId ?? $"api_key:{actor.ApiKeyId}";
    }

    private Task<bool> CanAsync(string organizationId, OrganizationAction action, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, action, ct);

    private static IResult Error(DevelopmentWorkspaceException error) =>
        error.Code.EndsWith("_not_found", StringComparison.Ordinal)
            ? EndpointResults.NotFound(error.Code, error.Message)
            : error.Conflict ? EndpointResults.Conflict(error.Code, error.Message)
            : Results.BadRequest(new ErrorResponse(new ErrorBody(error.Code, error.Message, [])));
}
