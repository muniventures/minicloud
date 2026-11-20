using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using Npgsql;

namespace Minicloud.Worker.Tests.DevelopmentWorkspaces;

public sealed class DevelopmentWorkspaceDeletionPostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task VerifiedDeletionReleasesProvisionedStorageAndNameWithoutTouchingNeighbors(bool loseResponse) =>
        WithPostgres(async fixture =>
        {
            await fixture.ApplyDeletionIdentityMigrationAsync();
            await fixture.VerifyPermanentDeletionAsync(loseResponse);
        });

    [Fact]
    public Task FailedTerminalCommitCanRetryAfterUpgradeWithProviderVolumeAlreadyAbsent() =>
        WithPostgres(fixture => fixture.VerifyDeletionUpgradeRetryAsync());

    [Fact]
    public Task IdentityGuardRejectsEveryOtherIdentityChangeAndRollbackRestoresOriginalGuard() =>
        WithPostgres(fixture => fixture.VerifyDeletionIdentityGuardAsync());

    [Theory]
    [InlineData("labels", "workspace_delete_volume_mismatch")]
    [InlineData("region", "workspace_delete_volume_mismatch")]
    [InlineData("attachment", "workspace_delete_volume_attached")]
    [InlineData("account", "workspace_delete_identity_mismatch")]
    [InlineData("missing", "workspace_delete_volume_unverified")]
    [InlineData("inventory", "workspace_delete_volume_unexpected")]
    public Task UnverifiedOrMismatchedStorageRemainsReserved(string mismatch, string errorCode) =>
        WithPostgres(async fixture =>
        {
            await fixture.ApplyDeletionIdentityMigrationAsync();
            await fixture.VerifyUnsafeDeletionAsync(mismatch, errorCode);
        });

    [Fact]
    public Task TerminalCompletionStillRequiresCurrentVerifiedOperationAndLease() =>
        WithPostgres(async fixture =>
        {
            await fixture.ApplyDeletionIdentityMigrationAsync();
            await fixture.VerifyDeletionFencingAsync();
        });

    [Theory]
    [InlineData("upsert")]
    [InlineData("read")]
    [InlineData("prune")]
    public Task ResourceHistoryUpsertReadAndPruneWorkOnPostgres(string scenario) =>
        WithPostgres(async fixture =>
        {
            await fixture.ApplyDeletionIdentityMigrationAsync();
            await (scenario switch
            {
                "upsert" => fixture.HeartbeatRecordsNewestSamplePerMinuteIdempotentlyAndPreservesUnknownMetrics(),
                "read" => fixture.HistoryReadIsScopedBoundedOrderedAndLimitedToTheCurrentGeneration(),
                _ => fixture.ExpiredHistoryIsPrunedInBatchesWhileVmsAreStopped()
            });
        });

    private static async Task WithPostgres(Func<DevelopmentWorkspaceProcessorTests, Task> test)
    {
        var name = "mc-workspace-deletion-" + Guid.NewGuid().ToString("N");
        var started = false;
        try
        {
            await Docker("run", "--rm", "-d", "--name", name, "-e", "POSTGRES_PASSWORD=" + name,
                "-p", "127.0.0.1::5432", "postgres:16-alpine");
            started = true;
            var port = (await Docker("port", name, "5432/tcp")).Trim().Split(':')[^1];
            var options = new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseNpgsql($"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;Password={name}").Options;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await using var probe = new MinicloudDbContext(options);
                if (await probe.Database.CanConnectAsync()) break;
                await Task.Delay(250);
            }
            var fixture = new DevelopmentWorkspaceProcessorTests(options);
            try
            {
                await fixture.InitializeAsync();
                await test(fixture);
            }
            finally { await fixture.DisposeAsync(); }
        }
        finally { if (started) await Docker("rm", "-f", name); }
    }

    private static async Task<string> Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Disposable PostgreSQL fixture command failed: " + await stderr);
        return await stdout;
    }
}

public sealed partial class DevelopmentWorkspaceProcessorTests
{
    internal Task ApplyDeletionIdentityMigrationAsync() => db.Database.MigrateAsync();

