using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Tests.Services;

public sealed class ProviderSshKeyServiceTests
{
    [Fact]
    public async Task ReservationSnapshotsSelectingActor()
    {
        var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        try
        {
            var (organization, app) = await harness.SeedOrgAndAppAsync("ssh-actor");
            await using var db = harness.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            var account = new ProviderAccount
            {
                Id = "provider_ssh", OrganizationId = organization.Id, Name = "Fixture",
                CredentialPath = "fixture", CredentialGeneration = "generation"
            };
            var plan = new ProviderPlan
            {
                Id = "plan_ssh", OrganizationId = organization.Id, ProviderAccountId = account.Id,
                ProviderAccount = account, Name = "Fixture", NormalizedName = "FIXTURE",
                Region = "fsn", Sku = "cx22", CreatedAt = now, UpdatedAt = now
            };
            app.PlanId = plan.Id;
            app.SshAccessMode = "new";
            app.SshProviderAccountId = account.Id;
            app.SshSelectedBy = "user_original";
            var deployment = new Deployment
            {
                Id = "deployment_ssh", OrganizationId = organization.Id, AppId = app.Id,
                Database = DatabaseMode.None, Status = DeploymentStatus.Queued, CreatedAt = now, UpdatedAt = now
            };
            db.ProviderAccounts.Add(account);
            db.ProviderPlans.Add(plan);
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();

            await new ProviderCatalogService(db, new IdGenerator(), TimeProvider.System)
                .ReserveAsync(app, deployment, default);

            Assert.Equal("user_original", deployment.SshSelectingActor);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task AllocationRejectsProviderScopeChangedAfterReservation()
    {
        var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        try
        {
            var (organization, app) = await harness.SeedOrgAndAppAsync("ssh-scope");
            await using var db = harness.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            var account = new ProviderAccount
            {
                Id = "provider_ssh", OrganizationId = organization.Id, Name = "Fixture",
                CredentialPath = "fixture", CredentialGeneration = "current"
            };
            var (publicKey, fingerprint) = CreatePublicKey();
            app.SshSelectedBy = "user_ssh_owner";
            db.Users.Add(new User { Id = "user_ssh_owner", Email = "owner@example.test", CreatedAt = now, UpdatedAt = now });
            db.OrganizationMembers.Add(new OrganizationMember
            {
                Id = "member_ssh_owner", OrganizationId = organization.Id, UserId = "user_ssh_owner",
                Role = OrganizationRole.Owner, CreatedAt = now, UpdatedAt = now
            });
            db.ProviderAccounts.Add(account);
            var deployment = new Deployment
            {
                Id = "deployment_ssh", OrganizationId = organization.Id, AppId = app.Id,
                Database = DatabaseMode.None, Status = DeploymentStatus.Queued, CreatedAt = now, UpdatedAt = now,
                SshSelectionMode = "new", SshSelectionRevision = 1, SshProviderAccountId = account.Id,
                SshCredentialGeneration = "previous", SshSelectingActor = "user_ssh_owner",
                SshPublicKey = publicKey, SshFingerprint = fingerprint, SshKeyType = "ed25519",
                SshSnapshotTakenAt = now
            };
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();

            var clientFactory = new ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!);
            var service = new ProviderSshKeyService(db, clientFactory, new ControlPlaneAuthorization(db),
                new IdGenerator(), TimeProvider.System);

            var error = await Assert.ThrowsAsync<ProviderSshKeyException>(() =>
                service.ResolveForAllocationAsync(deployment.Id, organization.Id, app.Id, default));

            Assert.Equal("ssh_key_changed", error.Code);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    private static (string PublicKey, string Fingerprint) CreatePublicKey()
    {
        using var stream = new MemoryStream();
        WriteString(stream, Encoding.ASCII.GetBytes("ssh-ed25519"));
        WriteString(stream, Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var blob = stream.ToArray();
        return ("ssh-ed25519 " + Convert.ToBase64String(blob), "SHA256:" +
            Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='));
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
