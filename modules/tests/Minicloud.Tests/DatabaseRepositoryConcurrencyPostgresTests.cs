using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed partial class ApplicationEnvironmentMigrationPostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryDeletionLockPreventsRacingPolicyAssignmentAndEnrollment(bool enrollment)
    {
        await WithPostgres(async options =>
        {
            await using (var setup = new MinicloudDbContext(options))
            {
                await setup.Database.MigrateAsync();
                setup.Organizations.Add(new Organization { Id = "org_repository", Name = "Fixture", Slug = "repository" });
                setup.Apps.AddRange(
                    new MinicloudApp { Id = "app_repository", OrganizationId = "org_repository", Name = "Backups", Slug = "dbrepo-fixture", EnvironmentKind = EnvironmentKinds.Internal, Database = DatabaseMode.None },
                    new MinicloudApp { Id = "app_source", OrganizationId = "org_repository", Name = "Source", Slug = "source", Database = DatabaseMode.Postgres });
                if (!enrollment)
                {
                    setup.ManagedDatabases.Add(new ManagedDatabase { Id = "database_fixture", OrganizationId = "org_repository", Name = "Source", AppId = "app_source", Status = "ready" });
                    setup.DatabaseBackupPolicies.Add(new DatabaseBackupPolicy { Id = "policy_fixture", OrganizationId = "org_repository", DatabaseId = "database_fixture", Enabled = false });
                }
                await setup.SaveChangesAsync();
            }

            await using var deleting = new MinicloudDbContext(options);
            await using var transaction = await deleting.Database.BeginTransactionAsync();
            var repository = await deleting.Apps.FromSqlRaw("SELECT * FROM apps WHERE id = 'app_repository' FOR UPDATE").SingleAsync();
            var intercepted = new RepositoryAdmissionLockInterceptor();
            await using var admitting = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>(options).AddInterceptors(intercepted).Options);
            var ids = new IdGenerator();
            var management = new DatabaseManagementService(admitting, new(admitting), new(admitting, ids, TimeProvider.System), ids, TimeProvider.System);
            var actor = new ActorIdentity("api_key", null, "key_fixture", "org_repository", new HashSet<string> { ApiKeyScopes.DatabasesManage });
            Task admission = enrollment
                ? management.CreateDatabaseAsync(new("org_repository", "enrolled", AppId: "app_source", Backup: new(true, "app_repository")), actor, default)
                : management.SetPolicyAsync("database_fixture", new(true, "app_repository"), actor, default);
            await intercepted.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(admission.IsCompleted);

            repository.DeletionStatus = "queued";
            await deleting.SaveChangesAsync();
            await transaction.CommitAsync();

            await Assert.ThrowsAsync<ArgumentException>(() => admission.WaitAsync(TimeSpan.FromSeconds(10)));
            await using var verify = new MinicloudDbContext(options);
            Assert.False(await verify.DatabaseBackupPolicies.AnyAsync(x => x.Enabled));
            Assert.Empty(await verify.DatabaseOperations.ToListAsync());
            Assert.Equal("queued", (await verify.Apps.SingleAsync(x => x.Id == "app_repository")).DeletionStatus);
        });
    }

    private sealed class RepositoryAdmissionLockInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM apps", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
                Attempted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
