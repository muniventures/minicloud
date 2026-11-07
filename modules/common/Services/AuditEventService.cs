using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

/// <summary>
/// Minimal shared audit append service (T11). No tokens, log bodies, or secrets are stored.
/// </summary>
public sealed class AuditEventService(
    MinicloudDbContext db,
    IIdGenerator ids,
    TimeProvider timeProvider)
{
    public async Task AppendAsync(
        string organizationId,
        string? appId,
        string actor,
        string action,
        string? details,
        CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = ids.NewId("audit"),
            OrganizationId = organizationId,
            AppId = appId,
            Actor = actor,
            Action = action,
            Details = details,
            CreatedAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string ActorFor(ActorIdentity principal) => principal.Describe();
}
