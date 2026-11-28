using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Selfrun.Common.Data;
using Selfrun.Common.Extensions;
using Selfrun.Common.Services;
using Selfrun.Worker;
using Selfrun.Worker.Pipeline;
using Selfrun.Worker.Pipeline.Steps;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.DigitalOcean;
using Selfrun.Common.Providers.Dns;
using Selfrun.Common.Providers.Hetzner;
using Selfrun.Common.Providers.Vultr;
using Selfrun.Worker.Queue;
using Selfrun.Worker.Ssh;
using Selfrun.Worker.Storage;
using Selfrun.Common.Registry;
using Selfrun.Common.Diagnostics;
using Selfrun.Worker.Diagnostics;
using WorkerDiagnostics = Selfrun.Worker.Diagnostics.OperatorDiagnostics;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOperatorDiagnostics(builder.Configuration, "selfrun-worker");

// Configure Worker Options
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.AddOptions<WorkerOptions>()
    .Validate(options => options.MaxConcurrentDeployments is >= 1 and <= 16,
        "Worker:MaxConcurrentDeployments must be between 1 and 16.")
    .ValidateOnStart();
// Spec-literal fallback: compose/action pass DeploymentArtifacts__RootPath,
// while WorkerOptions binds Worker__ArtifactRootPath. Keep compose spec-literal
// and map here so LocalFileSystemArtifactRetriever sees the shared root.
builder.Services.PostConfigure<WorkerOptions>(options =>
{
    var workerRoot = builder.Configuration.GetValue<string>("Worker:ArtifactRootPath");
    if (!string.IsNullOrWhiteSpace(workerRoot))
    {
        return;
    }

    var specRoot = builder.Configuration.GetValue<string>("DeploymentArtifacts:RootPath");
    if (!string.IsNullOrWhiteSpace(specRoot))
    {
        options.ArtifactRootPath = specRoot;
    }
});
builder.Services.Configure<DeploymentPipelineOptions>(builder.Configuration.GetSection(DeploymentPipelineOptions.SectionName));
builder.Services.AddOptions<DeploymentPipelineOptions>()
    .Validate(options => options.MaxParallelBuilds is >= 1 and <= 4,
        "Worker:Pipeline:MaxParallelBuilds must be between 1 and 4.")
    .Validate(options => options.MaxParallelNetworkOperations is >= 1 and <= 8,
        "Worker:Pipeline:MaxParallelNetworkOperations must be between 1 and 8.")
    .Validate(options => options.HasValidBuildCachePolicy,
        "Worker:Pipeline build-cache limits are invalid: bytes 256 MiB–16 GiB, age 1–720 hours, free percent 1–50.")
    .ValidateOnStart();
builder.Services.PostConfigure<DeploymentPipelineOptions>(options =>
{
    var publicKeyFile = builder.Configuration["Worker:Pipeline:SshPublicKeyFile"];
    if (!string.IsNullOrWhiteSpace(publicKeyFile))
    {
        options.SshPublicKey = File.ReadAllText(publicKeyFile).TrimEnd('\r', '\n');
        return;
    }

    if (string.IsNullOrWhiteSpace(options.SshPublicKey))
    {
        options.SshPublicKey = builder.Configuration["SELFRUN_SSH_PUBLIC_KEY"]
            ?? builder.Configuration["SSH_PUBLIC_KEY"]
            ?? string.Empty;
    }
});

builder.Services.Configure<SshDeploymentOptions>(builder.Configuration.GetSection(SshDeploymentOptions.SectionName));
builder.Services.AddOptions<SshDeploymentOptions>()
    .Validate(options => options.Port is >= 1 and <= 65535,
        "Worker:Ssh:Port must be between 1 and 65535.")
    .Validate(options => options.MaxParallelArtifactTransfers is >= 1 and <= 4,
        "Worker:Ssh:MaxParallelArtifactTransfers must be between 1 and 4.")
    .ValidateOnStart();
builder.Services.PostConfigure<SshDeploymentOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.PrivateKeyContent))
    {
        options.PrivateKeyContent = builder.Configuration["SSH_PRIVATE_KEY"]
            ?? builder.Configuration["SELFRUN_SSH_PRIVATE_KEY"]
            ?? string.Empty;
    }
});

// Configure Provider Options with standard platform env var fallbacks
builder.Services.PostConfigure<HetznerOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ApiToken))
    {
        options.ApiToken = builder.Configuration["HCLOUD_TOKEN"]
            ?? builder.Configuration["HETZNER_API_TOKEN"]
            ?? string.Empty;
    }
});

