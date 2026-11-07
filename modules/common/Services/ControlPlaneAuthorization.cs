using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class ControlPlaneAuthorization(MinicloudDbContext db)
{
    public async Task<bool> CanAccessOrganizationAsync(ActorIdentity principal, string organizationId, OrganizationAction action, CancellationToken cancellationToken)
    {
        if (principal.IsApiKey)
        {
            return principal.OrganizationId == organizationId && HasScope(principal, ScopeFor(action));
        }

        if (principal.UserId is null)
        {
            return false;
        }

        var role = await db.OrganizationMembers
            .Where(x => x.OrganizationId == organizationId && x.UserId == principal.UserId)
            .Select(x => (OrganizationRole?)x.Role)
            .SingleOrDefaultAsync(cancellationToken);

        return role is not null && RoleAllows(role.Value, action);
    }

    public async Task<bool> CanAccessAppAsync(ActorIdentity principal, string appId, OrganizationAction action, CancellationToken cancellationToken)
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app?.DeletionStatus is not null && action is not (OrganizationAction.ReadApps or OrganizationAction.ReadDeployments or OrganizationAction.ReadRuntime or OrganizationAction.ReadSecrets or OrganizationAction.ReadApplicationLogs))
        {
            return false;
        }
        return app is not null && await CanAccessOrganizationAsync(principal, app.OrganizationId, action, cancellationToken);
    }

    private static bool HasScope(ActorIdentity principal, string? requiredScope) =>
        requiredScope is not null && principal.Scopes.Contains(requiredScope);

    private static string? ScopeFor(OrganizationAction action) => action switch
    {
        OrganizationAction.ReadApps => ApiKeyScopes.AppsRead,
        OrganizationAction.WriteApps => ApiKeyScopes.AppsWrite,
        OrganizationAction.ReadDeployments => ApiKeyScopes.DeploymentsRead,
        OrganizationAction.CreateDeployments => ApiKeyScopes.DeploymentsCreate,
        OrganizationAction.ReadRuntime => ApiKeyScopes.DeploymentsRead,
        OrganizationAction.ManageRuntime => ApiKeyScopes.DeploymentsCreate,
        OrganizationAction.ReadApiKeys => ApiKeyScopes.ApiKeysRead,
        OrganizationAction.WriteApiKeys => ApiKeyScopes.ApiKeysWrite,
        OrganizationAction.ReadSecrets => ApiKeyScopes.SecretsRead,
        OrganizationAction.WriteSecrets => ApiKeyScopes.SecretsWrite,
        OrganizationAction.ReadApplicationLogs => ApiKeyScopes.LogsRead,
        OrganizationAction.ManageTelemetry => ApiKeyScopes.TelemetryManage,
        _ => null
    };

    private static bool RoleAllows(OrganizationRole role, OrganizationAction action) => action switch
    {
        OrganizationAction.ReadApps or OrganizationAction.ReadDeployments or OrganizationAction.ReadRuntime or OrganizationAction.ReadSecrets or OrganizationAction.ReadApplicationLogs => true,
        OrganizationAction.WriteApps or OrganizationAction.CreateDeployments or OrganizationAction.ManageRuntime => role is OrganizationRole.Owner or OrganizationRole.Admin or OrganizationRole.Developer,
        OrganizationAction.WriteSecrets => role is OrganizationRole.Owner or OrganizationRole.Admin or OrganizationRole.Developer,
        OrganizationAction.ManageTelemetry => role is OrganizationRole.Owner or OrganizationRole.Admin or OrganizationRole.Developer,
        OrganizationAction.ReadApiKeys or OrganizationAction.WriteApiKeys or OrganizationAction.ManageMembers => role is OrganizationRole.Owner or OrganizationRole.Admin,
        _ => false
    };
}

public enum OrganizationAction
{
    ReadApps,
    WriteApps,
    ReadDeployments,
    CreateDeployments,
    ReadRuntime,
    ManageRuntime,
    ReadApiKeys,
    WriteApiKeys,
    ReadSecrets,
    WriteSecrets,
    ManageMembers,
    ReadApplicationLogs,
    ManageTelemetry
}