    internal async Task VerifyDeletionUpgradeRetryAsync()
    {
        var workspace = await SeedRetainedVolume();
        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        await Hop("delete-verified-volume");
        var operationId = (await CurrentVm(workspace.PrimaryVmId)).OperationId!;
        await Hop();
        Assert.Null(cloud.Volume);
        Assert.Equal("volume-retained", Assert.Single(cloud.DeletedVolumes));
        var failed = await CurrentOperation(operationId);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("delete-verified-volume", failed.Step);
        Assert.Equal("workspace_reservation_conflict", failed.ErrorCode);
        Assert.Equal("delete_failed", (await CurrentVm(workspace.PrimaryVmId)).Phase);
        Assert.Equal("volume-retained", (await CurrentWorkspace(workspace.Id)).ProviderVolumeId);
        Assert.Equal("deleting", (await CurrentWorkspace(workspace.Id)).VolumeStatus);
        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        await Hop();
        Assert.Equal("failed", (await CurrentOperation(operationId)).Status);
        Assert.Equal("workspace_reservation_conflict", (await CurrentOperation(operationId)).ErrorCode);
        Assert.Equal("volume-retained", (await CurrentWorkspace(workspace.Id)).ProviderVolumeId);
        Assert.Single(cloud.DeletedVolumes);
        var reserved = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() => lifecycle.CreateAsync(
            new("org", workspace.Name, "image", "standard", "plan", 20, "another-key"), "actor", default));
        Assert.Equal("workspace_reservation_conflict", reserved.Code);

