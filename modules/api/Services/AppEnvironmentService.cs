using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Api.Services;

// Each environment owns a runtime app record so all runtime resources remain isolated.
public sealed class AppEnvironmentService(MinicloudDbContext db, IIdGenerator ids, TimeProvider clock)
{
    public static bool IsValidName(string? name) => name is { Length: >= 1 and <= 63 }
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && char.IsAsciiLetterOrDigit(name[0]);

    public async Task<MinicloudApp> CreateAsync(MinicloudApp parent, CreateAppEnvironmentRequest request, CancellationToken ct)
    {
        if (parent.ParentAppId is not null || parent.BranchName is not null || parent.DeletionStatus is not null)
            throw new AppEnvironmentException("environment_parent_invalid", "Create environments under an active main application.");
        if (!IsValidName(request.Name))
            throw new AppEnvironmentException("environment_name_invalid", "Use 1 to 63 letters, numbers, dashes or underscores, starting with a letter or number.");
        if (!ControlPlaneValidation.TryParseEnum<AppPlan>(request.Plan, out var plan))
            throw new AppEnvironmentException("environment_plan_invalid", "Select a supported VPS plan.");
        var database = parent.Database;
        if (request.Database is not null && !ControlPlaneValidation.TryParseEnum(request.Database, out database))
            throw new AppEnvironmentException("environment_database_invalid", "Select sqlite or postgres.");
        await EnsureNameAvailableAsync(parent.Id, request.Name, null, ct);
        var slug = AppBranchService.BranchSlug(parent.Slug, request.Name.ToLowerInvariant().Replace('_', '-'));
        if (await db.Apps.AnyAsync(x => x.OrganizationId == parent.OrganizationId && x.Slug == slug, ct))
            throw new AppEnvironmentException("environment_slug_taken", "This name conflicts with an existing app or branch. Choose another name.");
        var now = clock.GetUtcNow();
        var environment = new MinicloudApp
        {
            Id = ids.NewId("app"), OrganizationId = parent.OrganizationId, Organization = parent.Organization,
            ParentAppId = parent.Id, ParentApp = parent, EnvironmentName = request.Name,
            EnvironmentKey = $"{parent.Id}:{request.Name.ToLowerInvariant()}",
            Name = $"{parent.Name} ({request.Name})", Slug = slug, Plan = plan, Database = database,
            CreatedAt = now, UpdatedAt = now
        };
        // Environments start with empty secrets. Production credentials must be set explicitly.
        db.Apps.Add(environment);
        await db.SaveChangesAsync(ct);
        return environment;
    }

    public async Task RenameAsync(MinicloudApp environment, string name, CancellationToken ct)
    {
        if (environment.BranchName is not null || environment.DeletionStatus is not null || !IsValidName(name))
            throw new AppEnvironmentException("environment_name_invalid", "Choose a valid name for an active environment.");
        await EnsureNameAvailableAsync(environment.ParentAppId ?? environment.Id, name, environment.Id, ct);
        if (environment.ParentAppId is not null)
        {
            var applicationName = await db.Apps.Where(x => x.Id == environment.ParentAppId).Select(x => x.Name).SingleAsync(ct);
            environment.Name = $"{applicationName} ({name})";
        }
        environment.EnvironmentName = name;
        environment.EnvironmentKey = $"{environment.ParentAppId ?? environment.Id}:{name.ToLowerInvariant()}";
        environment.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureNameAvailableAsync(string rootId, string name, string? exceptId, CancellationToken ct)
    {
        var key = name.ToLowerInvariant();
        if (await db.Apps.AnyAsync(x => x.Id != exceptId && (x.Id == rootId || x.ParentAppId == rootId)
            && x.BranchName == null && (x.EnvironmentName ?? "production").ToLower() == key, ct))
            throw new AppEnvironmentException("environment_name_taken", "Environment names must be unique within the application (ignoring case).");
    }
}

public sealed class AppEnvironmentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
