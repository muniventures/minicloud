using System.Text;
using System.Text.Json.Nodes;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Tests;

public sealed class RegistryImageValidatorTests
{
    [Theory]
    [InlineData(RegistryImageValidator.OciManifest, "linux/amd64")]
    [InlineData(RegistryImageValidator.DockerManifest, "linux/arm64")]
    public void Verifies_digest_platform_and_unique_referenced_bytes(string mediaType, string platform)
    {
        var (manifest, config) = Image(platform, mediaType);
        var bytes = Bytes(manifest);
        var verified = RegistryImageValidator.Verify(bytes, config, RegistryImageValidator.Digest(bytes), platform);
        Assert.Equal(platform, verified.Platform);
        Assert.Equal(config.Length + bytes.Length + 10, verified.SizeBytes);
        Assert.Single(verified.Layers);
        manifest["layers"]!.AsArray().Add(manifest["layers"]![0]!.DeepClone());
        var configNode = JsonNode.Parse(config)!;
        configNode["rootfs"]!["diff_ids"]!.AsArray().Add("sha256:" + new string('b', 64));
        config = Bytes(configNode);
        manifest["config"]!["digest"] = RegistryImageValidator.Digest(config);
        manifest["config"]!["size"] = config.Length;
        bytes = Bytes(manifest);
        var repeated = RegistryImageValidator.Verify(bytes, config, RegistryImageValidator.Digest(bytes), platform);
        Assert.Equal(2, repeated.Layers.Count);
        Assert.Equal(config.Length + bytes.Length + 10, repeated.SizeBytes);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("schema-string")]
    [InlineData("foreign")]
    [InlineData("urls")]
    [InlineData("inline-data")]
    [InlineData("negative-size")]
    [InlineData("string-size")]
    [InlineData("overflow-size")]
    [InlineData("total-byte-overflow")]
    [InlineData("artifact")]
    [InlineData("subject")]
    [InlineData("missing-config")]
    [InlineData("missing-layers")]
    [InlineData("digest-newline")]
    [InlineData("too-many-layers")]
    public void Rejects_unrunnable_or_unbounded_manifests(string change)
    {
        var (manifest, config) = Image();
        switch (change)
        {
            case "index": manifest["mediaType"] = "application/vnd.oci.image.index.v1+json"; break;
            case "schema-string": manifest["schemaVersion"] = "2"; break;
            case "foreign": manifest["layers"]![0]!["mediaType"] = "application/vnd.docker.image.rootfs.foreign.diff.tar.gzip"; break;
            case "urls": manifest["layers"]![0]!["urls"] = new JsonArray("https://example.invalid/blob"); break;
            case "inline-data": manifest["config"]!["data"] = "e30="; break;
            case "negative-size": manifest["layers"]![0]!["size"] = -1; break;
            case "string-size": manifest["layers"]![0]!["size"] = "10"; break;
            case "total-byte-overflow": manifest["layers"]![0]!["size"] = long.MaxValue; break;
            case "overflow-size": manifest["layers"]![0]!["size"] = JsonNode.Parse("18446744073709551616"); break;
            case "artifact": manifest["artifactType"] = "example/artifact"; break;
            case "subject": manifest["subject"] = new JsonObject(); break;
            case "missing-config": manifest.Remove("config"); break;
            case "missing-layers": manifest.Remove("layers"); break;
            case "digest-newline": manifest["layers"]![0]!["digest"] = "sha256:" + new string('a', 64) + "\n"; break;
            case "too-many-layers":
                var layers = manifest["layers"]!.AsArray();
                while (layers.Count <= ImagePublicationValidation.MaxLayers) { layers.Add(layers[0]!.DeepClone()); }
                break;
        }
        var bytes = Bytes(manifest);
        Assert.Throws<RegistryImageValidationException>(() => RegistryImageValidator.Verify(bytes, config, RegistryImageValidator.Digest(bytes), "linux/amd64"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2,\"schemaVersion\":2}")]
    [InlineData("{\"a\":{\"x\":1,\"x\":2}}")]
    public void Rejects_malformed_or_ambiguous_json(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.Throws<RegistryImageValidationException>(() => RegistryImageValidator.Verify(bytes, "{}"u8.ToArray(), RegistryImageValidator.Digest(bytes), "linux/amd64"));
    }

    [Fact]
    public void Bounds_documents_but_accepts_images_above_the_former_two_gib_quota()
    {
        var (_, config) = Image();
        var oversized = new byte[ImagePublicationValidation.MaxManifestBytes + 1];
        Assert.Throws<RegistryImageValidationException>(() => RegistryImageValidator.Verify(oversized, config, RegistryImageValidator.Digest(oversized), "linux/amd64"));
        var (manifest, _) = Image();
        manifest["layers"]![0]!["size"] = 3L * 1024 * 1024 * 1024;
        var bytes = Bytes(manifest);
        var image = RegistryImageValidator.Verify(bytes, config, RegistryImageValidator.Digest(bytes), "linux/amd64");
        Assert.True(image.SizeBytes > 3L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void Detects_manifest_config_and_platform_tampering()
    {
        var (manifest, config) = Image();
        var bytes = Bytes(manifest);
        Assert.Equal(ImagePublicationErrors.Incomplete, Assert.Throws<RegistryImageValidationException>(() =>
            RegistryImageValidator.Verify(bytes, config, "sha256:" + new string('0', 64), "linux/amd64")).Code);
        var changed = config.ToArray();
        changed[0] = (byte)' ';
        Assert.Equal(ImagePublicationErrors.Incomplete, Assert.Throws<RegistryImageValidationException>(() =>
            RegistryImageValidator.Verify(bytes, changed, RegistryImageValidator.Digest(bytes), "linux/amd64")).Code);
        Assert.Equal(ImagePublicationErrors.PlatformMismatch, Assert.Throws<RegistryImageValidationException>(() =>
            RegistryImageValidator.Verify(bytes, config, RegistryImageValidator.Digest(bytes), "linux/arm64")).Code);
    }

    [Theory]
    [InlineData("../org")]
    [InlineData("org/other")]
    [InlineData("org\n")]
    [InlineData("UPPER")]
    [InlineData("org___other")]
    [InlineData("org-")]
    public void Repository_identity_rejects_path_and_separator_injection(string segment)
    {
        Assert.Throws<ArgumentException>(() => ImagePublicationValidation.Repository(segment, "app_test", "api"));
    }

    [Fact]
    public void Lifecycle_is_immutable_and_checks_scope_target_and_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new TestClock(now);
        var lifecycle = new ImagePublicationLifecycleService(new IdGenerator(), clock);
        var app = new MinicloudApp { Id = "app_test", OrganizationId = "org_test" };
        var target = new ImageBuildTarget(new string('a', 64), "linux/amd64");
        var request = new CreateImagePublicationRequest("api", new string('b', 64), target, Guid.NewGuid().ToString("D"));
        var publication = lifecycle.Create(app, request, target);
        lifecycle.BeginUpload(publication);
        var (manifest, config) = Image();
        var bytes = Bytes(manifest);
        var digest = RegistryImageValidator.Digest(bytes);
        lifecycle.Complete(publication, bytes, config, digest);
        var revision = publication.Revision;
        lifecycle.Complete(publication, bytes, config, digest);
        Assert.Equal(revision, publication.Revision);
        Assert.True(lifecycle.ValidateBinding(publication, app, "api", target).IsValid);
        Assert.False(lifecycle.ValidateBinding(publication, app, "other-service", target).IsValid);
        Assert.False(lifecycle.ValidateBinding(publication, new MinicloudApp { Id = "app_other", OrganizationId = app.OrganizationId }, "api", target).IsValid);
        Assert.False(lifecycle.ValidateBinding(publication, new MinicloudApp { Id = app.Id, OrganizationId = "org_other" }, "api", target).IsValid);
        Assert.False(lifecycle.ValidateBinding(publication, app, "api", target with { Version = new string('c', 64) }).IsValid);
        Assert.Throws<RegistryImageValidationException>(() => lifecycle.Complete(publication, bytes, config, "sha256:" + new string('0', 64)));
        Assert.Throws<InvalidOperationException>(() => lifecycle.BeginUpload(publication));
        Assert.Throws<RegistryImageValidationException>(() => lifecycle.Create(app, request, target with { Platform = "linux/arm64" }));
        clock.Now = now.AddDays(2);
        Assert.False(lifecycle.ValidateBinding(publication, app, "api", target).IsValid);
        var pending = lifecycle.Create(app, request, target);
        clock.Now = clock.Now.AddDays(2);
        Assert.Equal(ImagePublicationErrors.Expired, Assert.Throws<RegistryImageValidationException>(() => lifecycle.BeginUpload(pending)).Code);
        var credentials = new ImagePublicationCredentialsResponse("example.invalid", "repo", "tag", "user", "SECRET", now);
        Assert.DoesNotContain("SECRET", credentials.ToString());
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("example:1", null, null, true)]
    [InlineData(null, "art_1", null, true)]
    [InlineData(null, null, "img_1", true)]
    [InlineData("example:1", "art_1", null, false)]
    [InlineData("example:1", null, "img_1", false)]
    [InlineData(null, "art_1", "img_1", false)]
    public void Deployment_input_is_an_exclusive_union(string? image, string? artifact, string? publication, bool valid)
    {
        var request = new CreateDeploymentRequest("app_test", "none", null,
            [new DeploymentServiceRequest("api", image, 8080, true, "/", "/health", ArtifactId: artifact, ImagePublicationId: publication)]);
        Assert.Equal(valid, ControlPlaneValidation.ValidateDeployment(request).IsValid);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (JsonObject Manifest, byte[] Config) Image(string platform = "linux/amd64", string mediaType = RegistryImageValidator.OciManifest)
    {
        var split = platform.Split('/');
        var config = Bytes(new JsonObject
        {
            ["os"] = split[0], ["architecture"] = split[1],
            ["rootfs"] = new JsonObject { ["type"] = "layers", ["diff_ids"] = new JsonArray("sha256:" + new string('b', 64)) }
        });
        var oci = mediaType == RegistryImageValidator.OciManifest;
        return (new JsonObject
        {
            ["schemaVersion"] = 2, ["mediaType"] = mediaType,
            ["config"] = new JsonObject
            {
                ["mediaType"] = oci ? "application/vnd.oci.image.config.v1+json" : "application/vnd.docker.container.image.v1+json",
                ["digest"] = RegistryImageValidator.Digest(config), ["size"] = config.Length
            },
            ["layers"] = new JsonArray(new JsonObject
            {
                ["mediaType"] = oci ? "application/vnd.oci.image.layer.v1.tar+gzip" : "application/vnd.docker.image.rootfs.diff.tar.gzip",
                ["digest"] = "sha256:" + new string('a', 64), ["size"] = 10
            })
        }, config);
    }

    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
}