        await ApplyDeletionIdentityMigrationAsync();
        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        Assert.Equal(operationId, (await CurrentVm(workspace.PrimaryVmId)).OperationId);
        await Hop();
        var deleted = await CurrentWorkspace(workspace.Id);
        Assert.Equal("deleted", deleted.VolumeStatus);
        Assert.Null(deleted.ProviderVolumeId);
        Assert.Null(deleted.VolumeFilesystemUuid);
        Assert.Equal(0m, deleted.StorageMonthlyRate);
        Assert.Equal("completed", (await CurrentOperation(operationId)).Status);
        Assert.Equal("volume-retained", (await CurrentOperation(operationId)).ProviderResourceId);
        Assert.Single(cloud.DeletedVolumes);
        var repeated = await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        Assert.Equal("deleted", repeated.VolumeStatus);
        Assert.False(await Processor().ProcessNextAsync(default));
        var recreated = await lifecycle.CreateAsync(
            new("org", workspace.Name, "image", "standard", "plan", 20, workspace.IdempotencyKey), "actor", default);
        Assert.NotEqual(workspace.Id, recreated.Id);
        Assert.Equal(recreated.Id, (await lifecycle.CreateAsync(
            new("org", workspace.Name, "image", "standard", "plan", 20, workspace.IdempotencyKey), "actor", default)).Id);
    }

    internal async Task VerifyDeletionIdentityGuardAsync()
    {
        var workspace = await SeedRetainedVolume();
        await AssertGuardRejectionAsync("volume_status = 'deleted', provider_volume_id = NULL", workspace.Id);
        await ApplyDeletionIdentityMigrationAsync();
        foreach (var assignment in new[]
        {
            "provider_volume_id = NULL",
            "provider_volume_id = 'replacement'",
            "volume_status = 'deleted', provider_volume_id = NULL"
        })
            await AssertGuardRejectionAsync(assignment, workspace.Id);
        await db.Database.ExecuteSqlAsync($"UPDATE development_workspaces SET volume_status = 'deleting' WHERE id = {workspace.Id}");
        foreach (var assignment in new[]
        {
            "provider_volume_id = NULL",
            "volume_status = 'retained', provider_volume_id = NULL",
            "volume_status = 'deleted', provider_volume_id = 'replacement'",
            "volume_status = 'deleted', provider_volume_id = NULL, primary_vm_id = 'other'",
            "volume_status = 'deleted', provider_volume_id = NULL, organization_id = 'other'",
            "volume_status = 'deleted', provider_volume_id = NULL, provider_account_id = 'other'",
            "volume_status = 'deleted', provider_volume_id = NULL, region = 'fsn1'"
        })
            await AssertGuardRejectionAsync(assignment, workspace.Id);
        foreach (var assignment in new[] { "kind = 'ephemeral'", "workspace_id = 'other'", "organization_id = 'other'" })
        {
            var sql = $"UPDATE development_workspace_vms SET {assignment} WHERE id = {{0}}";
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync(sql, workspace.PrimaryVmId));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Contains("VM kind and ownership are immutable", error.MessageText);
        }
        await db.Database.ExecuteSqlAsync($"UPDATE development_workspaces SET volume_status = 'deleted' WHERE id = {workspace.Id}");
        await AssertGuardRejectionAsync("provider_volume_id = NULL", workspace.Id);
        await db.Database.ExecuteSqlAsync($"UPDATE development_workspaces SET volume_status = 'deleting' WHERE id = {workspace.Id}");
        await db.Database.MigrateAsync("20261119160815_DevelopmentWorkspaceDeletion");
        await AssertGuardRejectionAsync("volume_status = 'deleted', provider_volume_id = NULL", workspace.Id);
        await ApplyDeletionIdentityMigrationAsync();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE development_workspaces SET volume_status = 'deleted', provider_volume_id = NULL WHERE id = {workspace.Id}");
        Assert.Null((await CurrentWorkspace(workspace.Id)).ProviderVolumeId);
    }

    private async Task AssertGuardRejectionAsync(string assignment, string workspaceId)
    {
        var sql = $"UPDATE development_workspaces SET {assignment} WHERE id = {{0}}";
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(sql, workspaceId));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Contains("Primary storage identity and ownership are immutable", error.MessageText);
        Assert.Equal("volume-retained", (await CurrentWorkspace(workspaceId)).ProviderVolumeId);
    }

    internal async Task VerifyUnsafeDeletionAsync(string mismatch, string errorCode)
    {
        var workspace = await SeedRetainedVolume();
        var volume = cloud.Volume!;
        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        switch (mismatch)
        {
            case "labels": cloud.Volume = volume with { Labels = new Dictionary<string, string>(volume.Labels) { ["workspace"] = "other" } }; break;
            case "region": cloud.Volume = volume with { Region = "fsn1" }; break;
            case "attachment": cloud.Volume = volume with { ServerId = "other-writer" }; break;
            case "missing": cloud.Volume = null; break;
            case "inventory": cloud.SiblingVolumes.Add("extra", volume with { Id = "extra" }); break;
            case "account":
                var operationId = (await CurrentVm(workspace.PrimaryVmId)).OperationId!;
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE development_workspace_operations SET provider_account_id = 'other' WHERE id = {operationId}");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mismatch));
        }
        await Hop();
        Assert.Equal(errorCode, (await CurrentVm(workspace.PrimaryVmId)).ErrorCode);
        Assert.Equal("deleting", (await CurrentWorkspace(workspace.Id)).VolumeStatus);
        Assert.Equal(volume.Id, (await CurrentWorkspace(workspace.Id)).ProviderVolumeId);
        Assert.Equal(workspace.StorageMonthlyRate, (await CurrentWorkspace(workspace.Id)).StorageMonthlyRate);
        Assert.Empty(cloud.DeletedVolumes);
        Assert.Empty(cloud.DeletedServers);
        Assert.Empty(cloud.DetachedVolumes);
    }

    internal async Task VerifyDeletionFencingAsync()
    {
        var workspace = await SeedRetainedVolume();
        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        var claim = (await lifecycle.ClaimOperationAsync("fixture", default))!;
        var unverified = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, claim.LeaseToken!, default));
        Assert.Equal("workspace_operation_stale", unverified.Code);
        var wrongLease = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, "not-current", default));
        Assert.Equal("workspace_operation_stale", wrongLease.Code);
        await lifecycle.UpdateClaimedOperationAsync(claim.Id, claim.LeaseToken!, (operation, _, _) =>
            operation.Step = "delete-verified-volume", default);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE development_workspace_vms SET operation_id = NULL WHERE id = {workspace.PrimaryVmId}");
        var staleOperation = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, claim.LeaseToken!, default));
        Assert.Equal("workspace_operation_stale", staleOperation.Code);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE development_workspace_vms SET operation_id = {claim.Id} WHERE id = {workspace.PrimaryVmId}");
        await db.Database.ExecuteSqlAsync(
            $"UPDATE development_workspace_vms SET generation = generation + 1 WHERE id = {workspace.PrimaryVmId}");
        var staleGeneration = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, claim.LeaseToken!, default));
        Assert.Equal("workspace_operation_stale", staleGeneration.Code);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE development_workspace_vms SET generation = {claim.Generation} WHERE id = {workspace.PrimaryVmId}");
        clock.Advance(TimeSpan.FromSeconds(policy.OperationLeaseSeconds + 1));
        var expiredLease = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, claim.LeaseToken!, default));
        Assert.Equal("workspace_operation_stale", expiredLease.Code);
        Assert.Equal("volume-retained", (await CurrentWorkspace(workspace.Id)).ProviderVolumeId);
        Assert.Empty(cloud.DeletedVolumes);
    }
}
