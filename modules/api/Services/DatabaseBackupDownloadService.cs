using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class DatabaseBackupDownloadService(MinicloudDbContext db, ControlPlaneAuthorization authorization, SecretEncryptionService encryption, AuditEventService audit, IHttpClientFactory clients)
{
    public const string ClientName = "database-backup-downloads";
    public async Task<DatabaseBackupDownload> PrepareAsync(string databaseId, string artifactId, string filename, ActorIdentity actor, CancellationToken ct)
    {
        var limit = filename switch { "database.dump.enc" => 8L * 1024 * 1024 * 1024, "manifest.json.enc" => 1024L * 1024, _ => throw new ArgumentException("Unsupported backup artifact file.") };
        var database = await db.ManagedDatabases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == databaseId, ct) ?? throw new KeyNotFoundException();
        if (!await authorization.CanAccessOrganizationAsync(actor, database.OrganizationId, OrganizationAction.ManageDatabases, ct) ||
            !await authorization.CanAccessOrganizationAsync(actor, database.OrganizationId, OrganizationAction.ReadSecrets, ct)) throw new UnauthorizedAccessException();
        var artifact = await db.DatabaseBackupArtifacts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifactId && x.DatabaseId == databaseId && x.OrganizationId == database.OrganizationId && x.Status == "complete", ct)
            ?? throw new KeyNotFoundException();
        if (!Regex.IsMatch(artifact.BackupId, "^[A-Za-z0-9][A-Za-z0-9_-]{7,95}$")) throw new InvalidOperationException("Stored backup identity is unavailable.");
        var repository = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artifact.RepositoryAppId && x.OrganizationId == database.OrganizationId && x.Database == DatabaseMode.None && x.Slug.StartsWith("dbrepo-") && x.DeletionStatus == null, ct)
            ?? throw new InvalidOperationException("Registered backup repository is unavailable.");
        var domain = await db.Domains.AsNoTracking().Where(x => x.AppId == repository.Id && x.OrganizationId == database.OrganizationId && x.ServiceName == "repository" && x.Status == DomainBindingService.StatusActive && x.SslStatus == DomainBindingService.SslReady)
            .OrderByDescending(x => x.IsPrimary).FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException("Repository HTTPS is unavailable.");
        if (Uri.CheckHostName(domain.Hostname) != UriHostNameType.Dns || domain.Hostname.Contains(':')) throw new InvalidOperationException("Repository HTTPS identity is unavailable.");
        var policy = await db.DatabaseBackupPolicies.AsNoTracking().SingleAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId, ct);
        if (policy.KeyVersion != artifact.KeyVersion)
            throw new InvalidOperationException("Retained backup credential version is unavailable.");
        string token;
        try
        {
            using var material = JsonDocument.Parse(encryption.Decrypt(policy.EncryptedKeyMaterialJson ?? throw new InvalidOperationException()));
            token = material.RootElement.GetProperty("repositoryToken").GetString() ?? "";
            if (token.Length < 32 || token.Any(char.IsWhiteSpace)) throw new InvalidOperationException();
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException or FormatException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidOperationException("Backup repository credential custody is unavailable."); }
        var sourceNamespace = database.OrganizationId.Replace('_', '-').ToLowerInvariant();
        var url = $"https://{domain.Hostname}/v1/{Uri.EscapeDataString(sourceNamespace)}/{Uri.EscapeDataString(database.Id)}/{Uri.EscapeDataString(artifact.BackupId)}/{filename}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new InvalidOperationException("Backup repository transfer is unavailable."); }
        if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri || response.Content.Headers.ContentLength is not { } length || length <= 0 || length > limit)
        { response.Dispose(); throw new InvalidOperationException("Backup repository did not return a complete bounded artifact."); }
        try
        {
            await audit.AppendAsync(database.OrganizationId, null, actor.Describe(), "database.backup.file.downloaded", JsonSerializer.Serialize(new { databaseId, artifactId, filename }), ct);
            return new DatabaseBackupDownload(response, artifact.BackupId + "-" + filename, length);
        }
        catch { response.Dispose(); throw; }
    }
}
