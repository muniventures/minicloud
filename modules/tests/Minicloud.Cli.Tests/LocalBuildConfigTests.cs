// SPDX-License-Identifier: MPL-2.0

using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

namespace Minicloud.Tests;

public sealed class LocalBuildConfigTests
{
    [Theory]
    [InlineData(null, "source")]
    [InlineData("source", "source")]
    [InlineData("local", "local")]
    public void External_image_mode_roundtrips_without_building(string? value, string expected)
    {
        var result = Parse(value);
        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Config!.Services["api"].BuildMode);
        var yaml = MinicloudConfigWriter.Write(result.Config);
        var roundtrip = MinicloudConfigLoader.Parse(yaml.Split('\n'));
        Assert.True(roundtrip.IsValid);
        Assert.Equal(expected, roundtrip.Config!.Services["api"].BuildMode);
        if (expected == ServiceBuildModes.Source) { Assert.DoesNotContain("buildMode:", yaml); }
    }

    [Theory]
    [InlineData(null, "local")]
    [InlineData("local", "local")]
    [InlineData("source", "source")]
    public void Source_services_default_to_local_and_preserve_explicit_source(string? mode, string expected)
    {
        var lines = new List<string>
        {
            "app: demo", "appId: app_demo", "services:", "  api:",
            "    sourcePath: .", "    port: 8080", "    public: true", "    path: /", "    healthPath: /health"
        };
        if (mode is not null) { lines.Add($"    buildMode: {mode}"); }
        var result = MinicloudConfigLoader.Parse(lines);
        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Config!.Services["api"].BuildMode);
        var yaml = MinicloudConfigWriter.Write(result.Config);
        Assert.Equal(expected, MinicloudConfigLoader.Parse(yaml.Split('\n')).Config!.Services["api"].BuildMode);
        if (expected == "local") { Assert.DoesNotContain("buildMode:", yaml); }
        else { Assert.Contains("buildMode: source", yaml); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("LOCAL")]
    [InlineData("remote")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Invalid_modes_are_rejected_instead_of_defaulting(string value)
    {
        var result = Parse(value);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "services.api.buildMode");
    }

    [Fact]
    public void Configured_build_jobs_round_trip()
    {
        var yaml = "buildJobs: 5\n" + MinicloudConfigWriter.Write(Parse(null).Config!);
        var parsed = MinicloudConfigLoader.Parse(yaml.Split('\n'));
        Assert.True(parsed.IsValid);
        Assert.Equal(5, parsed.Config!.BuildJobs);
        Assert.Contains("buildJobs: 5", MinicloudConfigWriter.Write(parsed.Config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("many")]
    public void Invalid_build_jobs_are_rejected(string value)
    {
        var yaml = $"buildJobs: {value}\n" + MinicloudConfigWriter.Write(Parse(null).Config!);
        var parsed = MinicloudConfigLoader.Parse(yaml.Split('\n'));
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Field == "buildJobs");
    }

    [Fact]
    public void Override_values_are_never_interpreted_as_service_names()
    {
        Assert.Equal(["api"], CliApplication.DeployServiceNamesFromArgs(["api", "--build-mode", "local"]));
        Assert.Equal(["api"], CliApplication.DeployServiceNamesFromArgs(["--build-mode=local", "api"]));
        Assert.Equal(["api"], CliApplication.DeployServiceNamesFromArgs(["api", "--build-jobs", "5"]));
        Assert.Equal(["api"], CliApplication.DeployServiceNamesFromArgs(["--build-jobs=5", "api"]));
    }

    [Theory]
    [InlineData(null, "--build-mode remote")]
    [InlineData(null, "--build-mode")]
    [InlineData(null, "--no-publish --build-mode local")]
    [InlineData(null, "--build-jobs")]
    [InlineData(null, "--build-jobs 0")]
    [InlineData(null, "--build-jobs 11")]
    [InlineData(null, "--no-publish --build-jobs 2")]
    [InlineData(null, "--build-mode source --build-jobs 2")]
    public async Task Invalid_override_never_contacts_api(string? configuredMode, string options)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-local-build-");
        try
        {
            var config = Parse(configuredMode).Config!;
            var path = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(path, MinicloudConfigWriter.Write(config));
            var environment = CliEnvironment.ForTests("https://example.invalid", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("test");
            var handler = new NoNetwork();
            using var http = new HttpClient(handler);
            var console = new TestConsole();
            var application = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, http));
            var args = new[] { "deploy", "--config", path }.Concat(options.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            Assert.Equal(CliExitCodes.ValidationError, await application.RunAsync(args, default));
            Assert.Equal(0, handler.Calls);
            Assert.True(console.Output.Contains(ImagePublicationErrors.Unavailable) || console.Output.Contains("Usage error"), console.Output);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "Dockerfile")));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Default_local_mode_refuses_unsupported_api_without_uploading_source()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-default-local-");
        try
        {
            var path = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(path, """
                app: demo
                appId: app_demo
                services:
                  api:
                    sourcePath: .
                    port: 8080
                    public: true
                    path: /
                    healthPath: /health
                """);
            var environment = CliEnvironment.ForTests("https://example.invalid", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("test");
            var handler = new UnsupportedRegistryApi();
            using var http = new HttpClient(handler);
            var console = new TestConsole();
            var application = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, http));
            Assert.Equal(CliExitCodes.ValidationError, await application.RunAsync(["deploy", "--config", path], default));
            Assert.Contains("local_build_unavailable", console.Output);
            Assert.Equal(["GET /v1/me", "GET /v1/apps/app_demo", "GET /v1/apps/app_demo/image-build-target"], handler.Requests);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "Dockerfile")));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(@"C:\apps\demo")]
    [InlineData(@"C:\work spaces\demo")]
    [InlineData("/apps/a\"quoted\" folder")]
    public void Config_writer_preserves_paths_requiring_yaml_escaping(string sourcePath)
    {
        var config = Parse(null).Config! with
        {
            Assets = new Dictionary<string, MinicloudAssetConfig>
            { ["cli"] = new(sourcePath, "Dockerfile", "/cli-releases") }
        };
        var parsed = MinicloudConfigLoader.Parse(MinicloudConfigWriter.Write(config).Split('\n'));
        Assert.True(parsed.IsValid, string.Join(';', parsed.Diagnostics));
        Assert.Equal(sourcePath, parsed.Config!.Assets["cli"].SourcePath);
    }

    [Fact]
    public async Task Asset_publication_still_packages_source_without_local_image_preflight()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-asset-source-");
        try
        {
            var config = Parse(null).Config! with
            {
                Assets = new Dictionary<string, MinicloudAssetConfig>
                { ["cli"] = new(directory.FullName, "Dockerfile", "/cli-releases") }
            };
            var path = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(path, MinicloudConfigWriter.Write(config));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Dockerfile"), "FROM scratch");
            var environment = CliEnvironment.ForTests("https://example.invalid", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("test");
            var handler = new UnsupportedRegistryApi();
            using var http = new HttpClient(handler);
            var console = new TestConsole();
            var application = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, http));
            Assert.NotEqual(CliExitCodes.Success, await application.RunAsync(["assets", "publish", "cli", "--config", path], default));
            Assert.True(handler.Requests.SequenceEqual(["GET /v1/apps/app_demo", "POST /v1/artifacts"]), console.Output);
            Assert.DoesNotContain("Local build requires", console.Output);
        }
        finally { directory.Delete(true); }
    }

    private sealed class UnsupportedRegistryApi : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");
            var json = path switch
            {
                "/v1/me" => """{"id":"user","organizations":[{"id":"org","name":"Org","slug":"org","role":"owner"}]}""",
                "/v1/apps/app_demo" => """{"id":"app_demo","organizationId":"org","name":"Demo","slug":"demo","plan":"p0","branches":[]}""",
                "/v1/apps/app_demo/image-build-target" => null,
                "/v1/artifacts" => null,
                _ => throw new InvalidOperationException($"Unexpected request: {request.Method} {path}")
            };
            return Task.FromResult(new HttpResponseMessage(json is null ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.OK)
            { Content = new StringContent(json ?? """{"error":{"code":"not_found","message":"Not found"}}""", System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static ConfigLoadResult Parse(string? value)
    {
        var lines = new List<string>
        {
            "app: demo", "appId: app_demo", "services:", "  api:",
            "    image: example:1", "    port: 8080", "    public: true", "    path: /", "    healthPath: /health"
        };
        if (value is not null) { lines.Add($"    buildMode: {value}"); }
        return MinicloudConfigLoader.Parse(lines);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Local build gate must run before network activity.");
        }
    }

    private sealed class TestConsole : IConsole
    {
        public bool SupportsAnsi => false;
        public string Output { get; private set; } = "";
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + "\n";
        public void WriteError(string message) => Output += message + "\n";
        public string? ReadLine() => throw new InvalidOperationException();
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
