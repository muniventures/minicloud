using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Api.Services;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Vultr;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class EnvironmentSshAccessServiceTests
{
    [Fact]
    public async Task ExistingSelectionIsAccountScopedAndSnapshotsValidatedPublicMaterial()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        await service.ApplySelectionAsync(fixture.Environment, fixture.Account.Id,
            new SshAccessRequest("existing", KeyId: fixture.Key.Id), fixture.Owner, false, default);

        Assert.Equal("existing", fixture.Environment.SshAccessMode);
        Assert.Equal(fixture.Account.Id, fixture.Environment.SshProviderAccountId);
        Assert.Equal(fixture.Key.Id, fixture.Environment.SshKeyId);
        Assert.Equal(fixture.PublicKey, fixture.Environment.SshPublicKey);
        Assert.Equal(fixture.Key.Fingerprint, fixture.Environment.SshFingerprint);
        Assert.Equal(1, fixture.Environment.SshRevision);
        Assert.Equal("user_owner", fixture.Environment.SshSelectedBy);

        var otherAccount = new ProviderAccount
        {
            Id = "provider_other", OrganizationId = fixture.Organization.Id, Name = "Other",
            CredentialPath = "fixture", CredentialGeneration = "generation"
        };
        fixture.Db.ProviderAccounts.Add(otherAccount);
        await fixture.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<SshAccessException>(() => service.ApplySelectionAsync(
            fixture.Environment, otherAccount.Id, new SshAccessRequest("existing", KeyId: fixture.Key.Id),
            fixture.Owner, false, default));
        Assert.Equal("ssh_key_not_found", error.Code);
    }

    [Fact]
    public async Task NewSelectionRequiresProviderWriteAndDoesNotRegisterDuringSave()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var request = new SshAccessRequest("new", Name: " Work laptop ", PublicKey: fixture.PublicKey);
        var developer = new ActorIdentity("browser_session", "user_developer", null, null, new HashSet<string>());

        var denied = await Assert.ThrowsAsync<SshAccessException>(() => service.ApplySelectionAsync(
            fixture.Environment, fixture.Account.Id, request, developer, false, default));
        Assert.Equal("forbidden", denied.Code);

        await service.ApplySelectionAsync(fixture.Environment, fixture.Account.Id, request, fixture.Owner, false, default);

        Assert.Equal("new", fixture.Environment.SshAccessMode);
        Assert.Equal("Work laptop", fixture.Environment.SshKeyName);
        Assert.Equal("not_registered", fixture.Environment.SshRegistrationState);
        Assert.Empty(await fixture.Db.ProviderSshKeys.Where(x => x.RegistrationState == "registering").ToListAsync());
    }

    [Fact]
    public async Task PatchRequiresMatchingRevisionAndUnionRejectsMixedFields()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var conflict = await Assert.ThrowsAsync<SshAccessException>(() => service.ApplySelectionAsync(
            fixture.Environment, fixture.Account.Id,
            new SshAccessRequest("none", ExpectedRevision: 1), fixture.Owner, true, default));
        Assert.Equal("ssh_selection_revision_conflict", conflict.Code);

        var mixed = await Assert.ThrowsAsync<SshAccessException>(() => service.ApplySelectionAsync(
            fixture.Environment, fixture.Account.Id,
            new SshAccessRequest("none", KeyId: fixture.Key.Id), fixture.Owner, false, default));
        Assert.Equal("ssh_key_invalid", mixed.Code);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, MinicloudDbContext db, Organization organization,
            ProviderAccount account, ProviderSshKey key, MinicloudApp environment, string publicKey)
        {
            _connection = connection;
            Db = db;
            Organization = organization;
            Account = account;
            Key = key;
            Environment = environment;
            PublicKey = publicKey;
            Owner = new ActorIdentity("browser_session", "user_owner", null, null, new HashSet<string>());
        }

        public MinicloudDbContext Db { get; }
        public Organization Organization { get; }
        public ProviderAccount Account { get; }
        public ProviderSshKey Key { get; }
        public MinicloudApp Environment { get; }
        public string PublicKey { get; }
        public ActorIdentity Owner { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options;
            var db = new MinicloudDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var organization = new Organization { Id = "org_ssh", Name = "SSH", Slug = "ssh", CreatedAt = now, UpdatedAt = now };
            var account = new ProviderAccount
            {
                Id = "provider_ssh", OrganizationId = organization.Id, Name = "Fixture", CredentialPath = "fixture",
                CredentialGeneration = "generation"
            };
            var publicKey = Ed25519PublicKey();
            var key = new ProviderSshKey
            {
                Id = "sshkey_local", OrganizationId = organization.Id, ProviderAccountId = account.Id,
                ProviderKeyId = "42", Name = "Laptop", PublicKey = publicKey, Fingerprint = Fingerprint(publicKey),
                KeyType = "ed25519", RegistrationState = "discovered", CreatedAt = now, UpdatedAt = now
            };
            var plan = new ProviderPlan
            {
                Id = "plan_ssh", OrganizationId = organization.Id, ProviderAccountId = account.Id, Name = "Fixture",
                NormalizedName = "FIXTURE", Region = "fsn", Sku = "cx22"
            };
            var environment = new MinicloudApp
            {
                Id = "app_ssh", OrganizationId = organization.Id, ApplicationId = null, Name = "SSH",
                Slug = "ssh", PlanId = plan.Id, SelectedPlan = plan, SshAccessMode = "none", CreatedAt = now, UpdatedAt = now
            };
            db.Organizations.Add(organization);
            db.Users.AddRange(
                new User { Id = "user_owner", Email = "owner@example.test", CreatedAt = now, UpdatedAt = now },
                new User { Id = "user_developer", Email = "developer@example.test", CreatedAt = now, UpdatedAt = now });
            db.ProviderAccounts.Add(account);
            db.ProviderPlans.Add(plan);
            db.ProviderSshKeys.Add(key);
            db.OrganizationMembers.Add(new OrganizationMember
            {
                Id = "member_owner", OrganizationId = organization.Id, UserId = "user_owner",
                Role = OrganizationRole.Owner, CreatedAt = now, UpdatedAt = now
            });
            db.OrganizationMembers.Add(new OrganizationMember
            {
                Id = "member_dev", OrganizationId = organization.Id, UserId = "user_developer",
                Role = OrganizationRole.Developer, CreatedAt = now, UpdatedAt = now
            });
            db.Apps.Add(environment);
            await db.SaveChangesAsync();
            return new Fixture(connection, db, organization, account, key, environment, publicKey);
        }

        public EnvironmentSshAccessService CreateService() =>
            new(Db, new ControlPlaneAuthorization(Db),
                new ProviderAccountClientFactory(Db, null!, null!, Options.Create(new HetznerOptions()),
                    Options.Create(new DigitalOceanOptions()), Options.Create(new VultrOptions()), null!),
                new IdGenerator(), TimeProvider.System);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static string Ed25519PublicKey()
        {
            using var stream = new MemoryStream();
            WriteString(stream, Encoding.ASCII.GetBytes("ssh-ed25519"));
            WriteString(stream, Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
            return "ssh-ed25519 " + Convert.ToBase64String(stream.ToArray()) + " fixture";
        }

        private static string Fingerprint(string key)
        {
            var blob = Convert.FromBase64String(key.Split(' ')[1]);
            return "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(blob)).TrimEnd('=');
        }

        private static void WriteString(Stream stream, byte[] value)
        {
            Span<byte> length = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
            stream.Write(length);
            stream.Write(value);
        }
    }
}
