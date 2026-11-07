using System.Text.RegularExpressions;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Common.Validation;

public static partial class ControlPlaneValidation
{
    public const int MaxDeploymentServices = 10;

    public static ValidationResult ValidateDeploymentPreparation(StartDeploymentPreparationRequest request)
    {
        var result = new ValidationResult();
        if (!Guid.TryParse(request.IdempotencyKey, out _)) { result.Add("idempotencyKey", "A UUID is required."); }
        if (request.ServiceNames is not { Count: > 0 and <= MaxDeploymentServices })
        { result.Add("serviceNames", "Select one to ten services."); return result; }
        if (request.ServiceNames.Distinct(StringComparer.Ordinal).Count() != request.ServiceNames.Count)
        { result.Add("serviceNames", "Service names must be unique."); }
        foreach (var name in request.ServiceNames) { ValidateSlug(result, name, "serviceNames", "Service name is invalid."); }
        return result;
    }

    public static ValidationResult ValidateOrganization(CreateOrganizationRequest request)
    {
        var result = new ValidationResult();
        Require(result, request.Name, "name", "Name is required.");
        ValidateSlug(result, request.Slug, "slug", "Organization slug is invalid.");
        return result;
    }

    public static ValidationResult ValidateApp(CreateAppRequest request)
    {
        var result = new ValidationResult();
        Require(result, request.OrganizationId, "organizationId", "Organization is required.");
        Require(result, request.Name, "name", "Name is required.");
        ValidateSlug(result, request.Slug, "slug", "App slug is invalid.");
        ValidateEnum<AppPlan>(result, request.Plan, "plan", "Plan is not supported.");
        ValidateEnum<DatabaseMode>(result, request.Database, "database", "Database mode is not supported.");
        return result;
    }

    public static ValidationResult ValidateApiKey(CreateApiKeyRequest request)
    {
        var result = new ValidationResult();
        Require(result, request.OrganizationId, "organizationId", "Organization is required.");
        Require(result, request.Name, "name", "Name is required.");

        if (request.Scopes.Count == 0)
        {
            result.Add("scopes", "At least one scope is required.");
        }

        for (var i = 0; i < request.Scopes.Count; i++)
        {
            if (!ApiKeyScopes.All.Contains(request.Scopes[i]))
            {
                result.Add($"scopes[{i}]", "Scope is not supported.");
            }
        }

        return result;
    }

    public static ValidationResult ValidateDeploymentSecrets(ValidateDeploymentSecretsRequest request)
    {
        var result = new ValidationResult();
        Require(result, request.AppId, "appId", "App is required.");
        if (request.Services is null || request.Services.Count is < 1 or > MaxDeploymentServices)
        {
            result.Add("services", $"Specify one to {MaxDeploymentServices} services.");
            return result;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < request.Services.Count; i++)
        {
            var service = request.Services[i];
            if (service is null)
            {
                result.Add($"services[{i}]", "Service is required.");
                continue;
            }
            ValidateSlug(result, service.Name, $"services[{i}].name", "Service name is invalid.");
            if (!names.Add(service.Name))
            {
                result.Add($"services[{i}].name", "Service names must be unique.");
            }
            foreach (var (envKey, secretName) in service.SecretEnv ?? new Dictionary<string, string>())
            {
                ValidateEnvironmentEntry(result, $"services[{i}].secretEnv", envKey, secretName, "Secret environment variable");
                if (string.IsNullOrWhiteSpace(secretName) || !EnvironmentVariableKeyRegex().IsMatch(secretName))
                {
                    result.Add($"services[{i}].secretEnv.{envKey}", "Secret name is invalid.");
                }
            }
        }
        return result;
    }

