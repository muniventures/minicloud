using System.IO.Compression;
using System.Text.Json;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;

namespace Minicloud.Tests;

public sealed class ArtifactDockerfilePathTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Manifest_dockerfile_path_resolves_to_bundled_file(bool outsideContext)
    {
        var root = Directory.CreateTempSubdirectory("minicloud-artifact-path-");
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root.FullName, "source"));
            var dockerfile = Path.Combine(outsideContext ? root.FullName : source.FullName, "Service.Dockerfile");
            File.WriteAllText(dockerfile, "FROM python:3.9\nEXPOSE 8000\nCMD [\"python\", \"main.py\"]");
            File.WriteAllText(Path.Combine(source.FullName, "main.py"), "print('test')");
            var service = new MinicloudServiceConfig(source.FullName, dockerfile, null, 8000, true, "/", "/docs");
            var bundle = DeploymentArtifactBundler.Create("app_test", "transfermarkt", service, "test", root.FullName);
            using var archive = ZipFile.OpenRead(bundle.ZipPath);
            using var stream = archive.GetEntry(DeploymentArtifactBundler.ManifestEntryName)!.Open();
            using var manifest = JsonDocument.Parse(stream);
            var path = manifest.RootElement.GetProperty("dockerfilePath").GetString()!;
            Assert.False(Path.IsPathRooted(path));
            Assert.Equal(outsideContext ? "minicloud-artifact/Dockerfile" : "Service.Dockerfile", path);
            Assert.NotNull(archive.GetEntry(path));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
