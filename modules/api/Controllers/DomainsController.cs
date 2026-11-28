using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Selfrun.Common.Data;
using Selfrun.Common.Contracts;
using Selfrun.Common.Models;
using Selfrun.Api.Auth;
using Selfrun.Api.Services;
using Selfrun.Common.Services;
using static Selfrun.Api.Controllers.EndpointResults;

namespace Selfrun.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class DomainsController : ControllerBase
{
    [HttpGet("services")]
    public async Task<IResult> GetServices(
        [FromQuery] string appId,
        [FromServices] SelfrunDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] DeploymentServiceSetService serviceSets,
        CancellationToken cancellationToken,
        [FromQuery] string? applicationId = null,
        [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), targetAppId, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var services = await serviceSets.BuildActiveServicesAsync(db, targetAppId, cancellationToken);
        var bindings = await db.Domains.AsNoTracking()
            .Where(x => x.AppId == targetAppId)
            .ToListAsync(cancellationToken);
        var latestStatus = await LatestStatusByServiceAsync(db, targetAppId, cancellationToken);

        return Results.Ok(services.Select(service =>
            new AppServiceInventoryResponse(
                service.Name,
                service.Image ?? string.Empty,
                service.Port,
                service.Public,
                service.Path,
                service.HealthPath,
                latestStatus.Services.TryGetValue(service.Name, out var status) ? status : null,
                bindings
                    .Where(x => x.ServiceName == service.Name)
                    .OrderBy(x => x.Hostname, StringComparer.Ordinal)
                    .Select(DomainBindingService.ToResponse)
                    .ToArray(),
                service.Sha256,
                latestStatus.ObservedAt, service.Storage)));
    }

    [HttpGet]
    public async Task<IResult> ListDomains(
        [FromQuery] string appId,
        [FromServices] SelfrunDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        CancellationToken cancellationToken,
        [FromQuery] string? applicationId = null,
        [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), targetAppId, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var bindings = await db.Domains.AsNoTracking()
            .Where(x => x.AppId == targetAppId)
            .OrderBy(x => x.ServiceName)
            .ThenBy(x => x.Hostname)
            .ToListAsync(cancellationToken);
        return Results.Ok(bindings.Select(DomainBindingService.ToResponse));
    }

    [HttpGet("availability")]
    public async Task<IResult> CheckDomainAvailability(
        [FromQuery] string appId,
        [FromQuery] string label,
        [FromServices] SelfrunDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] DomainBindingService domains,
        CancellationToken cancellationToken,
        [FromQuery] string? applicationId = null,
        [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), targetAppId, OrganizationAction.ReadApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var result = await domains.CheckSubdomainAvailabilityAsync(label, cancellationToken);
        return Results.Ok(new DomainAvailabilityResponse(result.Label, result.Hostname, result.Available, result.Message));
    }

    [HttpPost]
    public async Task<IResult> CreateDomain(
        [FromQuery] string appId,
        [FromBody] CreateDomainBindingRequest request,
        [FromServices] SelfrunDbContext db,
        [FromServices] AuditEventService audit,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] DeploymentServiceSetService serviceSets,
        [FromServices] DomainBindingService domains,
        [FromServices] CustomDomainRegistrationService registrations,
        [FromServices] EnvironmentSleepPolicyService sleepPolicy,
        [FromServices] DeploymentPreparationService preparations,
        CancellationToken cancellationToken,
        [FromQuery] string? applicationId = null,
        [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        var app = await db.Apps.Include(x => x.Organization).SingleOrDefaultAsync(x => x.Id == targetAppId, cancellationToken);
        if (app is null)
        {
            return EndpointResults.NotFound("app_not_found", "App was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), app.Id, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }
        if (request.Hostname is not null)
        {
            try
            {
                await sleepPolicy.EnsureAwakeAsync(app.Id, cancellationToken);
            }
            catch (EnvironmentSleepException ex)
            {
                return EndpointResults.Problem(ex.Code, ex.Message, ex.StatusCode);
            }
        }

        var activeServices = await serviceSets.BuildActiveServicesAsync(db, targetAppId, cancellationToken);
        if (request.Hostname is not null && request.Label is not null)
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", "Provide hostname or label, not both.", [])));
        }
        if (request.Hostname is not null)
        {
            var hostname = DomainBindingService.NormalizeCustomHostname(request.Hostname);
            var registered = hostname is null ? null : await db.CustomDomainRegistrations
                .Where(x => x.AppId == app.Id && x.OrganizationId == app.OrganizationId && x.Status == DomainBindingService.DnsVerified &&
                    (x.Hostname == hostname || hostname.EndsWith("." + x.Hostname)))
                .OrderByDescending(x => x.Hostname.Length).FirstOrDefaultAsync(cancellationToken);
            if (registered is not null)
            {
                await registrations.VerifyAsync(registered, cancellationToken);
            }
        }
        var result = request.Hostname is not null
            ? await domains.CreateCustomBindingAsync(app, request.ServiceName, request.Hostname, activeServices, cancellationToken)
            : await domains.CreateSubdomainBindingAsync(app, request.ServiceName, request.Label, activeServices, cancellationToken);
        if (!result.Succeeded)
        {
            if (result.IsConflict)
            {
                return EndpointResults.Conflict(result.ErrorCode!, result.ErrorMessage!);
            }

            return Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", "Domain binding request is invalid.", [
                new(result.ErrorField!, result.ErrorMessage!)
            ])));
        }

        if (result.Binding!.Kind == DomainBindingService.KindCustomDomain)
        {
            try
            {
                await preparations.LockedAsync(app.Id, async () =>
                {
                    await db.Entry(app).ReloadAsync(cancellationToken);
                    await sleepPolicy.EnsureAwakeAsync(app.Id, cancellationToken);
                    await sleepPolicy.EnsureRetainedAddressForCustomDomainAsync(app, cancellationToken);
                    var retained = await db.EnvironmentRetainedAddresses.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.EnvironmentId == app.Id &&
                            x.State != EnvironmentRetainedAddressStates.Released, cancellationToken);
                    if (retained is not null)
                        result.Binding.TargetIp = retained.Ipv4;
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                }, cancellationToken);
            }
            catch (EnvironmentSleepException ex)
            {
                return EndpointResults.Problem(ex.Code, ex.Message, ex.StatusCode);
            }
            catch (DbUpdateException)
            {
                return EndpointResults.Conflict("domain_hostname_taken", "Domain is already assigned.");
            }
        }
        else
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return EndpointResults.Conflict("domain_hostname_taken", "Domain is already assigned.");
            }
        }
        await audit.AppendAsync(app.OrganizationId, app.Id, AuditEventService.ActorFor(principal.ToActorIdentity()), "domain.created", result.Binding!.Id, cancellationToken);
        return Results.Created($"/api/domains/{result.Binding!.Id}?appId={Uri.EscapeDataString(targetAppId)}", DomainBindingService.ToResponse(result.Binding));
    }

    [HttpPost("{domainId}/verify")]
    public async Task<IResult> VerifyDomain(
        [FromQuery] string appId, string domainId,
        [FromServices] SelfrunDbContext db,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] CustomDomainVerificationService verification,
        [FromServices] EnvironmentSleepPolicyService sleepPolicy,
        CancellationToken cancellationToken,
        [FromQuery] string? applicationId = null,
        [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), targetAppId, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }
        var binding = await db.Domains.SingleOrDefaultAsync(x => x.AppId == targetAppId && x.Id == domainId, cancellationToken);
        if (binding is null)
        {
            return EndpointResults.NotFound("domain_not_found", "Domain binding was not found.");
        }
        try
        {
            await sleepPolicy.EnsureAwakeAsync(targetAppId, cancellationToken);
        }
        catch (EnvironmentSleepException ex)
        {
            return EndpointResults.Problem(ex.Code, ex.Message, ex.StatusCode);
        }
        await verification.VerifyAsync(binding, cancellationToken);
        return Results.Ok(DomainBindingService.ToResponse(binding));
    }

    [HttpPatch("{domainId}")]
    public async Task<IResult> UpdateDomain(
        [FromQuery] string appId,
        [FromRoute] string domainId,
        [FromBody] UpdateDomainBindingRequest request,
        [FromServices] SelfrunDbContext db,
        [FromServices] AuditEventService audit,
        [FromServices] ControlPlaneAuthorization authorization,
        [FromServices] TimeProvider timeProvider,
        [FromServices] EnvironmentSleepPolicyService sleepPolicy,
        CancellationToken cancellationToken,
        [FromQuery] string? applicationId = null,
        [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        var binding = await db.Domains.SingleOrDefaultAsync(x => x.AppId == targetAppId && x.Id == domainId, cancellationToken);
        if (binding is null)
        {
            return EndpointResults.NotFound("domain_not_found", "Domain binding was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), binding.AppId, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }
        if (request.Disabled is false)
        {
            try
            {
                await sleepPolicy.EnsureAwakeAsync(binding.AppId, cancellationToken);
            }
            catch (EnvironmentSleepException ex)
            {
                return EndpointResults.Problem(ex.Code, ex.Message, ex.StatusCode);
            }
        }

        if (request.Disabled is not null)
        {
            binding.Status = request.Disabled.Value ? DomainBindingService.StatusDisabled
                : binding.Kind == DomainBindingService.KindCustomDomain ? DomainBindingService.StatusPendingDns : DomainBindingService.StatusPendingApply;
            if (!request.Disabled.Value && binding.Kind == DomainBindingService.KindCustomDomain)
            {
                binding.DnsStatus = DomainBindingService.DnsPending;
                binding.LastCheckedAt = null;
            }
            binding.ApplyStatus = DomainBindingService.ApplyPending;
            binding.UpdatedAt = timeProvider.GetUtcNow();
        }

        if (request.IsPrimary is true)
        {
            var siblings = await db.Domains
                .Where(x => x.AppId == targetAppId && x.ServiceName == binding.ServiceName)
                .ToListAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            foreach (var sibling in siblings)
            {
                sibling.IsPrimary = sibling.Id == binding.Id;
                sibling.UpdatedAt = now;
            }
        }

        await audit.AppendAsync(binding.OrganizationId, binding.AppId, AuditEventService.ActorFor(principal.ToActorIdentity()), "domain.updated", binding.Id, cancellationToken);
        return Results.Ok(DomainBindingService.ToResponse(binding));
    }

    [HttpDelete("{domainId}")]
    public async Task<IResult> DeleteDomain(
        [FromQuery] string appId,
        [FromRoute] string domainId,
        [FromServices] SelfrunDbContext db,
        [FromServices] AuditEventService audit,
        [FromServices] ControlPlaneAuthorization authorization,
        CancellationToken cancellationToken,
        [FromQuery] string? applicationId = null,
        [FromQuery] string? environmentId = null)
    {
        var ownershipError = await VerifyTargetOwnershipAsync(db, appId, applicationId, environmentId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }
        var targetAppId = !string.IsNullOrWhiteSpace(environmentId) ? environmentId : appId;
        var principal = RequirePrincipal(HttpContext);
        var binding = await db.Domains.SingleOrDefaultAsync(x => x.AppId == targetAppId && x.Id == domainId, cancellationToken);
        if (binding is null)
        {
            return EndpointResults.NotFound("domain_not_found", "Domain binding was not found.");
        }

        if (!await authorization.CanAccessAppAsync(principal.ToActorIdentity(), binding.AppId, OrganizationAction.WriteApps, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        db.Domains.Remove(binding);
        await audit.AppendAsync(binding.OrganizationId, binding.AppId, AuditEventService.ActorFor(principal.ToActorIdentity()), "domain.deleted", binding.Id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<(Dictionary<string, RuntimeServiceStatusDto> Services, DateTimeOffset? ObservedAt)> LatestStatusByServiceAsync(SelfrunDbContext db, string appId, CancellationToken cancellationToken)
    {
        var snapshot = await db.RuntimeStatusSnapshots.AsNoTracking()
            .Where(x => x.AppId == appId)
            .OrderByDescending(x => x.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null)
        {
            return (new Dictionary<string, RuntimeServiceStatusDto>(StringComparer.Ordinal), null);
        }

        var status = JsonSerializer.Deserialize<StoredRuntimeStatus>(snapshot.StatusJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (status?.Services.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal)
            ?? new Dictionary<string, RuntimeServiceStatusDto>(StringComparer.Ordinal), snapshot.ObservedAt);
    }
}
