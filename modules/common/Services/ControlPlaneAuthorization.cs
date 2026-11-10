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
        var app = await db.Apps.AsNoTracking().Include(x => x.Application).SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return false;
        }
        var isReadOnlyAction = action is OrganizationAction.ReadApps or OrganizationAction.ReadDeployments or OrganizationAction.ReadRuntime or OrganizationAction.ReadSecrets or OrganizationAction.ReadApplicationLogs;
        if ((app.DeletionStatus is not null || app.Application?.DeletionStatus is not null) && !isReadOnlyAction)
        {
            return false;
        }
        return await CanAccessOrganizationAsync(principal, app.OrganizationId, action, cancellationToken);
    }

    public async Task<bool> CanAccessApplicationAsync(ActorIdentity principal, string applicationId, OrganizationAction action, CancellationToken cancellationToken)
    {
        var application = await db.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return false;
        }
        var isReadOnlyAction = action is OrganizationAction.ReadApps or OrganizationAction.ReadDeployments or OrganizationAction.ReadRuntime or OrganizationAction.ReadSecrets or OrganizationAction.ReadApplicationLogs;
        if (application.DeletionStatus is not null && !isReadOnlyAction)
        {
            return false;
        }
        return await CanAccessOrganizationAsync(principal, application.OrganizationId, action, cancellationToken);
    }

    private static bool HasScope(ActorIdentity principal, string? requiredScope) =>
        requiredScope is not null && principal.Scopes.Contains(requiredScope);

    private static string? ScopeFor(OrganizationAction action) => action switch
    {
        OrganizationAction.ReadDatabases => ApiKeyScopes.DatabasesRead,
        OrganizationAction.ManageDatabases => ApiKeyScopes.DatabasesManage,
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
        OrganizationAction.ReadProviders => ApiKeyScopes.ProvidersRead,
        OrganizationAction.ManageProviders => ApiKeyScopes.ProvidersWrite,
        OrganizationAction.ReadPlans => ApiKeyScopes.PlansRead,
        OrganizationAction.ManagePlans => ApiKeyScopes.PlansWrite,
        _ => null
    };

    private static bool RoleAllows(OrganizationRole role, OrganizationAction action) => action switch
    {
        OrganizationAction.ReadDatabases => true,
        OrganizationAction.ManageDatabases => role is OrganizationRole.Owner or OrganizationRole.Admin or OrganizationRole.Developer,
        OrganizationAction.ReadProviders or OrganizationAction.ReadPlans => true,
        OrganizationAction.ManageProviders or OrganizationAction.ManagePlans => role is OrganizationRole.Owner or OrganizationRole.Admin,
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
    ReadDatabases,
    ManageDatabases,
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
    ManageTelemetry,
    ReadProviders,
    ManageProviders,
    ReadPlans,
    ManagePlans
}
