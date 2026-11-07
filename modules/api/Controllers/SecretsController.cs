using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1/[controller]")]
public sealed class SecretsController : ControllerBase
{
    [HttpGet("services")]
    public async Task<IResult> ListSecretServices(
        [FromQuery] string appId,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.ReadSecrets, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        return Results.Ok(await secrets.ListServicesAsync(appId, cancellationToken));
    }

    [HttpPost("services")]
    public async Task<IResult> CreateSecretService(
        [FromQuery] string appId,
        [FromBody] CreateAppServiceSecretServiceRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        [FromServices] ILogger<SecretsController> logger,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), app.Id, OrganizationAction.WriteSecrets, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var result = await secrets.CreateServiceAsync(app, request, cancellationToken);
        if (!result.Succeeded)
        {
            return EndpointResults.ValidationProblem("Service request is invalid.", result.Validation!);
        }

        return Results.Created($"/v1/secrets/services/{result.Service!.Id}?appId={Uri.EscapeDataString(appId)}", result.Service);
    }

    [HttpPut("services/{serviceId}")]
    public async Task<IResult> RenameSecretService(
        [FromQuery] string appId,
        [FromRoute] string serviceId,
        [FromBody] UpdateAppServiceSecretServiceRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var service = await db.AppServiceSecretServices.SingleOrDefaultAsync(x => x.AppId == appId && x.Id == serviceId && x.DeletedAt == null, cancellationToken);
        if (service is null)
        {
            return EndpointResults.NotFound("service_not_found", "Service was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), service.AppId, OrganizationAction.WriteSecrets, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var result = await secrets.RenameServiceAsync(service, request, cancellationToken);
        if (!result.Succeeded)
        {
            return EndpointResults.ValidationProblem("Service request is invalid.", result.Validation!);
        }

        return Results.Ok(result.Service);
    }

    [HttpDelete("services/{serviceId}")]
    public async Task<IResult> DeleteSecretService(
        [FromQuery] string appId,
        [FromRoute] string serviceId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var service = await db.AppServiceSecretServices.SingleOrDefaultAsync(x => x.AppId == appId && x.Id == serviceId && x.DeletedAt == null, cancellationToken);
        if (service is null)
        {
            return EndpointResults.NotFound("service_not_found", "Service was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), service.AppId, OrganizationAction.WriteSecrets, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        await secrets.DeleteServiceAsync(service, cancellationToken);
        return Results.NoContent();
    }

    [HttpGet]
    public async Task<IResult> ListSecrets(
        [FromQuery] string appId,
        [FromQuery] string? service,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), appId, OrganizationAction.ReadSecrets, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        return Results.Ok(await secrets.ListAsync(appId, service, cancellationToken));
    }

    [HttpPost]
    public async Task<IResult> SetSecret(
        [FromQuery] string appId,
        [FromBody] SetAppServiceSecretRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        [FromServices] ILogger<SecretsController> logger,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), app.Id, OrganizationAction.WriteSecrets, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        try
        {
            var result = await secrets.SetAsync(app, request, cancellationToken);
            if (!result.Succeeded)
            {
                return EndpointResults.ValidationProblem("Secret request is invalid.", result.Validation!);
            }

            return Results.Created($"/v1/secrets/{result.Secret!.Id}?appId={Uri.EscapeDataString(appId)}", result.Secret);
        }
        catch (SecretStoreUnavailableException ex)
        {
            logger.LogWarning(ex, "Secret store unavailable while setting secret {SecretName} for app {AppId} service {ServiceName}.", request.Name, appId, request.ServiceName);
            return EndpointResults.Problem("secret_store_unavailable", $"Minicloud could not save this secret right now: {ex.Message}", StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPut("{secretId}")]
    public async Task<IResult> UpdateSecret(
        [FromQuery] string appId,
        [FromRoute] string secretId,
        [FromBody] UpdateAppServiceSecretRequest request,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        [FromServices] ILogger<SecretsController> logger,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var secret = await db.AppServiceSecrets.SingleOrDefaultAsync(x => x.AppId == appId && x.Id == secretId && x.DeletedAt == null, cancellationToken);
        if (secret is null)
        {
            return EndpointResults.NotFound("secret_not_found", "Secret was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), secret.AppId, OrganizationAction.WriteSecrets, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        try
        {
            var result = await secrets.UpdateAsync(secret, request, cancellationToken);
            if (!result.Succeeded)
            {
                return EndpointResults.ValidationProblem("Secret request is invalid.", result.Validation!);
            }

            return Results.Ok(result.Secret);
        }
        catch (SecretStoreUnavailableException ex)
        {
            logger.LogWarning(ex, "Secret store unavailable while updating secret {SecretId} for app {AppId}.", secretId, appId);
            return EndpointResults.Problem("secret_store_unavailable", $"Minicloud could not save this secret right now: {ex.Message}", StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpDelete("{secretId}")]
    public async Task<IResult> DeleteSecret(
        [FromQuery] string appId,
        [FromRoute] string secretId,
        [FromServices] MinicloudDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] AppServiceSecretService secrets,
        [FromServices] ILogger<SecretsController> logger,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var secret = await db.AppServiceSecrets.SingleOrDefaultAsync(x => x.AppId == appId && x.Id == secretId && x.DeletedAt == null, cancellationToken);
        if (secret is null)
        {
            return EndpointResults.NotFound("secret_not_found", "Secret was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), secret.AppId, OrganizationAction.WriteSecrets, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        try
        {
            await secrets.DeleteAsync(secret, cancellationToken);
            return Results.NoContent();
        }
        catch (SecretStoreUnavailableException ex)
        {
            logger.LogWarning(ex, "Secret store unavailable while deleting secret {SecretId} for app {AppId}.", secretId, appId);
            return EndpointResults.Problem("secret_store_unavailable", $"Minicloud could not remove this secret right now: {ex.Message}", StatusCodes.Status503ServiceUnavailable);
        }
    }
}
