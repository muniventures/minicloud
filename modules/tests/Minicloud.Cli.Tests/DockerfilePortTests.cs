using Minicloud.Cli;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;

namespace Minicloud.Tests;

public sealed class DockerfilePortTests
{
    [Theory]
    [InlineData("FROM node:24\nEXPOSE 6100", new[] { 6100 })]
    [InlineData("FROM node:24\nexpose 6102/tcp 6102 53/udp", new[] { 6102 })]
    [InlineData("FROM node:24 AS build\nEXPOSE 3000\nFROM node:24\nEXPOSE 6100", new[] { 6100 })]
    [InlineData("FROM base AS runtime\nEXPOSE 6103\nFROM sdk AS build\nEXPOSE 9999\nFROM runtime AS final", new[] { 6103 })]
    [InlineData("FROM node:24\nEXPOSE 6100\nEXPOSE 6102", new[] { 6100, 6102 })]
    [InlineData("FROM node:24\n# EXPOSE 1234\nEXPOSE 6100 \\\n 6102/tcp", new[] { 6100, 6102 })]
    [InlineData("FROM node:24", new int[] { })]
    public void Reads_final_stage_tcp_ports(string dockerfile, int[] expected)
    {
        Assert.Equal(expected, DockerfilePorts.Read(dockerfile.Split('\n')));
    }

    [Theory]
    [InlineData("$PORT")]
    [InlineData("0")]
    [InlineData("65536")]
    public void Rejects_unresolved_or_invalid_tcp_ports(string port)
    {
        Assert.Throws<CliCommandException>(() => DockerfilePorts.Read(["FROM node:24", $"EXPOSE {port}"]));
    }

    [Fact]
    public void Multiple_node_services_and_dotnet_keep_their_own_dockerfile_ports_in_written_config()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-port-test-");
        try
        {
            foreach (var (name, port) in new[] { ("agency", 6100), ("admin", 6102), ("api", 6103) })
            {
                var serviceDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, name));
                File.WriteAllText(Path.Combine(serviceDirectory.FullName, name == "api" ? "Api.csproj" : "package.json"),
                    name == "api" ? "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />" : "{\"dependencies\":{\"vite\":\"8.0.0\"}}");
                File.WriteAllText(Path.Combine(serviceDirectory.FullName, name == "api" ? "Dockerfile.api" : "Dockerfile"),
                    $"FROM runtime AS base\nEXPOSE {port}\nFROM builder AS build\nFROM base AS final\nCMD [\"start\"]");
            }
            var detected = ServiceDetection.Detect(directory.FullName);
            var config = new MinicloudConfig("test", "postgres", null, detected.ToDictionary(s => s.Name, s => s.ToConfig())) { AppId = "app_test" };
            var yaml = MinicloudConfigWriter.Write(config);
            Assert.Equal(6100, Assert.Single(detected, s => s.Name == "agency").Port);
            Assert.Equal(6102, Assert.Single(detected, s => s.Name == "admin").Port);
            Assert.Equal(6103, Assert.Single(detected, s => s.SourcePath == "api").Port);
            Assert.Contains("port: 6100", yaml);
            Assert.Contains("port: 6102", yaml);
            Assert.Contains("port: 6103", yaml);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
