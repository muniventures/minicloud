using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class AppBranchService(
    MinicloudDbContext db,
    AppServiceSecretService serviceSecrets,
    IIdGenerator ids,
    TimeProvider timeProvider,
    TelemetryService? telemetry = null,
    ProviderCatalogService? catalog = null)
{
    public async Task<MinicloudApp> EnsureAsync(MinicloudApp parent, string branchName, CancellationToken cancellationToken, string? planName = null)
    {
        await db.Entry(parent).ReloadAsync(cancellationToken);
        if (parent.ApplicationId is null || parent.EnvironmentKind != EnvironmentKinds.Persistent || parent.BranchName is not null)
            throw new AppBranchException("branch_parent_invalid", "Branches require a persistent source environment in an application.");
        if (parent.DeletionStatus is not null || await db.Applications.AnyAsync(x => x.Id == parent.ApplicationId && x.DeletionStatus != null, cancellationToken))
            throw new AppBranchException("app_deleting", "The application or source environment is being deleted.");

        var normalized = NormalizeBranchName(branchName);
        var existing = await db.Apps
            .Include(x => x.SelectedPlan)
            .Include(x => x.Organization)
            .Include(x => x.Deployments)
            .Include(x => x.Domains)
            .SingleOrDefaultAsync(x => x.ParentAppId == parent.Id && x.BranchName == normalized, cancellationToken);
        if (existing is not null)
        {
            if (planName is not null && catalog is not null)
            {
                if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null)
                {
                    await db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE id = {existing.Id} FOR UPDATE")
                        .AsNoTracking().SingleAsync(cancellationToken);
                    await db.Entry(existing).ReloadAsync(cancellationToken);
                }
                try { await catalog.BindAsync(existing, planName, cancellationToken); }
                catch (ProviderCatalogException ex) { throw new AppBranchException(ex.Code, ex.Message); }
                await db.SaveChangesAsync(cancellationToken);
            }
            return existing;
        }

        var collidesWithPersistent = await db.Apps.AnyAsync(x =>
            x.ApplicationId == parent.ApplicationId && x.BranchName == null &&
            (x.NormalizedEnvironmentName == normalized || (x.EnvironmentName ?? "production").ToLower() == normalized), cancellationToken);
        if (await db.Apps.AnyAsync(x => x.ApplicationId == parent.ApplicationId && x.ParentAppId != parent.Id && x.BranchName == normalized, cancellationToken))
            throw new AppBranchException("branch_source_conflict", "This preview already belongs to another source environment.");
        if (collidesWithPersistent)
        {
            throw new AppBranchException("branch_environment_name_conflict", $"Branch '{branchName}' conflicts with an existing environment name in this application.");
        }

        ProviderPlan? selectedPlan = null;
        if (catalog is not null)
        {
            try
            {
                if (planName is null && parent.PlanId is not null)
                    await db.Entry(parent).Reference(x => x.SelectedPlan).LoadAsync(cancellationToken);
                selectedPlan = await catalog.ResolveAsync(parent.OrganizationId, planName ?? parent.SelectedPlan?.Name
                    ?? Minicloud.Common.Validation.ControlPlaneValidation.ToWireValue(parent.Plan), cancellationToken);
            }
            catch (ProviderCatalogException ex) { throw new AppBranchException(ex.Code, ex.Message); }
        }

        var now = timeProvider.GetUtcNow();
        var slug = BranchSlug(parent.Slug, normalized);
        var branch = new MinicloudApp
        {
            Id = ids.NewId("app"),
            OrganizationId = parent.OrganizationId,
            Organization = parent.Organization,
            ApplicationId = parent.ApplicationId,
            ParentAppId = parent.Id,
            ParentApp = parent,
            BranchName = normalized,
            RuntimeParentSlug = parent.Slug,
            SourceBranch = branchName.Trim(),
            SourceEnvironmentId = parent.Id,
            EnvironmentName = normalized,
            NormalizedEnvironmentName = normalized,
            EnvironmentKind = EnvironmentKinds.Preview,
            EnvironmentKey = $"{parent.Id}:{normalized}",
            Name = $"{parent.Name} ({normalized})",
            Slug = slug,
            Plan = parent.Plan,
            PlanId = selectedPlan?.Id ?? parent.PlanId,
            SelectedPlan = selectedPlan ?? parent.SelectedPlan,
            Database = parent.Database,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Apps.Add(branch);

        var appSecrets = await db.AppSecrets.AsNoTracking()
            .Where(x => x.AppId == parent.Id)
            .ToListAsync(cancellationToken);
        foreach (var secret in appSecrets)
        {
            db.AppSecrets.Add(new AppSecret
            {
                Id = ids.NewId("secret"),
                OrganizationId = branch.OrganizationId,
                AppId = branch.Id,
                Name = secret.Name,
                EncryptedValue = secret.EncryptedValue,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await serviceSecrets.CloneAsync(parent, branch, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return branch;
    }

    public async Task DeleteSecretValuesAsync(string branchAppId, CancellationToken cancellationToken)
    {
        await serviceSecrets.DeleteAllAsync(branchAppId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DestroyAsync(MinicloudApp branch, CancellationToken cancellationToken)
    {
        if (branch.ParentAppId is null || branch.BranchName is null)
        {
            throw new AppBranchException("branch_destroy_invalid", "Main apps cannot be destroyed through the branch lifecycle.");
        }

        // T90: GitHub branch-teardown dispatch was decommissioned. minicloud-worker
        // owns infrastructure teardown; the API removes local branch state only.
        await DeleteSecretValuesAsync(branch.Id, cancellationToken);
        if (telemetry is not null)
        {
            await telemetry.TombstoneAppAsync(branch.Id, cancellationToken);
        }

        db.Apps.Remove(branch);
        if (branch.ApplicationId is not null)
        {
            var application = await db.Applications.SingleOrDefaultAsync(x => x.Id == branch.ApplicationId, cancellationToken);
            if (application is not null && application.DefaultEnvironmentId == branch.Id)
            {
                var remaining = await db.Apps.AsNoTracking()
                    .Where(x => x.ApplicationId == application.Id && x.Id != branch.Id)
                    .ToListAsync(cancellationToken);
                application.DefaultEnvironmentId = ApplicationEnvironmentMigrationService.SelectNextDefaultEnvironmentId(remaining);
                application.UpdatedAt = timeProvider.GetUtcNow();
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string NormalizeBranchName(string branchName)
    {
        var input = branchName?.Trim() ?? string.Empty;
        var result = new StringBuilder(input.Length);
        var pendingSeparator = false;
        foreach (var character in input)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingSeparator && result.Length > 0)
                {
                    result.Append('-');
                }
                result.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        var normalized = result.ToString().Trim('-');
        if (normalized.Length == 0)
        {
            throw new AppBranchException("branch_name_invalid", "The Git branch name cannot be normalized into a valid branch label.");
        }

        return Compact(normalized, 32);
    }

    public static string BranchSlug(string parentSlug, string normalizedBranch) =>
        Compact($"{parentSlug}-{normalizedBranch}", 63);

    private static string Compact(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
        return $"{value[..(maxLength - hash.Length - 1)].TrimEnd('-')}-{hash}";
    }
}

public sealed class AppBranchException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
