using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Api.Services;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DatabaseBackupDownloadTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private MinicloudDbContext db = default!;
    private DatabaseBackupDownloadService service = default!;
    private readonly Handler handler = new();
    private HttpClient client = default!;
    private readonly ActorIdentity actor = new("api_key", null, "key", "org_test", new HashSet<string> { ApiKeyScopes.DatabasesManage, ApiKeyScopes.SecretsRead });
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var encryption = new SecretEncryptionService(Options.Create(new SecretEncryptionOptions { EncryptionKey = "synthetic-test-key" }));
        db.Organizations.Add(new Organization { Id = "org_test", Name = "Test", Slug = "test" });
        db.Apps.Add(new MinicloudApp { Id = "app_repository", OrganizationId = "org_test", Name = "Repository", Slug = "dbrepo-test", Database = DatabaseMode.None });
        db.ManagedDatabases.Add(new ManagedDatabase { Id = "database_test", OrganizationId = "org_test", Name = "source", Engine = "sqlite", Status = "deleted" });
        db.Domains.Add(new Domain { Id = "domain_test", OrganizationId = "org_test", AppId = "app_repository", ServiceName = "repository", Hostname = "backup.example.test", Kind = "managed", DnsStatus = "ready", ApplyStatus = "ready", SslStatus = DomainBindingService.SslReady, Status = DomainBindingService.StatusActive, IsPrimary = true });
        db.DatabaseBackupPolicies.Add(new DatabaseBackupPolicy { Id = "policy_test", OrganizationId = "org_test", DatabaseId = "database_test", RepositoryAppId = "app_repository", KeyVersion = "key_test", EncryptedKeyMaterialJson = encryption.Encrypt(JsonSerializer.Serialize(new { repositoryToken = new string('t', 64), encryptionKey = "never-return-this-key", keyVersion = "key_test" })) });
        db.DatabaseBackupArtifacts.Add(new DatabaseBackupArtifact { Id = "artifact_test", OrganizationId = "org_test", DatabaseId = "database_test", RepositoryAppId = "app_repository", BackupId = "20261109T000000Z-test", KeyVersion = "key_test", Status = "complete", SizeBytes = 3 });
        await db.SaveChangesAsync();
        client = new HttpClient(handler);
        service = new(db, new(db), encryption, new(db, new IdGenerator(), TimeProvider.System), new Factory(client));
    }
    public async Task DisposeAsync() { client.Dispose(); await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task DeletedSQLiteSourceDownloadsEncryptedBytesWithoutSourceHostOrClientSecrets()
    {
        using var download = await service.PrepareAsync("database_test", "artifact_test", "database.dump.enc", actor, default);
        using var destination = new MemoryStream();
        await download.CopyToAsync(destination, default);
        Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
        Assert.Equal("Bearer " + new string('t', 64), handler.Authorization);
        Assert.Equal("https://backup.example.test/v1/org-test/database_test/20261109T000000Z-test/database.dump.enc", handler.Url);
        Assert.DoesNotContain("never-return-this-key", download.Filename);
        var audit = await db.AuditEvents.SingleAsync();
        Assert.DoesNotContain(new string('t', 64), audit.Details);
        Assert.DoesNotContain("never-return-this-key", audit.Details);
    }

    [Fact]
    public async Task RetainedRepositoryRemainsDownloadableAfterDestinationChange()
    {
        (await db.DatabaseBackupPolicies.SingleAsync()).RepositoryAppId = null;
        await db.SaveChangesAsync();
        using var download = await service.PrepareAsync("database_test", "artifact_test", "database.dump.enc", actor, default);
        Assert.StartsWith("https://backup.example.test/", handler.Url);
    }

    [Fact]
    public async Task RequiresBothAuthoritiesAndSourceScopedCompletedArtifact()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareAsync("database_test", "artifact_test", "database.dump.enc", actor with { OrganizationId = "other" }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareAsync("database_test", "artifact_test", "database.dump.enc", actor with { Scopes = new HashSet<string> { ApiKeyScopes.DatabasesManage } }, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.PrepareAsync("database_test", "different-artifact", "database.dump.enc", actor, default));
        (await db.DatabaseBackupArtifacts.SingleAsync()).Status = "pending";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.PrepareAsync("database_test", "artifact_test", "database.dump.enc", actor, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RejectsUnapprovedFilenamesRedirectsAndOversizedManifest()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync("database_test", "artifact_test", "../secrets", actor, default));
        handler.Status = HttpStatusCode.Redirect;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync("database_test", "artifact_test", "database.dump.enc", actor, default));
        handler.Status = HttpStatusCode.OK; handler.Length = 1024 * 1024 + 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync("database_test", "artifact_test", "manifest.json.enc", actor, default));
    }

    [Fact]
    public async Task RejectsTruncatedArtifactsDuringBoundedStreaming()
    {
        handler.Length = 4;
        using var download = await service.PrepareAsync("database_test", "artifact_test", "database.dump.enc", actor, default);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => download.CopyToAsync(destination, default));
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        public string? Url { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public long Length { get; set; } = 3;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Authorization = request.Headers.Authorization?.ToString(); Url = request.RequestUri?.ToString();
            var content = new ByteArrayContent([1, 2, 3]); content.Headers.ContentLength = Length;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = content, RequestMessage = request });
        }
    }
}
