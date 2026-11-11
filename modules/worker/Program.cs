using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Extensions;
using Minicloud.Common.Services;
using Minicloud.Worker;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Pipeline.Steps;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Dns;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Vultr;
using Minicloud.Worker.Queue;
using Minicloud.Worker.Ssh;
using Minicloud.Worker.Storage;
using Minicloud.Common.Registry;

var builder = Host.CreateApplicationBuilder(args);

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
        options.SshPublicKey = builder.Configuration["MINICLOUD_SSH_PUBLIC_KEY"]
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
            ?? builder.Configuration["MINICLOUD_SSH_PRIVATE_KEY"]
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
// Postgres provider, Minicloud/DefaultConnection/Database keys, DATABASE_URL,
// localhost fallback. URL normalization (postgres port default, sqlite://)
// is the canonical Common implementation.
builder.Services.AddMinicloudDbContext(new MinicloudDatabaseOptions
{
    Provider = builder.Configuration.GetValue<string>("Database:Provider") ?? "Postgres",
    MinicloudConnectionString = builder.Configuration.GetConnectionString("Minicloud"),
    DefaultConnectionString = builder.Configuration.GetConnectionString("DefaultConnection"),
    DatabaseConnectionString = builder.Configuration.GetConnectionString("Database"),
    DatabaseUrl = builder.Configuration["DATABASE_URL"],
    PasswordFile = builder.Configuration["Database:PasswordFile"]
});
builder.Services.AddMinicloudCommon();
builder.Services.AddMinicloudRegistry(x => builder.Configuration.GetSection("Registry").Bind(x), x => builder.Configuration.GetSection("Worker:Pipeline").Bind(x));
builder.Services.AddScoped<AuditEventService>();
builder.Services.AddScoped<ControlPlaneAuthorization>();
builder.Services.AddMinicloudSecrets(
    configureEncryption: options => builder.Configuration.GetSection(SecretEncryptionOptions.SectionName).Bind(options),
    configureStore: options => builder.Configuration.GetSection(OpenBaoSecretStoreOptions.SectionName).Bind(options));
builder.Services.AddHttpClient<ISecretStore, OpenBaoSecretStore>();
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
builder.Services.AddMinicloudProviders(builder.Configuration);
if (!builder.Configuration.GetValue<bool>("Providers:AllowLegacyConfiguration"))
{
    // A type-only resolver cannot identify an account. Keep legacy dispatch an
    // explicit, temporary upgrade option rather than silently using global keys.
    builder.Services.AddScoped<ProviderServiceCollectionExtensions.VpsProviderClientResolver>(sp => provider =>
        throw new ProviderCatalogException("legacy_provider_import_required", "Import legacy resources into organization provider accounts before resuming cloud operations."));
}
builder.Services.AddHttpClient("pipeline-health");
builder.Services.AddScoped<ResolveProvisionServerStep>();
builder.Services.Configure<ManagedDomainOptions>(builder.Configuration.GetSection(ManagedDomainOptions.SectionName));
builder.Services.AddScoped<DnsSetupStep>();
builder.Services.AddScoped<DomainDnsReconciler>();
builder.Services.AddHostedService<Minicloud.Worker.Maintenance.RuntimeLogRetentionWorker>();
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
builder.Services.AddScoped<ISshDeploymentClient, SshDeploymentClient>();
builder.Services.AddScoped<IDeploymentPipelineExecutor, DeploymentPipelineExecutor>();

builder.Services.AddScoped<Minicloud.Worker.Deletion.AppDeletionProcessor>();
builder.Services.AddHostedService<Minicloud.Worker.Deletion.AppDeletionWorker>();
builder.Services.AddScoped<Minicloud.Worker.Databases.DatabaseOperationProcessor>();
builder.Services.AddScoped<DeploymentLifecycleService>();
builder.Services.AddScoped<IAppSecretService, AppSecretService>();
builder.Services.AddScoped<DatabaseManagementService>();
builder.Services.AddScoped<DatabaseNetworkService>();
builder.Services.Configure<Minicloud.Worker.Databases.DatabasePlatformOptions>(builder.Configuration.GetSection(Minicloud.Worker.Databases.DatabasePlatformOptions.SectionName));
builder.Services.AddHostedService<Minicloud.Worker.Databases.DatabaseOperationWorker>();

// Queue Consumer Background Service
builder.Services.AddSingleton<PostgresDeploymentQueueConsumer>();
builder.Services.AddSingleton<IDeploymentQueueConsumer>(sp => sp.GetRequiredService<PostgresDeploymentQueueConsumer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PostgresDeploymentQueueConsumer>());

var host = builder.Build();
await host.RunAsync();

public partial class Program;
