using System.Diagnostics;
using System.Text.Json;

namespace Minicloud.Tests.DeploymentIntegration;

public sealed class DeploymentIntegrationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string SuiteDirectory = Path.Combine(
        RepositoryRoot, "modules", "tests", "Minicloud.Tests", "DeploymentIntegration");

    [Fact]
    public async Task ScenarioManifestMatchesSpecification()
    {
        var result = await RunPythonAsync("validate_coverage.py", TimeSpan.FromMinutes(1));
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("47 scenarios", result.Output);
    }

    [Fact]
    public async Task PackagedBackendWorkersClaimAndRecoverPostgresJobs()
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        var result = await RunPythonAsync("component_smoke.py", TimeSpan.FromMinutes(30), runId);
        Assert.True(result.ExitCode == 0, result.Output);
        using var evidence = JsonDocument.Parse(result.StandardOutput);
        var root = evidence.RootElement;
        Assert.Equal("passed", root.GetProperty("status").GetString());
        Assert.Equal("passed", root.GetProperty("cleanup").GetString());
        Assert.Equal(runId, root.GetProperty("runId").GetString());
        Assert.True(root.GetProperty("claimEvents").GetInt32() >= 6);
        Assert.True(root.GetProperty("migrationCount").GetInt32() > 0);
        Assert.Equal("failed_without_requeue", root.GetProperty("staleJob").GetString());
        Assert.Equal("unclaimable_same_app_excluded_abandoned_expired", root.GetProperty("localBuildWaiting").GetString());
        Assert.Equal("rejected", root.GetProperty("staleFinalization").GetString());
        Assert.Equal("exclusive_fifo", root.GetProperty("sameAppOrdering").GetString());
        Assert.Equal("excluded_canceled_or_deleted", root.GetProperty("deleteClaimRace").GetString());
        Assert.Equal("rejected_before_remote_mutation", root.GetProperty("serverCollision").GetString());
        Assert.Equal("recovered_without_reclaim", root.GetProperty("databasePartition").GetString());
    }

    [Fact]
    public async Task LocalImagesPushPullRunAndSurviveRegistryRestoreAndGc()
    {
        var result = await RunPythonAsync("registry_smoke.py", TimeSpan.FromMinutes(30), Guid.NewGuid().ToString("N")[..12]);
        Assert.True(result.ExitCode == 0, result.Output);
        using var evidence = JsonDocument.Parse(result.StandardOutput);
        var root = evidence.RootElement;
        Assert.Equal("passed", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("sourceZipCount").GetInt32());
        Assert.Equal("visible_before_build_same_record_admission_and_concurrent_exclusion", root.GetProperty("localBuildPreparation").GetString());
        Assert.Equal("passed", root.GetProperty("dockerPullAndHttp").GetString());
        Assert.Equal("passed", root.GetProperty("isolatedMetadataAndBlobRestore").GetString());
        Assert.Equal("passed", root.GetProperty("fencedGc").GetString());
        var pushes = root.GetProperty("pushes");
        Assert.True(pushes[1].GetProperty("uploadedBytes").GetInt64() < pushes[0].GetProperty("uploadedBytes").GetInt64());
    }

    private static async Task<(int ExitCode, string StandardOutput, string Output)> RunPythonAsync(
        string scriptName, TimeSpan timeout, string? runId = null)
    {
        var startInfo = new ProcessStartInfo("python3")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(SuiteDirectory, scriptName));
        if (runId is not null)
        {
            startInfo.Environment["MINICLOUD_TEST_RUN_ID"] = runId;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Python test process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            if (runId is not null)
            {
                await CleanupInterruptedRunAsync(runId);
            }
            throw new TimeoutException($"{scriptName} exceeded {timeout}.");
        }

        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0 && runId is not null)
        {
            await CleanupInterruptedRunAsync(runId);
        }
        return (process.ExitCode, stdout, $"{scriptName} exited {process.ExitCode}.\n{stdout}\n{stderr}");
    }

    private static async Task CleanupInterruptedRunAsync(string runId)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "compose", "-p", $"mcint{runId}", "-f",
                     Path.Combine(SuiteDirectory, "compose.component.yml"),
                     "down", "--volumes", "--remove-orphans", "--rmi", "local"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["TEST_POSTGRES_PASSWORD"] = "cleanup-only";
        startInfo.Environment["TEST_BAO_TOKEN"] = "cleanup-only";
        using var cleanup = Process.Start(startInfo);
        if (cleanup is null)
        {
            throw new InvalidOperationException("Could not start run-owned Docker cleanup.");
        }
        var output = cleanup.StandardOutput.ReadToEndAsync();
        var error = cleanup.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await cleanup.WaitForExitAsync(timeout.Token);
        if (cleanup.ExitCode != 0)
        {
            throw new InvalidOperationException($"Run-owned Docker cleanup failed: {await output}\n{await error}");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Minicloud.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "minicloud.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Cannot find repository root for deployment integration tests.");
    }
}
