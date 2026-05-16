using Minicloud.Cli.Config;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliConfigTests
{
    [Fact]
    public void Parse_accepts_multi_service_config()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "appId: app_123",
            "database: sqlite",
            "services:",
            "  frontend:",
            "    sourcePath: modules/frontend",
            "    image: ghcr.io/customer/teamcore/frontend:abc123",
            "    port: 3000",
            "    public: true",
            "    path: /",
            "    healthPath: /",
            "  backend:",
            "    image: ghcr.io/customer/teamcore/backend:abc123",
            "    port: 8080",
            "    public: true",
            "    path: /api",
            "    healthPath: /health"
        ]);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Config);
        Assert.Equal("teamcore", result.Config.App);
        Assert.Equal("app_123", result.Config.AppId);
        Assert.Equal(2, result.Config.Services.Count);
        Assert.Equal("modules/frontend", result.Config.Services["frontend"].SourcePath);
    }

    [Fact]
    public void Parse_accepts_service_environment_variables()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "appId: app_123",
            "services:",
            "  backend:",
            "    image: ghcr.io/customer/teamcore/backend:abc123",
            "    port: 8080",
            "    public: true",
            "    path: /",
            "    healthPath: /health",
            "    env:",
            "      ASPNETCORE_ENVIRONMENT: Production",
            "      FEATURE_FLAG_X: enabled"
        ]);

        Assert.True(result.IsValid);
        Assert.Equal("Production", result.Config?.Services["backend"].Env?["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("enabled", result.Config?.Services["backend"].Env?["FEATURE_FLAG_X"]);
    }

    [Fact]
    public void Parse_accepts_underscores_in_slugs()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: vet_core",
            "appId: app_123",
            "services:",
            "  web_api:",
            "    port: 8080",
            "    public: true",
            "    path: /",
            "    healthPath: /health"
        ]);

        Assert.True(result.IsValid);
        Assert.Equal("vet_core", result.Config?.App);
        Assert.Contains("web_api", result.Config?.Services.Keys ?? []);
    }

    [Theory]
    [InlineData("VetCore")]
    [InlineData("vet core")]
    [InlineData("vet.core")]
    public void Parse_rejects_invalid_app_slugs(string app)
    {
        var result = MinicloudConfigLoader.Parse(
        [
            $"app: {app}",
            "appId: app_123",
            "services:",
            "  backend:",
            "    port: 8080",
            "    public: true",
            "    path: /",
            "    healthPath: /health"
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "app");
    }

    [Fact]
    public void Parse_rejects_missing_app_id()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "services:",
            "  backend:",
            "    port: 8080",
            "    public: true",
            "    path: /",
            "    healthPath: /health"
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "appId");
    }

    [Fact]
    public void Parse_preserves_quoted_yaml_scalars()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "appId: app_123",
            "services:",
            "  backend:",
            "    image: \"ghcr.io/customer/teamcore/backend:abc123\"",
            "    port: 8080",
            "    public: true",
            "    path: \"/api#v1\"",
            "    healthPath: \"/health#ready\"",
            "    env:",
            "      FEATURE_VALUE: \"enabled # not a comment\""
        ]);

        Assert.True(result.IsValid);
        Assert.Equal("/api#v1", result.Config?.Services["backend"].Path);
        Assert.Equal("/health#ready", result.Config?.Services["backend"].HealthPath);
        Assert.Equal("enabled # not a comment", result.Config?.Services["backend"].Env?["FEATURE_VALUE"]);
    }

    [Fact]
    public void Parse_reports_yaml_syntax_errors()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "appId: app_123",
            "services:",
            "  backend:",
            "    port: [8080"
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "config" && x.Message.Contains("Invalid YAML", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_rejects_nested_environment_values()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "appId: app_123",
            "services:",
            "  backend:",
            "    image: ghcr.io/customer/teamcore/backend:abc123",
            "    port: 8080",
            "    public: true",
            "    path: /",
            "    healthPath: /health",
            "    env:",
            "      FEATURE:",
            "        ENABLED: true"
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "services.backend.env.FEATURE");
    }

    [Fact]
    public void Parse_rejects_invalid_environment_variable_names()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "appId: app_123",
            "services:",
            "  backend:",
            "    image: ghcr.io/customer/teamcore/backend:abc123",
            "    port: 8080",
            "    public: true",
            "    path: /",
            "    healthPath: /health",
            "    env:",
            "      BAD-NAME: value"
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "services.backend.env.BAD-NAME");
    }

    [Fact]
    public void Parse_rejects_invalid_port_and_path()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "services:",
            "  frontend:",
            "    port: 70000",
            "    public: true",
            "    path: app",
            "    healthPath: health"
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "services.frontend.port");
        Assert.Contains(result.Diagnostics, x => x.Field == "services.frontend.path");
        Assert.Contains(result.Diagnostics, x => x.Field == "services.frontend.healthPath");
    }

    [Fact]
    public void Parse_accepts_config_without_images_for_cli_publish_path()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "appId: app_123",
            "database: postgres",
            "services:",
            "  frontend:",
            "    sourcePath: modules/ui/dashboard",
            "    port: 3000",
            "    public: true",
            "    path: /",
            "    healthPath: /health",
            "  backend:",
            "    sourcePath: modules/api",
            "    port: 8080",
            "    public: true",
            "    path: /api",
            "    healthPath: /health"
        ]);

        Assert.True(result.IsValid);
        Assert.Null(result.Config?.Services["frontend"].Image);
        Assert.Null(result.Config?.Services["backend"].Image);
    }

    [Fact]
    public void Parse_accepts_private_service_config_for_service_scoped_deploy()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "appId: app_123",
            "services:",
            "  worker:",
            "    sourcePath: modules/worker",
            "    port: 8080",
            "    public: false",
            "    path: /worker",
            "    healthPath: /health"
        ]);

        Assert.True(result.IsValid);
        Assert.False(result.Config?.Services["worker"].Public);
    }

    [Fact]
    public void Write_round_trips_init_style_config()
    {
        var config = new MinicloudConfig(
            "teamcore",
            "sqlite",
            null,
            new Dictionary<string, MinicloudServiceConfig>
            {
                ["backend"] = new("modules/api", null, "ghcr.io/customer/teamcore-backend:latest", 8080, true, "/", "/health")
            })
        {
            AppId = "app_123"
        };

        var yaml = MinicloudConfigWriter.Write(config);
        var result = MinicloudConfigLoader.Parse(yaml.Split(Environment.NewLine));

        Assert.True(result.IsValid);
        Assert.NotNull(result.Config);
        Assert.Equal("app_123", result.Config.AppId);
        Assert.Equal("modules/api", result.Config.Services["backend"].SourcePath);
        Assert.Equal("ghcr.io/customer/teamcore-backend:latest", result.Config.Services["backend"].Image);
    }

    [Fact]
    public void Init_suggests_default_config_when_minicloud_yml_is_absent()
    {
        var path = CliApplication.SuggestedInitConfigPath("backend", fileExists: _ => false);

        Assert.Equal("minicloud.yml", path);
    }

    [Fact]
    public void Init_suggests_service_config_when_minicloud_yml_exists()
    {
        var path = CliApplication.SuggestedInitConfigPath("backend", fileExists: candidate => candidate == "minicloud.yml");

        Assert.Equal("minicloud.backend.yml", path);
    }

    [Fact]
    public void DeployServiceNamesFromArgs_reads_positional_service_names()
    {
        var services = CliApplication.DeployServiceNamesFromArgs(
        [
            "backend",
            "frontend",
            "--config",
            "minicloud.yml",
            "--tag=abc123",
            "--verbose"
        ]);

        Assert.Equal(["backend", "frontend"], services);
    }

    [Fact]
    public void DeployServiceNamesFromArgs_ignores_all_flag()
    {
        var services = CliApplication.DeployServiceNamesFromArgs(["--all", "--config", "minicloud.yml"]);

        Assert.Empty(services);
    }

    [Fact]
    public void FilterConfigServices_returns_only_selected_services()
    {
        var config = new MinicloudConfig(
            "teamcore",
            "postgres",
            "abc123",
            new Dictionary<string, MinicloudServiceConfig>
            {
                ["frontend"] = new("modules/ui", null, null, 3000, true, "/", "/"),
                ["backend"] = new("modules/api", null, null, 8080, true, "/api", "/health")
            })
        {
            AppId = "app_123"
        };

        var filtered = CliApplication.FilterConfigServices(config, ["backend"]);

        Assert.Equal("teamcore", filtered.App);
        Assert.Equal("app_123", filtered.AppId);
        Assert.Equal("abc123", filtered.CommitSha);
        Assert.Single(filtered.Services);
        Assert.Contains("backend", filtered.Services.Keys);
    }

    [Fact]
    public void Parse_accepts_custom_config_with_three_services()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: minicloud",
            "appId: app_123",
            "database: postgres",
            "services:",
            "  api:",
            "    sourcePath: modules/api",
            "    image: ghcr.io/minicloud/api:latest",
            "    port: 8080",
            "    public: true",
            "    path: /api",
            "    healthPath: /health",
            "  dashboard:",
            "    sourcePath: modules/dashboard",
            "    image: ghcr.io/minicloud/dashboard:latest",
            "    port: 3000",
            "    public: true",
            "    path: /",
            "    healthPath: /",
            "  registry:",
            "    sourcePath: modules/registry",
            "    image: ghcr.io/minicloud/registry:latest",
            "    port: 5000",
            "    public: false",
            "    path: /registry",
            "    healthPath: /health"
        ]);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Config?.Services.Count);
    }

    [Fact]
    public void Docker_buildx_arguments_target_minicloud_host_platform_and_push_manifest()
    {
        var service = new MinicloudServiceConfig("modules/frontend", "modules/frontend/Dockerfile", null, 3000, true, "/", "/health");

        var args = CliApplication.DockerBuildxBuildArgumentsForService(service, "ghcr.io/customer/teamcore-frontend:latest");

        Assert.Equal(
            [
                "buildx",
                "build",
                "--progress",
                "plain",
                "--platform",
                "linux/amd64",
                "--push",
                "-t",
                "ghcr.io/customer/teamcore-frontend:latest",
                "-f",
                "modules/frontend/Dockerfile",
                "modules/frontend"
            ],
            args);
    }

    [Fact]
    public void Docker_buildx_arguments_include_registry_cache_when_available()
    {
        var service = new MinicloudServiceConfig("modules/frontend", null, null, 3000, true, "/", "/health");

        var args = CliApplication.DockerBuildxBuildArgumentsForService(
            service,
            "host.docker.internal:5050/teamcore/frontend:latest",
            "host.docker.internal:5050/teamcore/frontend:buildcache");

        Assert.Contains("--cache-from", args);
        Assert.Contains("type=registry,ref=host.docker.internal:5050/teamcore/frontend:buildcache", args);
        Assert.Contains("--cache-to", args);
        Assert.Contains("type=registry,ref=host.docker.internal:5050/teamcore/frontend:buildcache,mode=max", args);
    }

    [Fact]
    public void Dockerfile_validation_rejects_missing_exposed_service_port()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-dockerfile-test-");
        try
        {
            var dockerfilePath = Path.Combine(tempDirectory.FullName, "Dockerfile");
            File.WriteAllLines(dockerfilePath,
            [
                "FROM node:20-alpine",
                "CMD [\"npm\", \"run\", \"start\"]"
            ]);
            var service = new MinicloudServiceConfig(tempDirectory.FullName, null, null, 3000, true, "/", "/health");

            var diagnostics = CliApplication.ValidateDockerfileForService("frontend", service);

            Assert.Contains(diagnostics, x => x.Field == "services.frontend.port" && x.Message.Contains("EXPOSE 3000", StringComparison.Ordinal));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Dockerfile_validation_accepts_start_command_and_matching_exposed_port()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-dockerfile-test-");
        try
        {
            var dockerfilePath = Path.Combine(tempDirectory.FullName, "Service.Dockerfile");
            File.WriteAllLines(dockerfilePath,
            [
                "FROM mcr.microsoft.com/dotnet/aspnet:10.0",
                "EXPOSE 8080/tcp",
                "ENTRYPOINT [\"dotnet\", \"TeamCore.Api.dll\"]"
            ]);
            var service = new MinicloudServiceConfig(tempDirectory.FullName, dockerfilePath, null, 8080, true, "/api", "/health");

            var diagnostics = CliApplication.ValidateDockerfileForService("backend", service);

            Assert.Empty(diagnostics);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Parse_rejects_more_than_five_services()
    {
        var lines = new List<string>
        {
            "app: teamcore",
            "services:"
        };
        for (var index = 1; index <= 6; index++)
        {
            lines.Add($"  service-{index}:");
            lines.Add($"    image: ghcr.io/customer/teamcore/service-{index}:latest");
            lines.Add("    port: 8080");
            lines.Add($"    public: {(index == 1 ? "true" : "false")}");
            lines.Add("    path: /");
            lines.Add("    healthPath: /health");
        }

        var result = MinicloudConfigLoader.Parse(lines);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "services" && x.Message.Contains("At most 5", StringComparison.Ordinal));
    }
}
