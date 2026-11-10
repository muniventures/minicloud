using System.Text.Json;
using System.Text.Json.Nodes;
using Minicloud.Api.Controllers;
using Minicloud.Api.Services;
using Microsoft.Extensions.Options;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ServerReleaseMetadataTests
{
    [Fact]
    public void SourceBuildDoesNotInventPackagedReleaseCompatibility()
    {
        Assert.Null(ServerReleaseMetadata.Load(null));
        var response = new InstanceController(Options.Create(new ServerReleaseOptions()))
            .Get(Options.Create(new ManagedDomainOptions { Suffix = "apps.example.invalid" }));
        Assert.Equal("minicloud", response.Product);
        Assert.Equal(1, response.ProtocolVersion);
        Assert.Equal("apps.example.invalid", response.AppDomain);
        Assert.Null(response.ServerVersion);
        Assert.Null(response.SourceRevision);
        Assert.Null(response.CliCompatibility);
    }

    [Fact]
    public void PackagedReleaseReportsExactMetadataAndDoesNotExposeOtherManifestFields()
    {
        using var fixture = new ReleaseMetadataFixture();
        var metadata = ServerReleaseMetadata.Load(fixture.Path)!;
        var response = new InstanceController(Options.Create(new ServerReleaseOptions { Metadata = metadata }))
            .Get(Options.Create(new ManagedDomainOptions { Suffix = "apps.example.invalid" }));
        var json = JsonSerializer.SerializeToNode(response, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("1.2.3-rc.1", json["serverVersion"]!.GetValue<string>());
        Assert.Equal(new string('a', 40), json["sourceRevision"]!.GetValue<string>());
        Assert.Equal(1, json["cliCompatibility"]!["protocolVersion"]!.GetValue<int>());
        Assert.Equal(new string('b', 40), json["cliCompatibility"]!["sourceRevisions"]![0]!.GetValue<string>());
        Assert.Null(json["images"]);
        Assert.Null(json["dependencies"]);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("serverVersion", "\"1.2.3-01\"")]
    [InlineData("releaseTag", "\"server-v2.0.0\"")]
    [InlineData("sourceRevision", "\"main\"")]
    [InlineData("sourceRevision", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\n\"")]
    [InlineData("cliCompatibility", "null")]
    [InlineData("cliCompatibility", "{\"protocolVersion\":2,\"sourceRevisions\":[]}")]
    [InlineData("cliCompatibility", "{\"protocolVersion\":1,\"sourceRevisions\":[]}")]
    [InlineData("cliCompatibility", "{\"protocolVersion\":1,\"sourceRevisions\":[\"latest\"]}")]
    [InlineData("cliCompatibility", "{\"protocolVersion\":1,\"sourceRevisions\":[],\"minimumVersion\":\"2.0.0\",\"maximumExclusiveVersion\":\"1.0.0\"}")]
    [InlineData("cliCompatibility", "{\"protocolVersion\":1,\"sourceRevisions\":[],\"minimumVersion\":\"1.0.0\",\"maximumExclusiveVersion\":\"1.0.0-rc.1\"}")]
    [InlineData("cliCompatibility", "{\"protocolVersion\":1,\"sourceRevisions\":[],\"minimumVersion\":\"1.0.0-01\",\"maximumExclusiveVersion\":\"2.0.0\"}")]
    [InlineData("cliCompatibility", "{\"protocolVersion\":1,\"sourceRevisions\":null,\"minimumVersion\":\"1.0.0\",\"maximumExclusiveVersion\":\"2.0.0\"}")]
    public void InvalidConfiguredMetadataFailsExplicitly(string field, string value)
    {
        using var fixture = new ReleaseMetadataFixture();
        var metadata = JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        metadata[field] = JsonNode.Parse(value);
        File.WriteAllText(fixture.Path, metadata.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => ServerReleaseMetadata.Load(fixture.Path));
    }

    [Theory]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10")]
    [InlineData("1.0.0-rc.10", "1.0.0")]
    [InlineData("1.0.0-2", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0", "2.0.0")]
    public void TestedVersionIntervalCanReplaceExplicitSourcePins(string minimum, string maximum)
    {
        using var fixture = new ReleaseMetadataFixture();
        var metadata = JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        metadata["cliCompatibility"] = new JsonObject
        {
            ["protocolVersion"] = 1,
            ["minimumVersion"] = minimum,
            ["maximumExclusiveVersion"] = maximum,
            ["sourceRevisions"] = new JsonArray()
        };
        File.WriteAllText(fixture.Path, metadata.ToJsonString());
        Assert.NotNull(ServerReleaseMetadata.Load(fixture.Path));
    }

    [Fact]
    public void MissingMalformedOrBlankConfiguredFileIsNotTreatedAsSourceBuild()
    {
        using var fixture = new ReleaseMetadataFixture();
        Assert.Throws<InvalidOperationException>(() => ServerReleaseMetadata.Load(" "));
        File.WriteAllText(fixture.Path, "not JSON");
        Assert.Throws<JsonException>(() => ServerReleaseMetadata.Load(fixture.Path));
        File.Delete(fixture.Path);
        Assert.Throws<FileNotFoundException>(() => ServerReleaseMetadata.Load(fixture.Path));
    }
}

internal sealed class ReleaseMetadataFixture : IDisposable
{
    private readonly string _directory;
    public string Path { get; }

    public ReleaseMetadataFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "Minicloud.slnx")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Run release metadata tests under the repository.");
        _directory = System.IO.Path.Combine(root.FullName, ".local", "artifacts", "server-metadata-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "server-release.json");
        File.WriteAllText(Path, $$"""
            {
              "schemaVersion": 1,
              "serverVersion": "1.2.3-rc.1",
              "releaseTag": "server-v1.2.3-rc.1",
              "sourceRevision": "{{new string('a', 40)}}",
              "cliCompatibility": {
                "protocolVersion": 1,
                "minimumVersion": null,
                "maximumExclusiveVersion": null,
                "sourceRevisions": ["{{new string('b', 40)}}"]
              },
              "images": {"api": "synthetic-not-exposed"}
            }
            """);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
