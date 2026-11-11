using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Api.Providers;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using Minicloud.Common.Registry;

namespace Minicloud.Api.Extensions;

public static class MinicloudApiServiceCollectionExtensions
{
    public static IServiceCollection AddMinicloudApi(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        services.AddOptions<ServerReleaseOptions>()
            .Bind(configuration.GetSection("ServerRelease"))
            .PostConfigure(options => options.Metadata = ServerReleaseMetadata.Load(options.ManifestPath))
            .ValidateOnStart();

        services.AddControllers(options =>
            {
                options.Conventions.Add(new RouteTokenTransformerConvention(new KebabCaseRouteTokenTransformer()));
            })
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });
        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var details = context.ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .SelectMany(entry => entry.Value!.Errors.Select(error =>
                        new ErrorDetail(NormalizeModelStateField(entry.Key), GetModelStateErrorMessage(error))))
                    .ToArray();

                return new BadRequestObjectResult(new ErrorResponse(new ErrorBody(
                    "validation_failed",
                    "Request body is invalid.",
                    details)));
            };
        });
        services.AddOpenApi();
        services.AddHealthChecks();
        services.AddCors(options =>
        {
            options.AddPolicy("Dashboard", policy =>
            {
                var origins = configuration
                    .GetSection("Cors:AllowedOrigins")
                    .Get<string[]>()
                    ?.Where(origin => !string.IsNullOrWhiteSpace(origin))
                    .ToArray();

                if (origins is { Length: > 0 })
                {
                    policy.WithOrigins(origins).AllowCredentials();
                }

                policy.AllowAnyHeader().AllowAnyMethod();
            });
        });

        services.Configure<KratosOptions>(
            configuration.GetSection(KratosOptions.SectionName));
        services.AddSingleton<AccountOriginValidator>();
        services.Configure<SecretEncryptionOptions>(
            configuration.GetSection(SecretEncryptionOptions.SectionName));
        services.Configure<OpenBaoSecretStoreOptions>(
            configuration.GetSection(OpenBaoSecretStoreOptions.SectionName));
        services.Configure<DeploymentArtifactStorageOptions>(
            configuration.GetSection(DeploymentArtifactStorageOptions.SectionName));

        services.AddSingleton<IConsoleUrlProvider, ConsoleUrlProvider>();
        services.AddSingleton<IDatabaseConnectionStringProvider, DatabaseConnectionStringProvider>();
        services.AddDbContext<MinicloudDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<IDatabaseConnectionStringProvider>();
            var databaseProvider = database.GetProviderName();
            var connectionString = database.GetConnectionString();

            if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                options.UseSqlite(SqliteDatabaseConfigurator.ConfigureConnectionString(connectionString ?? "Data Source=minicloud-dev.db"))
                    .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
                return;
            }

            if (databaseProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            {
                options.UseNpgsql(connectionString ?? "Host=localhost;Database=minicloud;Username=minicloud;Password=minicloud");
                return;
            }

            throw new InvalidOperationException($"Unsupported database provider '{databaseProvider}'. Use 'Sqlite' or 'Postgres'.");
        });

        services.AddSingleton(TimeProvider.System);
        services.AddMinicloudRegistry(x => configuration.GetSection("Registry").Bind(x), x => configuration.GetSection("Worker:Pipeline").Bind(x));
        services.AddHttpClient("registry-gateway", x => x.Timeout = TimeSpan.FromMinutes(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IIdGenerator, IdGenerator>();
        services.AddSingleton<IApiKeyTokenService, ApiKeyTokenService>();
        services.AddSingleton<ITelemetryTokenService, TelemetryTokenService>();
        services.AddScoped<TelemetryService>();
        services.AddScoped<TelemetryQueryService>();
        services.AddSingleton<TelemetryQueryLimiter>();
        services.AddScoped<TelemetryCredentialAuthService>();
        services.Configure<TelemetryClientOptions>(
            configuration.GetSection(TelemetryClientOptions.SectionName));
        services.AddSingleton<TelemetryAuthoritySyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryAuthoritySyncService>());
        services.Configure<TelemetryIngestionOptions>(
            configuration.GetSection(TelemetryIngestionOptions.SectionName));
        services.Configure<GrafanaOptions>(
            configuration.GetSection(GrafanaOptions.SectionName));
        services.AddHttpClient<IGrafanaApiClient, GrafanaApiClient>();
        services.AddScoped<GrafanaOperatorCredentialProvider>();
        services.AddScoped<GrafanaProvisioningService>();
        services.AddScoped<GrafanaSessionService>();
        services.AddScoped<GrafanaQueryGateway>();
        services.AddScoped<AuditEventService>();
        Minicloud.Common.Providers.ProviderServiceCollectionExtensions.AddMinicloudProviders(services, configuration);
        services.AddScoped<ProviderAccountService>();
        services.AddScoped<ProviderCatalogService>();
        services.Configure<ProviderCatalogOptions>(configuration.GetSection("Providers"));
        services.AddScoped<ProviderLegacyImportService>();
        services.AddScoped<IRuntimeTokenService, RuntimeTokenService>();
        services.AddHttpClient<IKratosSessionValidator, KratosSessionValidator>(client =>
            client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });
        services.AddScoped<BrowserSessionService>();
        services.AddHttpClient<KratosBrowserFlowClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });
        services.AddScoped<DeploymentLifecycleService>();
        services.AddScoped<DeploymentCancellationService>();
        services.AddScoped<DeploymentPreparationService>();
        services.AddScoped<AssetReleaseService>();
        services.AddScoped<DeploymentServiceSetService>();
        services.AddScoped<DeploymentArtifactStorageService>();
        services.Configure<ManagedDomainOptions>(configuration.GetSection(ManagedDomainOptions.SectionName));
        services.AddScoped<DomainBindingService>();
        services.AddHttpClient<Minicloud.Common.Providers.Dns.ICustomDomainDnsResolver, Minicloud.Common.Providers.Dns.CustomDomainDnsResolver>(http => http.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<CustomDomainVerificationService>();
        services.AddScoped<CustomDomainRegistrationService>();
        services.AddScoped<AppBranchService>();
        services.AddScoped<AppEnvironmentService>();
        services.AddScoped<EnvironmentSshAccessService>();
        services.AddScoped<AppDeletionService>();
        services.AddScoped<DeploymentOrchestrator>();
        services.AddScoped<IAppSecretService, AppSecretService>();
        services.AddScoped<AppServiceSecretService>();
        services.AddScoped<IAppServiceSecretResolver>(provider => provider.GetRequiredService<AppServiceSecretService>());
        services.AddSingleton<SecretEncryptionService>();
        services.AddHttpClient<ISecretStore, OpenBaoSecretStore>()
            .ConfigurePrimaryHttpMessageHandler(provider =>
                OpenBaoHttpHandler.Create(provider.GetRequiredService<IOptions<OpenBaoSecretStoreOptions>>().Value));
        services.AddHttpClient(nameof(OpenBaoTokenRenewalService))
            .ConfigurePrimaryHttpMessageHandler(provider =>
                OpenBaoHttpHandler.Create(provider.GetRequiredService<IOptions<OpenBaoSecretStoreOptions>>().Value));
        services.AddHostedService<OpenBaoTokenRenewalService>();
        services.AddHostedService<ProviderCredentialCleanupWorker>();
        services.AddMinicloudMaintenanceParticipant(configuration, "api");
        services.AddHostedService<MaintenanceParticipantRegistrationService>();
        // T90: GitHub dispatch client and DeploymentWorkflowMonitor decommissioned.
        // minicloud-worker claims Queued deployments via PostgreSQL SKIP LOCKED.
        services.AddScoped<ControlPlanePrincipalResolver>();
        services.AddScoped<ControlPlaneAuthorization>();
        services
            .AddAuthentication(ControlPlaneAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ControlPlaneAuthenticationHandler>(ControlPlaneAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization();

        return services;
    }

    private static string NormalizeModelStateField(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return "body";
        }

        return field.StartsWith("$.", StringComparison.Ordinal) ? field[2..] : field;
    }

    private static string GetModelStateErrorMessage(ModelError error) =>
        string.IsNullOrWhiteSpace(error.ErrorMessage)
            ? "Invalid value."
            : error.ErrorMessage;
}

internal sealed partial class KebabCaseRouteTokenTransformer : IOutboundParameterTransformer
{
    public string? TransformOutbound(object? value) =>
        value is null ? null : KebabCaseRegex().Replace(value.ToString()!, "$1-$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex KebabCaseRegex();
}