builder.Services.PostConfigure<DigitalOceanOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ApiToken))
    {
        options.ApiToken = builder.Configuration["DIGITALOCEAN_ACCESS_TOKEN"]
            ?? builder.Configuration["DIGITALOCEAN_TOKEN"]
            ?? string.Empty;
    }
    if (string.IsNullOrWhiteSpace(options.ProjectId))
    {
        options.ProjectId = builder.Configuration["DIGITALOCEAN_PROJECT_ID"];
    }
});

builder.Services.PostConfigure<VultrOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        options.ApiKey = builder.Configuration["VULTR_API_KEY"]
            ?? builder.Configuration["VULTR_KEY"]
            ?? string.Empty;
    }
});

builder.Services.PostConfigure<CloudflareOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ApiToken))
    {
        options.ApiToken = builder.Configuration["CLOUDFLARE_API_TOKEN"] ?? string.Empty;
    }
    if (string.IsNullOrWhiteSpace(options.ZoneId))
    {
        options.ZoneId = builder.Configuration["CLOUDFLARE_ZONE_ID"] ?? string.Empty;
    }
});

// TimeProvider
builder.Services.AddSingleton(TimeProvider.System);

// Database configuration. Worker defaults preserve the pre-Common behavior:
// Postgres provider, Selfrun/DefaultConnection/Database keys, DATABASE_URL,
// localhost fallback. URL normalization (postgres port default, sqlite://)
// is the canonical Common implementation.
builder.Services.AddSelfrunDbContext(new SelfrunDatabaseOptions
{
    Provider = builder.Configuration.GetValue<string>("Database:Provider") ?? "Postgres",
    SelfrunConnectionString = builder.Configuration.GetConnectionString("Selfrun"),
    DefaultConnectionString = builder.Configuration.GetConnectionString("DefaultConnection"),
    DatabaseConnectionString = builder.Configuration.GetConnectionString("Database"),
    DatabaseUrl = builder.Configuration["DATABASE_URL"],
    PasswordFile = builder.Configuration["Database:PasswordFile"]
});
builder.Services.AddSelfrunCommon();
builder.Services.AddSelfrunRegistry(x => builder.Configuration.GetSection("Registry").Bind(x), x => builder.Configuration.GetSection("Worker:Pipeline").Bind(x));
builder.Services.AddScoped<AuditEventService>();
builder.Services.AddScoped<ControlPlaneAuthorization>();
builder.Services.AddSelfrunSecrets(
    configureEncryption: options => builder.Configuration.GetSection(SecretEncryptionOptions.SectionName).Bind(options),
    configureStore: options => builder.Configuration.GetSection(OpenBaoSecretStoreOptions.SectionName).Bind(options));
builder.Services.AddHttpClient<ISecretStore, OpenBaoSecretStore>()
    .ConfigurePrimaryHttpMessageHandler(provider =>
        OpenBaoHttpHandler.Create(provider.GetRequiredService<IOptions<OpenBaoSecretStoreOptions>>().Value));
builder.Services.AddScoped<IAppServiceSecretResolver, AppServiceSecretService>();

// Storage and Artifact Retrieval
builder.Services.AddHttpClient<ApiHttpArtifactRetriever>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(options.ApiBaseUrl))
    {
        client.BaseAddress = new Uri(options.ApiBaseUrl);
    }

    if (!string.IsNullOrWhiteSpace(options.ApiToken))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
    }
});
builder.Services.AddSingleton<LocalFileSystemArtifactRetriever>();
builder.Services.AddSingleton<IArtifactRetriever>(sp => sp.GetRequiredService<LocalFileSystemArtifactRetriever>());

