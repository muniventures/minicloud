using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Services;

public interface IAppServiceSecretResolver
{
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ResolveForDeploymentAsync(
        string appId,
        IReadOnlyList<DeploymentServiceRequest> services,
        CancellationToken cancellationToken);
}

public sealed partial class AppServiceSecretService(
    MinicloudDbContext db,
    ISecretStore secretStore,
    IIdGenerator ids,
    TimeProvider timeProvider) : IAppServiceSecretResolver
{
    private const string AppScopeServiceName = "__app";
    public const string StatusActive = "active";
    public const string StatusDeleted = "deleted";
    public const string ReservedTelemetryHeadersName = TelemetryLimits.ReservedHeadersSecretName;

    public async Task CloneAsync(MinicloudApp sourceApp, MinicloudApp targetApp, CancellationToken cancellationToken)
    {
        var sourceDefinitions = await db.AppServiceSecretServices.AsNoTracking()
            .Where(x => x.AppId == sourceApp.Id && x.DeletedAt == null)
            .ToListAsync(cancellationToken);
        var sourceSecrets = await db.AppServiceSecrets.AsNoTracking()
            .Where(x => x.AppId == sourceApp.Id && x.DeletedAt == null)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        foreach (var definition in sourceDefinitions)
        {
            db.AppServiceSecretServices.Add(new AppServiceSecretServiceDefinition
            {
                Id = ids.NewId("svc"),
                OrganizationId = targetApp.OrganizationId,
                AppId = targetApp.Id,
                ServiceName = definition.ServiceName,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        foreach (var sourceSecret in sourceSecrets.Where(x => !string.Equals(x.Name, ReservedTelemetryHeadersName, StringComparison.Ordinal)))
        {
            var value = await secretStore.GetAsync(
                new StoredSecretReference(sourceSecret.StoreProvider, sourceSecret.StorePath, sourceSecret.StoreVersion),
                cancellationToken);
            var reference = await secretStore.PutAsync(
                new SecretStoreWriteRequest(targetApp.OrganizationId, targetApp.Id, sourceSecret.ServiceName, sourceSecret.Name, value),
                cancellationToken);
            db.AppServiceSecrets.Add(new AppServiceSecret
            {
                Id = ids.NewId("sec"),
                OrganizationId = targetApp.OrganizationId,
                AppId = targetApp.Id,
                ServiceName = sourceSecret.ServiceName,
                Name = sourceSecret.Name,
                StoreProvider = reference.Provider,
                StorePath = reference.Path,
                StoreVersion = reference.Version,
                Status = StatusActive,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }

    public async Task DeleteAllAsync(string appId, CancellationToken cancellationToken)
    {
        var secrets = await db.AppServiceSecrets
            .Where(x => x.AppId == appId && x.DeletedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var secret in secrets)
        {
            await secretStore.DeleteAsync(
                new StoredSecretReference(secret.StoreProvider, secret.StorePath, secret.StoreVersion),
                cancellationToken);
            secret.Status = StatusDeleted;
            secret.DeletedAt = timeProvider.GetUtcNow();
            secret.UpdatedAt = secret.DeletedAt.Value;
        }
    }

    public async Task<IReadOnlyList<AppServiceSecretServiceResponse>> ListServicesAsync(string appId, CancellationToken cancellationToken)
    {
        var definitions = await db.AppServiceSecretServices.AsNoTracking()
            .Where(x => x.AppId == appId && x.DeletedAt == null)
            .OrderBy(x => x.ServiceName)
            .ToListAsync(cancellationToken);
        var secretCounts = await db.AppServiceSecrets.AsNoTracking()
            .Where(x => x.AppId == appId && x.DeletedAt == null)
            .GroupBy(x => x.ServiceName)
            .Select(x => new { ServiceName = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.ServiceName, x => x.Count, StringComparer.Ordinal, cancellationToken);

        return definitions.Select(definition => ToResponse(definition, secretCounts.GetValueOrDefault(definition.ServiceName))).ToArray();
    }

    public async Task<CreateServiceResult> CreateServiceAsync(MinicloudApp app, CreateAppServiceSecretServiceRequest request, CancellationToken cancellationToken)
    {
        var validation = ValidateServiceName(request.ServiceName);
        if (!validation.IsValid)
        {
            return CreateServiceResult.Invalid(validation);
        }

        var existing = await EnsureServiceDefinitionAsync(app, request.ServiceName, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return CreateServiceResult.Success(ToResponse(existing, await CountSecretsAsync(app.Id, existing.ServiceName, cancellationToken)));
    }

    public async Task<CreateServiceResult> RenameServiceAsync(AppServiceSecretServiceDefinition service, UpdateAppServiceSecretServiceRequest request, CancellationToken cancellationToken)
    {
        var validation = ValidateServiceName(request.ServiceName);
        if (!validation.IsValid)
        {
            return CreateServiceResult.Invalid(validation);
        }

        var newName = request.ServiceName;
        if (service.ServiceName == newName)
        {
            return CreateServiceResult.Success(ToResponse(service, await CountSecretsAsync(service.AppId, service.ServiceName, cancellationToken)));
        }

        var duplicate = await db.AppServiceSecretServices.AnyAsync(
            x => x.AppId == service.AppId && x.ServiceName == newName && x.DeletedAt == null && x.Id != service.Id,
            cancellationToken);
        if (duplicate)
        {
            validation.Add("serviceName", "Service name already exists.");
            return CreateServiceResult.Invalid(validation);
        }

        var conflicts = await db.AppServiceSecrets.AnyAsync(
            x => x.AppId == service.AppId && x.ServiceName == newName && x.DeletedAt == null,
            cancellationToken);
        if (conflicts)
        {
            validation.Add("serviceName", "Service already has secrets.");
            return CreateServiceResult.Invalid(validation);
        }

        var deletedDuplicate = await db.AppServiceSecretServices.SingleOrDefaultAsync(
            x => x.AppId == service.AppId && x.ServiceName == newName && x.DeletedAt != null && x.Id != service.Id,
            cancellationToken);
        if (deletedDuplicate is not null)
        {
            db.AppServiceSecretServices.Remove(deletedDuplicate);
        }

        var oldName = service.ServiceName;
        var now = timeProvider.GetUtcNow();
        var secrets = await db.AppServiceSecrets
            .Where(x => x.AppId == service.AppId && x.ServiceName == oldName && x.DeletedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var secret in secrets)
        {
            secret.ServiceName = newName;
            secret.UpdatedAt = now;
        }

        service.ServiceName = newName;
        service.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return CreateServiceResult.Success(ToResponse(service, secrets.Count));
    }

    public async Task DeleteServiceAsync(AppServiceSecretServiceDefinition service, CancellationToken cancellationToken)
    {
        service.DeletedAt = timeProvider.GetUtcNow();
        service.UpdatedAt = service.DeletedAt.Value;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AppServiceSecretResponse>> ListAsync(string appId, string? serviceName, CancellationToken cancellationToken)
    {
        var query = db.AppServiceSecrets.AsNoTracking()
            .Where(x => x.AppId == appId && x.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            query = query.Where(x => x.ServiceName == serviceName);
        }

        var rows = await query
            .OrderBy(x => x.ServiceName)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return rows.Select(ToResponse).ToArray();
    }

    public async Task<SetSecretResult> SetAsync(MinicloudApp app, SetAppServiceSecretRequest request, CancellationToken cancellationToken)
    {
        var validation = ValidateSetRequest(request);
        if (!validation.IsValid)
        {
            return SetSecretResult.Invalid(validation);
        }

        var now = timeProvider.GetUtcNow();
        var serviceName = NormalizeSecretServiceName(request.ServiceName);
        if (!IsAppScopeServiceName(serviceName))
        {
            await EnsureServiceDefinitionAsync(app, serviceName, cancellationToken);
        }

        var reference = await secretStore.PutAsync(
            new SecretStoreWriteRequest(app.OrganizationId, app.Id, serviceName, request.Name, request.Value),
            cancellationToken);

        var existing = await db.AppServiceSecrets.SingleOrDefaultAsync(
            x => x.AppId == app.Id && x.ServiceName == serviceName && x.Name == request.Name,
            cancellationToken);
        if (existing is null)
        {
            existing = new AppServiceSecret
            {
                Id = ids.NewId("sec"),
                OrganizationId = app.OrganizationId,
                AppId = app.Id,
                ServiceName = serviceName,
                Name = request.Name,
                CreatedAt = now
            };
            db.AppServiceSecrets.Add(existing);
        }

        existing.StoreProvider = reference.Provider;
        existing.StorePath = reference.Path;
        existing.StoreVersion = reference.Version;
        existing.Status = StatusActive;
        existing.DeletedAt = null;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return SetSecretResult.Success(ToResponse(existing));
    }

    public async Task<SetSecretResult> UpdateAsync(AppServiceSecret secret, UpdateAppServiceSecretRequest request, CancellationToken cancellationToken)
    {
        if (request.Value is null)
        {
            var validation = new ValidationResult();
            validation.Add("value", "Secret value is required.");
            return SetSecretResult.Invalid(validation);
        }

        var previousReference = new StoredSecretReference(secret.StoreProvider, secret.StorePath, secret.StoreVersion);
        var reference = await secretStore.PutAsync(
            new SecretStoreWriteRequest(secret.OrganizationId, secret.AppId, secret.ServiceName, secret.Name, request.Value),
            cancellationToken);
        secret.StoreProvider = reference.Provider;
        secret.StorePath = reference.Path;
        secret.StoreVersion = reference.Version;
        secret.Status = StatusActive;
        secret.DeletedAt = null;
        secret.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        if (previousReference.Provider != reference.Provider || previousReference.Path != reference.Path || previousReference.Version != reference.Version)
        {
            await secretStore.DeleteAsync(previousReference, cancellationToken);
        }

        return SetSecretResult.Success(ToResponse(secret));
    }

    public async Task DeleteAsync(AppServiceSecret secret, CancellationToken cancellationToken)
    {
        await secretStore.DeleteAsync(new StoredSecretReference(secret.StoreProvider, secret.StorePath, secret.StoreVersion), cancellationToken);
        secret.Status = StatusDeleted;
        secret.DeletedAt = timeProvider.GetUtcNow();
        secret.UpdatedAt = secret.DeletedAt.Value;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ValidateReferencesAsync(
        string appId,
        IReadOnlyList<DeploymentSecretServiceRequest> services,
        CancellationToken cancellationToken)
    {
        var stored = await db.AppServiceSecrets.AsNoTracking()
            .Where(x => x.AppId == appId && x.DeletedAt == null)
            .Select(x => new { x.ServiceName, x.Name })
            .ToListAsync(cancellationToken);
        foreach (var service in services)
        {
            foreach (var (envName, secretName) in service.SecretEnv ?? new Dictionary<string, string>())
            {
                if (secretName == ReservedTelemetryHeadersName)
                {
                    continue;
                }
                if (!stored.Any(x => x.Name == secretName && (x.ServiceName == service.Name || x.ServiceName == AppScopeServiceName)))
                {
                    throw new MissingAppServiceSecretException(service.Name, envName, secretName);
                }
            }
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ResolveForDeploymentAsync(
        string appId,
        IReadOnlyList<DeploymentServiceRequest> services,
        CancellationToken cancellationToken)
    {
        return await ResolveReferencesAsync(
            appId,
            services.Select(service => new DeploymentSecretServiceRequest(service.Name, service.SecretEnv)).ToArray(),
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ResolveReferencesAsync(
        string appId,
        IReadOnlyList<DeploymentSecretServiceRequest> services,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            if (service.SecretEnv is not { Count: > 0 })
            {
                var storedSecrets = await db.AppServiceSecrets.AsNoTracking()
                    .Where(x => x.AppId == appId && x.ServiceName == service.Name && x.DeletedAt == null)
                    .OrderBy(x => x.Name)
                    .ToListAsync(cancellationToken);
                if (storedSecrets.Count == 0)
                {
                    continue;
                }

                var resolvedStoredSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var secret in storedSecrets)
                {
                    resolvedStoredSecrets[secret.Name] = await secretStore.GetAsync(
                        new StoredSecretReference(secret.StoreProvider, secret.StorePath, secret.StoreVersion),
                        cancellationToken);
                }

                resolved[service.Name] = resolvedStoredSecrets;
                continue;
            }

            var serviceSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (envName, secretName) in service.SecretEnv)
            {
                var secret = await db.AppServiceSecrets.AsNoTracking()
                    .Where(x => x.AppId == appId && x.Name == secretName && x.DeletedAt == null && (x.ServiceName == service.Name || x.ServiceName == AppScopeServiceName))
                    .OrderByDescending(x => x.ServiceName == service.Name)
                    .ThenByDescending(x => x.ServiceName == AppScopeServiceName)
                    .FirstOrDefaultAsync(
                    cancellationToken);
                if (secret is null)
                {
                    if (string.Equals(secretName, TelemetryLimits.ReservedHeadersSecretName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    throw new MissingAppServiceSecretException(service.Name, envName, secretName);
                }

                serviceSecrets[envName] = await secretStore.GetAsync(
                    new StoredSecretReference(secret.StoreProvider, secret.StorePath, secret.StoreVersion),
                    cancellationToken);
            }

            resolved[service.Name] = serviceSecrets;
        }

        return resolved;
    }

    public static AppServiceSecretResponse ToResponse(AppServiceSecret secret) =>
        new(secret.Id, secret.AppId, IsAppScopeServiceName(secret.ServiceName) ? string.Empty : secret.ServiceName, secret.Name, secret.Status, secret.CreatedAt, secret.UpdatedAt);

    public static AppServiceSecretServiceResponse ToResponse(AppServiceSecretServiceDefinition service, int secretCount) =>
        new(service.Id, service.AppId, service.ServiceName, secretCount, service.CreatedAt, service.UpdatedAt);

    private static ValidationResult ValidateSetRequest(SetAppServiceSecretRequest request)
    {
        var validation = string.IsNullOrWhiteSpace(request.ServiceName)
            ? new ValidationResult()
            : ValidateServiceName(request.ServiceName);

        if (string.IsNullOrWhiteSpace(request.Name) || !ControlPlaneValidation.EnvironmentVariableKeyRegex().IsMatch(request.Name))
        {
            validation.Add("name", "Secret name is invalid.");
        }

        if (request.Value is null)
        {
            validation.Add("value", "Secret value is required.");
        }

        return validation;
    }

    private static string NormalizeSecretServiceName(string? serviceName) =>
        string.IsNullOrWhiteSpace(serviceName) ? AppScopeServiceName : serviceName;

    private static bool IsAppScopeServiceName(string serviceName) =>
        string.Equals(serviceName, AppScopeServiceName, StringComparison.Ordinal);

    private async Task<AppServiceSecretServiceDefinition> EnsureServiceDefinitionAsync(MinicloudApp app, string serviceName, CancellationToken cancellationToken)
    {
        var existing = await db.AppServiceSecretServices.SingleOrDefaultAsync(
            x => x.AppId == app.Id && x.ServiceName == serviceName,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.DeletedAt is not null)
            {
                existing.DeletedAt = null;
                existing.UpdatedAt = timeProvider.GetUtcNow();
            }

            return existing;
        }

        var now = timeProvider.GetUtcNow();
        existing = new AppServiceSecretServiceDefinition
        {
            Id = ids.NewId("svc"),
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            ServiceName = serviceName,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AppServiceSecretServices.Add(existing);
        return existing;
    }

    private async Task<int> CountSecretsAsync(string appId, string serviceName, CancellationToken cancellationToken) =>
        await db.AppServiceSecrets.CountAsync(x => x.AppId == appId && x.ServiceName == serviceName && x.DeletedAt == null, cancellationToken);

    private static ValidationResult ValidateServiceName(string serviceName)
    {
        var validation = new ValidationResult();
        if (string.IsNullOrWhiteSpace(serviceName) || !MinicloudConfigNameRegex().IsMatch(serviceName))
        {
            validation.Add("serviceName", "Service name is invalid.");
        }

        return validation;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial System.Text.RegularExpressions.Regex MinicloudConfigNameRegex();
}

public sealed record SetSecretResult(bool Succeeded, AppServiceSecretResponse? Secret, ValidationResult? Validation)
{
    public static SetSecretResult Success(AppServiceSecretResponse secret) => new(true, secret, null);
    public static SetSecretResult Invalid(ValidationResult validation) => new(false, null, validation);
}

public sealed record CreateServiceResult(bool Succeeded, AppServiceSecretServiceResponse? Service, ValidationResult? Validation)
{
    public static CreateServiceResult Success(AppServiceSecretServiceResponse service) => new(true, service, null);
    public static CreateServiceResult Invalid(ValidationResult validation) => new(false, null, validation);
}

public sealed class MissingAppServiceSecretException(string serviceName, string envName, string secretName)
    : Exception($"Service '{serviceName}' references missing secret '{secretName}' for environment variable '{envName}'.")
{
    public string ServiceName { get; } = serviceName;
    public string EnvName { get; } = envName;
    public string SecretName { get; } = secretName;
}