    public static ValidationResult ValidateDeployment(CreateDeploymentRequest request)
    {
        var result = new ValidationResult();
        Require(result, request.AppId, "appId", "App is required.");
        ValidateEnum<DatabaseMode>(result, request.Database, "database", "Database mode is not supported.");

        if (request.Services is null)
        {
            result.Add("services", "Services are required.");
            return result;
        }

        if (request.Services.Count == 0)
        {
            result.Add("services", "At least one service is required.");
            return result;
        }

        if (request.Services.Count > MaxDeploymentServices)
        {
            result.Add("services", $"At most {MaxDeploymentServices} services are supported.");
        }

        foreach (var error in StorageValidation.Validate(request.Services.Select(x => (x.Name, x.Storage,
            (IEnumerable<string>)(x.AssetMounts?.Values.Select(m => m.MountPath) ?? []))),
            string.Equals(request.Database, "sqlite", StringComparison.OrdinalIgnoreCase)).Errors)
        { result.Add(error.Field, error.Message); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < request.Services.Count; i++)
        {
            var service = request.Services[i];
            ValidateSlug(result, service.Name, $"services[{i}].name", "Service name is invalid.");
            var hasImage = !string.IsNullOrWhiteSpace(service.Image);
            var hasArtifact = !string.IsNullOrWhiteSpace(service.ArtifactId);
            var hasPublication = !string.IsNullOrWhiteSpace(service.ImagePublicationId);
            if ((hasImage ? 1 : 0) + (hasArtifact ? 1 : 0) + (hasPublication ? 1 : 0) != 1)
            {
                result.Add($"services[{i}].image", "Exactly one of image, artifactId or imagePublicationId is required.");
            }
            if (service.Port is < 1 or > 65535)
            {
                result.Add($"services[{i}].port", "Port must be between 1 and 65535.");
            }
            if (service.Path != "/")
            {
                result.Add($"services[{i}].path", "Service path must be '/'.");
            }
            if (!service.HealthPath.StartsWith("/", StringComparison.Ordinal))
            {
                result.Add($"services[{i}].healthPath", "Health path must start with '/'.");
            }
            if (!names.Add(service.Name))
            {
                result.Add($"services[{i}].name", "Service names must be unique.");
            }

            if (service.Env is not null)
            {
                foreach (var (envKey, envValue) in service.Env)
                {
                    ValidateEnvironmentEntry(result, $"services[{i}].env", envKey, envValue, "Environment variable");
                }
            }

            if (service.SecretEnv is not null)
            {
                foreach (var (envKey, secretName) in service.SecretEnv)
                {
                    ValidateEnvironmentEntry(result, $"services[{i}].secretEnv", envKey, secretName, "Secret environment variable");
                    if (!EnvironmentVariableKeyRegex().IsMatch(secretName))
                    {
                        result.Add($"services[{i}].secretEnv.{envKey}", "Secret name is invalid.");
                    }
                }
            }

            if (service.Env is not null && service.SecretEnv is not null)
            {
                foreach (var duplicate in service.Env.Keys.Intersect(service.SecretEnv.Keys, StringComparer.Ordinal))
                {
                    result.Add($"services[{i}].secretEnv.{duplicate}", "Environment variables cannot be defined in both env and secretEnv.");
                }
            }
        }

        return result;
    }

    public static ValidationResult ValidateMergedDeploymentServices(IReadOnlyList<DeploymentServiceRequest> services, bool sqlite = false)
    {
        var result = new ValidationResult();
        if (services.Count == 0)
        {
            result.Add("services", "At least one service is required.");
            return result;
        }

        if (services.Count > MaxDeploymentServices)
        {
            result.Add("services", $"At most {MaxDeploymentServices} active services are supported.");
        }

        foreach (var error in StorageValidation.Validate(services.Select(x => (x.Name, x.Storage,
            (IEnumerable<string>)(x.AssetMounts?.Values.Select(m => m.MountPath) ?? []))), sqlite).Errors)
        { result.Add(error.Field, error.Message); }
        if (!services.Any(x => x.Public))
        {
            result.Add("services", "At least one public service is required across the app's active services.");
        }

        for (var i = 0; i < services.Count; i++)
        {
            var service = services[i];
            if (service.Path != "/")
            {
                result.Add($"services[{i}].path", "Service path must be '/'.");
            }
        }

        return result;
    }

    public static bool TryParseEnum<TEnum>(string value, out TEnum parsed) where TEnum : struct, Enum =>
        Enum.TryParse(NormalizeEnumValue(value), ignoreCase: true, out parsed);

    public static string ToWireValue<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (value is AppPlan appPlan)
        {
            return appPlan switch
            {
                AppPlan.P0H0 => "p0-h-0",
                AppPlan.P0V0 => "p0-v-0",
                AppPlan.P0V1 => "p0-v-1",
                AppPlan.P0V2 => "p0-v-2",
                AppPlan.P0D0 => "p0-d-0",
                AppPlan.P0D1 => "p0-d-1",
                AppPlan.P0D2 => "p0-d-2",
                _ => "p0"
            };
        }

        var name = value.ToString();
        return string.Concat(name.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
    }

    private static void Require(ValidationResult result, string? value, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result.Add(field, message);
        }
    }

    private static void ValidateSlug(ValidationResult result, string? value, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || !SlugRegex().IsMatch(value))
        {
            result.Add(field, message);
        }
    }

    private static void ValidateEnum<TEnum>(ValidationResult result, string value, string field, string message) where TEnum : struct, Enum
    {
        if (!TryParseEnum<TEnum>(value, out _))
        {
            result.Add(field, message);
        }
    }

    private static string NormalizeEnumValue(string value) =>
        string.Concat(value.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex SlugRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    internal static partial Regex EnvironmentVariableKeyRegex();

    private static void ValidateEnvironmentEntry(
        ValidationResult result,
        string fieldPrefix,
        string envKey,
        string? value,
        string label)
    {
        if (!EnvironmentVariableKeyRegex().IsMatch(envKey))
        {
            result.Add($"{fieldPrefix}.{envKey}", $"{label} name is invalid.");
        }

        if (value is null)
        {
            result.Add($"{fieldPrefix}.{envKey}", $"{label} value must be a string.");
        }
    }
}
