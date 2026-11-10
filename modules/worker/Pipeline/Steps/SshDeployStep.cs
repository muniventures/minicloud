using Minicloud.Common.Registry;
using Minicloud.Common.Validation;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Ssh;
using Minicloud.Worker.Storage;

namespace Minicloud.Worker.Pipeline.Steps;

/// <summary>
/// Step 3: SFTP the deployment payload, remote-deploy.sh and service zips to
/// the target VPS, execute remote-deploy.sh, and stream output line-by-line
/// into <c>DeploymentLogs</c> (batched). The runtime emits a phase-specific
/// failure marker when it can classify the failure.
/// </summary>
public sealed partial class SshDeployStep : IPipelineStep
{
    public const string FailureCode = "docker_build_failed";

    public string Name => "ssh-deploy";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IArtifactRetriever _artifactRetriever;
    private readonly ISshDeploymentClient _sshClient;
    private readonly SshDeploymentOptions _sshOptions;
    private readonly DeploymentPipelineOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SshDeployStep>? _logger;
    private readonly IRuntimeAgentDeployment? _runtimeAgent;

    public SshDeployStep(
        IServiceScopeFactory scopeFactory,
        IArtifactRetriever artifactRetriever,
        ISshDeploymentClient sshClient,
        IOptions<SshDeploymentOptions> sshOptions,
        IOptions<DeploymentPipelineOptions> options,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<SshDeployStep>? logger = null,
        IRuntimeAgentDeployment? runtimeAgent = null)
        : this(scopeFactory, artifactRetriever, sshClient, sshOptions.Value, options.Value, environment, timeProvider, logger, runtimeAgent)
    {
    }

    public SshDeployStep(
        IServiceScopeFactory scopeFactory,
        IArtifactRetriever artifactRetriever,
        ISshDeploymentClient sshClient,
        SshDeploymentOptions sshOptions,
        DeploymentPipelineOptions options,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<SshDeployStep>? logger = null,
        IRuntimeAgentDeployment? runtimeAgent = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(artifactRetriever);
        ArgumentNullException.ThrowIfNull(sshClient);
        ArgumentNullException.ThrowIfNull(sshOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _artifactRetriever = artifactRetriever;
        _sshClient = sshClient;
        _sshOptions = sshOptions;
        _options = options;
        _environment = environment;
        _timeProvider = timeProvider;
        _logger = logger;
        _runtimeAgent = runtimeAgent;
    }

    public async Task ExecuteAsync(PipelineStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.ServerIp))
        {
            throw new PipelineStepException(FailureCode, "SSH deploy requires a provisioned server IP.");
        }

        var deployment = await LoadDeploymentAsync(context, cancellationToken);
        var privateKey = await ResolvePrivateKeyAsync(cancellationToken);
        var scriptContent = await LoadRemoteDeployScriptAsync(cancellationToken);
        if (deployment.AssetReleaseId is not null)
        {
            await PublishAssetAsync(context, deployment, privateKey, scriptContent, cancellationToken);
            return;
        }
        var retainedImageOwners = await ResolveRetainedImageOwnersAsync(deployment, cancellationToken);

        await VerifyRetainedImagesAsync(context, retainedImageOwners, privateKey, cancellationToken);

        context.RemoteDirectory = RemoteDirectoryFor(context.DeploymentId);
        var agentTimer = System.Diagnostics.Stopwatch.StartNew();
        using var agentProbeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        agentProbeCts.CancelAfter(TimeSpan.FromSeconds(15));
        var agent = _runtimeAgent is null ? null : await _runtimeAgent.PrepareAsync(context,
            command => _sshClient.ExecuteCommandAsync(context.ServerIp,
                string.IsNullOrWhiteSpace(context.SshUsername) ? _sshOptions.Username : context.SshUsername,
                privateKey, command, (_, _) => Task.CompletedTask, agentProbeCts.Token), cancellationToken);
        if (agent is not null)
        {
            await RecordEventAsync(context.DeploymentId, EventLevel.Info,
                $"Runtime agent: action={agent.Action} reason={agent.Reason} uploads={agent.Uploads.Count} credentials={(agent.CreatesCredential ? 1 : 0)} preparation_ms={agentTimer.ElapsedMilliseconds}.", cancellationToken);
        }
        var payloadJson = await BuildPayloadJsonAsync(context, deployment, retainedImageOwners, cancellationToken, agent);
        var uploads = BuildArtifactUploads(deployment, retainedImageOwners);
        uploads.Add(RuntimeHelperUpload("runtime-maintenance.py"));
        if (deployment.Services.Any(s => s.StorageJson is not null)) { uploads.Add(RuntimeHelperUpload("storage-volume.py")); }
        if (deployment.Services.Any(s => s.AssetMountsJson is not null)) { uploads.Add(AssetHelperUpload()); }
        if (agent is not null)
        {
            uploads.AddRange(agent.Uploads);
        }

