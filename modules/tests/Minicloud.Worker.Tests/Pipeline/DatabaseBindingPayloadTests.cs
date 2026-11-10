using System.Text.Json;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Pipeline.Steps;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class DatabaseBindingPayloadTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness harness;
    public DatabaseBindingPayloadTests(WorkerSqliteTestHarness harness) { this.harness = harness; }

    [Fact]
    public async Task AppliedBindingsPersistByServiceAcrossReleasePayloadsAndEscapeCredentials()
    {
        var (org, app) = await harness.SeedOrgAndAppAsync("binding-payload");
        await using var db = harness.CreateDbContext();
        var encryption = new SecretEncryptionService(Options.Create(new SecretEncryptionOptions { EncryptionKey = "test-binding-key" }));
        var database = new ManagedDatabase { Id = "database_payload", OrganizationId = org.Id, Name = "orders", Placement = "shared", Status = "ready", EncryptedCredentialsJson = encryption.Encrypt(JsonSerializer.Serialize(new DatabaseConnectionCredentials("orders", "user", "p@ss;word"), new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        db.ManagedDatabases.Add(database);
        db.ManagedDatabaseBindings.Add(new ManagedDatabaseBinding { Id = "binding_payload", OrganizationId = org.Id, DatabaseId = database.Id, AppId = app.Id, ServiceName = "backend", Status = "applied", Host = "host.docker.internal", Port = 23456 });
        await db.SaveChangesAsync();
        var mapped = await SshDeployStep.ResolveDatabaseBindingsAsync(db, encryption, org.Id, app.Id, new HashSet<string> { "backend", "frontend" }, default);
        Assert.Single(mapped);
        Assert.Contains("p%40ss%3Bword", mapped["backend"]["DATABASE_URL"]);
        Assert.DoesNotContain("frontend", mapped.Keys);
        var repeated = await SshDeployStep.ResolveDatabaseBindingsAsync(db, encryption, org.Id, app.Id, new HashSet<string> { "backend" }, default);
        Assert.Equal(mapped["backend"]["DATABASE_URL"], repeated["backend"]["DATABASE_URL"]);
        Assert.Empty(await SshDeployStep.ResolveDatabaseBindingsAsync(db, encryption, "foreign", app.Id, new HashSet<string> { "backend" }, default));
    }

    [Fact]
    public async Task PendingBindingBlocksOnlyItsSelectedService()
    {
        var (org, app) = await harness.SeedOrgAndAppAsync("binding-pending");
        await using var db = harness.CreateDbContext();
        db.ManagedDatabases.Add(new ManagedDatabase { Id = "database_pending", OrganizationId = org.Id, Name = "orders", Placement = "shared", Status = "ready" });
        db.ManagedDatabaseBindings.Add(new ManagedDatabaseBinding { Id = "binding_pending", OrganizationId = org.Id, DatabaseId = "database_pending", AppId = app.Id, ServiceName = "backend", Status = "pending" });
        await db.SaveChangesAsync();
        Assert.Empty(await SshDeployStep.ResolveDatabaseBindingsAsync(db, null, org.Id, app.Id, new HashSet<string> { "frontend" }, default));
        await Assert.ThrowsAsync<PipelineStepException>(() => SshDeployStep.ResolveDatabaseBindingsAsync(db, null, org.Id, app.Id, new HashSet<string> { "backend" }, default));
    }
}
