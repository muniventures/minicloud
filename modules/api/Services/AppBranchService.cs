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
        if (parent.ParentAppId is not null)
        {
            throw new AppBranchException("branch_parent_invalid", "Branches can only be created from a main app.");
        }

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
            ParentAppId = parent.Id,
            ParentApp = parent,
            BranchName = normalized,
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
