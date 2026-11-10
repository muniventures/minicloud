using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Minicloud.Worker.Storage;

namespace Minicloud.Worker.Tests.Storage;

public sealed class ArtifactRetrieverTests : IDisposable
{
    private readonly string _tempRoot;

    public ArtifactRetrieverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"worker-test-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFileSystemArtifactRetriever_ReadsZipFileSuccessfully()
    {
        var options = Options.Create(new WorkerOptions { ArtifactRootPath = _tempRoot });
        var retriever = new LocalFileSystemArtifactRetriever(options);

        var zipPath = Path.Combine(_tempRoot, "test-artifact.zip");
        CreateSampleZipFile(zipPath, "sample.txt", "hello worker artifact");

        await using var stream = await retriever.OpenArtifactStreamAsync("test-artifact.zip");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("sample.txt");

        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        var content = await reader.ReadToEndAsync();
        Assert.Equal("hello worker artifact", content);
    }

    [Fact]
    public async Task LocalFileSystemArtifactRetriever_ResolvesNestedServiceZipByArtifactId()
    {
        var options = Options.Create(new WorkerOptions { ArtifactRootPath = _tempRoot });
        var retriever = new LocalFileSystemArtifactRetriever(options);

        var nestedDir = Path.Combine(_tempRoot, "org_1", "app_1", "art_xyz123");
        Directory.CreateDirectory(nestedDir);
        var zipPath = Path.Combine(nestedDir, "service.zip");
        CreateSampleZipFile(zipPath, "entry.txt", "nested service content");

        await using var stream = await retriever.OpenArtifactStreamAsync("art_xyz123");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("entry.txt");

        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        var content = await reader.ReadToEndAsync();
        Assert.Equal("nested service content", content);
    }

    [Fact]
    public async Task LocalFileSystemArtifactRetriever_ThrowsWhenFileNotFound()
    {
        var options = Options.Create(new WorkerOptions { ArtifactRootPath = _tempRoot });
        var retriever = new LocalFileSystemArtifactRetriever(options);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            retriever.OpenArtifactStreamAsync("non_existent_art"));
    }

    [Fact]
    public async Task ApiHttpArtifactRetriever_FetchesAndStreamsFromApiEndpoint()
    {
        string? capturedUri = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            capturedUri = request.RequestUri?.PathAndQuery;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("api zip stream"))
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000")
        };

        var retriever = new ApiHttpArtifactRetriever(httpClient);
        await using var stream = await retriever.OpenArtifactStreamAsync("art_remote999");
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        Assert.Equal("/api/artifacts/art_remote999/file", capturedUri);
        Assert.Equal("api zip stream", content);
    }

    private static void CreateSampleZipFile(string zipPath, string entryName, string entryContent)
    {
        using var zipFile = File.Create(zipPath);
        using var archive = new ZipArchive(zipFile, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(entryContent);
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
