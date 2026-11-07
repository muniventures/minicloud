using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Api.Controllers;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using Minicloud.Telemetry.Data;
using Minicloud.Telemetry.Services;

namespace Minicloud.Tests;

public sealed class TelemetryDockerServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<TelemetryDbContext>? _options;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private TelemetryDbContext CreateDbContext()
    {
        return new TelemetryDbContext(_options!);
    }

    private static (string rawToken, string keyPrefix, string keyHash) CreateTestCredential()
    {
        var keyPrefix = string.Join('_', "mcl", "ingest", "testapp12");
        var rawToken = $"{keyPrefix}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(rawToken),
            salt,
            1000,
            HashAlgorithmName.SHA256,
            32);
        var keyHash = $"pbkdf2_sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        return (rawToken, keyPrefix, keyHash);
    }

    [Fact]
    public async Task CheckpointFreshness_Under60Minutes_AllowsIngestion()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var timeProvider = new TestTimeProvider(baseTime);

        await using var db = CreateDbContext();
        var authority = new TelemetryAuthorityService(db, timeProvider, NullLogger<TelemetryAuthorityService>.Instance);
        var reporter = new TelemetryStatusReporter();
        var authService = new TelemetryCredentialAuthService(db, authority, reporter, timeProvider, NullLogger<TelemetryCredentialAuthService>.Instance);

        var (rawToken, keyPrefix, keyHash) = CreateTestCredential();

        // Sync batch at baseTime
        await authority.ApplySyncBatchAsync(new TelemetrySyncBatchRequest
        {
            CheckpointSequence = 1,
            CheckpointTimestamp = baseTime,
            Tenants =
            [
                new TelemetryTenantSyncItem
                {
                    AppId = "app_1",
                    OrganizationId = "org_1",
                    TenantId = "tenant_1",
                    Enabled = true,
                    RetentionPolicy = "7d",
                    UpdatedAt = baseTime
                }
            ],
            Credentials =
            [
                new TelemetryCredentialSyncItem
                {
                    Id = "cred_1",
                    AppId = "app_1",
                    OrganizationId = "org_1",
                    ServiceName = "backend",
                    TenantId = "tenant_1",
                    KeyPrefix = keyPrefix,
                    KeyHash = keyHash,
                    CredentialVersion = 1,
                    Status = "active",
                    ExpiresAt = baseTime.AddDays(30),
                    UpdatedAt = baseTime
                }
            ]
        }, CancellationToken.None);

        // Advance time by 50 minutes (still under 60m allowance)
        timeProvider.SetUtcNow(baseTime.AddMinutes(50));

        var snapshot = await authService.AuthenticateAsync(rawToken, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal("app_1", snapshot.AppId);
        Assert.Equal("tenant_1", snapshot.TenantId);
    }

    [Fact]
    public async Task CheckpointFreshness_Over60Minutes_ThrowsUnavailableException()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var timeProvider = new TestTimeProvider(baseTime);

        await using var db = CreateDbContext();
        var authority = new TelemetryAuthorityService(db, timeProvider, NullLogger<TelemetryAuthorityService>.Instance);
        var reporter = new TelemetryStatusReporter();
        var authService = new TelemetryCredentialAuthService(db, authority, reporter, timeProvider, NullLogger<TelemetryCredentialAuthService>.Instance);

        var (rawToken, keyPrefix, keyHash) = CreateTestCredential();

        await authority.ApplySyncBatchAsync(new TelemetrySyncBatchRequest
        {
            CheckpointSequence = 1,
            CheckpointTimestamp = baseTime,
            Tenants =
            [
                new TelemetryTenantSyncItem { AppId = "app_1", OrganizationId = "org_1", TenantId = "tenant_1", Enabled = true, UpdatedAt = baseTime }
            ],
            Credentials =
            [
                new TelemetryCredentialSyncItem
                {
                    Id = "cred_1",
                    AppId = "app_1",
                    OrganizationId = "org_1",
                    ServiceName = "backend",
                    TenantId = "tenant_1",
                    KeyPrefix = keyPrefix,
                    KeyHash = keyHash,
                    CredentialVersion = 1,
                    Status = "active",
                    ExpiresAt = baseTime.AddDays(30),
                    UpdatedAt = baseTime
                }
            ]
        }, CancellationToken.None);

        // Advance time by 61 minutes (exceeds 60m outage allowance)
        timeProvider.SetUtcNow(baseTime.AddMinutes(61));

        await Assert.ThrowsAsync<TelemetryAuthUnavailableException>(() =>
            authService.AuthenticateAsync(rawToken, CancellationToken.None));
    }

    [Fact]
    public async Task EarlierCredentialExpiry_RejectsEvenIfCheckpointIsFresh()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var timeProvider = new TestTimeProvider(baseTime);

        await using var db = CreateDbContext();
        var authority = new TelemetryAuthorityService(db, timeProvider, NullLogger<TelemetryAuthorityService>.Instance);
        var reporter = new TelemetryStatusReporter();
        var authService = new TelemetryCredentialAuthService(db, authority, reporter, timeProvider, NullLogger<TelemetryCredentialAuthService>.Instance);

        var (rawToken, keyPrefix, keyHash) = CreateTestCredential();

        // Credential expires in 20 minutes
        await authority.ApplySyncBatchAsync(new TelemetrySyncBatchRequest
        {
            CheckpointSequence = 1,
            CheckpointTimestamp = baseTime,
            Tenants =
            [
                new TelemetryTenantSyncItem { AppId = "app_1", OrganizationId = "org_1", TenantId = "tenant_1", Enabled = true, UpdatedAt = baseTime }
            ],
            Credentials =
            [
                new TelemetryCredentialSyncItem
                {
                    Id = "cred_1",
                    AppId = "app_1",
                    OrganizationId = "org_1",
                    ServiceName = "backend",
                    TenantId = "tenant_1",
                    KeyPrefix = keyPrefix,
                    KeyHash = keyHash,
                    CredentialVersion = 1,
                    Status = "active",
                    ExpiresAt = baseTime.AddMinutes(20),
                    UpdatedAt = baseTime
                }
            ]
        }, CancellationToken.None);

        // Advance 25 minutes: checkpoint is 25m old (< 60m fresh), but credential expired 5m ago
        timeProvider.SetUtcNow(baseTime.AddMinutes(25));

        var snapshot = await authService.AuthenticateAsync(rawToken, CancellationToken.None);
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Revocation_DeniesImmediately()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var timeProvider = new TestTimeProvider(baseTime);

        await using var db = CreateDbContext();
        var authority = new TelemetryAuthorityService(db, timeProvider, NullLogger<TelemetryAuthorityService>.Instance);
        var reporter = new TelemetryStatusReporter();
        var authService = new TelemetryCredentialAuthService(db, authority, reporter, timeProvider, NullLogger<TelemetryCredentialAuthService>.Instance);

        var (rawToken, keyPrefix, keyHash) = CreateTestCredential();

        // First sync: active
        await authority.ApplySyncBatchAsync(new TelemetrySyncBatchRequest
        {
            CheckpointSequence = 1,
            CheckpointTimestamp = baseTime,
            Tenants =
            [
                new TelemetryTenantSyncItem { AppId = "app_1", OrganizationId = "org_1", TenantId = "tenant_1", Enabled = true, UpdatedAt = baseTime }
            ],
            Credentials =
            [
                new TelemetryCredentialSyncItem
                {
                    Id = "cred_1",
                    AppId = "app_1",
                    OrganizationId = "org_1",
                    ServiceName = "backend",
                    TenantId = "tenant_1",
                    KeyPrefix = keyPrefix,
                    KeyHash = keyHash,
                    CredentialVersion = 1,
                    Status = "active",
                    ExpiresAt = baseTime.AddDays(30),
                    UpdatedAt = baseTime
                }
            ]
        }, CancellationToken.None);

        // Advance 5 minutes, revoke credential in sequence 2
        timeProvider.SetUtcNow(baseTime.AddMinutes(5));
        await authority.ApplySyncBatchAsync(new TelemetrySyncBatchRequest
        {
            CheckpointSequence = 2,
            CheckpointTimestamp = baseTime.AddMinutes(5),
            Credentials =
            [
                new TelemetryCredentialSyncItem
                {
                    Id = "cred_1",
                    AppId = "app_1",
                    OrganizationId = "org_1",
                    ServiceName = "backend",
                    TenantId = "tenant_1",
                    KeyPrefix = keyPrefix,
                    KeyHash = keyHash,
                    CredentialVersion = 2,
                    Status = "revoked",
                    RevokedAt = baseTime.AddMinutes(5),
                    UpdatedAt = baseTime.AddMinutes(5)
                }
            ]
        }, CancellationToken.None);

        // Immediate rejection even though checkpoint is only 0 seconds old
        var snapshot = await authService.AuthenticateAsync(rawToken, CancellationToken.None);
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Restart_PreservesDeadline_DoesNotExtendWindow()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var timeProvider = new TestTimeProvider(baseTime);

        var (rawToken, keyPrefix, keyHash) = CreateTestCredential();

        // Instance 1: establishes checkpoint at baseTime
        await using (var db1 = CreateDbContext())
        {
            var authority1 = new TelemetryAuthorityService(db1, timeProvider, NullLogger<TelemetryAuthorityService>.Instance);
            await authority1.ApplySyncBatchAsync(new TelemetrySyncBatchRequest
            {
                CheckpointSequence = 1,
                CheckpointTimestamp = baseTime,
                Tenants =
                [
                    new TelemetryTenantSyncItem { AppId = "app_1", OrganizationId = "org_1", TenantId = "tenant_1", Enabled = true, UpdatedAt = baseTime }
                ],
                Credentials =
                [
                    new TelemetryCredentialSyncItem
                    {
                        Id = "cred_1",
                        AppId = "app_1",
                        OrganizationId = "org_1",
                        ServiceName = "backend",
                        TenantId = "tenant_1",
                        KeyPrefix = keyPrefix,
                        KeyHash = keyHash,
                        CredentialVersion = 1,
                        Status = "active",
                        ExpiresAt = baseTime.AddDays(30),
                        UpdatedAt = baseTime
                    }
                ]
            }, CancellationToken.None);
        }

        // Advance clock by 61 minutes and simulate restart (new DbContext, new services)
        timeProvider.SetUtcNow(baseTime.AddMinutes(61));

        await using (var db2 = CreateDbContext())
        {
            var authority2 = new TelemetryAuthorityService(db2, timeProvider, NullLogger<TelemetryAuthorityService>.Instance);
            var reporter2 = new TelemetryStatusReporter();
            var authService2 = new TelemetryCredentialAuthService(db2, authority2, reporter2, timeProvider, NullLogger<TelemetryCredentialAuthService>.Instance);

            // Restart cannot extend the deadline: still fails with TelemetryAuthUnavailableException
            await Assert.ThrowsAsync<TelemetryAuthUnavailableException>(() =>
                authService2.AuthenticateAsync(rawToken, CancellationToken.None));
        }
    }

    [Fact]
    public async Task MonotonicSequencing_ReplayOfOldSequenceIsIgnored()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var timeProvider = new TestTimeProvider(baseTime);

        await using var db = CreateDbContext();
        var authority = new TelemetryAuthorityService(db, timeProvider, NullLogger<TelemetryAuthorityService>.Instance);

        var response1 = await authority.ApplySyncBatchAsync(new TelemetrySyncBatchRequest
        {
            CheckpointSequence = 5,
            CheckpointTimestamp = baseTime
        }, CancellationToken.None);

        Assert.True(response1.Success);
        Assert.Equal(5, response1.AppliedSequence);

        // Attempt replay of sequence 3
        var response2 = await authority.ApplySyncBatchAsync(new TelemetrySyncBatchRequest
        {
            CheckpointSequence = 3,
            CheckpointTimestamp = baseTime.AddMinutes(10)
        }, CancellationToken.None);

        Assert.False(response2.Success);
        Assert.Equal(5, response2.AppliedSequence);

        // ConfirmedAt was NOT updated by the replay
        var checkpoint = await authority.GetCheckpointAsync(CancellationToken.None);
        Assert.Equal(baseTime, checkpoint!.ConfirmedAt);
    }

    [Fact]
    public void ReadToken_GenerationAndValidation()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = new TestTimeProvider(now);
        var key = "minicloud-telemetry-auth-signing-test-key-32b";

        var token = TelemetryReadToken.Create("user_1", "org_1", "app_1", "tenant_1", TimeSpan.FromMinutes(15), key, timeProvider);
        Assert.StartsWith("mcl_rt_", token);

        var valid = TelemetryReadToken.TryValidate(token, key, timeProvider, out var claims, out var error);
        Assert.True(valid, error);
        Assert.NotNull(claims);
        Assert.Equal("user_1", claims.Sub);
        Assert.Equal("org_1", claims.OrganizationId);
        Assert.Equal("app_1", claims.AppId);
        Assert.Equal("tenant_1", claims.TenantId);

        // Tampered token fails
        var tampered = token[..^4] + "xxxx";
        Assert.False(TelemetryReadToken.TryValidate(tampered, key, timeProvider, out _, out _));

        // Expired token fails
        timeProvider.SetUtcNow(now.AddMinutes(16));
        Assert.False(TelemetryReadToken.TryValidate(token, key, timeProvider, out _, out var expiredError));
        Assert.Equal("expired_token", expiredError);
    }

    [Fact]
    public async Task GrafanaSession_IssueAndSingleUseExchange()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = new TestTimeProvider(now);

        await using var db = CreateDbContext();
        var options = Options.Create(new TelemetryOptions
        {
            GrafanaBaseUrl = "http://localhost:3000",
            LokiBaseUrl = "http://localhost:3100"
        });
        db.TenantBindings.Add(new TelemetryTenantBinding
        {
            AppId = "app_1",
            OrganizationId = "org_1",
            TenantId = "tenant_1",
            Enabled = true,
            RetentionPolicy = "7d",
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var sessions = new GrafanaSessionService(db, timeProvider, options, NullLogger<GrafanaSessionService>.Instance);

        var issueResult = await sessions.IssueLaunchCodeAsync(new TelemetryLaunchCodeIssueRequest
        {
            MinicloudUserId = "user_1",
            OrganizationId = "org_1",
            AppId = "app_1",
            Destination = "/explore"
        }, CancellationToken.None);

        Assert.NotNull(issueResult.LaunchCode);
        Assert.StartsWith("mcl_glaunch_", issueResult.LaunchCode);

        // First exchange succeeds
        var exchangeResult = await sessions.ExchangeLaunchCodeAsync(issueResult.LaunchCode, CancellationToken.None);
        Assert.True(exchangeResult.Ok);
        Assert.Equal("/explore", exchangeResult.Destination);
        Assert.NotNull(exchangeResult.SessionToken);

        // Second exchange fails (single-use)
        var secondExchange = await sessions.ExchangeLaunchCodeAsync(issueResult.LaunchCode, CancellationToken.None);
        Assert.False(secondExchange.Ok);
    }

    [Fact]
    public void ControlPlaneApi_OtlpEndpoint_Returns410Gone()
    {
        var controller = new OtlpLogsController();
        var result = controller.Export();

        var statusCodeResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, statusCodeResult.StatusCode);
    }

    private sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset now) => _utcNow = now;
    }
}
