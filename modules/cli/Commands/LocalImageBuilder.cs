// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Minicloud.Cli.Api;
using Minicloud.Cli.Config;
using Minicloud.Cli.Rendering;
using Minicloud.Common.Contracts;
using Minicloud.Common.Validation;

namespace Minicloud.Cli.Commands;

internal sealed class LocalImageBuilder(MinicloudApiClient api, LocalImageBuildRenderer output, string builderName)
{
    public static async Task<string> PreflightAsync(string platform, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        ct = timeout.Token;
        static bool LocalEndpoint(string endpoint) => endpoint.StartsWith("unix://", StringComparison.Ordinal) || endpoint.StartsWith("npipe://", StringComparison.Ordinal) ||
            (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback);
        var endpoint = Environment.GetEnvironmentVariable("DOCKER_HOST") ?? (await RunAsync(["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"], null, null, ct)).Trim();
        if (!LocalEndpoint(endpoint)) { throw new CliCommandException(CliExitCodes.ValidationError, "Local build requires a local Docker engine; the current Docker context points to a remote host."); }
        var context = (await RunAsync(["context", "show"], null, null, ct)).Trim();
        await RunAsync(["version", "--format", "{{.Server.Os}}"], null, null, ct);
        var inspection = await RunAsync(["buildx", "inspect"], null, null, ct);
        var fields = inspection.Split('\n').Select(x => x.Trim()).ToArray();
        var driver = fields.FirstOrDefault(x => x.StartsWith("Driver:", StringComparison.Ordinal))?[7..].Trim();
        if (driver is not ("docker" or "docker-container") || fields.Where(x => x.StartsWith("Endpoint:", StringComparison.Ordinal))
            .Select(x => x[9..].Trim()).Any(x => x != context && x != "default" && !LocalEndpoint(x)))
        { throw new CliCommandException(CliExitCodes.ValidationError, "Select a local docker or docker-container buildx builder before using local mode."); }
        var name = fields.First(x => x.StartsWith("Name:", StringComparison.Ordinal))[5..].Trim();
        var output = await RunAsync(["buildx", "inspect", "--builder", name, "--bootstrap"], null, null, ct);
        var platforms = output.Split('\n').FirstOrDefault(x => x.TrimStart().StartsWith("Platforms:", StringComparison.Ordinal));
        if (platforms is null || !platforms.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Any(x => x.TrimEnd('*') == platform))
        { throw new CliCommandException(CliExitCodes.ValidationError, $"The local Docker builder does not support {platform}. Configure Docker/buildx emulation or a compatible local builder."); }
        return name;
    }

    public async Task BuildAsync(string name, MinicloudServiceConfig service, ImageBuildTarget target, string localImage, CancellationToken ct)
    {
        var args = new List<string> { "buildx", "build", "--builder", builderName, "--platform", target.Platform,
            "--provenance=false", "--sbom=false", "--load", "--progress", "rawjson", "--tag", localImage, "--file", service.Dockerfile! };
        foreach (var pair in service.BuildEnv ?? new Dictionary<string, string>())
        {
            args.Add("--build-arg");
            args.Add(pair.Key + "=" + pair.Value);
        }
        args.Add(service.SourcePath!);

        output.Activity(name, "Building image");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var progress = new DockerBuildProgress(line => output.Activity(name, line), line => output.Output(name, line), line => line);
        try { await RunAsync(args, null, progress.Process, timeout.Token); }
        finally { progress.Flush(); }
    }

    public async Task<ImagePublicationResponse> PublishAsync(string appId, string name, ServiceInputSnapshot snapshot, ImageBuildTarget target, string localImage, CancellationToken ct)
    {
        var publication = await api.CreateImagePublicationAsync(appId, new CreateImagePublicationRequest(name,
            ServiceChecksumCalculator.Compute(name, snapshot.Service, snapshot), target, Guid.NewGuid().ToString("D")), ct);
        DirectoryInfo? directory = null;
        string? remoteImage = null;
        try
        {
            var credentials = await api.GetImagePublicationCredentialsAsync(appId, publication.Id, ct);
            remoteImage = credentials.Registry + "/" + publication.Repository + ":" + publication.CandidateTag;
            directory = Directory.CreateTempSubdirectory("minicloud-docker-");
            if (!OperatingSystem.IsWindows()) { directory.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute; }
            await WritePushConfigAsync(directory.FullName, credentials, ct);

            await RunAsync(["image", "tag", localImage, remoteImage], null, null, ct);
            var environment = new Dictionary<string, string> { ["DOCKER_CONFIG"] = directory.FullName };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            var prefix = publication.CandidateTag + ": digest: ";
            string? digest = null;
            await RunAsync(["image", "push", remoteImage], environment, line =>
            {
                output.Output(name, line.Replace(credentials.Password, "[redacted]", StringComparison.Ordinal));
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var value = line[prefix.Length..].Split(' ', 2)[0];
                    if (ImagePublicationValidation.IsDigest(value)) { Interlocked.Exchange(ref digest, value); }
                }
            }, timeout.Token);
            if (digest is null)
            {
                throw new CliCommandException(CliExitCodes.ValidationError, "Docker push did not report an image digest. Review the service build output.");
            }
            output.Activity(name, "Verifying image");
            var ready = await api.CompleteImagePublicationAsync(appId, publication.Id, digest, ct);
            output.Output(name, $"Image ready: {digest} ({ready.SizeBytes} bytes).");
            return ready;
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await api.CancelImagePublicationAsync(appId, publication.Id, cleanup.Token); } catch { }
            throw;
        }
        finally
        {
            try { directory?.Delete(true); }
            finally
            {
                if (remoteImage is not null) { await RemoveImagesAsync([remoteImage]); }
            }
        }
    }

    private static async Task WritePushConfigAsync(string directory, ImagePublicationCredentialsResponse credentials, CancellationToken ct)
    {
        var original = Environment.GetEnvironmentVariable("DOCKER_CONFIG") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
        var configFile = Path.Combine(original, "config.json");
        var config = File.Exists(configFile) ? JsonNode.Parse(await File.ReadAllTextAsync(configFile, ct))!.AsObject() : new JsonObject();
        config.Remove("credsStore");
        config.Remove("credHelpers");
        config["auths"] = new JsonObject
        {
            [credentials.Registry] = new JsonObject { ["auth"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.Username + ":" + credentials.Password)) }
        };
        var contexts = Path.Combine(original, "contexts");
        if (Directory.Exists(contexts))
        {
            foreach (var file in Directory.EnumerateFiles(contexts, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(directory, "contexts", Path.GetRelativePath(contexts, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
        }
        var privateConfig = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(privateConfig, config.ToJsonString(), ct);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(privateConfig, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    public static async Task RemoveImagesAsync(IEnumerable<string> images)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await RunAsync(["image", "rm", "--no-prune", .. images], null, null, cleanup.Token); } catch { }
    }

    private static async Task<string> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, Action<string>? output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) { start.ArgumentList.Add(arg); }
        foreach (var pair in environment ?? new Dictionary<string, string>()) { start.Environment[pair.Key] = pair.Value; }
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new CliCommandException(CliExitCodes.ValidationError, "Local build requires Docker and buildx."); }
        using var registration = ct.Register(() => { try { if (!process.HasExited) { process.Kill(true); } } catch (InvalidOperationException) { } });
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync(ct) is { } line) { if (lines.Count < 200) { lines.Enqueue(line); } output?.Invoke(line); }
        }
        await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) { throw new CliCommandException(CliExitCodes.ValidationError, "Local Docker command failed. Review the service build output."); }
        return string.Join('\n', lines);
    }
}