        var logWriter = new DeploymentLogWriter(
            _scopeFactory, _timeProvider, context.DeploymentId, _options.LogBatchSize);

        await RecordEventAsync(context.DeploymentId, EventLevel.Info,
            $"Transferring deployment payload to {context.ServerIp} ({uploads.Count} artifact(s)).",
            cancellationToken);

        SshDeploymentResult result;
        try
        {
            result = await _sshClient.ExecuteDeploymentAsync(
                new SshDeploymentRequest(
                    context.ServerIp,
                    string.IsNullOrWhiteSpace(context.SshUsername) ? _sshOptions.Username : context.SshUsername,
                    privateKey,
                    context.RemoteDirectory,
                    payloadJson,
                    scriptContent,
                    uploads,
                    _options.SshCommandTimeout),
                async (source, line) =>
                {
                    await logWriter.AppendAsync(ClassifySource(line), line, cancellationToken);
                },
                cancellationToken);
        }
        catch (PipelineStepException)
        {
            await logWriter.FlushAsync(CancellationToken.None);
            throw;
        }
        catch (RemoteQuiescenceException)
        {
            await logWriter.FlushAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await logWriter.FlushAsync(CancellationToken.None);
            throw new PipelineStepException(FailureCode, $"SSH deployment to {context.ServerIp} failed: {ex.Message}", ex);
        }

        await logWriter.FlushAsync(cancellationToken);

        if (result.ExitCode != 0 || !result.CompletionMarkerFound)
        {
            var failureCode = ResolveFailureCode(result.OutputLines);
            var failureLine = result.OutputLines.LastOrDefault(line =>
                line.StartsWith("ERROR: ", StringComparison.Ordinal));
            var excerpt = result.OutputLines.Count > 0
                ? string.Join("\n", result.OutputLines.TakeLast(5))
                : "no remote output captured";
            var failureSummary = string.IsNullOrWhiteSpace(failureLine)
                ? string.Empty
                : $" Runtime error: {failureLine}";
            throw new PipelineStepException(failureCode,
                $"remote-deploy.sh failed (exit {result.ExitCode}, completion marker {(result.CompletionMarkerFound ? "found" : "missing")}).{failureSummary} Last output:\n{excerpt}");
        }

        var observed = result.OutputLines.LastOrDefault(x => x.StartsWith("__MINICLOUD_BUILD_PLATFORM__=", StringComparison.Ordinal))?.Split('=', 2).Last();
        if (ImagePublicationValidation.IsPlatform(observed))
        {
            await using var observationScope = _scopeFactory.CreateAsyncScope();
            var observationDb = observationScope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            if (await observationDb.Deployments.AnyAsync(x => x.Id == context.DeploymentId && x.OwnershipToken == context.OwnershipToken, cancellationToken))
            {
                await observationDb.ServerInstances.Where(x => x.Id == context.ServerInstanceId && x.AppId == context.AppId && x.OrganizationId == context.OrganizationId)
                    .ExecuteUpdateAsync(x => x.SetProperty(s => s.BuildPlatform, observed).SetProperty(s => s.BuildPlatformObservedAt, _timeProvider.GetUtcNow()), cancellationToken);
            }
        }

        if (agent is not null)
        {
            await _runtimeAgent!.CompleteAsync(context, agent, cancellationToken);
        }

