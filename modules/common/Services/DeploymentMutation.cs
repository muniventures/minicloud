using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public static class DeploymentMutation
{
    // Call inside a transaction. All lifecycle writers share this row lock.
    public static async Task<Deployment?> LockAsync(MinicloudDbContext db, string id, CancellationToken ct)
    {
        var deployment = db.Database.IsNpgsql()
            ? await db.Deployments.FromSqlInterpolated($"SELECT * FROM deployments WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await db.Deployments.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (deployment is not null) { await db.Entry(deployment).ReloadAsync(ct); }
        return deployment;
    }

    public static bool IsTerminal(DeploymentStatus status) =>
        status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Canceled;
}