// Pipeline Executor (T40 full pipeline)
builder.Services.AddSelfrunProviders(builder.Configuration);
builder.Services.AddScoped<ProviderSshKeyService>();
if (!builder.Configuration.GetValue<bool>("Providers:AllowLegacyConfiguration"))
{
    // A type-only resolver cannot identify an account. Keep legacy dispatch an
    // explicit, temporary upgrade option rather than silently using global keys.
    builder.Services.AddScoped<ProviderServiceCollectionExtensions.VpsProviderClientResolver>(sp => provider =>
        throw new ProviderCatalogException("legacy_provider_import_required", "Import legacy resources into organization provider accounts before resuming cloud operations."));
}
builder.Services.AddHttpClient("pipeline-health");
builder.Services.AddSingleton<IServerHostKeyProbe, SshNetServerHostKeyProbe>();
builder.Services.AddScoped<ResolveProvisionServerStep>();
builder.Services.Configure<ManagedDomainOptions>(builder.Configuration.GetSection(ManagedDomainOptions.SectionName));
builder.Services.AddScoped<DnsSetupStep>();
builder.Services.AddScoped<DomainDnsReconciler>();
builder.Services.AddScoped<Selfrun.Worker.Power.EnvironmentIdleSweepProcessor>();
builder.Services.AddHostedService<Selfrun.Worker.Power.EnvironmentIdleSweepWorker>();
builder.Services.AddHostedService<Selfrun.Worker.Maintenance.RuntimeLogRetentionWorker>();
builder.Services.AddHostedService<DomainDnsWorker>();
builder.Services.AddHostedService<CustomDomainDnsWorker>();
builder.Services.Configure<RuntimeAgentOptions>(builder.Configuration.GetSection(RuntimeAgentOptions.SectionName));
builder.Services.AddScoped<IRuntimeAgentDeployment, RuntimeAgentDeployment>();
builder.Services.AddScoped<SshDeployStep>(sp => new SshDeployStep(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IArtifactRetriever>(),
    sp.GetRequiredService<ISshDeploymentClient>(),
    sp.GetRequiredService<IOptions<SshDeploymentOptions>>(),
    sp.GetRequiredService<IOptions<DeploymentPipelineOptions>>(),
    sp.GetRequiredService<IHostEnvironment>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetService<ILogger<SshDeployStep>>(),
    sp.GetRequiredService<IRuntimeAgentDeployment>()));
builder.Services.AddScoped<HealthCheckFinalizeStep>(sp =>
{
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var options = sp.GetRequiredService<IOptions<DeploymentPipelineOptions>>();
    var timeProvider = sp.GetRequiredService<TimeProvider>();
    var logger = sp.GetService<ILogger<HealthCheckFinalizeStep>>();
    return new HealthCheckFinalizeStep(
        scopeFactory,
        httpFactory.CreateClient("pipeline-health"),
        options,
        timeProvider,
        logger);
});
builder.Services.AddSingleton<ISshHostKeyPinSource, ServerHostKeyPinSource>();
builder.Services.AddScoped<ISshDeploymentClient, SshDeploymentClient>();
builder.Services.AddScoped<IDeploymentPipelineExecutor, DeploymentPipelineExecutor>();

builder.Services.AddScoped<Selfrun.Worker.Deletion.AppDeletionProcessor>();
builder.Services.AddHostedService<Selfrun.Worker.Deletion.AppDeletionWorker>();
builder.Services.AddScoped<Selfrun.Worker.Databases.DatabaseOperationProcessor>();
builder.Services.AddScoped<DeploymentLifecycleService>();
builder.Services.AddScoped<IAppSecretService, AppSecretService>();
builder.Services.AddScoped<DatabaseManagementService>();
builder.Services.AddScoped<DatabaseNetworkService>();
builder.Services.Configure<Selfrun.Worker.Databases.DatabasePlatformOptions>(builder.Configuration.GetSection(Selfrun.Worker.Databases.DatabasePlatformOptions.SectionName));
builder.Services.AddHostedService<Selfrun.Worker.Databases.DatabaseOperationWorker>();
builder.Services.Configure<Selfrun.Worker.Power.EnvironmentPowerOptions>(
    builder.Configuration.GetSection(Selfrun.Worker.Power.EnvironmentPowerOptions.SectionName));
builder.Services.AddScoped<Selfrun.Worker.Power.EnvironmentPowerProcessor>();

// Maintenance authority: every claim loop validates this process's epoch/host before claiming work.
builder.Services.AddSelfrunMaintenanceParticipant(builder.Configuration, "worker");
builder.Services.AddHostedService<Selfrun.Worker.Maintenance.MaintenanceParticipantRegistrationService>();
builder.Services.AddHostedService<Selfrun.Worker.Power.EnvironmentPowerWorker>();

// Queue Consumer Background Service
builder.Services.AddSingleton<PostgresDeploymentQueueConsumer>();
builder.Services.AddSingleton<IDeploymentQueueConsumer>(sp => sp.GetRequiredService<PostgresDeploymentQueueConsumer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PostgresDeploymentQueueConsumer>());

if (builder.Configuration.GetValue<bool>("OperatorProfiling:Enabled"))
{
    builder.AddOperatorProfiling();
    builder.Services.AddSingleton<WorkerDiagnostics>(_ => new WorkerDiagnostics());
    builder.Services.AddHostedService<OperatorDiagnosticSummaryService>();
}

var host = builder.Build();
await host.RunAsync();

public partial class Program;
