// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Collections.Concurrent;
using Minicloud.Cli.Api;
using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private const int DefaultBuildJobs = 2;

    private async Task<int> RunDeployAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.FirstOrDefault() == "cancel")
        {
            if (args.Length != 2 || args[1].StartsWith("-", StringComparison.Ordinal))
            {
                _console.WriteError("Usage: minicloud deploy cancel <deployment-id>");
                return CliExitCodes.ValidationError;
            }
            await _apiClient.CancelDeploymentAsync(args[1], cancellationToken);
            _console.WriteLine($"Cancellation requested for {args[1]}. Use 'minicloud status {args[1]} --watch' to follow completion. Changes already applied are not restored.");
            return CliExitCodes.Success;
        }
        var branchDeploy = args.FirstOrDefault() == "branch";
        if (branchDeploy)
        {
            args = args.Skip(1).ToArray();
        }

        var configPath = GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath();
        var databaseOverride = GetOption(args, "--database");
        var postgresPassword = GetOption(args, "--pgpassword");
        var noPublish = args.Contains("--no-publish", StringComparer.Ordinal);
        var buildMode = GetOption(args, "--build-mode");
        var hasBuildMode = args.Any(x => x == "--build-mode" || x.StartsWith("--build-mode=", StringComparison.Ordinal));
        if (hasBuildMode && (!ServiceBuildModes.IsSupported(buildMode) || noPublish))
        {
            _console.WriteError("Usage error: --build-mode requires source or local and cannot be combined with --no-publish.");
            return CliExitCodes.ValidationError;
        }
        var buildJobs = DefaultBuildJobs;
        var hasBuildJobs = args.Any(x => x == "--build-jobs" || x.StartsWith("--build-jobs=", StringComparison.Ordinal));
        if (hasBuildJobs &&
            (noPublish || buildMode == ServiceBuildModes.Source ||
             !int.TryParse(GetOption(args, "--build-jobs"), out buildJobs) ||
             buildJobs is < 1 or > MinicloudConfigValidator.MaxDeploymentServices))
        {
            _console.WriteError($"Usage error: --build-jobs requires an integer from 1 to {MinicloudConfigValidator.MaxDeploymentServices} and applies only to local builds.");
            return CliExitCodes.ValidationError;
        }
        var deployAll = branchDeploy ||
            args.Contains("--all", StringComparer.Ordinal) ||
            args.Any(arg => string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase));
        var requestedServiceNames = DeployServiceNamesFromArgs(args);
        if (args.Contains("--publish-only", StringComparer.Ordinal))
        {
            _console.WriteError("Usage error: --publish-only has been removed.");
            return CliExitCodes.ValidationError;
        }

        if (deployAll && requestedServiceNames.Count > 0)
        {
            _console.WriteError("Usage error: --all cannot be combined with service names.");
            return CliExitCodes.ValidationError;
        }

        var configResult = MinicloudConfigLoader.Load(configPath);
        if (!configResult.IsValid || configResult.Config is null)
        {
            PrintDiagnostics(configResult.Diagnostics);
            return CliExitCodes.ValidationError;
        }

        var config = configResult.Config;
        if (!hasBuildJobs)
        {
            buildJobs = config.BuildJobs ?? DefaultBuildJobs;
        }
        if (hasBuildMode)
        {
            config = config with
            {
                Services = config.Services.ToDictionary(x => x.Key, x => string.IsNullOrWhiteSpace(x.Value.SourcePath) ? x.Value : x.Value with { BuildMode = buildMode! }, StringComparer.Ordinal)
            };
        }
        if (branchDeploy && requestedServiceNames.Count > 0)
        {
            _console.WriteError("Usage error: minicloud deploy branch deploys all configured services and does not accept service names.");
            return CliExitCodes.ValidationError;
        }

        var candidates = requestedServiceNames.Count > 0 && !deployAll
            ? config.Services.Where(x => requestedServiceNames.Contains(x.Key, StringComparer.Ordinal))
            : config.Services;
        var hasLocalCandidate = !noPublish && candidates.Any(x => x.Value.BuildMode == ServiceBuildModes.Local && !string.IsNullOrWhiteSpace(x.Value.SourcePath));

        var me = await _apiClient.GetMeAsync(cancellationToken);
        var app = await _apiClient.GetAppAsync(config.AppId!, cancellationToken);
        var organization = me.Organizations.FirstOrDefault(x => x.Id == app.OrganizationId);
        if (organization is null)
        {
            _console.WriteError($"Auth error: your token is not associated with app '{config.AppId}'.");
            return CliExitCodes.AuthError;
        }

        if (branchDeploy)
        {
            var gitBranch = CurrentGitBranch(Environment.CurrentDirectory);
            _console.WriteLine($"Branch: {gitBranch}");
            app = await _apiClient.EnsureBranchAsync(app.Id, new EnsureAppBranchRequest(gitBranch), cancellationToken);
            config = config with { AppId = app.Id };
            _console.WriteLine($"Branch app: {app.Name} ({app.Slug})");
        }

        ImageBuildTarget? buildTarget = null;
        string? localBuilderName = null;
        if (hasLocalCandidate)
        {
            try { buildTarget = await _apiClient.GetImageBuildTargetAsync(app.Id, cancellationToken); }
            catch (ApiException ex) when (ex.StatusCode == 404)
            { throw new CliCommandException(CliExitCodes.ValidationError, "local_build_unavailable: this API does not support local image publication."); }
            localBuilderName = await LocalImageBuilder.PreflightAsync(buildTarget.Platform, cancellationToken);
            config = config with { Services = config.Services.ToDictionary(x => x.Key, x => x.Value.BuildMode == ServiceBuildModes.Local && !string.IsNullOrWhiteSpace(x.Value.SourcePath) ? x.Value with { BuildPlatform = buildTarget.Platform } : x.Value) };
        }
        if (noPublish)
        { config = config with { Services = config.Services.ToDictionary(x => x.Key, x => x.Value with { BuildMode = ServiceBuildModes.Source }) }; }

        if (!noPublish)
        {
            foreach (var service in config.Services.Values.Where(s => !string.IsNullOrWhiteSpace(s.SourcePath)))
            {
                if (!File.Exists(EffectiveDockerfilePath(service))) { DockerfileGenerator.TryWriteDockerfile(service, out _, out _); }
            }
        }
        var snapshotStartedAt = DateTimeOffset.UtcNow;
        var snapshotTimer = Stopwatch.StartNew();
        using var inputSession = new DeploymentInputSession();
        var snapshots = config.Services.ToDictionary(x => x.Key, x => noPublish && (string.IsNullOrWhiteSpace(x.Value.SourcePath) || !File.Exists(EffectiveDockerfilePath(x.Value))) ? new ServiceInputSnapshot(x.Value, []) : inputSession.Get(x.Key, x.Value), StringComparer.Ordinal);
        WriteCliTiming("input_snapshot", snapshotStartedAt, snapshotTimer.Elapsed, inputSession.SourceFilesRead);
        _timingSink.RecordTiming($"Inputs: unique_files={inputSession.SourceFilesRead} captured_bytes={inputSession.CapturedBytes} snapshot_bytes={inputSession.SnapshotBytes} services={config.Services.Count}");
        var selectedServiceNames = await ResolveDeployServiceNamesAsync(config, app.Id, requestedServiceNames, deployAll, snapshots, cancellationToken);
        if (selectedServiceNames.Count == 0)
        {
            return CliExitCodes.Success;
        }

        config = FilterConfigServices(config, selectedServiceNames);

        if (!noPublish)
        {
            var deploymentModeDiagnostics = ValidateDeploymentSources(config);
            if (deploymentModeDiagnostics.Count > 0)
            {
                PrintDiagnostics(deploymentModeDiagnostics);
                return CliExitCodes.ValidationError;
            }
        }
        else
        {
            var imageDiagnostics = ValidateExplicitImagesForNoPublish(config);
            if (imageDiagnostics.Count > 0)
            {
                PrintDiagnostics(imageDiagnostics);
                return CliExitCodes.ValidationError;
            }
        }

        await ValidateDeploymentSecretsAsync(app.Id, config, cancellationToken);

        LocalBuildDeploymentSession? preparation = null;
        try
        {
            var imagePublications = new ConcurrentDictionary<string, ImagePublicationResponse>(StringComparer.Ordinal);
            if (!noPublish && buildTarget is not null)
            {
                var localServices = config.Services.Where(x => x.Value.BuildMode == ServiceBuildModes.Local && !string.IsNullOrWhiteSpace(x.Value.SourcePath)).ToArray();
                foreach (var (name, service) in localServices)
                {
                    var diagnostics = ValidateDockerfileForService(name, service);
                    if (diagnostics.Count > 0) { PrintDiagnostics(diagnostics); return CliExitCodes.ValidationError; }
                    var framework = FrameworkDeploymentValidator.ValidatePublicHostCompatibility(name, service, organization.Slug, app.Slug, _environment.AppDomain);
                    if (framework.Count > 0) { PrintDiagnostics(framework); return CliExitCodes.ValidationError; }
                }
                if (localServices.Length > 0)
                {
                    var pending = await _apiClient.StartDeploymentPreparationAsync(new(app.Id, Guid.NewGuid().ToString("D"), selectedServiceNames), cancellationToken);
                    preparation = new LocalBuildDeploymentSession(_apiClient, app.Id, pending.Id, cancellationToken);
                    cancellationToken = preparation.Token;
                    _console.WriteLine($"Deployment {pending.Id}: waiting for local build.");
                    using var builds = new Rendering.LocalImageBuildRenderer(_console, app.Name, localServices.Select(x => x.Key).ToArray());
                    var builder = new LocalImageBuilder(_apiClient, builds, localBuilderName!);
                    var localImages = localServices.ToDictionary(x => x.Key, _ => $"minicloud-build:{Guid.NewGuid():N}", StringComparer.Ordinal);
                    try
                    {
                        await Parallel.ForEachAsync(
                            localServices,
                            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = buildJobs },
                            async (pair, ct) =>
                            {
                                var name = pair.Key;
                                builds.Start(name, buildTarget.Platform);
                                try
                                {
                                    await builder.BuildAsync(name, snapshots[name].Service, buildTarget, localImages[name], ct);
                                    builds.Built(name);
                                }
                                catch (OperationCanceledException) { builds.Fail(name, cancelled: ct.IsCancellationRequested); throw; }
                                catch { builds.Fail(name, cancelled: false); throw; }
                            });
                        await Parallel.ForEachAsync(
                            localServices,
                            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 2 },
                            async (pair, ct) =>
                            {
                                var name = pair.Key;
                                builds.StartUpload(name);
                                try
                                {
                                    imagePublications[name] = await builder.PublishAsync(app.Id, name, snapshots[name], buildTarget, localImages[name], ct);
                                    builds.Complete(name);
                                }
                                catch (OperationCanceledException) { builds.Fail(name, cancelled: ct.IsCancellationRequested); throw; }
                                catch { builds.Fail(name, cancelled: false); throw; }
                            });
                    }
                    finally
                    {
                        await LocalImageBuilder.RemoveImagesAsync(localImages.Values);
                    }
                }
            }

            if (preparation is not null) { preparation.Stage = "preparation"; }
            var totalSecretsCount = CountLocalSecrets(config);
            var sourceServices = config.Services
                .Where(pair => pair.Value.BuildMode == ServiceBuildModes.Source && !string.IsNullOrWhiteSpace(pair.Value.SourcePath))
                .Select(x => x.Key)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            var plan = new Rendering.DeploymentPlan(
                HasSecretsStage: totalSecretsCount > 0,
                TotalSecrets: totalSecretsCount,
                HasArtifactsStage: !noPublish && sourceServices.Length > 0,
                SourceServices: sourceServices,
                HasDeployStage: true,
                SelectedServicesCount: config.Services.Count);

            await using var renderer = Rendering.DeploymentRendererFactory.Create(_console);
            renderer.Initialize(plan);

            if (totalSecretsCount > 0)
            {
                await SyncLocalSecretsAsync(app, config, renderer, cancellationToken);
            }

            IReadOnlyDictionary<string, string> serviceArtifactIds = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!noPublish)
            {
                serviceArtifactIds = await BundleAndUploadDeploymentArtifactsAsync(config, organization.Slug, app.Slug, renderer, snapshots, cancellationToken);
            }

            renderer.DeploymentCreationStarted();

            var hashStartedAt = DateTimeOffset.UtcNow;
            var hashTimer = Stopwatch.StartNew();
            var serviceHashes = config.Services.ToDictionary(
                x => x.Key,
                x => ServiceChecksumCalculator.Compute(x.Key, x.Value, snapshots[x.Key]),
                StringComparer.Ordinal);
            hashTimer.Stop();
            WriteCliTiming("hash", hashStartedAt, hashTimer.Elapsed, config.Services.Count);

            var request = new CreateDeploymentRequest(
                app.Id,
                ResolveDeploymentDatabase(databaseOverride, config),
                config.CommitSha,
                config.Services.Select(x => new DeploymentServiceRequest(
                    x.Key,
                    noPublish || (!serviceArtifactIds.ContainsKey(x.Key) && !imagePublications.ContainsKey(x.Key))
                        ? x.Value.Image
                        : null,
                    x.Value.Port!.Value,
                    x.Value.Public!.Value,
                    x.Value.Path!,
                    x.Value.HealthPath!,
                    x.Value.Env,
                    x.Value.SecretEnv,
                    serviceArtifactIds.TryGetValue(x.Key, out var artifactId) ? artifactId : null,
                    serviceHashes.GetValueOrDefault(x.Key), x.Value.AssetMounts, imagePublications.GetValueOrDefault(x.Key)?.Id)).ToArray(),
                postgresPassword, preparation?.Id);

            if (preparation is not null)
            {
                preparation.Stage = "admission";
                await preparation.StopHeartbeatAsync();
            }
            var created = await _apiClient.CreateDeploymentAsync(request, cancellationToken);
            preparation?.Submitted();
            renderer.DeploymentCreated(created.Id, created.Status);

            var finalDeployment = await PollDeploymentAsync(created.Id, created.Status, cancellationToken, renderer, plan.SelectedServicesCount);
            if (finalDeployment.Status == "succeeded")
            {
                var publicUrls = new List<(string ServiceName, string Url)>();
                foreach (var service in finalDeployment.Services.OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    foreach (var url in (service.Urls ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                    {
                        publicUrls.Add((service.Name, url));
                    }
                }

                renderer.DeploymentSucceeded(new Rendering.DeploymentSuccessResult(
                    finalDeployment.Services.Count,
                    finalDeployment.ConsoleUrl,
                    publicUrls));

                return CliExitCodes.Success;
            }

            renderer.DeploymentFailed(new Rendering.DeploymentFailureResult(
                finalDeployment.Id,
                finalDeployment.FailureCode ?? finalDeployment.Status,
                finalDeployment.FailureMessage,
                finalDeployment.ConsoleUrl));

            return CliExitCodes.DeploymentFailed;
        }
        catch
        {
            preparation?.ThrowIfLost();
            throw;
        }
        finally
        {
            if (preparation is not null) { await preparation.DisposeAsync(); }
        }
    }

    private async Task<int> RunBranchAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.FirstOrDefault() != "destroy")
        {
            _console.WriteError("Usage: minicloud branch destroy [branch] [--config minicloud.yml]");
            return CliExitCodes.ValidationError;
        }

        var configPath = GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath();
        var configResult = MinicloudConfigLoader.Load(configPath);
        if (!configResult.IsValid || string.IsNullOrWhiteSpace(configResult.Config?.AppId))
        {
            PrintDiagnostics(configResult.Diagnostics);
            return CliExitCodes.ValidationError;
        }

        var mainApp = await _apiClient.GetAppAsync(configResult.Config.AppId, cancellationToken);
        if (mainApp.ParentAppId is not null)
        {
            _console.WriteError("Config error: branch destroy must use the main app's minicloud.yml.");
            return CliExitCodes.ValidationError;
        }

        if (mainApp.Branches.Count == 0)
        {
            _console.WriteError($"App '{mainApp.Name}' has no branch deployments.");
            return CliExitCodes.ValidationError;
        }

        var requestedBranch = FirstPositionalArg(args.Skip(1).ToArray());
        AppBranchResponse branch;
        if (!string.IsNullOrWhiteSpace(requestedBranch))
        {
            branch = mainApp.Branches.FirstOrDefault(x =>
                    string.Equals(x.BranchName, requestedBranch, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Id, requestedBranch, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Branch deployment '{requestedBranch}' was not found.");
        }
        else if (mainApp.Branches.Count == 1)
        {
            branch = mainApp.Branches[0];
        }
        else
        {
            var selected = PromptSingleSelect(
                "Branch deployment",
                mainApp.Branches.OrderBy(x => x.BranchName, StringComparer.Ordinal)
                    .Select(x => (x.Id, $"{x.BranchName} ({FormatBranchUrls(x)})"))
                    .ToArray(),
                mainApp.Branches.OrderBy(x => x.BranchName, StringComparer.Ordinal).First().Id);
            branch = mainApp.Branches.Single(x => x.Id == selected);
        }

        if (!Confirm($"Destroy branch '{branch.BranchName}' and its Vultr VPS?", defaultValue: false))
        {
            _console.WriteLine("Branch destroy canceled.");
            return CliExitCodes.Success;
        }

        await _apiClient.DestroyBranchAsync(mainApp.Id, branch.Id, cancellationToken);
        _console.WriteLine($"Branch destroy queued: {branch.BranchName}");
        return CliExitCodes.Success;
    }

    internal static IReadOnlyList<string> DeployServiceNamesFromArgs(IReadOnlyList<string> args)
    {
        var names = new List<string>();
        var optionsWithValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "--config",
            "--database",
            "--pgpassword",
            "--tag",
            "--build-mode",
            "--build-jobs"
        };

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (optionsWithValues.Contains(arg))
            {
                i++;
                continue;
            }

            if (optionsWithValues.Any(option => arg.StartsWith(option + "=", StringComparison.Ordinal)))
            {
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            names.Add(arg);
        }

        return names;
    }

    internal static string CurrentGitBranch(string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList =
            {
                "rev-parse",
                "--abbrev-ref",
                "HEAD"
            }
        });
        if (process is null)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "Git error: could not inspect the current branch.");
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Git error: {error}");
        }

        if (string.IsNullOrWhiteSpace(output) || output == "HEAD")
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "Git error: branch deployments require a checked-out branch, not detached HEAD.");
        }

        return output;
    }

    private async Task<IReadOnlyList<string>> ResolveDeployServiceNamesAsync(
        MinicloudConfig config,
        string appId,
        IReadOnlyList<string> requestedServiceNames,
        bool deployAll,
        IReadOnlyDictionary<string, ServiceInputSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (deployAll)
        {
            foreach (var name in config.Services.Keys) { _timingSink.RecordTiming($"Selection: service={name} reason=forced_all"); }
            return config.Services.Keys.ToArray();
        }

        if (requestedServiceNames.Count > 0)
        {
            var missing = requestedServiceNames
                .Where(name => !config.Services.ContainsKey(name))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new CliCommandException(
                    CliExitCodes.ValidationError,
                    $"Config error: service '{missing[0]}' was not found in the selected config.");
            }

            foreach (var name in requestedServiceNames.Distinct(StringComparer.Ordinal)) { _timingSink.RecordTiming($"Selection: service={name} reason=explicit_selection"); }
            return requestedServiceNames.Distinct(StringComparer.Ordinal).ToArray();
        }

        var hashStartedAt = DateTimeOffset.UtcNow;
        var hashTimer = Stopwatch.StartNew();
        var computedHashes = config.Services.ToDictionary(
            x => x.Key,
            x => ServiceChecksumCalculator.Compute(x.Key, x.Value, snapshots[x.Key]),
            StringComparer.Ordinal);
        hashTimer.Stop();
        WriteCliTiming("change_detection_hash", hashStartedAt, hashTimer.Elapsed, config.Services.Count);

        IReadOnlyList<AppServiceInventoryResponse> activeServices;
        try
        {
            activeServices = await _apiClient.GetAppServicesAsync(appId, cancellationToken);
        }
        catch (ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            activeServices = [];
        }

        var activeHashes = activeServices
            .Where(x => !string.IsNullOrWhiteSpace(x.Sha256))
            .ToDictionary(x => x.Name, x => x.Sha256!, StringComparer.Ordinal);

        var changed = new List<string>();
        var unchanged = new List<string>();
        foreach (var serviceName in config.Services.Keys)
        {
            var currentHash = computedHashes[serviceName];
            if (!activeHashes.TryGetValue(serviceName, out var deployedHash) ||
                !string.Equals(currentHash, deployedHash, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(serviceName);
                _timingSink.RecordTiming($"Selection: service={serviceName} reason={(deployedHash is null ? "no_active_hash" : "inputs_changed")}");
            }
            else
            {
                unchanged.Add(serviceName);
                _timingSink.RecordTiming($"Selection: service={serviceName} reason=unchanged");
            }
        }

        if (changed.Count == 0)
        {
            _console.WriteLine("No services have changed since the last deployment.");
            _console.WriteLine("Use 'minicloud deploy all' to force deployment, or specify service name: minicloud deploy <service>.");
            return [];
        }

        if (unchanged.Count > 0)
        {
            _console.WriteLine($"Deploying changed services: {string.Join(", ", changed)} (skipping unchanged: {string.Join(", ", unchanged)})");
        }

        return changed;
    }

    internal static MinicloudConfig FilterConfigServices(MinicloudConfig config, IReadOnlyList<string> selectedServiceNames)
    {
        var selected = selectedServiceNames.ToHashSet(StringComparer.Ordinal);
        return new MinicloudConfig(
            config.App,
            config.Database,
            config.CommitSha,
            config.Services.Where(x => selected.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal))
        {
            AppId = config.AppId,
            BuildJobs = config.BuildJobs
        };
    }

    private async Task ValidateDeploymentSecretsAsync(string appId, MinicloudConfig config, CancellationToken cancellationToken)
    {
        var references = new List<DeploymentSecretServiceRequest>();
        foreach (var (serviceName, service) in config.Services)
        {
            if (service.SecretEnv is not { Count: > 0 })
            {
                continue;
            }
            var localPath = string.IsNullOrWhiteSpace(service.SourcePath) ? null : Path.Combine(service.SourcePath, LocalSecretsFile.FileName);
            var local = localPath is not null && File.Exists(localPath) ? LocalSecretsFile.Parse(localPath) : new Dictionary<string, string>();
            var remote = service.SecretEnv.Where(x => !local.ContainsKey(x.Value)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            if (remote.Count > 0)
            {
                references.Add(new DeploymentSecretServiceRequest(serviceName, remote));
            }
        }
        if (references.Count > 0)
        {
            await _apiClient.ValidateDeploymentSecretsAsync(new ValidateDeploymentSecretsRequest(appId, references), cancellationToken);
        }
    }

    private static int CountLocalSecrets(MinicloudConfig config)
    {
        var total = 0;
        foreach (var (_, service) in config.Services)
        {
            if (string.IsNullOrWhiteSpace(service.SourcePath)) continue;
            var path = Path.Combine(service.SourcePath, LocalSecretsFile.FileName);
            if (!File.Exists(path)) continue;
            total += LocalSecretsFile.Parse(path).Count;
        }
        return total;
    }

    private async Task SyncLocalSecretsAsync(AppResponse app, MinicloudConfig config, Rendering.IDeploymentRenderer renderer, CancellationToken cancellationToken)
    {
        foreach (var (serviceName, service) in config.Services)
        {
            if (string.IsNullOrWhiteSpace(service.SourcePath))
            {
                continue;
            }

            var secretsPath = Path.Combine(service.SourcePath, LocalSecretsFile.FileName);
            if (!File.Exists(secretsPath))
            {
                continue;
            }

            var secrets = LocalSecretsFile.Parse(secretsPath);
            if (secrets.Count == 0)
            {
                continue;
            }

            foreach (var (name, value) in secrets)
            {
                renderer.SecretStarted(serviceName, name);
                await _apiClient.SetSecretAsync(app.Id, new SetAppServiceSecretRequest(serviceName, name, value), cancellationToken);
                renderer.SecretCompleted(serviceName, name);
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> BundleAndUploadDeploymentArtifactsAsync(
        MinicloudConfig config,
        string organizationSlug,
        string appSlug,
        Rendering.IDeploymentRenderer renderer,
        IReadOnlyDictionary<string, ServiceInputSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var sourceServices = config.Services
            .Where(pair => pair.Value.BuildMode == ServiceBuildModes.Source && !string.IsNullOrWhiteSpace(pair.Value.SourcePath))
            .ToArray();

        foreach (var (serviceName, service) in sourceServices)
        {
            if (!File.Exists(EffectiveDockerfilePath(service)))
            {
                if (DockerfileGenerator.TryWriteDockerfile(service, out var generatedDockerfilePath, out var generationReason))
                {
                    // Generated Dockerfile
                }
                else if (!string.IsNullOrWhiteSpace(generationReason))
                {
                    _console.WriteError($"Unable to generate Dockerfile for {serviceName}: {generationReason}.");
                }
            }

            var dockerfileDiagnostics = ValidateDockerfileForService(serviceName, service);
            if (dockerfileDiagnostics.Count > 0)
            {
                PrintDiagnostics(dockerfileDiagnostics);
                throw new CliCommandException(CliExitCodes.ValidationError, "Dockerfile validation failed.");
            }

            var frameworkDiagnostics = FrameworkDeploymentValidator.ValidatePublicHostCompatibility(
                serviceName,
                service,
                organizationSlug,
                appSlug,
                _environment.AppDomain);
            if (frameworkDiagnostics.Count > 0)
            {
                PrintDiagnostics(frameworkDiagnostics);
                throw new CliCommandException(CliExitCodes.ValidationError, "Framework deployment validation failed.");
            }
        }

        var artifactIds = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var bundles = new ConcurrentDictionary<string, DeploymentArtifactBundle>(StringComparer.Ordinal);
        var effectiveConcurrency = Math.Min(2, sourceServices.Length);
        var artifactStartedAt = DateTimeOffset.UtcNow;
        var artifactTimer = Stopwatch.StartNew();
        _timingSink.RecordTiming($"Timing: phase=artifact_prepare started_at={artifactStartedAt:O} items={sourceServices.Length} effective_concurrency={effectiveConcurrency}");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "minicloud-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            await Parallel.ForEachAsync(
                sourceServices,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 2 },
                async (pair, ct) =>
                {
                    var (serviceName, service) = pair;
                    var bundleStartedAt = DateTimeOffset.UtcNow;
                    var bundleTimer = Stopwatch.StartNew();
                    renderer.ArtifactPackageStarted(serviceName);
                    var bundle = DeploymentArtifactBundler.Create(config.AppId!, serviceName, service, config.CommitSha, outputDirectory, snapshot: snapshots[serviceName]);
                    bundleTimer.Stop();
                    _timingSink.RecordTiming($"Timing: phase=package service={serviceName} started_at={bundleStartedAt:O} duration_ms={bundleTimer.ElapsedMilliseconds} artifact_bytes={bundle.SizeBytes} outcome=succeeded");
                    bundles[serviceName] = bundle;
                    renderer.ArtifactPackageCompleted(serviceName, bundle.SizeBytes);
                    await Task.CompletedTask;
                });

            await Parallel.ForEachAsync(
                sourceServices,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 2 },
                async (pair, ct) =>
                {
                    var (serviceName, _) = pair;
                    var bundle = bundles[serviceName];
                    renderer.ArtifactUploadStarted(serviceName, bundle.SizeBytes);
                    var createRequest = new CreateDeploymentArtifactRequest(
                        config.AppId!,
                        serviceName,
                        Path.GetFileName(bundle.ZipPath),
                        "application/zip",
                        bundle.SizeBytes,
                        bundle.Sha256,
                        bundle.Manifest);
                    var uploadStartedAt = DateTimeOffset.UtcNow;
                    var uploadTimer = Stopwatch.StartNew();
                    var created = await _apiClient.CreateDeploymentArtifactAsync(createRequest, ct);
                    var uploaded = await _apiClient.UploadDeploymentArtifactContentAsync(
                        created.Id, created.UploadUrl, bundle.ZipPath, bundle.Sha256, bundle.SizeBytes, ct);
                    uploadTimer.Stop();
                    _timingSink.RecordTiming($"Timing: phase=upload service={serviceName} started_at={uploadStartedAt:O} duration_ms={uploadTimer.ElapsedMilliseconds} artifact_bytes={bundle.SizeBytes} outcome=succeeded");
                    renderer.ArtifactUploadCompleted(serviceName, bundle.SizeBytes);
                    artifactIds[serviceName] = uploaded.Id;
                });
        }
        finally
        {
            foreach (var bundle in bundles.Values)
            {
                TryDeleteFile(bundle.ZipPath);
            }
            artifactTimer.Stop();
            _timingSink.RecordTiming($"Timing: phase=artifact_prepare started_at={artifactStartedAt:O} duration_ms={artifactTimer.ElapsedMilliseconds} items={sourceServices.Length} effective_concurrency={effectiveConcurrency} outcome={(artifactIds.Count == sourceServices.Length ? "succeeded" : "incomplete")}");
            try
            {
                if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only after all workers have stopped.
            }
        }

        return new Dictionary<string, string>(artifactIds, StringComparer.Ordinal);
    }

    private void WriteCliTiming(string phase, DateTimeOffset startedAt, TimeSpan elapsed, int itemCount)
    {
        var line = $"Timing: phase={phase} started_at={startedAt:O} duration_ms={(long)elapsed.TotalMilliseconds} items={itemCount} outcome=succeeded";
        _timingSink.RecordTiming(line);
        if (phase == "change_detection_hash")
        {
            _console.WriteLine(line);
        }
    }

    private IReadOnlyList<ConfigDiagnostic> ValidateDeploymentSources(MinicloudConfig config)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        foreach (var (serviceName, service) in config.Services)
        {
            var hasSource = !string.IsNullOrWhiteSpace(service.SourcePath);
            var hasImage = !string.IsNullOrWhiteSpace(service.Image);
            if (!hasSource && !hasImage)
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}", "Service must define sourcePath or image."));
            }
        }

        return diagnostics;
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    internal static IReadOnlyList<ConfigDiagnostic> ValidateDockerfileForService(string serviceName, MinicloudServiceConfig service)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var dockerfilePath = EffectiveDockerfilePath(service);
        if (!File.Exists(dockerfilePath))
        {
            diagnostics.Add(new ConfigDiagnostic(
                $"services.{serviceName}.dockerfile",
                $"Dockerfile '{dockerfilePath}' was not found. Add a Dockerfile or set services.{serviceName}.dockerfile."));
            return diagnostics;
        }

        var dockerfile = File.ReadAllLines(dockerfilePath);
        if (!DockerfileContainsInstruction(dockerfile, "FROM"))
        {
            diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}.dockerfile", "Dockerfile must contain at least one FROM instruction."));
        }

        if (!DockerfileContainsInstruction(dockerfile, "CMD") && !DockerfileContainsInstruction(dockerfile, "ENTRYPOINT"))
        {
            diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}.dockerfile", "Dockerfile must define CMD or ENTRYPOINT so the service starts when deployed."));
        }

        if (service.Port is { } port && !DockerfileExposesPort(dockerfile, port))
        {
            diagnostics.Add(new ConfigDiagnostic(
                $"services.{serviceName}.port",
                $"Dockerfile must include EXPOSE {port} to match services.{serviceName}.port."));
        }

        return diagnostics;
    }

    internal static string EffectiveDockerfilePath(MinicloudServiceConfig service) =>
        string.IsNullOrWhiteSpace(service.Dockerfile)
            ? Path.Combine(service.SourcePath!, "Dockerfile")
            : service.Dockerfile;

    private static bool DockerfileContainsInstruction(IEnumerable<string> dockerfile, string instruction) =>
        dockerfile.Any(line => DockerfileInstruction(line).Equals(instruction, StringComparison.OrdinalIgnoreCase));

    private static bool DockerfileExposesPort(IEnumerable<string> dockerfile, int port) =>
        DockerfilePorts.Read(dockerfile).Contains(port);

    private static string DockerfileInstruction(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return "";
        }

        var separatorIndex = trimmed.IndexOfAny([' ', '\t']);
        return separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];
    }

    private static IEnumerable<string> DockerfileArguments(string line)
    {
        var trimmed = line.TrimStart();
        var separatorIndex = trimmed.IndexOfAny([' ', '\t']);
        if (separatorIndex < 0)
        {
            return [];
        }

        return trimmed[(separatorIndex + 1)..]
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(argument => argument.Trim());
    }

    private IReadOnlyList<ConfigDiagnostic> ValidateExplicitImagesForNoPublish(MinicloudConfig config)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        foreach (var (serviceName, service) in config.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Image))
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}.image", "Image is required when using --no-publish."));
            }
        }

        return diagnostics;
    }

}
