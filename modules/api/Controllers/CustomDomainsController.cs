using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1/custom-domains")]
public sealed class CustomDomainsController(
    MinicloudDbContext db, ControlPlaneAuthorization authorization, CustomDomainRegistrationService registrations) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List([FromQuery] string appId, CancellationToken ct)
    {
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), appId, OrganizationAction.ReadApps, ct))
        {
            return EndpointResults.Forbidden();
        }
        var rows = await db.CustomDomainRegistrations.AsNoTracking().Where(x => x.AppId == appId).OrderBy(x => x.Hostname).ToListAsync(ct);
        return Results.Ok(rows.Select(CustomDomainRegistrationService.ToResponse));
    }

    [HttpPost]
    public async Task<IResult> Register([FromQuery] string appId, [FromBody] RegisterCustomDomainRequest request, CancellationToken ct)
    {
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), appId, OrganizationAction.WriteApps, ct))
        {
            return EndpointResults.Forbidden();
        }
        try
        {
            var row = await registrations.RegisterAsync((await db.Apps.SingleAsync(x => x.Id == appId, ct)).OrganizationId, appId, request.Hostname, ct, AuditEventService.ActorFor(RequirePrincipal(HttpContext).ToActorIdentity()));
            return Results.Ok(CustomDomainRegistrationService.ToResponse(row));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", ex.Message, [])));
        }
        catch (DomainBindingConflictException ex)
        {
            return EndpointResults.Conflict(ex.Code, ex.Message);
        }
        catch (DbUpdateException)
        {
            return EndpointResults.Conflict("domain_hostname_taken", "Domain is already registered.");
        }
    }

    [HttpPost("{id}/verify")]
    public async Task<IResult> Verify([FromQuery] string appId, string id, CancellationToken ct)
    {
        if (!await authorization.CanAccessAppAsync(RequirePrincipal(HttpContext).ToActorIdentity(), appId, OrganizationAction.WriteApps, ct))
        {
            return EndpointResults.Forbidden();
        }
        var row = await db.CustomDomainRegistrations.SingleOrDefaultAsync(x => x.Id == id && x.AppId == appId, ct);
        if (row is null)
        {
            return EndpointResults.NotFound("domain_not_found", "Domain was not found.");
        }
        await registrations.VerifyAsync(row, ct, AuditEventService.ActorFor(RequirePrincipal(HttpContext).ToActorIdentity()));
        return Results.Ok(CustomDomainRegistrationService.ToResponse(row));
    }
    [HttpDelete("{id}")]
    public async Task<IResult> Remove([FromQuery] string appId, string id, CancellationToken ct)
    {
        var actor = RequirePrincipal(HttpContext).ToActorIdentity();
        if (!await authorization.CanAccessAppAsync(actor, appId, OrganizationAction.WriteApps, ct))
        {
            return EndpointResults.Forbidden();
        }
        try
        {
            return await registrations.RemoveAsync(appId, id, AuditEventService.ActorFor(actor), ct)
                ? Results.NoContent()
                : EndpointResults.NotFound("domain_not_found", "Domain was not found.");
        }
        catch (DomainBindingConflictException ex)
        {
            return EndpointResults.Conflict(ex.Code, ex.Message);
        }
    }

}
