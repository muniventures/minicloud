using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class BootstrapService(
    MinicloudDbContext db, IBootstrapIdentityStore identities, IIdGenerator ids, TimeProvider clock)
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);

    public async Task<BootstrapStatusResponse> StatusAsync(string installationId, CancellationToken ct = default)
    {
        ValidateId(installationId);
        await using var mutex = await BootstrapLock.AcquireAsync(db, ct);
        var records = await db.BootstrapRecords.ToListAsync(ct);
        if (records.Count == 0)
            return new(await PopulatedAsync(ct) || await identities.HasIdentitiesAsync(ct) ? "blocked" : "required", null);
        if (records.Count != 1 || records[0].InstallationId != installationId)
            return new("blocked", null);
        var record = records[0];
        if (record.State == "required" && await identities.HasIdentitiesAsync(ct))
            return new("blocked", null);
        return new(await ValidStateAsync(record, ct) ? record.State : "blocked", record.SetupTokenExpiresAt);
    }

    public Task<BootstrapResult> PrepareAsync(string installationId, string operationId,
        IBootstrapTokenOutput output, CancellationToken ct = default) =>
        PrepareCoreAsync(installationId, operationId, output, false, ct);

    public Task<BootstrapResult> RotateTokenAsync(string installationId, string operationId,
        IBootstrapTokenOutput output, CancellationToken ct = default) =>
        PrepareCoreAsync(installationId, operationId, output, true, ct);

    private async Task<BootstrapResult> PrepareCoreAsync(string installationId, string operationId,
        IBootstrapTokenOutput output, bool rotate, CancellationToken ct)
    {
        ValidateId(installationId);
        ValidateId(operationId);
        ArgumentNullException.ThrowIfNull(output);
        await using var mutex = await BootstrapLock.AcquireAsync(db, ct);
        var record = await LoadAsync(installationId, operationId, ct);
        if (record is null)
        {
            if (rotate || await PopulatedAsync(ct) || await identities.HasIdentitiesAsync(ct))
                throw BootstrapException.Conflict();
            record = NewRecord(installationId, operationId);
            db.BootstrapRecords.Add(record);
        }
        else
        {
            if (!await ValidStateAsync(record, ct)) throw BootstrapException.Conflict();
            if (record.State == "ready")
            {
                if (rotate) throw BootstrapException.Conflict();
                return Result(record);
            }
            if (record.UserId is not null || record.OrganizationId is not null) throw BootstrapException.Conflict();
            if (!rotate) return Result(record);
            if (record.State == "required" && await identities.HasIdentitiesAsync(ct))
                throw BootstrapException.Conflict();
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        record.SetupTokenHash = Hash(installationId, operationId, token);
        record.SetupTokenExpiresAt = clock.GetUtcNow().Add(TokenLifetime);
        record.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await output.WriteAsync(token, ct);
        return Result(record);
    }

    public Task<BootstrapResult> CompleteHostAsync(string installationId, string operationId,
        CompleteSetupRequest? request, CancellationToken ct = default) =>
        CompleteAsync(installationId, operationId, request, false, ct);

    public Task<BootstrapResult> CompleteBrowserAsync(string installationId,
        CompleteSetupRequest request, CancellationToken ct = default) =>
        CompleteAsync(installationId, null, request, true, ct);

    private async Task<BootstrapResult> CompleteAsync(string installationId, string? operationId,
        CompleteSetupRequest? request, bool browser, CancellationToken ct)
    {
        ValidateId(installationId);
        if (operationId is not null) ValidateId(operationId);
        await using var mutex = await BootstrapLock.AcquireAsync(db, ct);
        var record = await LoadAsync(installationId, operationId, ct) ?? throw BootstrapException.Conflict();
        if (!await ValidStateAsync(record, ct)) throw BootstrapException.Conflict();
        if (record.State == "ready")
        {
            if (browser)
                throw new BootstrapException("setup_already_completed", 409, "Setup has already completed.");
            return Result(record);
        }
        if (record.UserId is not null || record.OrganizationId is not null) throw BootstrapException.Conflict();
        ValidateAdmission(record, request?.SetupToken);
        if (request is null) throw InvalidToken();
        ValidateSeed(request);

        var identity = await identities.FindOperationAsync(installationId, record.OperationId, ct);
        if (record.State == "in_progress" && identity is null) throw InProgress();
        if (record.State == "required")
        {
            if (identity is not null || await identities.HasIdentitiesAsync(ct)) throw BootstrapException.Conflict();
            record.State = "in_progress";
            record.UpdatedAt = clock.GetUtcNow();
            // Commit the attempt BEFORE the external mutation. A missing reconciliation
            // match on a later attempt is uncertainty, not permission to create again.
            await db.SaveChangesAsync(ct);
            try
            {
                identity = await identities.CreateAsync(installationId, record.OperationId, request.Email,
                    request.Password, request.DisplayName, ct);
            }
            catch (BootstrapIdentityCreationRejectedException)
            {
                record.State = "required";
                await db.SaveChangesAsync(CancellationToken.None);
                throw RejectedInput();
            }
        }
        if (identity is null || !string.Equals(identity.Email, request.Email, StringComparison.OrdinalIgnoreCase))
            throw BootstrapException.Conflict();
        if (record.KratosIdentityId is not null && record.KratosIdentityId != identity.Id)
            throw BootstrapException.Conflict();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var user = new User
        {
            Id = ids.NewId("usr"), KratosIdentityId = identity.Id, Email = identity.Email,
            DisplayName = request.DisplayName, CreatedAt = now, UpdatedAt = now
        };
        var org = new Organization
        {
            Id = ids.NewId("org"), Name = request.OrganizationName, Slug = request.OrganizationSlug,
            CreatedAt = now, UpdatedAt = now
        };
        db.Users.Add(user);
        db.Organizations.Add(org);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = ids.NewId("mem"), UserId = user.Id, OrganizationId = org.Id,
            Role = OrganizationRole.Owner, CreatedAt = now, UpdatedAt = now
        });
        Finish(record, user.Id, org.Id, identity.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result(record);
    }

    public async Task<BootstrapResult> BindOperatorAsync(string installationId, string operationId,
        string userId, string organizationId, string password, CancellationToken ct = default)
    {
        ValidateId(installationId);
        ValidateId(operationId);
        ValidatePassword(password);
        await using var mutex = await BootstrapLock.AcquireAsync(db, ct);
        var record = await LoadAsync(installationId, operationId, ct);
        var user = await RequireOwnerAsync(userId, organizationId, ct);
        if (record?.State == "ready") throw BootstrapException.Conflict();
        if (record is not null && (record.State is not ("required" or "in_progress") ||
            record.UserId != userId || record.OrganizationId != organizationId))
            throw BootstrapException.Conflict();
        var retry = record?.State == "in_progress";
        if (record is null)
        {
            record = NewRecord(installationId, operationId);
            record.State = "in_progress";
            record.UserId = userId;
            record.OrganizationId = organizationId;
            record.KratosIdentityId = user.KratosIdentityId;
            db.BootstrapRecords.Add(record);
        }
        record.State = "in_progress";
        record.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await RevokeLocalAsync(userId, ct);
        BootstrapIdentity identity;
        if (user.KratosIdentityId is not null)
        {
            if (record.KratosIdentityId != user.KratosIdentityId) throw BootstrapException.Conflict();
            identity = await identities.GetAsync(user.KratosIdentityId, ct);
            if (identity.Id != user.KratosIdentityId ||
                !string.Equals(user.Email, identity.Email, StringComparison.OrdinalIgnoreCase))
                throw BootstrapException.Conflict();
        }
        else
        {
            identity = (await identities.FindOperationAsync(installationId, operationId, ct))!;
            if (identity is null)
            {
                if (retry) throw InProgress();
                try
                {
                    identity = await identities.CreateAsync(installationId, operationId, user.Email,
                        password, user.DisplayName ?? "", ct);
                }
                catch (BootstrapIdentityCreationRejectedException)
                {
                    record.State = "required";
                    await db.SaveChangesAsync(CancellationToken.None);
                    throw RejectedInput();
                }
            }
        }
        if (!string.Equals(user.Email, identity.Email, StringComparison.OrdinalIgnoreCase) ||
            await db.Users.AnyAsync(x => x.Id != userId && x.KratosIdentityId == identity.Id, ct))
            throw BootstrapException.Conflict();
        await identities.RevokeSessionsAsync(identity.Id, ct);
        // Reconciled creation may have accepted a different password before interruption.
        await identities.SetPasswordAsync(identity.Id, password, ct);
        await identities.RevokeSessionsAsync(identity.Id, ct);
        await RevokeLocalAsync(userId, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        user.KratosIdentityId = identity.Id;
        user.UpdatedAt = clock.GetUtcNow();
        Finish(record, userId, organizationId, identity.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result(record);
    }

    public async Task<BootstrapResult> RecoverOperatorAsync(string installationId, string operationId,
        string userId, string identityId, string password, CancellationToken ct = default)
    {
        ValidateId(installationId);
        ValidateId(operationId);
        ValidatePassword(password);
        await using var mutex = await BootstrapLock.AcquireAsync(db, ct);
        var record = await LoadAsync(installationId, operationId, ct) ?? throw BootstrapException.Conflict();
        if (record.State != "ready" || !await ValidStateAsync(record, ct) ||
            record.UserId != userId || record.KratosIdentityId != identityId)
            throw BootstrapException.Conflict();
        var identity = await identities.GetAsync(identityId, ct);
        if (identity.Id != identityId) throw BootstrapException.Conflict();
        await RevokeLocalAsync(userId, ct);
        await identities.RevokeSessionsAsync(identityId, ct);
        await identities.SetPasswordAsync(identityId, password, ct);
        await identities.RevokeSessionsAsync(identityId, ct);
        await RevokeLocalAsync(userId, ct);
        return Result(record);
    }

    private async Task RevokeLocalAsync(string userId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        await db.BrowserSessions.Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, now), ct);
        await db.GrafanaProxySessions.Where(x => x.MinicloudUserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, now), ct);
        await db.GrafanaLaunchCodes.Where(x => x.MinicloudUserId == userId && x.ConsumedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.ConsumedAt, now), ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<BootstrapRecord?> LoadAsync(string installationId, string? operationId, CancellationToken ct)
    {
        var records = await db.BootstrapRecords.ToListAsync(ct);
        if (records.Count == 0) return null;
        if (records.Count != 1 || records[0].InstallationId != installationId ||
            (operationId is not null && records[0].OperationId != operationId))
            throw BootstrapException.Conflict();
        return records[0];
    }

    private async Task<bool> ValidStateAsync(BootstrapRecord record, CancellationToken ct)
    {
        if (record.State == "ready")
            return record.CompletedAt is not null && record.KratosIdentityId is not null &&
                record.UserId is not null && record.OrganizationId is not null &&
                await db.Users.AnyAsync(x => x.Id == record.UserId && x.KratosIdentityId == record.KratosIdentityId, ct) &&
                await db.OrganizationMembers.AnyAsync(x => x.UserId == record.UserId &&
                    x.OrganizationId == record.OrganizationId && x.Role == OrganizationRole.Owner, ct);
        if (record.State is not ("required" or "in_progress") || record.CompletedAt is not null)
            return false;
        if (record.State == "required" && record.KratosIdentityId is not null)
            return false;
        if (record.UserId is not null || record.OrganizationId is not null)
            return record.State == "in_progress" && record.UserId is not null && record.OrganizationId is not null &&
                await db.OrganizationMembers.AnyAsync(x => x.UserId == record.UserId &&
                    x.OrganizationId == record.OrganizationId && x.Role == OrganizationRole.Owner, ct);
        return !await PopulatedAsync(ct);
    }

    private async Task<bool> PopulatedAsync(CancellationToken ct) =>
        await db.Users.AnyAsync(ct) || await db.Organizations.AnyAsync(ct) ||
        await db.OrganizationMembers.AnyAsync(ct) ||
        await db.Applications.AnyAsync(ct) || await db.Apps.AnyAsync(ct) ||
        await db.ProviderAccounts.AnyAsync(ct) || await db.ApiKeys.AnyAsync(ct);

    private async Task<User> RequireOwnerAsync(string userId, string organizationId, CancellationToken ct)
    {
        if (!await db.OrganizationMembers.AnyAsync(x => x.UserId == userId &&
            x.OrganizationId == organizationId && x.Role == OrganizationRole.Owner, ct))
            throw BootstrapException.Conflict();
        return await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct) ?? throw BootstrapException.Conflict();
    }

    private BootstrapRecord NewRecord(string installationId, string operationId) => new()
    {
        InstallationId = installationId, OperationId = operationId, State = "required",
        CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow()
    };

    private void Finish(BootstrapRecord record, string userId, string organizationId, string identityId)
    {
        record.UserId = userId;
        record.OrganizationId = organizationId;
        record.KratosIdentityId = identityId;
        record.State = "ready";
        var now = clock.GetUtcNow();
        // PostgreSQL persists microseconds. Return exactly the durable timestamp
        // on the first response as well as subsequent installer replays.
        record.CompletedAt = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        record.UpdatedAt = record.CompletedAt.Value;
        record.SetupTokenHash = null;
        record.SetupTokenExpiresAt = null;
    }

    private void ValidateAdmission(BootstrapRecord record, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256 || record.SetupTokenHash is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(record.SetupTokenHash),
                Encoding.UTF8.GetBytes(Hash(record.InstallationId, record.OperationId, token))))
            throw InvalidToken();
        if (record.SetupTokenExpiresAt is null || record.SetupTokenExpiresAt <= clock.GetUtcNow())
            throw new BootstrapException("setup_token_expired", 410, "The setup token has expired.");
    }

    private static string Hash(string installationId, string operationId, string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{installationId}\n{operationId}\n{token}")));
    private static BootstrapResult Result(BootstrapRecord record) =>
        new(record.InstallationId, record.OperationId, record.State, record.UserId, record.OrganizationId, record.CompletedAt);
    private static BootstrapException InvalidToken() => new("setup_token_invalid", 401, "The setup token is invalid.");
    private static BootstrapException InProgress() => new("setup_in_progress", 409, "Setup is awaiting identity reconciliation.");
    private static BootstrapException RejectedInput() =>
        new("setup_state_conflict", 409, "The identity service rejected the operator input.");
    private static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, @"\A[a-zA-Z0-9_-]{1,128}\z"))
            throw BootstrapException.Conflict();
    }
    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 1024)
            throw new BootstrapException("setup_state_conflict", 409, "A password between 8 and 1024 characters is required.");
    }
    private static void ValidateSeed(CompleteSetupRequest request)
    {
        ValidatePassword(request.Password);
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254 ||
            !MailAddress.TryCreate(request.Email, out var address) || address.Address != request.Email ||
            string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200 ||
            string.IsNullOrWhiteSpace(request.OrganizationName) || request.OrganizationName.Length > 200 ||
            string.IsNullOrWhiteSpace(request.OrganizationSlug) ||
            !Regex.IsMatch(request.OrganizationSlug, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z"))
            throw new BootstrapException("setup_state_conflict", 409, "The operator and organization fields are invalid.");
    }
}
