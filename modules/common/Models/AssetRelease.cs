using System.ComponentModel.DataAnnotations;

namespace Minicloud.Common.Models;

public sealed class AssetRelease
{
    [Key] public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string ArtifactId { get; set; } = default!;
    public DeploymentArtifact Artifact { get; set; } = default!;
    public string InputSha256 { get; set; } = default!;
    public string OutputPath { get; set; } = default!;
    public string? ManifestJson { get; set; }
    public string? ManifestSha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
}
