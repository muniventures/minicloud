using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class BootstrapServiceTests
{
    private const string Installation = "installation-test";
    private const string Operation = "operation-test";
    private static CompleteSetupRequest Request(string token) =>
        new(token, "operator@example.test", "Synthetic-password-123!", "Test Operator", "Workspace", "workspace");

    [Fact]
    public async Task TokenIsVerifierOnlyBoundExpiringAndRotatable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var output = new TokenOutput();
        var prepared = await fixture.Service.PrepareAsync(Installation, Operation, output);
        Assert.Equal("required", prepared.State);
        var record = await fixture.Db.BootstrapRecords.AsNoTracking().SingleAsync();
        Assert.NotEqual(output.Token, record.SetupTokenHash);
        Assert.Equal(fixture.Clock.Now.AddMinutes(30), record.SetupTokenExpiresAt);
        Assert.DoesNotContain(output.Token!, JsonSerializer.Serialize(prepared));
        await Error("setup_state_conflict", () => fixture.Service.CompleteHostAsync("other", Operation, Request(output.Token!)));
        await Error("setup_token_invalid", () => fixture.Service.CompleteHostAsync(Installation, Operation, Request("wrong")));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(30);
        await Error("setup_token_expired", () => fixture.Service.CompleteBrowserAsync(Installation, Request(output.Token!)));
        var old = output.Token!;
        await fixture.Service.RotateTokenAsync(Installation, Operation, output);
        Assert.NotEqual(old, output.Token);
        await Error("setup_token_invalid", () => fixture.Service.CompleteBrowserAsync(Installation, Request(old)));
        await fixture.Service.CompleteBrowserAsync(Installation, Request(output.Token!));
        Assert.Equal(1, fixture.Identities.Creates);
        Assert.Null((await fixture.Db.BootstrapRecords.SingleAsync()).SetupTokenHash);
        await Error("setup_state_conflict", () => fixture.Service.RotateTokenAsync(Installation, Operation, output));
    }

    [Fact]
    public async Task CompletedHostReplayNeedsNoSecretButBrowserReplayIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = new TokenOutput();
        await fixture.Service.PrepareAsync(Installation, Operation, token);
        var original = await fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!));
        fixture.Identities.Unavailable = true;
        var replay = await fixture.Service.CompleteHostAsync(Installation, Operation, null);
        Assert.Equal(original, replay);
        await Error("setup_already_completed", () => fixture.Service.CompleteBrowserAsync(Installation, Request(token.Token!)));
        Assert.Equal("ready", (await fixture.Service.StatusAsync(Installation)).State);
        Assert.Equal(1, await fixture.Db.Users.CountAsync());
        Assert.Equal(1, await fixture.Db.Organizations.CountAsync());
        Assert.Equal(OrganizationRole.Owner, (await fixture.Db.OrganizationMembers.SingleAsync()).Role);
        Assert.Empty(await fixture.Db.Apps.ToListAsync());
        Assert.Empty(await fixture.Db.ProviderAccounts.ToListAsync());
        Assert.Equal(0, fixture.Identities.PasswordChanges);
    }

    [Fact]
    public async Task ConcurrentIndependentContextsCreateExactlyOneOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = new TokenOutput();
        await fixture.Service.PrepareAsync(Installation, Operation, token);
        await using var otherDb = fixture.CreateDb();
        var other = fixture.CreateService(otherDb);
        var results = await Task.WhenAll(
            fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!)),
            other.CompleteHostAsync(Installation, Operation, Request(token.Token!)));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, fixture.Identities.Creates);
        Assert.Equal(1, await fixture.Db.Users.CountAsync());
        Assert.Equal(1, await fixture.Db.Organizations.CountAsync());
        Assert.Equal(1, await fixture.Db.OrganizationMembers.CountAsync());
    }

    [Fact]
    public async Task InterruptedCreationIsReconciledByExactPrivateOperationAfterTokenRotation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = new TokenOutput();
        await fixture.Service.PrepareAsync(Installation, Operation, token);
        fixture.Identities.FailAfterCreate = true;
        fixture.Identities.BeforeCreate = async () =>
        {
            await using var observer = fixture.CreateDb();
            var persisted = await observer.BootstrapRecords.SingleAsync();
            Assert.Equal(Installation, persisted.InstallationId);
            Assert.Equal(Operation, persisted.OperationId);
            Assert.Equal("in_progress", persisted.State);
            Assert.Empty(await observer.Users.ToListAsync());
        };
        await Error("auth_unavailable", () => fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!)));
        fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
        await fixture.Service.RotateTokenAsync(Installation, Operation, token);
        var result = await fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!));
        Assert.Equal("ready", result.State);
        Assert.Equal(1, fixture.Identities.Creates);
        Assert.Equal(fixture.Identities.Identity!.Id, (await fixture.Db.Users.SingleAsync()).KratosIdentityId);
    }

    [Fact]
    public async Task UncertainOutcomeNeverPermitsFreshIdentityEvenAfterRotation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = new TokenOutput();
        await fixture.Service.PrepareAsync(Installation, Operation, token);
        fixture.Identities.FailBeforeCreate = true;
        await Error("auth_unavailable", () => fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!)));
        fixture.Identities.FailBeforeCreate = false;
        await fixture.Service.RotateTokenAsync(Installation, Operation, token);
        await Error("setup_in_progress", () => fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!)));
        Assert.Equal(1, fixture.Identities.Creates);
        Assert.Empty(await fixture.Db.Users.ToListAsync());
        Assert.Equal("in_progress", (await fixture.Db.BootstrapRecords.SingleAsync()).State);
    }

    [Fact]
    public async Task DefinitivelyRejectedCreationCanCorrectInputWithoutTreatingUncertaintyAsSafe()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = new TokenOutput();
        await fixture.Service.PrepareAsync(Installation, Operation, token);
        fixture.Identities.RejectCreation = true;
        await Error("setup_state_conflict", () => fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!)));
        Assert.Equal("required", (await fixture.Db.BootstrapRecords.SingleAsync()).State);
        fixture.Identities.RejectCreation = false;
        await fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!));
        Assert.Equal(2, fixture.Identities.Creates);
        Assert.Single(await fixture.Db.Users.ToListAsync());
    }

    [Fact]
    public async Task DependencyOutageAndUnknownPopulatedStatesFailClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identities.Unavailable = true;
        await Error("auth_unavailable", () => fixture.Service.StatusAsync(Installation));
        await Error("auth_unavailable", () => fixture.Service.PrepareAsync(Installation, Operation, new TokenOutput()));
        Assert.Empty(await fixture.Db.BootstrapRecords.ToListAsync());
        fixture.Identities.Unavailable = false;
        fixture.Identities.UnrelatedIdentity = true;
        Assert.Equal("blocked", (await fixture.Service.StatusAsync(Installation)).State);
        await Error("setup_state_conflict", () => fixture.Service.PrepareAsync(Installation, Operation, new TokenOutput()));
        fixture.Identities.UnrelatedIdentity = false;
        fixture.Db.Users.Add(new User { Id = "legacy", Email = "legacy@example.test" });
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("blocked", (await fixture.Service.StatusAsync(Installation)).State);
        await Error("setup_state_conflict", () => fixture.Service.PrepareAsync(Installation, Operation, new TokenOutput()));
        Assert.Empty(await fixture.Db.BootstrapRecords.ToListAsync());
    }

    [Fact]
    public async Task MismatchedAndCorruptedInstallationCannotSeedOrReplay()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = new TokenOutput();
        await fixture.Service.PrepareAsync(Installation, Operation, token);
        Assert.Equal("blocked", (await fixture.Service.StatusAsync("other")).State);
        await Error("setup_state_conflict", () => fixture.Service.PrepareAsync(Installation, "other", token));
        var record = await fixture.Db.BootstrapRecords.SingleAsync();
        record.State = "unknown";
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("blocked", (await fixture.Service.StatusAsync(Installation)).State);
        await Error("setup_state_conflict", () => fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!)));
        Assert.Equal(0, fixture.Identities.Creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitBindAndRecoveryPreserveIdsMembershipsKeysAndRevokeHumanSessions(bool existingIdentity)
    {
        await using var fixture = await Fixture.CreateAsync();
        var identityId = Guid.NewGuid().ToString();
        fixture.Db.Users.Add(new User
        {
            Id = "legacy-user", Email = "operator@example.test",
            KratosIdentityId = existingIdentity ? identityId : null
        });
        fixture.Db.Organizations.Add(new Organization { Id = "legacy-org", Name = "Existing", Slug = "existing" });
        fixture.Db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = "legacy-member", OrganizationId = "legacy-org", UserId = "legacy-user", Role = OrganizationRole.Owner
        });
        await fixture.Db.SaveChangesAsync();
        if (existingIdentity)
            fixture.Identities.Identity = new BootstrapIdentity(identityId, "operator@example.test");
        await AddSessionsAsync(fixture.Db);
        fixture.Db.ApiKeys.Add(new ApiKey
        {
            Id = "legacy-key", OrganizationId = "legacy-org", Name = "Independent key",
            KeyHash = "synthetic-hash", KeyPrefix = "mc_live_synthetic", ScopesJson = "[]"
        });
        await fixture.Db.SaveChangesAsync();

        await Error("setup_state_conflict", () => fixture.Service.BindOperatorAsync(
            Installation, Operation, "wrong-user", "legacy-org", "New-password-123!"));
        var bound = await fixture.Service.BindOperatorAsync(
            Installation, Operation, "legacy-user", "legacy-org", "New-password-123!");
        Assert.Equal("legacy-user", bound.UserId);
        Assert.Equal("legacy-org", bound.OrganizationId);
        await AssertSessionsRevokedAsync(fixture.Db);
        await AddSessionsAsync(fixture.Db, "second");
        await Error("setup_state_conflict", () => fixture.Service.RecoverOperatorAsync(
            Installation, Operation, "legacy-user", "wrong-identity", "Another-password-123!"));
        await fixture.Service.RecoverOperatorAsync(Installation, Operation, "legacy-user",
            fixture.Identities.Identity!.Id, "Another-password-123!");
        await AssertSessionsRevokedAsync(fixture.Db);
        Assert.Equal("legacy-user", (await fixture.Db.Users.SingleAsync()).Id);
        Assert.Equal("legacy-org", (await fixture.Db.Organizations.SingleAsync()).Id);
        Assert.Equal("legacy-member", (await fixture.Db.OrganizationMembers.SingleAsync()).Id);
        Assert.Null((await fixture.Db.ApiKeys.SingleAsync()).RevokedAt);
        Assert.Equal("synthetic-hash", (await fixture.Db.ApiKeys.SingleAsync()).KeyHash);
        Assert.Equal(existingIdentity ? 0 : 1, fixture.Identities.Creates);
        Assert.Equal(4, fixture.Identities.Revocations);
    }

    [Fact]
    public async Task RecoveryOutageLeavesLocalSessionsRevokedAndDoesNotReplaceOperator()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = new TokenOutput();
        await fixture.Service.PrepareAsync(Installation, Operation, token);
        var result = await fixture.Service.CompleteHostAsync(Installation, Operation, Request(token.Token!));
        var now = fixture.Clock.Now;
        fixture.Db.BrowserSessions.Add(new BrowserSession
        {
            Id = "session", UserId = result.UserId!, KratosIdentityId = fixture.Identities.Identity!.Id,
            KratosSessionId = "upstream-session", TokenHash = "hash", CsrfTokenHash = "csrf",
            CreatedAt = now, ExpiresAt = now.AddHours(1), IdleExpiresAt = now.AddHours(1)
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Identities.FailPasswordChange = true;
        await Error("auth_unavailable", () => fixture.Service.RecoverOperatorAsync(
            Installation, Operation, result.UserId!, fixture.Identities.Identity.Id, "New-password-123!"));
        Assert.NotNull((await fixture.Db.BrowserSessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Equal(1, fixture.Identities.Creates);
        Assert.Equal(1, fixture.Identities.Revocations);
        Assert.Equal("ready", (await fixture.Db.BootstrapRecords.SingleAsync()).State);
    }

    [Fact]
    public async Task InterruptedLegacyBindingReconcilesExactlyWithoutReplacingUserOrOrganization()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Users.Add(new User { Id = "legacy-user", Email = "operator@example.test" });
        fixture.Db.Organizations.Add(new Organization { Id = "legacy-org", Name = "Existing", Slug = "existing" });
        fixture.Db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = "legacy-member", OrganizationId = "legacy-org", UserId = "legacy-user", Role = OrganizationRole.Owner
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Identities.FailAfterCreate = true;
        await Error("auth_unavailable", () => fixture.Service.BindOperatorAsync(
            Installation, Operation, "legacy-user", "legacy-org", "Synthetic-password-123!"));
        Assert.Null((await fixture.Db.Users.SingleAsync()).KratosIdentityId);
        Assert.Equal("in_progress", (await fixture.Db.BootstrapRecords.SingleAsync()).State);
        await fixture.Service.BindOperatorAsync(Installation, Operation, "legacy-user", "legacy-org", "Corrected-password-123!");
        Assert.Equal(1, fixture.Identities.Creates);
        Assert.Equal(fixture.Identities.Identity!.Id, (await fixture.Db.Users.SingleAsync()).KratosIdentityId);
        Assert.Equal("legacy-member", (await fixture.Db.OrganizationMembers.SingleAsync()).Id);
        Assert.Single(await fixture.Db.Organizations.ToListAsync());
    }

    [Fact]
    public async Task HostInputAndTokenOutputAreBoundedProtectedAndSecretFree()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inputPath = Path.Combine(fixture.Directory, "input.json");
        await File.WriteAllTextAsync(inputPath, """{"installationId":"installation-test","operationId":"operation-test"}""");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(inputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            await Error("setup_state_conflict", () => BootstrapCommand.ReadInputAsync(inputPath, TextReader.Null));
            File.SetUnixFileMode(inputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var input = await BootstrapCommand.ReadInputAsync(inputPath, TextReader.Null);
        var symlinkPath = Path.Combine(fixture.Directory, "input-link");
        File.CreateSymbolicLink(symlinkPath, inputPath);
        await Error("setup_state_conflict", () => BootstrapCommand.ReadInputAsync(symlinkPath, TextReader.Null));
        await Error("setup_state_conflict", () => BootstrapCommand.ReadInputAsync(fixture.Directory, TextReader.Null));
        await Error("setup_state_conflict", () => BootstrapCommand.ReadInputAsync("/dev/null", TextReader.Null));
        var fifo = Path.Combine(fixture.Directory, "input-fifo");
        Assert.Equal(0, MakeFifo(fifo, 0x180));
        await Error("setup_state_conflict", () => BootstrapCommand.ReadInputAsync(fifo, TextReader.Null));
        var outputPath = Path.Combine(fixture.Directory, "token");
        var ordinaryOutput = new StringWriter();
        var sink = new BootstrapCommand.ProtectedTokenOutput(outputPath, ordinaryOutput, false);
        var result = await BootstrapCommand.ExecuteAsync("prepare", input, fixture.Service, sink);
        var secret = (await File.ReadAllTextAsync(outputPath)).Trim();
        Assert.Equal(64, secret.Length);
        Assert.Empty(ordinaryOutput.ToString());
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(outputPath));
        await Assert.ThrowsAsync<IOException>(() => sink.WriteAsync("must-not-overwrite", CancellationToken.None));
        Assert.Equal(secret, (await File.ReadAllTextAsync(outputPath)).Trim());
        await Error("setup_state_conflict", () => BootstrapCommand.ReadInputAsync(null, new StringReader(new string('x', 65537))));
        await Error("setup_state_conflict", () => BootstrapCommand.ReadInputAsync(null, new StringReader(new string('é', 40000))));
        await Error("setup_state_conflict", () => BootstrapCommand.ReadInputAsync(null,
            new StringReader("""{"installationId":"one","installationId":"two"}""")));
        await Error("setup_state_conflict", () => new BootstrapCommand.ProtectedTokenOutput(
            null, ordinaryOutput, false).WriteAsync("secret", CancellationToken.None));
    }

    private static async Task AddSessionsAsync(MinicloudDbContext db, string suffix = "first")
    {
        db.BrowserSessions.Add(new BrowserSession
        {
            Id = "browser-" + suffix, UserId = "legacy-user", KratosIdentityId = "upstream",
            KratosSessionId = "upstream-" + suffix, TokenHash = "token-" + suffix, CsrfTokenHash = "csrf"
        });
        db.GrafanaProxySessions.Add(new GrafanaProxySession
        {
            Id = "grafana-" + suffix, OrganizationId = "legacy-org", AppId = "synthetic-app",
            MinicloudUserId = "legacy-user", SessionHash = "grafana-hash-" + suffix
        });
        db.GrafanaLaunchCodes.Add(new GrafanaLaunchCode
        {
            Id = "launch-" + suffix, OrganizationId = "legacy-org", AppId = "synthetic-app",
            MinicloudUserId = "legacy-user", CodeHash = "launch-hash-" + suffix, Destination = "/"
        });
        await db.SaveChangesAsync();
    }

    private static async Task AssertSessionsRevokedAsync(MinicloudDbContext db)
    {
        Assert.All(await db.BrowserSessions.AsNoTracking().ToListAsync(), x => Assert.NotNull(x.RevokedAt));
        Assert.All(await db.GrafanaProxySessions.AsNoTracking().ToListAsync(), x => Assert.NotNull(x.RevokedAt));
        Assert.All(await db.GrafanaLaunchCodes.AsNoTracking().ToListAsync(), x => Assert.NotNull(x.ConsumedAt));
    }

    private static async Task Error(string code, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<BootstrapException>(action);
        Assert.Equal(code, error.Code);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    internal sealed class TokenOutput : IBootstrapTokenOutput
    {
        public string? Token { get; private set; }
        public Task WriteAsync(string token, CancellationToken cancellationToken) { Token = token; return Task.CompletedTask; }
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class IdentityStore : IBootstrapIdentityStore
    {
        public BootstrapIdentity? Identity { get; set; }
        public string? InstallationId { get; private set; }
        public string? OperationId { get; private set; }
        public bool Unavailable { get; set; }
        public bool UnrelatedIdentity { get; set; }
        public bool FailAfterCreate { get; set; }
        public bool FailBeforeCreate { get; set; }
        public bool FailPasswordChange { get; set; }
        public bool RejectCreation { get; set; }
        public int Creates { get; private set; }
        public int PasswordChanges { get; private set; }
        public int Revocations { get; private set; }
        public Func<Task>? BeforeCreate { get; set; }
        private void Available() { if (Unavailable) throw BootstrapException.Unavailable(); }
        public Task<bool> HasIdentitiesAsync(CancellationToken cancellationToken)
        {
            Available();
            return Task.FromResult(Identity is not null || UnrelatedIdentity);
        }
        public Task<BootstrapIdentity?> FindOperationAsync(string installationId, string operationId, CancellationToken cancellationToken)
        {
            Available();
            return Task.FromResult(InstallationId == installationId && OperationId == operationId ? Identity : null);
        }
        public async Task<BootstrapIdentity> CreateAsync(string installationId, string operationId,
            string email, string password, string displayName, CancellationToken cancellationToken)
        {
            Available();
            Creates++;
            if (BeforeCreate is not null) await BeforeCreate();
            if (RejectCreation) throw new BootstrapIdentityCreationRejectedException();
            if (FailBeforeCreate) throw BootstrapException.Unavailable();
            InstallationId = installationId;
            OperationId = operationId;
            Identity = new(Guid.NewGuid().ToString(), email);
            if (FailAfterCreate) { FailAfterCreate = false; throw BootstrapException.Unavailable(); }
            return Identity;
        }
        public Task<BootstrapIdentity> GetAsync(string identityId, CancellationToken cancellationToken)
        {
            Available();
            return Task.FromResult(Identity ?? throw BootstrapException.Conflict());
        }
        public Task SetPasswordAsync(string identityId, string password, CancellationToken cancellationToken)
        {
            Available();
            if (FailPasswordChange) throw BootstrapException.Unavailable();
            PasswordChanges++;
            return Task.CompletedTask;
        }
        public Task RevokeSessionsAsync(string identityId, CancellationToken cancellationToken)
        {
            Available();
            Revocations++;
            return Task.CompletedTask;
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(FindRoot(), ".local", "artifacts", "bootstrap", "fixtures", Guid.NewGuid().ToString("N"));
        public MinicloudDbContext Db { get; private set; } = null!;
        public IdentityStore Identities { get; } = new();
        public Clock Clock { get; } = new();
        public BootstrapService Service { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            System.IO.Directory.CreateDirectory(fixture.Directory);
            fixture.Db = fixture.CreateDb();
            await fixture.Db.Database.EnsureCreatedAsync();
            fixture.Service = fixture.CreateService(fixture.Db);
            return fixture;
        }
        public MinicloudDbContext CreateDb() => new(new DbContextOptionsBuilder<MinicloudDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Directory, "state.db")};Pooling=False")
            .Options);
        public BootstrapService CreateService(MinicloudDbContext db) => new(db, Identities, new IdGenerator(), Clock);
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
        private static string FindRoot()
        {
            var path = new DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (path is not null && !File.Exists(Path.Combine(path.FullName, "Minicloud.slnx"))) path = path.Parent;
            return path?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}
