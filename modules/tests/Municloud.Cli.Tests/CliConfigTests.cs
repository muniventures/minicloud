using Municloud.Cli.Config;
using Municloud.Cli.Commands;

namespace Municloud.Tests;

public sealed class CliConfigTests
{
    [Fact]
    public void Parse_accepts_backend_frontend_config()
    {
        var result = MunicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "environment: staging",
            "deploymentType: backend_frontend",
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
        Assert.Equal(2, result.Config.Services.Count);
        Assert.Equal("modules/frontend", result.Config.Services["frontend"].SourcePath);
    }

    [Fact]
    public void Parse_accepts_service_environment_variables()
    {
        var result = MunicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "deploymentType: backend_only",
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
    public void Parse_rejects_invalid_environment_variable_names()
    {
        var result = MunicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "deploymentType: backend_only",
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
        var result = MunicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "deploymentType: frontend_only",
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
        var result = MunicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "deploymentType: backend_frontend",
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
    public void Parse_rejects_deployment_type_service_shape_mismatch()
    {
        var result = MunicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "deploymentType: backend_only",
            "services:",
            "  frontend:",
            "    image: ghcr.io/customer/teamcore/frontend:abc123",
            "    port: 3000",
            "    public: true",
            "    path: /",
            "    healthPath: /"
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "services" && x.Message.Contains("backend_only", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_round_trips_init_style_config()
    {
        var config = new MunicloudConfig(
            "teamcore",
            "staging",
            "backend_only",
            "sqlite",
            null,
            new Dictionary<string, MunicloudServiceConfig>
            {
                ["backend"] = new("modules/api", null, "ghcr.io/customer/teamcore-backend:latest", 8080, true, "/", "/health")
            });

        var yaml = MunicloudConfigWriter.Write(config);
        var result = MunicloudConfigLoader.Parse(yaml.Split(Environment.NewLine));

        Assert.True(result.IsValid);
        Assert.NotNull(result.Config);
        Assert.Equal("modules/api", result.Config.Services["backend"].SourcePath);
        Assert.Equal("ghcr.io/customer/teamcore-backend:latest", result.Config.Services["backend"].Image);
    }

    [Fact]
    public void Parse_accepts_custom_config_with_three_services()
    {
        var result = MunicloudConfigLoader.Parse(
        [
            "app: municloud",
            "deploymentType: custom",
            "database: postgres",
            "services:",
            "  api:",
            "    sourcePath: modules/api",
            "    image: ghcr.io/municloud/api:latest",
            "    port: 8080",
            "    public: true",
            "    path: /api",
            "    healthPath: /health",
            "  dashboard:",
            "    sourcePath: modules/dashboard",
            "    image: ghcr.io/municloud/dashboard:latest",
            "    port: 3000",
            "    public: true",
            "    path: /",
            "    healthPath: /",
            "  registry:",
            "    sourcePath: modules/registry",
            "    image: ghcr.io/municloud/registry:latest",
            "    port: 5000",
            "    public: false",
            "    path: /registry",
            "    healthPath: /health"
        ]);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Config?.Services.Count);
    }

    [Fact]
    public void Docker_buildx_arguments_target_municloud_host_platform_and_push_manifest()
    {
        var service = new MunicloudServiceConfig("modules/frontend", "modules/frontend/Dockerfile", null, 3000, true, "/", "/health");

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
        var service = new MunicloudServiceConfig("modules/frontend", null, null, 3000, true, "/", "/health");

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
        var tempDirectory = Directory.CreateTempSubdirectory("municloud-dockerfile-test-");
        try
        {
            var dockerfilePath = Path.Combine(tempDirectory.FullName, "Dockerfile");
            File.WriteAllLines(dockerfilePath,
            [
                "FROM node:20-alpine",
                "CMD [\"npm\", \"run\", \"start\"]"
            ]);
            var service = new MunicloudServiceConfig(tempDirectory.FullName, null, null, 3000, true, "/", "/health");

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
        var tempDirectory = Directory.CreateTempSubdirectory("municloud-dockerfile-test-");
        try
        {
            var dockerfilePath = Path.Combine(tempDirectory.FullName, "Service.Dockerfile");
            File.WriteAllLines(dockerfilePath,
            [
                "FROM mcr.microsoft.com/dotnet/aspnet:10.0",
                "EXPOSE 8080/tcp",
                "ENTRYPOINT [\"dotnet\", \"TeamCore.Api.dll\"]"
            ]);
            var service = new MunicloudServiceConfig(tempDirectory.FullName, dockerfilePath, null, 8080, true, "/api", "/health");

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
            "deploymentType: custom",
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

        var result = MunicloudConfigLoader.Parse(lines);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "services" && x.Message.Contains("At most 5", StringComparison.Ordinal));
    }
}
