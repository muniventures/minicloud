using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;

namespace Minicloud.Common.Data;

/// <summary>Raised before insertion when an organization's daily deployment counter cannot advance.</summary>
public sealed class DeploymentNumberExhaustedException(string organizationId, DateOnly utcDate)
    : InvalidOperationException($"Deployment numbers for organization '{organizationId}' on {utcDate:yyyy-MM-dd} are exhausted.")
{
    public const string Code = "deployment_number_exhausted";
}

// Every newly inserted deployment receives its organization/UTC-date number here, in the same
// transaction as its INSERT, so no creation path can persist an unnumbered or reused number.
public sealed partial class MinicloudDbContext
{
    // The conditional upsert locks the counter row until commit; a rolled-back insert rolls back its increment.
    private const string AllocateNumberSql = """
        INSERT INTO deployment_number_counters (organization_id, utc_date, last_value) VALUES (@organization_id, @utc_date, 1)
        ON CONFLICT (organization_id, utc_date) DO UPDATE SET last_value = deployment_number_counters.last_value + 1
        WHERE deployment_number_counters.last_value < 9223372036854775807
        RETURNING last_value
        """;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var pending = PendingNumberAllocations();
        if (pending.Count == 0)
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        if (Database.CurrentTransaction is not null)
        {
            return SaveWithNumbers(pending, () => base.SaveChanges(acceptAllChangesOnSuccess));
        }

        return Database.CreateExecutionStrategy().Execute(() =>
        {
            using var transaction = Database.BeginTransaction();
            var result = SaveWithNumbers(pending, () => base.SaveChanges(acceptAllChangesOnSuccess: false));
            try { transaction.Commit(); }
            catch { ResetNumbers(pending); throw; }
            if (acceptAllChangesOnSuccess) ChangeTracker.AcceptAllChanges();
            return result;
        });
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var pending = PendingNumberAllocations();
        if (pending.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        if (Database.CurrentTransaction is not null)
        {
            return await SaveWithNumbersAsync(pending, () => base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken), cancellationToken);
        }

        return await Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            var result = await SaveWithNumbersAsync(pending, () => base.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken), cancellationToken);
            try { await transaction.CommitAsync(cancellationToken); }
            catch { ResetNumbers(pending); throw; }
            if (acceptAllChangesOnSuccess) ChangeTracker.AcceptAllChanges();
            return result;
        });
    }

    private List<Deployment> PendingNumberAllocations()
    {
        ChangeTracker.DetectChanges();
        return ChangeTracker.Entries<Deployment>()
            .Where(entry => entry.State == EntityState.Added && string.IsNullOrEmpty(entry.Entity.Number))
            .Select(entry => entry.Entity)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
    }

    private int SaveWithNumbers(List<Deployment> pending, Func<int> save)
    {
        try
        {
            foreach (var deployment in pending)
            {
                using var command = CreateAllocationCommand(deployment, out var utcDate);
                deployment.Number = FormatAllocated(command.ExecuteScalar(), deployment.OrganizationId, utcDate);
            }

            return save();
        }
        catch
        {
            ResetNumbers(pending);
            throw;
        }
    }

    private async Task<int> SaveWithNumbersAsync(List<Deployment> pending, Func<Task<int>> save, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var deployment in pending)
            {
                await using var command = CreateAllocationCommand(deployment, out var utcDate);
                deployment.Number = FormatAllocated(await command.ExecuteScalarAsync(cancellationToken), deployment.OrganizationId, utcDate);
            }

            return await save();
        }
        catch
        {
            ResetNumbers(pending);
            throw;
        }
    }

    private DbCommand CreateAllocationCommand(Deployment deployment, out DateOnly utcDate)
    {
        // The number's date and CreatedAt come from the same server instant.
        utcDate = DateOnly.FromDateTime(deployment.CreatedAt.UtcDateTime);
        var command = Database.GetDbConnection().CreateCommand();
        command.Transaction = Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = AllocateNumberSql;
        if (Database.GetCommandTimeout() is { } timeout) command.CommandTimeout = timeout;
        AddParameter(command, "@organization_id", deployment.OrganizationId);
        AddParameter(command, "@utc_date", utcDate);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string FormatAllocated(object? value, string organizationId, DateOnly utcDate) =>
        value is null or DBNull
            ? throw new DeploymentNumberExhaustedException(organizationId, utcDate)
            : DeploymentNumber.Format(utcDate, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));

    private static void ResetNumbers(List<Deployment> pending)
    {
        foreach (var deployment in pending) deployment.Number = string.Empty;
    }
}
