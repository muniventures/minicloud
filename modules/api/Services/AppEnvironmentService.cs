using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Api.Services;

// Each environment owns a runtime app record so all runtime resources remain isolated.
public sealed class AppEnvironmentService(
    MinicloudDbContext db,
    IIdGenerator ids,
    TimeProvider clock,
    ProviderCatalogService? catalog = null,
    EnvironmentSshAccessService? sshAccess = null)
{
    public static bool IsValidName(string? name) => name is { Length: >= 1 and <= 63 }
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && char.IsAsciiLetterOrDigit(name[0]);

    public Task<MinicloudApp> CreateForApplicationAsync(MinicloudApplication application, CreateEnvironmentRequest request, CancellationToken ct) =>
        CreateForApplicationAsync(application, request, null, ct);

    public Task<MinicloudApp> CreateForApplicationAsync(
        MinicloudApplication application,
        CreateEnvironmentRequest request,
        ActorIdentity? actor,
        CancellationToken ct) =>
        WithApplicationLockAsync(application.Id, () => CreateForApplicationCoreAsync(application, request, actor, ct), ct);

    private async Task<MinicloudApp> CreateForApplicationCoreAsync(
        MinicloudApplication application,
        CreateEnvironmentRequest request,
        ActorIdentity? actor,
        CancellationToken ct)
    {
        if (application.DeletionStatus is not null)
            throw new AppEnvironmentException("app_deleting", "The application is being deleted.");
        if (!IsValidName(request.Name))
            throw new AppEnvironmentException("environment_name_invalid", "Use 1 to 63 letters, numbers, dashes or underscores, starting with a letter or number.");
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? EnvironmentKinds.Persistent : request.Kind.Trim().ToLowerInvariant();
        if (kind is not (EnvironmentKinds.Persistent or EnvironmentKinds.Preview))
            throw new AppEnvironmentException("environment_kind_invalid", "Environment kind must be persistent or preview.");

        var plan = default(AppPlan);
        ProviderPlan? selectedPlan = null;
        if (catalog is not null)
        {
            try { selectedPlan = await catalog.ResolveAsync(application.OrganizationId, request.Plan, ct); }
            catch (ProviderCatalogException ex) { throw new AppEnvironmentException(ex.Code, ex.Message); }
        }
        else if (!ControlPlaneValidation.TryParseEnum(request.Plan, out plan))
            throw new AppEnvironmentException("environment_plan_invalid", "Select a supported VPS plan.");

        var databaseStr = string.IsNullOrWhiteSpace(request.Database) ? "none" : request.Database;
        if (!ControlPlaneValidation.TryParseEnum<DatabaseMode>(databaseStr, out var database))
            throw new AppEnvironmentException("environment_database_invalid", "Select sqlite, postgres, or none.");

        if (request.SourceEnvironmentId is not null &&
            !await db.Apps.AnyAsync(x => x.Id == request.SourceEnvironmentId && x.ApplicationId == application.Id && x.OrganizationId == application.OrganizationId && x.DeletionStatus == null, ct))
            throw new AppEnvironmentException("target_mismatch", "Source environment must be an active environment of this application.");

        var rootCandidates = await db.Apps
            .Where(x => x.ApplicationId == application.Id && x.ParentAppId == null && x.BranchName == null && x.DeletionStatus == null)
            .ToListAsync(ct);
        var rootEnv = rootCandidates
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        await EnsureNameAvailableAsync(rootEnv?.Id ?? application.Id, application.Id, request.Name, null, ct);

        var trimmedName = request.Name.Trim();
        var normName = trimmedName.ToLowerInvariant();
        var slug = await ResolveAvailableEnvironmentSlugAsync(application.OrganizationId, application.Slug, normName, rootEnv is null, ct);
        var now = clock.GetUtcNow();
        var keyPrefix = rootEnv?.Id ?? application.LegacyRootAppId ?? application.Id;

        var environment = new MinicloudApp
        {
            Id = ids.NewId("app"),
            OrganizationId = application.OrganizationId,
            ApplicationId = application.Id,
            Application = application,
            ParentAppId = rootEnv?.Id,
            BranchName = kind == EnvironmentKinds.Preview ? (request.SourceBranch ?? normName) : null,
            RuntimeParentSlug = kind == EnvironmentKinds.Preview ? rootEnv?.Slug : null,
            SourceBranch = kind == EnvironmentKinds.Preview ? (request.SourceBranch ?? trimmedName) : null,
            SourceEnvironmentId = kind == EnvironmentKinds.Preview ? (request.SourceEnvironmentId ?? rootEnv?.Id) : null,
            EnvironmentName = trimmedName,
            NormalizedEnvironmentName = normName,
            EnvironmentKind = kind,
            EnvironmentKey = $"{keyPrefix}:{normName}",
            Name = rootEnv is null && normName == "production" ? application.Name : $"{application.Name} ({trimmedName})",
            Slug = slug,
            Plan = plan,
            Database = database,
            PlanId = selectedPlan?.Id,
            SelectedPlan = selectedPlan,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (rootEnv is null)
        {
            environment.EnvironmentKey = $"{environment.Id}:{normName}";
        }

        db.Apps.Add(environment);
        if (application.DefaultEnvironmentId is null)
        {
            application.DefaultEnvironmentId = environment.Id;
            application.UpdatedAt = now;
        }

        if (request.SshAccess is not null)
        {
            if (sshAccess is null || actor is null)
                throw new AppEnvironmentException("ssh_key_invalid", "SSH access cannot be configured in this context.");
            await sshAccess.ApplySelectionAsync(environment, selectedPlan?.ProviderAccountId, request.SshAccess, actor, false, ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }
            or Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        {
            throw new AppEnvironmentException("environment_name_taken", "Environment names must be unique within the application (ignoring case).");
        }

        return environment;
    }

    public async Task RenameAsync(MinicloudApp environment, string name, CancellationToken ct, bool allowPreview = false)
    {
        if ((!allowPreview && environment.BranchName is not null) || environment.DeletionStatus is not null || !IsValidName(name))
            throw new AppEnvironmentException("environment_name_invalid", "Choose a valid name for an active environment.");
        var trimmed = name.Trim();
        var normName = trimmed.ToLowerInvariant();
        await EnsureNameAvailableAsync(environment.ParentAppId ?? environment.Id, environment.ApplicationId, trimmed, environment.Id, ct);
        if (environment.ParentAppId is not null)
        {
            var applicationName = environment.Application?.Name
                ?? await db.Applications.Where(x => x.Id == environment.ApplicationId).Select(x => x.Name).SingleOrDefaultAsync(ct)
                ?? await db.Apps.Where(x => x.Id == environment.ParentAppId).Select(x => x.Name).SingleOrDefaultAsync(ct)
                ?? environment.Name;
            environment.Name = $"{applicationName} ({trimmed})";
        }
        environment.EnvironmentName = trimmed;
        environment.NormalizedEnvironmentName = normName;
        environment.EnvironmentKey = $"{environment.ParentAppId ?? environment.Id}:{normName}";
        environment.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }
            or Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        {
            throw new AppEnvironmentException("environment_name_taken", "Environment names must be unique within the application (ignoring case).");
        }
    }

    // Serialize environment admission with application deletion. Runtime IDs remain the
    // lock scope for deployments, while the logical application protects its membership.
    private async Task<MinicloudApp> WithApplicationLockAsync(string? applicationId, Func<Task<MinicloudApp>> action, CancellationToken ct)
    {
        if (applicationId is null) return await action();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var ownsTransaction = db.Database.CurrentTransaction is null;
            await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(ct) : null;
            var application = db.Database.IsNpgsql()
                ? await db.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {applicationId} FOR UPDATE").SingleAsync(ct)
                : await db.Applications.SingleAsync(x => x.Id == applicationId, ct);
            await db.Entry(application).ReloadAsync(ct);
            if (application.DeletionStatus is not null)
                throw new AppEnvironmentException("app_deleting", "The application is being deleted.");
            var result = await action();
            if (transaction is not null) await transaction.CommitAsync(ct);
            return result;
        });
    }

    private async Task EnsureNameAvailableAsync(string rootId, string? applicationId, string name, string? exceptId, CancellationToken ct)
    {
        var key = name.Trim().ToLowerInvariant();
        var taken = applicationId is not null
            ? await db.Apps.AnyAsync(x =>
                x.Id != exceptId &&
                x.ApplicationId == applicationId &&
                (x.NormalizedEnvironmentName == key || (x.EnvironmentName ?? "production").ToLower() == key), ct)
            : await db.Apps.AnyAsync(x =>
                x.Id != exceptId &&
                (x.Id == rootId || x.ParentAppId == rootId) &&
                x.BranchName == null &&
                (x.EnvironmentName ?? "production").ToLower() == key, ct);
        if (taken)
            throw new AppEnvironmentException("environment_name_taken", "Environment names must be unique within the application (ignoring case).");
    }

    private async Task<string> ResolveAvailableEnvironmentSlugAsync(string organizationId, string appSlug, string normalizedEnvName, bool isFirstRoot, CancellationToken ct)
    {
        if (isFirstRoot && normalizedEnvName == "production" && !await db.Apps.AnyAsync(x => x.OrganizationId == organizationId && x.Slug == appSlug, ct))
        {
            return appSlug;
        }

        var candidate = AppBranchService.BranchSlug(appSlug, normalizedEnvName.Replace('_', '-'));
        if (!await db.Apps.AnyAsync(x => x.OrganizationId == organizationId && x.Slug == candidate, ct))
        {
            return candidate;
        }

        for (var suffix = 2; suffix <= 99; suffix++)
        {
            var next = AppBranchService.BranchSlug(appSlug, $"{normalizedEnvName.Replace('_', '-')}-{suffix}");
            if (!await db.Apps.AnyAsync(x => x.OrganizationId == organizationId && x.Slug == next, ct))
            {
                return next;
            }
        }

        throw new AppEnvironmentException("environment_slug_taken", "This name conflicts with an existing app or branch. Choose another name.");
    }
}

public sealed class AppEnvironmentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
