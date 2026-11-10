using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed record ApplicationEnvironmentPreflightConflict(
    string Code,
    string Message,
    string? RootAppId = null,
    string? ChildAppId = null,
    string? Value = null);

public sealed record ApplicationEnvironmentPreflightReport(
    bool IsValid,
    IReadOnlyList<ApplicationEnvironmentPreflightConflict> Conflicts,
    int CustomerRootCount,
    int PersistentEnvironmentCount,
    int PreviewEnvironmentCount,
    int InternalRecordCount);

public sealed record ApplicationEnvironmentBackfillResult(
    int ApplicationsCount,
    int PersistentEnvironmentsCount,
    int PreviewEnvironmentsCount,
    int InternalRecordsCount);

public sealed class ApplicationEnvironmentMigrationException(
    string code,
    string message,
    ApplicationEnvironmentPreflightReport? report = null) : Exception(message)
{
    public string Code { get; } = code;
    public ApplicationEnvironmentPreflightReport? Report { get; } = report;
}

public static partial class ApplicationEnvironmentMigrationService
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$")]
    private static partial Regex ValidEnvironmentNameRegex();

    public static bool IsValidEnvironmentName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ValidEnvironmentNameRegex().IsMatch(name.Trim());

    public static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    public static string DeterministicApplicationId(string rootAppId) =>
        rootAppId.StartsWith("app_", StringComparison.Ordinal)
            ? "appl_" + rootAppId[4..]
            : "appl_" + rootAppId;

    public static bool IsInternalRecord(MinicloudApp app, IReadOnlySet<string>? databaseServerAppIds = null) =>
        string.Equals(app.EnvironmentKind, EnvironmentKinds.Internal, StringComparison.Ordinal) ||
        app.Slug.StartsWith("dbrepo-", StringComparison.Ordinal) ||
        (databaseServerAppIds is not null && databaseServerAppIds.Contains(app.Id));

    public static string? SelectNextDefaultEnvironmentId(IEnumerable<MinicloudApp> environments) =>
        environments
            .Where(x => x.DeletionStatus is null && !string.Equals(x.EnvironmentKind, EnvironmentKinds.Internal, StringComparison.Ordinal))
            .OrderBy(x => string.Equals(x.EnvironmentKind, EnvironmentKinds.Persistent, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.Id)
            .FirstOrDefault();

    public static async Task<ApplicationEnvironmentPreflightReport> InspectPreflightAsync(
        MinicloudDbContext db,
        IReadOnlyDictionary<string, string>? explicitBranchMappings = null,
        CancellationToken ct = default)
    {
        var allApps = (await db.Apps.AsNoTracking().ToListAsync(ct))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
        var dbServerAppIds = (await db.ManagedDatabaseServers.AsNoTracking()
            .Where(x => x.RuntimeAppId != null)
            .Select(x => x.RuntimeAppId!)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var conflicts = new List<ApplicationEnvironmentPreflightConflict>();
        var internalApps = allApps.Where(x => IsInternalRecord(x, dbServerAppIds)).ToList();
        var customerApps = allApps.Where(x => !IsInternalRecord(x, dbServerAppIds)).ToList();
        var byId = customerApps.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var roots = customerApps.Where(x => x.ParentAppId is null).ToList();
        var children = customerApps.Where(x => x.ParentAppId is not null).ToList();

        foreach (var slugGroup in roots.Where(x => x.ApplicationId is null).GroupBy(x => (x.OrganizationId, Slug: x.Slug.Trim().ToLowerInvariant())))
        {
            if (slugGroup.Count() > 1)
            {
                var items = slugGroup.ToList();
                conflicts.Add(new ApplicationEnvironmentPreflightConflict(
                    "duplicate_application_slug",
                    $"Duplicate case-insensitive application slug '{slugGroup.Key.Slug}' in organization '{slugGroup.Key.OrganizationId}'.",
                    items[0].Id,
                    items[1].Id,
                    slugGroup.Key.Slug));
            }
        }

        foreach (var child in children)
        {
            if (!byId.TryGetValue(child.ParentAppId!, out var parent))
            {
                conflicts.Add(new ApplicationEnvironmentPreflightConflict(
                    "orphaned_parent",
                    $"Environment record '{child.Id}' references missing or internal parent '{child.ParentAppId}'.",
                    child.ParentAppId,
                    child.Id));
                continue;
            }

            if (parent.ParentAppId is not null)
            {
                conflicts.Add(new ApplicationEnvironmentPreflightConflict(
                    "unsupported_parent_hierarchy",
                    $"Environment record '{child.Id}' references non-root parent '{parent.Id}'.",
                    parent.Id,
                    child.Id));
                continue;
            }

            if (!string.Equals(child.OrganizationId, parent.OrganizationId, StringComparison.Ordinal))
            {
                conflicts.Add(new ApplicationEnvironmentPreflightConflict(
                    "cross_organization_parent",
                    $"Environment record '{child.Id}' in organization '{child.OrganizationId}' references parent '{parent.Id}' in organization '{parent.OrganizationId}'.",
                    parent.Id,
                    child.Id));
            }
        }

        var persistentCount = 0;
        var previewCount = 0;

        // After root-environment deletion several siblings may have no legacy parent.
        // Explicit application ownership remains authoritative on subsequent preflights.
        foreach (var familyGroup in customerApps.GroupBy(x => x.ApplicationId
            ?? (x.ParentAppId is not null && byId.TryGetValue(x.ParentAppId, out var owner) ? owner.ApplicationId ?? owner.Id : x.Id)))
        {
            var family = familyGroup.ToList();
            var root = family.FirstOrDefault(x => x.ParentAppId is null) ?? family[0];

            var persistentByNorm = new Dictionary<string, MinicloudApp>(StringComparer.Ordinal);
            foreach (var env in family.Where(x => x.BranchName is null && !string.Equals(x.EnvironmentKind, EnvironmentKinds.Preview, StringComparison.Ordinal)))
            {
                persistentCount++;
                var displayName = ResolvePersistentDisplayName(env);
                if (!IsValidEnvironmentName(displayName))
                {
                    conflicts.Add(new ApplicationEnvironmentPreflightConflict(
                        "invalid_environment_name",
                        $"Environment '{env.Id}' has invalid environment name '{displayName}'.",
                        root.Id,
                        env.Id,
                        displayName));
                    continue;
                }

                var norm = NormalizeName(displayName);
                if (persistentByNorm.TryGetValue(norm, out var existing))
                {
                    conflicts.Add(new ApplicationEnvironmentPreflightConflict(
                        "duplicate_environment_name",
                        $"Application root '{root.Id}' has duplicate environment name '{displayName}' ('{existing.Id}' and '{env.Id}').",
                        root.Id,
                        env.Id,
                        displayName));
                }
                else
                {
                    persistentByNorm[norm] = env;
                }
            }

            var previewByNorm = new Dictionary<string, MinicloudApp>(StringComparer.Ordinal);
            foreach (var branch in family.Where(x => x.BranchName is not null || string.Equals(x.EnvironmentKind, EnvironmentKinds.Preview, StringComparison.Ordinal)))
            {
                previewCount++;
                var displayName = ResolvePreviewDisplayName(root, branch, explicitBranchMappings);
                if (!IsValidEnvironmentName(displayName))
                {
                    conflicts.Add(new ApplicationEnvironmentPreflightConflict(
                        "branch_environment_name_conflict",
                        $"Preview branch '{branch.BranchName ?? branch.EnvironmentName}' on '{branch.Id}' requires a valid explicit environment display name mapping.",
                        root.Id,
                        branch.Id,
                        displayName));
                    continue;
                }

                var norm = NormalizeName(displayName);
                if (persistentByNorm.TryGetValue(norm, out var collidingPersistent))
                {
                    conflicts.Add(new ApplicationEnvironmentPreflightConflict(
                        "branch_environment_name_conflict",
                        $"Preview branch '{branch.BranchName}' on '{branch.Id}' collides with persistent environment '{collidingPersistent.EnvironmentName ?? "production"}' ('{collidingPersistent.Id}'). Provide an explicit migration mapping.",
                        root.Id,
                        branch.Id,
                        displayName));
                }
                else if (previewByNorm.TryGetValue(norm, out var collidingPreview))
                {
                    conflicts.Add(new ApplicationEnvironmentPreflightConflict(
                        "branch_environment_name_conflict",
                        $"Preview branch '{branch.BranchName}' on '{branch.Id}' collides with preview environment '{collidingPreview.Id}' under '{root.Id}'.",
                        root.Id,
                        branch.Id,
                        displayName));
                }
                else
                {
                    previewByNorm[norm] = branch;
                }
            }
        }

        return new ApplicationEnvironmentPreflightReport(
            conflicts.Count == 0,
            conflicts,
            roots.Count,
            persistentCount,
            previewCount,
            internalApps.Count);
    }

    public static async Task<ApplicationEnvironmentBackfillResult> BackfillAsync(
        MinicloudDbContext db,
        IReadOnlyDictionary<string, string>? explicitBranchMappings = null,
        CancellationToken ct = default)
    {
        var report = await InspectPreflightAsync(db, explicitBranchMappings, ct);
        if (!report.IsValid)
        {
            var first = report.Conflicts[0];
            throw new ApplicationEnvironmentMigrationException(first.Code, first.Message, report);
        }

        var allApps = (await db.Apps.ToListAsync(ct))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
        var existingApplications = await db.Applications.ToListAsync(ct);
        var dbServerAppIds = (await db.ManagedDatabaseServers.AsNoTracking()
            .Where(x => x.RuntimeAppId != null)
            .Select(x => x.RuntimeAppId!)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var appsByLegacyRoot = existingApplications
            .Where(x => x.LegacyRootAppId is not null)
            .ToDictionary(x => x.LegacyRootAppId!, StringComparer.Ordinal);
        var appsById = existingApplications.ToDictionary(x => x.Id, StringComparer.Ordinal);

        var internalCount = 0;
        var persistentCount = 0;
        var previewCount = 0;

        foreach (var internalApp in allApps.Where(x => IsInternalRecord(x, dbServerAppIds)))
        {
            internalCount++;
            internalApp.EnvironmentKind = EnvironmentKinds.Internal;
            internalApp.ApplicationId = null;
            internalApp.NormalizedEnvironmentName = null;
        }

        var customerApps = allApps.Where(x => !IsInternalRecord(x, dbServerAppIds)).ToList();
        var roots = customerApps.Where(x => x.ParentAppId is null).ToList();
        var children = customerApps.Where(x => x.ParentAppId is not null).ToList();
        var rootApplications = new Dictionary<string, MinicloudApplication>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            if (root.ApplicationId is not null && appsById.TryGetValue(root.ApplicationId, out var mapped))
            {
                rootApplications[root.Id] = mapped;
                if (root.EnvironmentKind == EnvironmentKinds.Preview) previewCount++; else persistentCount++;
                continue;
            }
            MinicloudApplication? application = null;
            if (root.ApplicationId is not null)
            {
                appsById.TryGetValue(root.ApplicationId, out application);
            }
            if (application is null)
            {
                appsByLegacyRoot.TryGetValue(root.Id, out application);
            }
            if (application is null)
            {
                var appId = DeterministicApplicationId(root.Id);
                appsById.TryGetValue(appId, out application);
            }

            if (application is null)
            {
                var appId = DeterministicApplicationId(root.Id);
                application = new MinicloudApplication
                {
                    Id = appId,
                    OrganizationId = root.OrganizationId,
                    Name = root.Name,
                    Slug = root.Slug,
                    NormalizedSlug = root.Slug.Trim().ToLowerInvariant(),
                    DefaultEnvironmentId = root.Id,
                    LegacyRootAppId = root.Id,
                    DeletionStatus = root.DeletionStatus,
                    DeletionError = root.DeletionError,
                    CreatedAt = root.CreatedAt,
                    UpdatedAt = root.UpdatedAt
                };
                db.Applications.Add(application);
                appsById[application.Id] = application;
                appsByLegacyRoot[root.Id] = application;
            }
            else
            {
                application.NormalizedSlug = application.Slug.Trim().ToLowerInvariant();
                application.LegacyRootAppId ??= root.Id;
                application.DefaultEnvironmentId ??= root.Id;
            }

            rootApplications[root.Id] = application;

            var envName = ResolvePersistentDisplayName(root);
            var normName = NormalizeName(envName);
            root.ApplicationId = application.Id;
            root.EnvironmentKind = EnvironmentKinds.Persistent;
            root.EnvironmentName = envName;
            root.NormalizedEnvironmentName = normName;
            root.EnvironmentKey = $"{root.Id}:{normName}";
            persistentCount++;
        }

        foreach (var child in children)
        {
            if (child.ApplicationId is not null)
            {
                if (child.EnvironmentKind == EnvironmentKinds.Preview) previewCount++; else persistentCount++;
                continue;
            }
            var application = rootApplications[child.ParentAppId!];
            child.ApplicationId = application.Id;
            if (child.BranchName is not null || string.Equals(child.EnvironmentKind, EnvironmentKinds.Preview, StringComparison.Ordinal))
            {
                var root = roots.Single(x => x.Id == child.ParentAppId);
                var displayName = ResolvePreviewDisplayName(root, child, explicitBranchMappings);
                var normName = NormalizeName(displayName);
                child.EnvironmentKind = EnvironmentKinds.Preview;
                child.RuntimeParentSlug ??= root.Slug;
                child.SourceBranch = child.SourceBranch ?? child.BranchName;
                child.SourceEnvironmentId = child.SourceEnvironmentId ?? child.ParentAppId;
                child.EnvironmentName = displayName;
                child.NormalizedEnvironmentName = normName;
                child.EnvironmentKey = $"{child.ParentAppId}:{normName}";
                previewCount++;
            }
            else
            {
                var displayName = ResolvePersistentDisplayName(child);
                var normName = NormalizeName(displayName);
                child.EnvironmentKind = EnvironmentKinds.Persistent;
                child.EnvironmentName = displayName;
                child.NormalizedEnvironmentName = normName;
                child.EnvironmentKey = $"{child.ParentAppId}:{normName}";
                persistentCount++;
            }
        }

        await db.SaveChangesAsync(ct);

        return new ApplicationEnvironmentBackfillResult(
            appsById.Count,
            persistentCount,
            previewCount,
            internalCount);
    }

    public static Task EnsureBackfilledAsync(
        MinicloudDbContext db,
        CancellationToken ct) =>
        EnsureBackfilledAsync(db, null, ct);

    public static async Task EnsureBackfilledAsync(
        MinicloudDbContext db,
        string? organizationId = null,
        CancellationToken ct = default)
    {
        var query = db.Apps.AsQueryable();
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            query = query.Where(x => x.OrganizationId == organizationId);
        }

        var unlinked = (await query
            .Where(x => x.ApplicationId == null && x.EnvironmentKind != EnvironmentKinds.Internal && !x.Slug.StartsWith("dbrepo-"))
            .ToListAsync(ct))
            .OrderBy(x => x.ParentAppId != null ? 1 : 0)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();

        if (unlinked.Count == 0)
        {
            return;
        }

        var dbServerAppIds = (await db.ManagedDatabaseServers.AsNoTracking()
            .Where(x => x.RuntimeAppId != null)
            .Select(x => x.RuntimeAppId!)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var changed = false;
        foreach (var app in unlinked)
        {
            if (IsInternalRecord(app, dbServerAppIds))
            {
                app.EnvironmentKind = EnvironmentKinds.Internal;
                app.ApplicationId = null;
                app.NormalizedEnvironmentName = null;
                changed = true;
                continue;
            }

            if (app.ParentAppId is null)
            {
                var appId = DeterministicApplicationId(app.Id);
                var application = db.Applications.Local.FirstOrDefault(x => x.Id == appId || x.LegacyRootAppId == app.Id)
                    ?? await db.Applications.SingleOrDefaultAsync(
                        x => x.Id == appId || x.LegacyRootAppId == app.Id,
                        ct);
                if (application is null)
                {
                    application = new MinicloudApplication
                    {
                        Id = appId,
                        OrganizationId = app.OrganizationId,
                        Name = app.Name,
                        Slug = app.Slug,
                        NormalizedSlug = app.Slug.Trim().ToLowerInvariant(),
                        DefaultEnvironmentId = app.Id,
                        LegacyRootAppId = app.Id,
                        DeletionStatus = app.DeletionStatus,
                        DeletionError = app.DeletionError,
                        CreatedAt = app.CreatedAt == default ? DateTimeOffset.UtcNow : app.CreatedAt,
                        UpdatedAt = app.UpdatedAt == default ? DateTimeOffset.UtcNow : app.UpdatedAt
                    };
                    db.Applications.Add(application);
                }

                var envName = ResolvePersistentDisplayName(app);
                var normName = NormalizeName(envName);
                app.ApplicationId = application.Id;
                app.EnvironmentKind = EnvironmentKinds.Persistent;
                app.EnvironmentName = envName;
                app.NormalizedEnvironmentName = normName;
                app.EnvironmentKey ??= $"{app.Id}:{normName}";
                changed = true;
            }
            else
            {
                var parent = db.Apps.Local.FirstOrDefault(x => x.Id == app.ParentAppId)
                    ?? await db.Apps.SingleOrDefaultAsync(x => x.Id == app.ParentAppId, ct);
                if (parent is null || parent.ParentAppId is not null || IsInternalRecord(parent, dbServerAppIds))
                    throw new ApplicationEnvironmentMigrationException("orphaned_parent", $"Environment '{app.Id}' has no customer root parent.");
                if (parent.OrganizationId != app.OrganizationId)
                    throw new ApplicationEnvironmentMigrationException("cross_organization_parent", $"Environment '{app.Id}' and its parent belong to different organizations.");

                var parentAppId = parent.ApplicationId ?? DeterministicApplicationId(parent.Id);
                var application = db.Applications.Local.FirstOrDefault(x => x.Id == parentAppId || x.LegacyRootAppId == parent.Id)
                    ?? await db.Applications.SingleOrDefaultAsync(
                        x => x.Id == parentAppId || x.LegacyRootAppId == parent.Id,
                        ct);
                if (application is null)
                {
                    application = new MinicloudApplication
                    {
                        Id = parentAppId,
                        OrganizationId = parent.OrganizationId,
                        Name = parent.Name,
                        Slug = parent.Slug,
                        NormalizedSlug = parent.Slug.Trim().ToLowerInvariant(),
                        DefaultEnvironmentId = parent.Id,
                        LegacyRootAppId = parent.Id,
                        DeletionStatus = parent.DeletionStatus,
                        DeletionError = parent.DeletionError,
                        CreatedAt = parent.CreatedAt == default ? DateTimeOffset.UtcNow : parent.CreatedAt,
                        UpdatedAt = parent.UpdatedAt == default ? DateTimeOffset.UtcNow : parent.UpdatedAt
                    };
                    db.Applications.Add(application);
                    parent.ApplicationId = application.Id;
                    var parentEnvName = ResolvePersistentDisplayName(parent);
                    parent.EnvironmentName = parentEnvName;
                    parent.NormalizedEnvironmentName = NormalizeName(parentEnvName);
                    parent.EnvironmentKey ??= $"{parent.Id}:{parent.NormalizedEnvironmentName}";
                }

                app.ApplicationId = application.Id;
                if (app.BranchName is not null || string.Equals(app.EnvironmentKind, EnvironmentKinds.Preview, StringComparison.Ordinal))
                {
                    var displayName = ResolvePreviewDisplayName(parent, app, null);
                    var normName = NormalizeName(displayName);
                    app.EnvironmentKind = EnvironmentKinds.Preview;
                    app.RuntimeParentSlug ??= parent.Slug;
                    app.SourceBranch ??= app.BranchName;
                    app.SourceEnvironmentId ??= app.ParentAppId;
                    app.EnvironmentName = displayName;
                    app.NormalizedEnvironmentName = normName;
                    app.EnvironmentKey ??= $"{app.ParentAppId}:{normName}";
                }
                else
                {
                    var displayName = ResolvePersistentDisplayName(app);
                    var normName = NormalizeName(displayName);
                    app.EnvironmentKind = EnvironmentKinds.Persistent;
                    app.EnvironmentName = displayName;
                    app.NormalizedEnvironmentName = normName;
                    app.EnvironmentKey ??= $"{app.ParentAppId}:{normName}";
                }
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static string ResolvePersistentDisplayName(MinicloudApp env)
    {
        if (!string.IsNullOrWhiteSpace(env.EnvironmentName))
        {
            return env.EnvironmentName.Trim();
        }

        return env.ParentAppId is null ? "production" : env.Slug.Trim();
    }

    private static string ResolvePreviewDisplayName(
        MinicloudApp root,
        MinicloudApp branch,
        IReadOnlyDictionary<string, string>? explicitBranchMappings)
    {
        if (explicitBranchMappings is not null)
        {
            if (explicitBranchMappings.TryGetValue(branch.Id, out var mappedById) && !string.IsNullOrWhiteSpace(mappedById))
            {
                return mappedById.Trim();
            }

            if (branch.BranchName is not null &&
                explicitBranchMappings.TryGetValue($"{root.Id}:{branch.BranchName}", out var mappedByKey) &&
                !string.IsNullOrWhiteSpace(mappedByKey))
            {
                return mappedByKey.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(branch.EnvironmentName))
        {
            return branch.EnvironmentName.Trim();
        }

        return (branch.BranchName ?? branch.Slug).Trim();
    }
}