        _logger?.LogInformation(
            "Remote deploy completed for {DeploymentId} (sha {Sha})",
            context.DeploymentId, result.CompletionSha);
        await RecordEventAsync(context.DeploymentId, EventLevel.Info,
            "Remote deploy script completed successfully.", cancellationToken);
    }

    internal static string ResolveFailureCode(IReadOnlyList<string> outputLines)
    {
        const string marker = "__MINICLOUD_DEPLOY_FAILURE__=";
        var value = outputLines.LastOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal));
        if (value is null)
        {
            return FailureCode;
        }

        var failureCode = value[marker.Length..].Trim();
        return failureCode.Length > 0 && failureCode.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? failureCode
            : FailureCode;
    }

    private async Task VerifyRetainedImagesAsync(
        PipelineStepContext context,
        IReadOnlyDictionary<string, string> retainedImageOwners,
        string privateKey,
        CancellationToken cancellationToken)
    {
        if (retainedImageOwners.Count == 0)
        {
            return;
        }

        var username = string.IsNullOrWhiteSpace(context.SshUsername) ? _sshOptions.Username : context.SshUsername;
        var checks = retainedImageOwners
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"docker image inspect {EscapeShell($"minicloud/{context.AppSlug}-{item.Key}:{item.Value}")} >/dev/null 2>&1")
            .ToArray();
        var exitCode = await _sshClient.ExecuteCommandAsync(
            context.ServerIp!, username, privateKey, string.Join(" && ", checks), (_, _) => Task.CompletedTask, cancellationToken);
        if (exitCode != 0)
        {
            throw new PipelineStepException(
                FailureCode,
                "A retained service image is missing on the target server. Redeploy all affected services to rebuild the local images.");
        }
    }

    public async Task ReleaseRolloutGateAsync(PipelineStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.ServerIp) || string.IsNullOrWhiteSpace(context.RemoteDirectory) || string.IsNullOrWhiteSpace(context.OwnershipToken))
        {
            throw new PipelineStepException(FailureCode, "Cannot release rollout gate before SSH preparation starts.");
        }

        var privateKey = await ResolvePrivateKeyAsync(cancellationToken);
        var username = string.IsNullOrWhiteSpace(context.SshUsername) ? _sshOptions.Username : context.SshUsername;
        var preparationPath = $"{context.RemoteDirectory}/preparation.ready";
        var gatePath = $"{context.RemoteDirectory}/rollout.ready";
        var preparationReady = $"test \"$(cat {EscapeShell(preparationPath)} 2>/dev/null)\" = {EscapeShell(context.OwnershipToken)}";
        var waitCommand =
            $"umask 077; i=0; while ! {preparationReady} && test $i -lt 720; " +
            "do sleep 1; i=$((i+1)); done; " +
            preparationReady;
        var exitCode = await _sshClient.ExecuteCommandAsync(
            context.ServerIp, username, privateKey, waitCommand, (_, _) => Task.CompletedTask, cancellationToken);
        if (exitCode != 0)
        {
            throw new PipelineStepException(FailureCode, $"SSH preparation did not become ready on {context.ServerIp}.");
        }

        await EnsureOwnershipAsync(context, cancellationToken);
        exitCode = await _sshClient.ExecuteCommandAsync(
            context.ServerIp, username, privateKey,
            $"umask 077 && : > {EscapeShell(gatePath)}", (_, _) => Task.CompletedTask, cancellationToken);
        if (exitCode != 0)
        {
            throw new PipelineStepException(FailureCode, $"Failed to release rollout gate on {context.ServerIp}.");
        }
    }

    private async Task EnsureOwnershipAsync(PipelineStepContext context, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        if (!await db.Deployments.AsNoTracking().AnyAsync(
                deployment => deployment.Id == context.DeploymentId &&
                    deployment.OwnershipToken == context.OwnershipToken && deployment.CancellationRequestedAt == null,
                cancellationToken))
        {
            throw new OperationCanceledException("Deployment ownership was lost before rollout.", cancellationToken);
        }
    }

    private async Task<Deployment> LoadDeploymentAsync(
        PipelineStepContext context, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var deployment = await db.Deployments
            .AsNoTracking()
            .Include(d => d.Services)
            .ThenInclude(s => s.Artifact)
            .Include(d => d.Services).ThenInclude(s => s.ImagePublication)
            .Include(d => d.AssetRelease).ThenInclude(a => a!.Artifact)
            .Include(d => d.App)
            .FirstOrDefaultAsync(d => d.Id == context.DeploymentId &&
                d.OwnershipToken == context.OwnershipToken, cancellationToken);
        if (deployment is null)
        {
            throw new PipelineStepException(FailureCode, $"Deployment {context.DeploymentId} not found.");
        }

        if (!string.Equals(deployment.OrganizationId, context.OrganizationId, StringComparison.Ordinal) ||
            !string.Equals(deployment.AppId, context.AppId, StringComparison.Ordinal))
        {
            throw new PipelineStepException(FailureCode, "Deployment organization/app scope mismatch.");
        }

        return deployment;
    }

    private async Task<string> ResolvePrivateKeyAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_sshOptions.PrivateKeyContent))
        {
            return _sshOptions.PrivateKeyContent;
        }

        if (!string.IsNullOrWhiteSpace(_sshOptions.PrivateKeyPath) && File.Exists(_sshOptions.PrivateKeyPath))
        {
            return await File.ReadAllTextAsync(_sshOptions.PrivateKeyPath, cancellationToken);
        }

        var fallbackPath = Path.Combine(_environment.ContentRootPath, "minicloud-deploy-key");
        if (File.Exists(fallbackPath))
        {
            return await File.ReadAllTextAsync(fallbackPath, cancellationToken);
        }

        throw new PipelineStepException(FailureCode,
            "No SSH deployment private key configured. Set Worker:Ssh:PrivateKeyPath or Worker:Ssh:PrivateKeyContent.");
    }

    private async Task<string> LoadRemoteDeployScriptAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.RemoteDeployScriptPath))
        {
            candidates.Add(_options.RemoteDeployScriptPath);
        }

        candidates.Add(Path.Combine(_environment.ContentRootPath, "scripts", "github-actions", "minicloud-deploy", "remote-deploy.sh"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "scripts", "github-actions", "minicloud-deploy", "remote-deploy.sh"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "remote-deploy.sh"));

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return await File.ReadAllTextAsync(candidate, cancellationToken);
            }
        }

        throw new PipelineStepException(FailureCode,
            "remote-deploy.sh not found. Set Worker:Pipeline:RemoteDeployScriptPath.");
    }

    private async Task<string> BuildPayloadJsonAsync(
        PipelineStepContext context,
        Deployment deployment,
        IReadOnlyDictionary<string, string> retainedImageOwners,
        CancellationToken cancellationToken,
        RuntimeAgentPackage? agent = null)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        string? postgresPassword = null;
        if (deployment.Database == DatabaseMode.Postgres)
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var secret = await db.AppSecrets.AsNoTracking().SingleOrDefaultAsync(
                s => s.OrganizationId == context.OrganizationId && s.AppId == context.AppId && s.Name == AppSecretService.PostgresPasswordSecretName,
                cancellationToken);
            if (secret is null)
            {
                throw new PipelineStepException(FailureCode, "Postgres password secret is missing for this app.");
            }

            var encryption = scope.ServiceProvider.GetRequiredService<SecretEncryptionService>();
            postgresPassword = encryption.Decrypt(secret.EncryptedValue);
            if (string.IsNullOrWhiteSpace(postgresPassword))
            {
                throw new PipelineStepException(FailureCode, "Postgres password secret is empty for this app.");
            }
        }

        var secretRequests = deployment.Services.Select(s => new DeploymentServiceRequest(
            s.Name, s.Image, s.Port, s.Public, s.Path, s.HealthPath,
            DeserializeEnv(s.EnvJson), DeserializeEnv(s.SecretEnvJson), s.ArtifactId)).ToArray();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> resolvedSecrets;
        try
        {
            resolvedSecrets = await scope.ServiceProvider.GetRequiredService<IAppServiceSecretResolver>()
                .ResolveForDeploymentAsync(context.AppId, secretRequests, cancellationToken);
        }
        catch (MissingAppServiceSecretException ex)
        {
            throw new PipelineStepException("missing_service_secret", ex.Message, ex);
        }
        catch (SecretStoreUnavailableException ex)
        {
            throw new PipelineStepException("secret_store_unavailable", "Minicloud could not read deployment secrets. Retry when the secret store is available.", ex);
        }

        var databaseContext = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var databaseBindings = await ResolveDatabaseBindingsAsync(databaseContext, scope.ServiceProvider.GetService<SecretEncryptionService>(), context.OrganizationId, context.AppId, deployment.Services.Select(x => x.Name).ToHashSet(StringComparer.Ordinal), cancellationToken);
        var managedDatabaseServer = deployment.Database == DatabaseMode.Postgres &&
            await databaseContext.ManagedDatabaseServers.AsNoTracking().AnyAsync(x => x.OrganizationId == context.OrganizationId && x.RuntimeAppId == context.AppId && x.Status != "deleted", cancellationToken);
        var storageMounts = await ResolveStorageAsync(context, deployment, cancellationToken);
        var assetMounts = await ResolveAssetMountsAsync(context, deployment.Services, cancellationToken);
        var services = deployment.Services
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new
            {
                name = s.Name,
                managed = s.ImagePublicationId is not null,
                image = !string.IsNullOrWhiteSpace(s.ArtifactId) || string.IsNullOrWhiteSpace(s.Image)
                    ? $"minicloud/{context.AppSlug}-{s.Name}:{retainedImageOwners.GetValueOrDefault(s.Name, context.DeploymentId)}"
                    : s.Image,
                port = s.Port,
                @public = s.Public,
                path = string.IsNullOrWhiteSpace(s.Path) ? "/" : s.Path,
                health_path = string.IsNullOrWhiteSpace(s.HealthPath) ? "/health" : s.HealthPath,
                hosts = ResolveServiceHosts(context, s),
                asset_mounts = assetMounts.GetValueOrDefault(s.Name, []),
                storage_mounts = storageMounts.GetValueOrDefault(s.Name, []).Select(m => new { name = m.Name, volume_id = m.VolumeId, mount_path = m.MountPath, uid = m.Uid, gid = m.Gid }).ToArray(),
                managed_database_binding = databaseBindings.ContainsKey(s.Name),
                env = MergeEnvironment(MergeEnvironment(DeserializeEnv(s.EnvJson), resolvedSecrets.GetValueOrDefault(s.Name)), databaseBindings.GetValueOrDefault(s.Name)),
            })
            .ToArray();

        var buildArtifacts = deployment.Services
            .Where(s => !string.IsNullOrWhiteSpace(s.ArtifactId) && !retainedImageOwners.ContainsKey(s.Name))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new
            {
                service_name = s.Name,
                artifact_archive = $"{context.RemoteDirectory}/{s.Name}.zip",
                image_tag = $"minicloud/{context.AppSlug}-{s.Name}:{context.DeploymentId}",
            })
            .ToArray();

        var managedImages = new List<object>();
        var imageService = scope.ServiceProvider.GetService<ImagePublicationService>();
        foreach (var service in deployment.Services.Where(x => x.ImagePublicationId is not null))
        {
            var publication = service.ImagePublication ?? throw new PipelineStepException("image_incomplete", "Managed image publication is missing.");
            if (imageService is null) { throw new PipelineStepException("local_build_unavailable", "Managed image services are unavailable."); }
            var credentials = await imageService.PullCredentialsAsync(deployment, publication, context.ServerInstanceId, cancellationToken);
            managedImages.Add(new
            {
                name = service.Name,
                image = service.Image,
                platform = publication.Platform,
                registry = credentials.Registry,
                username = credentials.Username,
                password = credentials.Password
            });
        }

        var payload = new
        {
            app_name = context.AppSlug,
            customer = context.OrganizationSlug,
            app_env = "production",
            deployment_type = "worker",
            deployment_id = context.DeploymentId,
            github_sha = string.IsNullOrWhiteSpace(context.CommitSha) ? context.DeploymentId : context.CommitSha,
            domain = context.PrimaryHostname,
            database = deployment.Database.ToString().ToLowerInvariant(),
            managed_database_server = managedDatabaseServer,
            postgres_password = postgresPassword,
            postgres_password_update = postgresPassword is not null,
            runtime_agent = agent is null ? null : new { config = agent.Configuration, token = agent.Token, action = agent.Action, reason = agent.Reason, verification = agent.Verification, final_verification = agent.FinalVerification },
            services,
            build_artifacts = buildArtifacts,
            managed_images = managedImages,
            managed_image_scope = imageService?.Enabled == true ? imageService.RepositoryScope(context.OrganizationId, context.AppId) : null,
            build_cache = new
            {
                max_bytes = _options.BuildCacheMaxBytes,
                max_age_hours = _options.BuildCacheMaxAgeHours,
                min_free_bytes = _options.BuildCacheMinFreeBytes,
                min_free_percent = _options.BuildCacheMinFreePercent,
            },
            max_parallel_builds = _options.MaxParallelBuilds,
            max_parallel_network_operations = _options.MaxParallelNetworkOperations,
            ownership_token = context.OwnershipToken,
            worker_lease_file = $"{context.RemoteDirectory}/worker.lease",
            rollout_gate_file = context.RequireRolloutGate
                ? $"{context.RemoteDirectory}/rollout.ready"
                : null,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static IReadOnlyDictionary<string, string>? MergeEnvironment(
        IReadOnlyDictionary<string, string>? plain,
        IReadOnlyDictionary<string, string>? secrets)
    {
        if (secrets is null || secrets.Count == 0) return plain;
        var merged = new Dictionary<string, string>(plain ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (name, value) in secrets) merged[name] = value;
        return merged;
    }

    public static string RemoteDirectoryFor(string deploymentId) =>
        $"/tmp/minicloud-deploy-{SanitizeId(deploymentId)}";

    private static string EscapeShell(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    internal static string[] ResolveServiceHosts(
        PipelineStepContext context, DeploymentService service)
    {
        if (!service.Public)
        {
            return [];
        }

        return context.ServiceHostnames.GetValueOrDefault(service.Name) ?? [];
    }

    private List<SshDeploymentArtifactUpload> BuildArtifactUploads(
        Deployment deployment,
        IReadOnlyDictionary<string, string> retainedImageOwners)
    {
        var uploads = new List<SshDeploymentArtifactUpload>();
        foreach (var service in deployment.Services
                     .Where(s => !string.IsNullOrWhiteSpace(s.ArtifactId) && !retainedImageOwners.ContainsKey(s.Name))
                     .OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var artifactId = service.ArtifactId!;
            var serviceName = service.Name;
            var retriever = _artifactRetriever;
            uploads.Add(new SshDeploymentArtifactUpload(
                serviceName,
                $"{serviceName}.zip",
                ct => retriever.OpenArtifactStreamAsync(artifactId, ct),
                service.Artifact?.SizeBytes ?? 0));
        }

        return uploads;
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveRetainedImageOwnersAsync(
        Deployment deployment,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var successfulUnordered = await db.Deployments
            .AsNoTracking()
            .Include(item => item.Services)
            .Where(item => item.AppId == deployment.AppId && item.Status == DeploymentStatus.Succeeded)
            .ToListAsync(cancellationToken);
        var successful = successfulUnordered
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal);

        var active = new Dictionary<string, (string? ArtifactId, string DeploymentId)>(StringComparer.Ordinal);
        foreach (var prior in successful)
        {
            foreach (var service in prior.Services)
            {
                var prefix = $"minicloud/{deployment.App.Slug}-{service.Name}:";
                var owner = service.Image?.StartsWith(prefix, StringComparison.Ordinal) == true ? service.Image[prefix.Length..] : prior.Id;
                active[service.Name] = (service.ArtifactId, owner);
            }
        }

        return deployment.Services
            .Where(service => !string.IsNullOrWhiteSpace(service.ArtifactId) &&
                active.TryGetValue(service.Name, out var prior) &&
                string.Equals(prior.ArtifactId, service.ArtifactId, StringComparison.Ordinal))
            .ToDictionary(service => service.Name, service => active[service.Name].DeploymentId, StringComparer.Ordinal);
    }

    internal static string ClassifySource(string line)
    {
        if (line.StartsWith("[build:", StringComparison.OrdinalIgnoreCase))
        {
            return "build";
        }

        if (line.Contains("compose", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Container ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Network ", StringComparison.OrdinalIgnoreCase))
        {
            return "compose";
        }

        if (line.Contains("health", StringComparison.OrdinalIgnoreCase))
        {
            return "health";
        }

        return "deploy";
    }

    internal static string SanitizeId(string deploymentId)
    {
        var sanitized = new string(deploymentId.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "deploy" : sanitized;
    }

    private static IReadOnlyDictionary<string, string>? DeserializeEnv(string? envJson)
    {
        if (string.IsNullOrWhiteSpace(envJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(envJson, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private Task RecordEventAsync(
        string deploymentId, EventLevel level, string message, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            PipelineDb.AddEvent(db, deploymentId, level, message, now);
            var deployment = await db.Deployments
                .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
            if (deployment is not null)
            {
                PipelineDb.Touch(deployment, now);
            }

            await Task.CompletedTask;
        }, cancellationToken);
    }
}
