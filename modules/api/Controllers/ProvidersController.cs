using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Services;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("api/providers")]
public sealed class ProvidersController(MinicloudDbContext db, ControlPlaneAuthorization authorization,
    ProviderAccountService accounts, AuditEventService audit) : ControllerBase
{
    [HttpGet("/api/provider-types")]
    public IResult Types() => Results.Ok(new[]
    {
        new ProviderTypeResponse("hetzner", "Hetzner", true, "ubuntu-24.04"),
        new ProviderTypeResponse("digital-ocean", "DigitalOcean", false, "ubuntu-24-04-x64"),
        new ProviderTypeResponse("vultr", "Vultr", false, null, 2284)
    });

    [HttpGet]
    public async Task<IResult> List(string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadProviders, ct)) return EndpointResults.Forbidden();
        return Results.Ok((await db.ProviderAccounts.AsNoTracking().Include(x => x.Plans)
            .Where(x => x.OrganizationId == organizationId).OrderBy(x => x.Name).ToListAsync(ct)).Select(ToResponse));
    }

    [HttpGet("permissions")]
    public async Task<IResult> Permissions(string organizationId, CancellationToken ct) =>
        Results.Ok(new ProviderPermissionsResponse(
            await CanAsync(organizationId, OrganizationAction.ReadProviders, ct),
            await CanAsync(organizationId, OrganizationAction.ManageProviders, ct)));

    [HttpGet("{providerAccountId}/ssh-keys")]
    public async Task<IResult> SshKeys(
        [FromRoute] string providerAccountId,
        [FromQuery] string organizationId,
        [FromServices] EnvironmentSshAccessService sshAccess,
        CancellationToken ct)
    {
        try
        {
            var keys = await sshAccess.ListKeysAsync(organizationId, providerAccountId,
                EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), ct);
            return Results.Ok(keys);
        }
        catch (SshAccessException ex)
        {
            return EndpointResults.Problem(ex.Code, ex.Message, ex.StatusCode);
        }
        catch (SecretStoreUnavailableException)
        {
            return EndpointResults.Problem("ssh_inventory_unavailable", "The provider SSH key inventory is temporarily unavailable.", 503);
        }
        catch (HttpRequestException)
        {
            return EndpointResults.Problem("ssh_inventory_unavailable", "The provider SSH key inventory is temporarily unavailable.", 503);
        }
        catch (OperationCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            return EndpointResults.Problem("ssh_inventory_unavailable", "The provider SSH key inventory is temporarily unavailable.", 503);
        }
    }

    [HttpGet("{id}")]
    public async Task<IResult> Get(string id, CancellationToken ct)
    {
        var account = await db.ProviderAccounts.AsNoTracking().Include(x => x.Plans).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (account is null) return EndpointResults.NotFound("provider_not_found", "Provider account was not found.");
        if (!await CanAsync(account.OrganizationId, OrganizationAction.ReadProviders, ct)) return EndpointResults.Forbidden();
        return Results.Ok(ToResponse(account));
    }

    [HttpPost]
    public async Task<IResult> Create(CreateProviderRequest request, CancellationToken ct)
    {
        if (!await CanAsync(request.OrganizationId, OrganizationAction.ManageProviders, ct)) return EndpointResults.Forbidden();
        return await ProviderEndpointResults.RunAsync(async () =>
        {
            var account = await accounts.CreateAsync(request, ct);
            await SaveAuditAsync(account, "provider.created", ct);
            return Results.Created($"/api/providers/{account.Id}", ToResponse(account));
        });
    }

    [HttpPatch("{id}")]
    public async Task<IResult> Update(string id, UpdateProviderRequest request, CancellationToken ct) =>
        await MutateAsync(id, async account =>
        {
            accounts.Update(account, request);
            await SaveAuditAsync(account, "provider.updated", ct);
            return Results.Ok(ToResponse(account));
        }, ct);

    [HttpPut("{id}/credentials")]
    public async Task<IResult> Credentials(string id, ReplaceProviderCredentialRequest request, CancellationToken ct) =>
        await MutateAsync(id, async account =>
        {
            await accounts.ReplaceCredentialsAsync(account, request, ct);
            await SaveAuditAsync(account, "provider.credentials_replaced", ct);
            return Results.Ok(ToResponse(account));
        }, ct);

    [HttpPost("{id}/validate")]
    public async Task<IResult> Validate(string id, CancellationToken ct) =>
        await MutateAsync(id, async account =>
        {
            var result = await accounts.ValidateAsync(account, ct);
            await SaveAuditAsync(account, "provider.validated", ct);
            return Results.Ok(result);
        }, ct);

    [HttpPost("{id}/import-legacy")]
    public async Task<IResult> ImportLegacy(string id, ProviderLegacyImportRequest request,
        [FromServices] ProviderLegacyImportService importer, CancellationToken ct)
    {
        if (request.ProviderAccountId != id)
            return EndpointResults.Problem("legacy_import_invalid", "The manifest account must match the requested account.", 400);
        if (!await CanAsync(request.OrganizationId, OrganizationAction.ManageProviders, ct)
            || !await CanAsync(request.OrganizationId, OrganizationAction.ManagePlans, ct)) return EndpointResults.Forbidden();
        return await ProviderEndpointResults.RunAsync(async () =>
        {
            var result = await importer.ImportAsync(request, ct);
            var account = await db.ProviderAccounts.SingleAsync(x => x.Id == id && x.OrganizationId == request.OrganizationId, ct);
            await SaveAuditAsync(account, "provider.legacy_imported", ct);
            return Results.Ok(result);
        });
    }

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id, CancellationToken ct) =>
        await MutateAsync(id, async account =>
        {
            await accounts.DeleteAsync(account, ct);
            await SaveAuditAsync(account, "provider.deleted", ct);
            return Results.NoContent();
        }, ct);

    private async Task<IResult> MutateAsync(string id, Func<ProviderAccount, Task<IResult>> action, CancellationToken ct)
    {
        var account = await db.ProviderAccounts.Include(x => x.Plans).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (account is null) return EndpointResults.NotFound("provider_not_found", "Provider account was not found.");
        if (!await CanAsync(account.OrganizationId, OrganizationAction.ManageProviders, ct)) return EndpointResults.Forbidden();
        return await ProviderEndpointResults.RunAsync(() => action(account));
    }

    private Task<bool> CanAsync(string organizationId, OrganizationAction action, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, action, ct);

    private Task SaveAuditAsync(ProviderAccount account, string action, CancellationToken ct) =>
        audit.AppendAsync(account.OrganizationId, null, AuditEventService.ActorFor(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity()),
            action, System.Text.Json.JsonSerializer.Serialize(new { providerAccountId = account.Id, account.Revision }), ct);

    public static ProviderAccountResponse ToResponse(ProviderAccount account) => new(account.Id, account.OrganizationId,
        account.Name, ProviderAccountService.WireType(account.ProviderType), account.Enabled, account.ProjectId, account.DefaultOsId,
        account.ValidationStatus, account.LastValidatedAt, account.Revision, account.Plans.Count);
}

internal static class ProviderEndpointResults
{
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ProviderCatalogException ex)
        {
            var status = ex.Code is "revision_conflict" or "plan_name_taken" or "plan_mapping_locked" or "provider_in_use" or "plan_in_use"
                ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            return EndpointResults.Problem(ex.Code, ex.Message, status);
        }
        catch (DbUpdateConcurrencyException) { return EndpointResults.Conflict("revision_conflict", "This item changed. Reload it before saving."); }
        catch (DbUpdateException) { return EndpointResults.Conflict("catalog_conflict", "This change conflicts with another catalog update or an existing resource reference."); }
        catch (SecretStoreUnavailableException) { return EndpointResults.Problem("provider_credentials_unavailable", "Provider credential storage is unavailable. Retry when OpenBao is available.", 503); }
        catch (HttpRequestException) { return EndpointResults.Problem("provider_validation_failed", "Provider credentials or access could not be validated. Check the token and cloud account permissions.", 400); }
        catch (System.Text.Json.JsonException) { return EndpointResults.Problem("provider_validation_failed", "The provider returned an invalid response.", 502); }
        catch (OperationCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        { return EndpointResults.Problem("provider_unavailable", "The provider did not respond in time.", 503); }
    }
}
