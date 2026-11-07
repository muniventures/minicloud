using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Registry;
using Minicloud.Common.Auth;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Minicloud.Tests;

public sealed class RegistryPersistenceTests : IAsyncLifetime
{
    private SqliteConnection? _sqlite;
    private DbContextOptions<MinicloudDbContext> _options = default!;
    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private MinicloudApp _app = default!;

    public async Task InitializeAsync()
    {
        var postgres = Environment.GetEnvironmentVariable("MINICLOUD_REGISTRY_TEST_CONNECTION");
        var builder = new DbContextOptionsBuilder<MinicloudDbContext>();
        if (string.IsNullOrWhiteSpace(postgres))
        {
            _sqlite = new SqliteConnection("Data Source=:memory:");
            await _sqlite.OpenAsync();
            builder.UseSqlite(_sqlite);
        }
        else { builder.UseNpgsql(postgres); }
        _options = builder.Options;
        await using var db = CreateContext();
        if (_sqlite is null) { await db.Database.MigrateAsync(); }
        else { await db.Database.EnsureCreatedAsync(); }
        var now = DateTimeOffset.UtcNow;
        var org = new Organization { Id = "org_" + _suffix, Slug = "org-" + _suffix, Name = "Registry test", CreatedAt = now, UpdatedAt = now };
        _app = new MinicloudApp { Id = "app_" + _suffix, OrganizationId = org.Id, Slug = "app-" + _suffix, Name = "Registry test", CreatedAt = now, UpdatedAt = now };
        db.Organizations.Add(org);
        db.Apps.Add(_app);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Duplicate_publication_request_cannot_create_two_candidates()
    {
        var key = Guid.NewGuid().ToString("D");
        var first = Publication(key);
        await using (var db = CreateContext())
        {
            db.ImagePublications.Add(first);
            await db.SaveChangesAsync();
        }
        await using var duplicate = CreateContext();
        duplicate.ImagePublications.Add(Publication(key));
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    }

    [Fact]
    public async Task Stale_completion_cannot_overwrite_publication_revision()
    {
        var publication = Publication();
        await using (var seed = CreateContext())
        {
            seed.ImagePublications.Add(publication);
            await seed.SaveChangesAsync();
        }
        await using var first = CreateContext();
        await using var second = CreateContext();
        var a = await first.ImagePublications.SingleAsync(x => x.Id == publication.Id);
        var b = await second.ImagePublications.SingleAsync(x => x.Id == publication.Id);
        a.Status = ImagePublicationStatus.Uploading;
        a.Revision++;
        await first.SaveChangesAsync();
        b.Status = ImagePublicationStatus.Failed;
        b.Revision++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var check = CreateContext();
        Assert.Equal(ImagePublicationStatus.Uploading, (await check.ImagePublications.FindAsync(publication.Id))!.Status);
    }

    [Fact]
    public async Task Sessions_retain_accounting_after_revocation_and_reject_stale_updates()
    {
        var publication = Publication();
        var session = new RegistrySession
        {
            Id = "rs_" + _suffix, OrganizationId = _app.OrganizationId, AppId = _app.Id,
            ImagePublicationId = publication.Id, SubjectId = "api_key_test", CredentialHash = "hash_" + _suffix,
            Kind = RegistrySessionKind.Push, ReservedBytes = 2048, ChargedBytes = 512,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        await using (var seed = CreateContext())
        {
            seed.ImagePublications.Add(publication);
            seed.RegistrySessions.Add(session);
            await seed.SaveChangesAsync();
        }
        await using var first = CreateContext();
        await using var second = CreateContext();
        var a = await first.RegistrySessions.SingleAsync(x => x.Id == session.Id);
        var b = await second.RegistrySessions.SingleAsync(x => x.Id == session.Id);
        a.RevokedAt = DateTimeOffset.UtcNow;
        a.Revision++;
        await first.SaveChangesAsync();
        b.ChargedBytes += 100;
        b.Revision++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var check = CreateContext();
        var retained = (await check.RegistrySessions.FindAsync(session.Id))!;
        Assert.NotNull(retained.RevokedAt);
        Assert.Equal(2048, retained.ReservedBytes);
        Assert.Equal(512, retained.ChargedBytes);
        Assert.Null(retained.ReclaimedAt);
    }

    [Fact]
    public async Task Bound_publication_cannot_be_deleted_and_survives_service_state_replay()
    {
        var publication = Publication();
        publication.Status = ImagePublicationStatus.Ready;
        var lifecycle = new DeploymentLifecycleService(new IdGenerator(), TimeProvider.System);
        var deployment = lifecycle.CreateQueued(_app, new CreateDeploymentRequest(_app.Id, "none", null,
            [new DeploymentServiceRequest("api", null, 8080, true, "/", "/health", ImagePublicationId: publication.Id)]));
        deployment.Status = DeploymentStatus.Succeeded;
        deployment.Services[0].Image = "registry.example.invalid/" + publication.Repository + "@sha256:" + new string('a', 64);
        await using (var seed = CreateContext())
        {
            seed.ImagePublications.Add(publication);
            seed.Deployments.Add(deployment);
            await seed.SaveChangesAsync();
        }
        await using (var read = CreateContext())
        {
            var active = await new DeploymentServiceSetService().BuildActiveServicesAsync(read, _app.Id, default);
            Assert.Equal(publication.Id, Assert.Single(active).ImagePublicationId);
            Assert.Equal(deployment.Services[0].Image, active[0].Image);
        }
        await using var delete = CreateContext();
        delete.ImagePublications.Remove((await delete.ImagePublications.FindAsync(publication.Id))!);
        await Assert.ThrowsAsync<DbUpdateException>(() => delete.SaveChangesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sweep_retires_recent_superseded_images_and_preserves_active_deployments(bool externalReplacement)
    {
        await using var db = CreateContext();
        var lifecycle = new DeploymentLifecycleService(new IdGenerator(), TimeProvider.System);
        var images = Enumerable.Range(0, 4).Select(_ => Publication()).ToArray();
        foreach (var image in images) { image.Status = ImagePublicationStatus.Ready; image.ReadyAt = DateTimeOffset.UtcNow; }
        db.ImagePublications.AddRange(images);
        for (var i = 0; i < images.Length; i++)
        {
            var deployment = lifecycle.CreateQueued(_app, new CreateDeploymentRequest(_app.Id, "none", null,
                [new DeploymentServiceRequest("api", "example/api:latest", 8080, true, "/", "/health",
                    ImagePublicationId: externalReplacement && i == 1 ? null : images[i].Id)]));
            deployment.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i - 10);
            deployment.Status = i == 3 ? DeploymentStatus.Failed : i == 2 ? DeploymentStatus.Deploying : DeploymentStatus.Succeeded;
            db.Deployments.Add(deployment);
        }
        await db.SaveChangesAsync();
        var maintenance = Maintenance(db, new RegistryOptions());
        var owner = await maintenance.BeginAsync(default);
        await maintenance.SweepAsync(owner, default);
        Assert.NotNull(images[0].RetiredAt);
        Assert.Null(images[1].RetiredAt);
        Assert.Null(images[2].RetiredAt);
        Assert.NotNull(images[3].RetiredAt);
        Assert.Equal(2, await db.AuditEvents.CountAsync(x => x.Action == "image_publication.retired"));
    }

    [Fact]
    public async Task Finish_reclaims_recent_revoked_upload_charges_only_after_gc()
    {
        await using var db = CreateContext();
        var image = Publication();
        db.ImagePublications.Add(image);
        var session = new RegistrySession
        {
            Id = "recent_" + _suffix, OrganizationId = _app.OrganizationId, AppId = _app.Id,
            ImagePublicationId = image.Id, SubjectId = "test", CredentialHash = "recent_" + _suffix,
            Kind = RegistrySessionKind.Push, ChargedBytes = 4096, ReservedBytes = 8192,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        db.RegistrySessions.Add(session);
        await db.SaveChangesAsync();
        var directory = Path.Combine(Path.GetTempPath(), "registry-retention-" + _suffix);
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=retention-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var options = new RegistryOptions { CertificatePath = Path.Combine(directory, "cert.pem"), KeyPath = Path.Combine(directory, "key.pem") };
            File.WriteAllText(options.CertificatePath, cert.ExportCertificatePem());
            File.WriteAllText(options.KeyPath, rsa.ExportPkcs8PrivateKeyPem());
            var maintenance = Maintenance(db, options);
            var owner = await maintenance.BeginAsync(default);
            await maintenance.FinishAsync(owner, default, collected: false);
            Assert.Null(session.ReclaimedAt);
            owner = await maintenance.BeginAsync(default);
            await maintenance.FinishAsync(owner, default);
            Assert.NotNull(session.ReclaimedAt);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Upload_credentials_have_no_fixed_byte_reservation_but_still_require_free_disk()
    {
        await using var db = CreateContext();
        var image = Publication();
        db.ImagePublications.Add(image);
        await db.SaveChangesAsync();
        var settings = new RegistryOptions { Enabled = true, PublicUrl = "https://registry.example.invalid", StoragePath = Path.GetTempPath(), MinFreeBytes = 0 };
        var services = RegistryServices(db, settings);
        var actor = new ActorIdentity("api_key", null, "key", _app.OrganizationId, new HashSet<string>());
        await services.Publications.CredentialsAsync(image, actor, default);
        Assert.Equal(0, (await db.RegistrySessions.SingleAsync(x => x.ImagePublicationId == image.Id)).ReservedBytes);
        settings.MinFreeBytes = long.MaxValue;
        var error = Assert.Throws<RegistryImageValidationException>(() => services.Publications.CheckStorage());
        Assert.Contains("shared-volume reserve", error.Message);
    }

    private static RegistryMaintenanceService Maintenance(MinicloudDbContext db, RegistryOptions settings)
    {
        var services = RegistryServices(db, settings);
        return new RegistryMaintenanceService(db, services.Publications, services.Registry, TimeProvider.System);
    }

    private static (ImagePublicationService Publications, RegistryClient Registry) RegistryServices(MinicloudDbContext db, RegistryOptions settings)
    {
        var options = Options.Create(settings);
        var ids = new IdGenerator();
        var clock = TimeProvider.System;
        var registry = new RegistryClient(new HttpClient(new SuccessfulRegistryHandler()), options, new RegistryTokens(options, clock), clock);
        var publications = new ImagePublicationService(db, options,
            new ImageBuildTargetService(db, options, Options.Create(new ProviderPlanOptions())),
            new ImagePublicationLifecycleService(ids, clock), registry, new ControlPlaneAuthorization(db), ids, clock,
            new AuditEventService(db, ids, clock));
        return (publications, registry);
    }

    private sealed class SuccessfulRegistryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private MinicloudDbContext CreateContext() => new(_options);

    private ImagePublication Publication(string? key = null) => new ImagePublicationLifecycleService(new IdGenerator(), TimeProvider.System)
        .Create(_app, new CreateImagePublicationRequest("api", new string('b', 64), new ImageBuildTarget(new string('a', 64), "linux/amd64"), key ?? Guid.NewGuid().ToString("D")),
            new ImageBuildTarget(new string('a', 64), "linux/amd64"));

    public async Task DisposeAsync()
    {
        if (_sqlite is not null) { await _sqlite.DisposeAsync(); }
    }
}
