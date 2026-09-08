using Minicloud.Cli.Config;
using Minicloud.Cli.Commands;
using System.IO.Compression;

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
            "    path: /",
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
    public void Parse_accepts_service_secret_environment_references()
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
            "    secretEnv:",
            "      MAPBOX_ACCESS_TOKEN: MAPBOX_ACCESS_TOKEN",
            "      STRIPE_SECRET_KEY: STRIPE_SECRET_KEY"
        ]);

        Assert.True(result.IsValid);
        Assert.Equal("MAPBOX_ACCESS_TOKEN", result.Config?.Services["backend"].SecretEnv?["MAPBOX_ACCESS_TOKEN"]);
        Assert.Equal("STRIPE_SECRET_KEY", result.Config?.Services["backend"].SecretEnv?["STRIPE_SECRET_KEY"]);
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
            "    path: \"/\"",
            "    healthPath: \"/health#ready\"",
            "    env:",
            "      FEATURE_VALUE: \"enabled # not a comment\""
        ]);

        Assert.True(result.IsValid);
        Assert.Equal("/", result.Config?.Services["backend"].Path);
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
    public void Parse_rejects_secret_env_collisions_with_plain_env()
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
            "      MAPBOX_ACCESS_TOKEN: public",
            "    secretEnv:",
            "      MAPBOX_ACCESS_TOKEN: MAPBOX_ACCESS_TOKEN"
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "services.backend.secretEnv.MAPBOX_ACCESS_TOKEN");
    }

    [Fact]
    public void Parse_rejects_invalid_secret_env_references()
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
            "    secretEnv:",
            "      BAD-NAME: MAPBOX_ACCESS_TOKEN",
            "      GOOD_NAME: bad-secret-name"
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Field == "services.backend.secretEnv.BAD-NAME");
        Assert.Contains(result.Diagnostics, x => x.Field == "services.backend.secretEnv.GOOD_NAME");
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
    public void Parse_accepts_config_without_images_for_cli_artifact_path()
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
            "    path: /",
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
            "    path: /",
            "    healthPath: /health"
        ]);

        Assert.True(result.IsValid);
        Assert.False(result.Config?.Services["worker"].Public);
    }

    [Fact]
    public void Parse_accepts_none_database_for_manual_database_setup()
    {
        var result = MinicloudConfigLoader.Parse(
        [
            "app: teamcore",
            "appId: app_123",
            "database: none",
            "services:",
            "  frontend:",
            "    sourcePath: modules/frontend",
            "    port: 3000",
            "    public: true",
            "    path: /",
            "    healthPath: /health"
        ]);

        Assert.True(result.IsValid);
        Assert.Equal("none", result.Config?.Database);
    }

    [Fact]
    public void Init_database_choices_include_descriptions()
    {
        var choices = CliApplication.DatabaseChoices();

        Assert.Equal([
            ("sqlite", "SQLite - instance inside the VPS. Not backed up"),
            ("postgres", "Postgres - instance inside the VPS. Not backed up"),
            ("none", "None/Manual - no database or manual set up - pick this if you want to use Firebase for example")
        ], choices);
    }

    [Fact]
    public void Deploy_database_defaults_to_none_when_config_omits_database()
    {
        var config = new MinicloudConfig(
            "teamcore",
            null,
            null,
            new Dictionary<string, MinicloudServiceConfig>(StringComparer.Ordinal))
        {
            AppId = "app_123"
        };

        Assert.Equal("none", CliApplication.ResolveDeploymentDatabase(null, config));
    }

    [Fact]
    public void Deploy_database_uses_config_database_when_present()
    {
        var config = new MinicloudConfig(
            "teamcore",
            "sqlite",
            null,
            new Dictionary<string, MinicloudServiceConfig>(StringComparer.Ordinal))
        {
            AppId = "app_123"
        };

        Assert.Equal("sqlite", CliApplication.ResolveDeploymentDatabase(null, config));
    }

    [Fact]
    public void Deploy_database_option_overrides_config_database()
    {
        var config = new MinicloudConfig(
            "teamcore",
            "sqlite",
            null,
            new Dictionary<string, MinicloudServiceConfig>(StringComparer.Ordinal))
        {
            AppId = "app_123"
        };

        Assert.Equal("postgres", CliApplication.ResolveDeploymentDatabase("postgres", config));
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
                {
                    Env = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ASPNETCORE_ENVIRONMENT"] = "Production"
                    },
                    SecretEnv = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MAPBOX_ACCESS_TOKEN"] = "MAPBOX_ACCESS_TOKEN"
                    }
                }
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
        Assert.Equal("Production", result.Config.Services["backend"].Env?["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("MAPBOX_ACCESS_TOKEN", result.Config.Services["backend"].SecretEnv?["MAPBOX_ACCESS_TOKEN"]);
    }

    [Fact]
    public void Init_suggests_default_config_when_minicloud_yml_is_absent()
    {
        var path = CliApplication.SuggestedInitConfigPath();

        Assert.Equal("minicloud.yml", path);
    }

    [Fact]
    public void Init_still_uses_default_config_when_minicloud_yml_exists()
    {
        var path = CliApplication.SuggestedInitConfigPath();

        Assert.Equal("minicloud.yml", path);
    }

    [Fact]
    public void Service_detection_finds_common_project_types_and_dockerfiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-service-detection-test-");
        try
        {
            var web = Path.Combine(tempDirectory.FullName, "apps", "web");
            Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, "package.json"), """
                {
                  "name": "@acme/web",
                  "dependencies": {
                    "next": "15.0.0",
                    "react": "19.0.0"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(web, "Dockerfile"), "FROM node:22-alpine");

            var api = Path.Combine(tempDirectory.FullName, "services", "TeamCore.Api");
            Directory.CreateDirectory(api);
            File.WriteAllText(Path.Combine(api, "TeamCore.Api.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                </Project>
                """);
            File.WriteAllText(Path.Combine(api, "Dockerfile.api"), "FROM mcr.microsoft.com/dotnet/aspnet:10.0");

            var spring = Path.Combine(tempDirectory.FullName, "spring");
            Directory.CreateDirectory(spring);
            File.WriteAllText(Path.Combine(spring, "pom.xml"), "<project><artifactId>spring-boot-starter-web</artifactId></project>");

            var services = ServiceDetection.Detect(tempDirectory.FullName);

            var webService = Assert.Single(services, x => x.Name == "web");
            Assert.Equal("nextjs", webService.Framework);
            Assert.Equal("apps/web", webService.SourcePath);
            Assert.Null(webService.Dockerfile);
            Assert.Equal(3000, webService.Port);

            var apiService = Assert.Single(services, x => x.Name == "teamcore-api");
            Assert.Equal("dotnet-web", apiService.Framework);
            Assert.Equal("services/TeamCore.Api", apiService.SourcePath);
            Assert.Equal("services/TeamCore.Api/Dockerfile.api", apiService.Dockerfile);
            Assert.Equal("/health", apiService.HealthPath);

            var springService = Assert.Single(services, x => x.Name == "spring");
            Assert.Equal("springboot", springService.Framework);
            Assert.Equal(8080, springService.Port);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Service_detection_skips_generated_folders_and_depths_beyond_five()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-service-detection-test-");
        try
        {
            var nodeModules = Path.Combine(tempDirectory.FullName, "node_modules", "ignored");
            Directory.CreateDirectory(nodeModules);
            File.WriteAllText(Path.Combine(nodeModules, "package.json"), """{"name":"ignored"}""");

            var bin = Path.Combine(tempDirectory.FullName, "src", "bin", "ignored");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "Ignored.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

            var tooDeep = Path.Combine(tempDirectory.FullName, "a", "b", "c", "d", "e", "f");
            Directory.CreateDirectory(tooDeep);
            File.WriteAllText(Path.Combine(tooDeep, "package.json"), """{"name":"too-deep","dependencies":{"next":"15.0.0"}}""");

            var valid = Path.Combine(tempDirectory.FullName, "apps", "api");
            Directory.CreateDirectory(valid);
            File.WriteAllText(Path.Combine(valid, "package.json"), """{"name":"api","dependencies":{"express":"5.0.0"}}""");

            var services = ServiceDetection.Detect(tempDirectory.FullName);

            Assert.Single(services);
            Assert.Equal("api", services[0].Name);
            Assert.Equal("node-api", services[0].Framework);
            Assert.Equal("/health", services[0].HealthPath);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Service_detection_skips_non_web_dotnet_and_android_projects()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-service-detection-test-");
        try
        {
            var library = Path.Combine(tempDirectory.FullName, "modules", "core");
            Directory.CreateDirectory(library);
            File.WriteAllText(Path.Combine(library, "VetCore.Core.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <FrameworkReference Include="Microsoft.AspNetCore.App" />
                  </ItemGroup>
                </Project>
                """);

            var android = Path.Combine(tempDirectory.FullName, "modules", "mobile", "customer", "android");
            Directory.CreateDirectory(android);
            File.WriteAllText(Path.Combine(android, "build.gradle"), """
                plugins {
                    id 'com.android.application'
                }
                """);

            var api = Path.Combine(tempDirectory.FullName, "modules", "api");
            Directory.CreateDirectory(api);
            File.WriteAllText(Path.Combine(api, "VetCore.Api.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                </Project>
                """);

            var spring = Path.Combine(tempDirectory.FullName, "modules", "spring-api");
            Directory.CreateDirectory(spring);
            File.WriteAllText(Path.Combine(spring, "build.gradle"), """
                plugins {
                    id 'org.springframework.boot' version '3.4.0'
                }

                dependencies {
                    implementation 'org.springframework.boot:spring-boot-starter-web'
                }
                """);

            var services = ServiceDetection.Detect(tempDirectory.FullName);

            Assert.Equal(["spring-api", "vetcore-api"], services.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.DoesNotContain(services, x => x.SourcePath.Contains("mobile", StringComparison.Ordinal));
            Assert.DoesNotContain(services, x => x.SourcePath == "modules/core");
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Init_detects_default_dockerfile_in_service_folder()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-init-dockerfile-test-");
        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "Dockerfile"), "FROM scratch");

            Assert.True(CliApplication.HasDefaultDockerfile(tempDirectory.FullName));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Init_allows_service_folder_without_default_dockerfile()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-init-dockerfile-test-");
        try
        {
            Assert.False(CliApplication.HasDefaultDockerfile(tempDirectory.FullName));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
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
    public void FirstPositionalArg_reads_add_service_app_argument()
    {
        var app = CliApplication.FirstPositionalArg(["teamcore-dev", "--config", "minicloud.api.yml", "--advanced"]);

        Assert.Equal("teamcore-dev", app);
    }

    [Fact]
    public void FirstPositionalArg_ignores_options_with_values()
    {
        var app = CliApplication.FirstPositionalArg(["--app", "teamcore-dev", "--config=minicloud.api.yml"]);

        Assert.Null(app);
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
                ["backend"] = new("modules/api", null, null, 8080, true, "/", "/health")
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
            "    path: /",
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
            "    path: /",
            "    healthPath: /health"
        ]);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Config?.Services.Count);
    }

    [Fact]
    public void Artifact_selection_excludes_forced_sensitive_and_build_paths()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-artifact-select-test-");
        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "app.js"), "console.log('ok');");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, ".env"), "SECRET=value");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "id_ed25519"), "private");
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "node_modules"));
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "node_modules", "pkg.js"), "ignored");
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "dist"));
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "dist", "bundle.js"), "ignored");

            var files = DeploymentArtifactBundler.SelectFiles(tempDirectory.FullName)
                .Select(file => Path.GetRelativePath(tempDirectory.FullName, file).Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray();

            Assert.Equal(["app.js"], files);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Artifact_selection_uses_git_tracked_and_untracked_unignored_files()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-artifact-git-test-");
        try
        {
            RunGit(tempDirectory.FullName, "init");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, ".gitignore"), "ignored.txt\n");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "tracked.txt"), "tracked");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "ignored.txt"), "ignored");
            RunGit(tempDirectory.FullName, "add", ".gitignore", "tracked.txt");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "tracked.txt"), "tracked changed");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "untracked.txt"), "untracked");

            var files = DeploymentArtifactBundler.SelectFiles(tempDirectory.FullName)
                .Select(file => Path.GetRelativePath(tempDirectory.FullName, file).Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray();

            Assert.Contains(".gitignore", files);
            Assert.Contains("tracked.txt", files);
            Assert.Contains("untracked.txt", files);
            Assert.DoesNotContain("ignored.txt", files);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Artifact_bundle_writes_manifest_and_rejects_over_limit_zip()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-artifact-bundle-test-");
        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "app.js"), "console.log('ok');");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "Dockerfile"), "FROM node:22-alpine\nEXPOSE 3000\nCMD [\"node\", \"app.js\"]\n");
            var service = new MinicloudServiceConfig(tempDirectory.FullName, null, null, 3000, true, "/", "/health");

            var bundle = DeploymentArtifactBundler.Create("app_123", "web", service, "abc123", tempDirectory.FullName);
            using (var archive = ZipFile.OpenRead(bundle.ZipPath))
            {
                Assert.Contains(archive.Entries, entry => entry.FullName == "app.js");
                Assert.Contains(archive.Entries, entry => entry.FullName == "Dockerfile");
                Assert.Contains(archive.Entries, entry => entry.FullName == DeploymentArtifactBundler.ManifestEntryName);
            }

            Assert.Equal("app_123", bundle.Manifest.AppId);
            Assert.Equal("web", bundle.Manifest.ServiceName);
            Assert.Equal(2, bundle.Manifest.FileCount);
            Assert.Equal(64, bundle.Sha256.Length);

            var ex = Assert.Throws<CliCommandException>(() => DeploymentArtifactBundler.Create("app_123", "web", service, "abc123", tempDirectory.FullName, maxArtifactBytes: 1));
            Assert.Contains("above the", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Dockerfile_generator_writes_vite_react_template()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-dockerfile-generator-test-");
        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "package.json"), """
                {
                  "name": "route-analyzer",
                  "dependencies": {
                    "vite": "6.3.5",
                    "react": "19.1.0"
                  }
                }
                """);
            var service = new MinicloudServiceConfig(tempDirectory.FullName, null, null, 3000, true, "/", "/");

            var generated = DockerfileGenerator.TryWriteDockerfile(service, out var dockerfilePath, out var reason);

            Assert.True(generated);
            Assert.Null(reason);
            Assert.Equal(Path.Combine(tempDirectory.FullName, "Dockerfile"), dockerfilePath);
            var dockerfile = File.ReadAllText(dockerfilePath);
            Assert.Contains("npm run build", dockerfile, StringComparison.Ordinal);
            Assert.Contains("""CMD ["sh", "-c", "npm run build && npx vite preview --host 0.0.0.0 --port 3000"]""", dockerfile, StringComparison.Ordinal);
            Assert.Empty(CliApplication.ValidateDockerfileForService("frontend", service));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Dockerfile_generator_reports_unsupported_project()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-dockerfile-generator-test-");
        try
        {
            var service = new MinicloudServiceConfig(tempDirectory.FullName, null, null, 3000, true, "/", "/");

            var generated = DockerfileGenerator.TryWriteDockerfile(service, out _, out var reason);

            Assert.False(generated);
            Assert.Equal("unsupported project type", reason);
            Assert.False(File.Exists(Path.Combine(tempDirectory.FullName, "Dockerfile")));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
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
            var service = new MinicloudServiceConfig(tempDirectory.FullName, dockerfilePath, null, 8080, true, "/", "/health");

            var diagnostics = CliApplication.ValidateDockerfileForService("backend", service);

            Assert.Empty(diagnostics);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Framework_validation_rejects_vite_without_minicloud_allowed_host()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-framework-test-");
        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "package.json"), """
                {
                  "dependencies": {
                    "vite": "6.4.3",
                    "react": "19.1.0"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "vite.config.ts"), """
                import { defineConfig } from "vite";

                export default defineConfig({});
                """);
            var service = new MinicloudServiceConfig(tempDirectory.FullName, null, null, 3000, true, "/", "/");

            var diagnostics = FrameworkDeploymentValidator.ValidatePublicHostCompatibility("web", service, "teamcore", "airlinesim-routes");

            Assert.Contains(diagnostics, x =>
                x.Field == "services.web.sourcePath" &&
                x.Message.Contains("teamcore-airlinesim-routes-web.app.muni.dev", StringComparison.Ordinal) &&
                x.Message.Contains("preview.allowedHosts", StringComparison.Ordinal));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Framework_validation_accepts_vite_with_minicloud_allowed_host_suffix()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-framework-test-");
        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "package.json"), """
                {
                  "dependencies": {
                    "vite": "6.4.3"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "vite.config.ts"), """
                import { defineConfig } from "vite";

                export default defineConfig({
                  preview: {
                    allowedHosts: [".app.muni.dev"],
                  },
                });
                """);
            var service = new MinicloudServiceConfig(tempDirectory.FullName, null, null, 3000, true, "/", "/");

            var diagnostics = FrameworkDeploymentValidator.ValidatePublicHostCompatibility("web", service, "teamcore", "airlinesim-routes");

            Assert.Empty(diagnostics);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Framework_validation_rejects_next_dev_server_for_public_deployments()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-framework-test-");
        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "package.json"), """
                {
                  "dependencies": {
                    "next": "15.0.0",
                    "react": "19.1.0",
                    "react-dom": "19.1.0"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "Dockerfile"), """
                FROM node:22-alpine
                EXPOSE 3000
                CMD ["npx", "next", "dev", "-H", "0.0.0.0", "-p", "3000"]
                """);
            var service = new MinicloudServiceConfig(tempDirectory.FullName, null, null, 3000, true, "/", "/");

            var diagnostics = FrameworkDeploymentValidator.ValidatePublicHostCompatibility("web", service, "teamcore", "portal");

            Assert.Contains(diagnostics, x =>
                x.Field == "services.web.dockerfile" &&
                x.Message.Contains("development server", StringComparison.Ordinal));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Local_secrets_file_parser_reads_dotenv_values()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-local-secrets-test-");
        try
        {
            var secretsPath = Path.Combine(tempDirectory.FullName, LocalSecretsFile.FileName);
            File.WriteAllText(secretsPath, """
                # local helper
                VITE_FIREBASE_API_KEY=abc123
                VITE_FIREBASE_AUTH_DOMAIN="example.firebaseapp.com"
                VITE_GOOGLE_MAPS_API_KEY='maps-key'
                """);

            var secrets = LocalSecretsFile.Parse(secretsPath);

            Assert.Equal("abc123", secrets["VITE_FIREBASE_API_KEY"]);
            Assert.Equal("example.firebaseapp.com", secrets["VITE_FIREBASE_AUTH_DOMAIN"]);
            Assert.Equal("maps-key", secrets["VITE_GOOGLE_MAPS_API_KEY"]);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Local_secrets_file_parser_rejects_invalid_names()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-local-secrets-test-");
        try
        {
            var secretsPath = Path.Combine(tempDirectory.FullName, LocalSecretsFile.FileName);
            File.WriteAllText(secretsPath, "BAD-NAME=value");

            var ex = Assert.Throws<CliCommandException>(() => LocalSecretsFile.Parse(secretsPath));

            Assert.Contains("Invalid local secret name", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Parse_enforces_ten_service_limit(int serviceCount, bool expectedValid)
    {
        var lines = new List<string>
        {
            "app: teamcore",
            "appId: app_123",
            "services:"
        };
        for (var index = 1; index <= serviceCount; index++)
        {
            lines.Add($"  service-{index}:");
            lines.Add($"    image: ghcr.io/customer/teamcore/service-{index}:latest");
            lines.Add("    port: 8080");
            lines.Add($"    public: {(index == 1 ? "true" : "false")}");
            lines.Add("    path: /");
            lines.Add("    healthPath: /health");
        }

        var result = MinicloudConfigLoader.Parse(lines);

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(result.Diagnostics, x => x.Field == "services" && x.Message.Contains("At most 10", StringComparison.Ordinal));
        }
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "git";
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
