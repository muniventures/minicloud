using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Minicloud.Common.Contracts;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1/apps/{appId}/telemetry")]
public sealed class TelemetryController : ControllerBase
{
    private static IResult HiddenNotFound() =>
        EndpointResults.NotFound("telemetry_not_found", "The requested resource was not found.");

    [HttpGet]
    public async Task<IResult> GetSettings(
        [FromRoute] string appId,
        [FromServices] TelemetryService telemetry,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var settings = await telemetry.GetSettingsAsync(principal, appId, cancellationToken);
        return settings is null ? HiddenNotFound() : Results.Ok(settings);
    }

    [HttpPut]
    public async Task<IResult> SetEnabled(
        [FromRoute] string appId,
        [FromBody] UpdateTelemetrySettingsRequest request,
        [FromServices] TelemetryService telemetry,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var result = await telemetry.SetEnabledAsync(principal, appId, request.Enabled, cancellationToken);
        return result is null ? HiddenNotFound() : Results.Ok(result.Settings);
    }

    [HttpPost("credentials")]
    public async Task<IResult> IssueCredential(
        [FromRoute] string appId,
        [FromBody] IssueTelemetryCredentialRequest request,
        [FromServices] TelemetryService telemetry,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var result = await telemetry.IssueCredentialAsync(
            principal, appId, request.ServiceName, request.Name, request.ExpiresAt, cancellationToken);
        if (result is null)
        {
            return HiddenNotFound();
        }

        if (result.Error is not null)
        {
            return result.Error.Code == "credential_limit_exceeded"
                ? EndpointResults.Conflict(result.Error.Code, result.Error.Message)
                : Results.BadRequest(new ErrorResponse(new ErrorBody(result.Error.Code, result.Error.Message, [])));
        }

        var response = result.Success!.Response;
        return Results.Created($"/v1/apps/{appId}/telemetry/credentials/{response.Id}", response);
    }

    [HttpGet("credentials")]
    public async Task<IResult> ListCredentials(
        [FromRoute] string appId,
        [FromServices] TelemetryService telemetry,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var credentials = await telemetry.ListCredentialsAsync(principal, appId, cancellationToken);
        return credentials is null ? HiddenNotFound() : Results.Ok(credentials);
    }

    [HttpDelete("credentials/{credentialId}")]
    public async Task<IResult> RevokeCredential(
        [FromRoute] string appId,
        [FromRoute] string credentialId,
        [FromServices] TelemetryService telemetry,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var revoked = await telemetry.RevokeCredentialAsync(principal, appId, credentialId, cancellationToken);
        return revoked is null ? HiddenNotFound() : Results.NoContent();
    }

    [HttpPost("read-token")]
    public async Task<IResult> IssueReadToken(
        [FromRoute] string appId,
        [FromServices] TelemetryService telemetry,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var token = await telemetry.IssueReadTokenAsync(principal, appId, cancellationToken);
        return token is null ? HiddenNotFound() : Results.Ok(token);
    }
}
