using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Pipeline.Steps;

public sealed class RuntimeAgentOptions
{
    public const string SectionName = "Worker:RuntimeAgent";
    public string ApiBaseUrl { get; set; } = "https://api.cloud.muni.dev";
    public string BundlePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "runtime-agent");
}

public sealed record RuntimeAgentPackage(string TokenId, object Configuration, string Token,
    IReadOnlyList<SshDeploymentArtifactUpload> Uploads)
{
    public string Action { get; init; } = "install";
    public string Reason { get; init; } = "unverified_host";
    public string? Verification { get; init; }
    public string? FinalVerification { get; init; }
    public bool CreatesCredential { get; init; } = true;
    public DateTimeOffset? ContactAfter { get; init; }
}

public interface IRuntimeAgentDeployment
{
    Task<RuntimeAgentPackage> PrepareAsync(PipelineStepContext context, CancellationToken cancellationToken);
    Task<RuntimeAgentPackage> PrepareAsync(PipelineStepContext context, Func<string, Task<int>> probe, CancellationToken cancellationToken) => PrepareAsync(context, cancellationToken);
    Task CompleteAsync(PipelineStepContext context, RuntimeAgentPackage package, CancellationToken cancellationToken);
}

public sealed class RuntimeAgentDeployment(
    IServiceScopeFactory scopeFactory, IOptions<RuntimeAgentOptions> options, TimeProvider timeProvider) : IRuntimeAgentDeployment
{
    private static readonly string[] BundleFiles =
        ["minicloud-agent", "minicloud-agent.service", "apply-minicloud-routing.py", "install-minicloud-agent.sh"];

    public Task<RuntimeAgentPackage> PrepareAsync(PipelineStepContext context, CancellationToken cancellationToken) =>
        PrepareCoreAsync(context, null, cancellationToken);

    public Task<RuntimeAgentPackage> PrepareAsync(PipelineStepContext context, Func<string, Task<int>> probe, CancellationToken cancellationToken) =>
        PrepareCoreAsync(context, probe, cancellationToken);

    private async Task<RuntimeAgentPackage> PrepareCoreAsync(PipelineStepContext context, Func<string, Task<int>>? probe, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(options.Value.ApiBaseUrl, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(apiUri.UserInfo) || !string.IsNullOrEmpty(apiUri.Query) || !string.IsNullOrEmpty(apiUri.Fragment))
        {
            throw new PipelineStepException("runtime_agent_install_failed", "Runtime agent requires a public HTTPS control API URL.");
        }

        if (probe is null)
        {
            throw new PipelineStepException("runtime_agent_install_failed",
                "Runtime agent deployment requires a remote target architecture probe.");
        }

        RuntimeAgentPackage? package = null;
        await PipelineDb.ExecuteInTransactionAsync(scopeFactory, async (db, ct) =>
        {
            if (!await db.Deployments.AnyAsync(d => d.Id == context.DeploymentId && d.AppId == context.AppId &&
                d.OrganizationId == context.OrganizationId && d.OwnershipToken == context.OwnershipToken &&
                d.Status == DeploymentStatus.Deploying, ct))
            {
                throw new PipelineStepException("runtime_agent_install_failed", "Deployment ownership is no longer current.");
            }
            var server = await db.ServerInstances.SingleOrDefaultAsync(s => s.Id == context.ServerInstanceId &&
                s.AppId == context.AppId && s.OrganizationId == context.OrganizationId, ct);
            if (server is null || server.ProviderAccountId != context.ProviderAccountId || string.IsNullOrWhiteSpace(server.ProviderServerId) || server.IpAddress != context.ServerIp)
            {
                throw new PipelineStepException("runtime_agent_install_failed", "Runtime server identity does not match the deployment target.");
            }
            if (await db.ServerInstances.AnyAsync(s => s.ProviderAccountId == server.ProviderAccountId && s.Provider.ToLower() == server.Provider.ToLower() && s.ProviderServerId == server.ProviderServerId &&
                (s.AppId != context.AppId || s.OrganizationId != context.OrganizationId), ct) ||
                await db.RuntimeServers.AnyAsync(s => s.ProviderAccountId == server.ProviderAccountId && s.Provider.ToLower() == server.Provider.ToLower() && s.ProviderServerId == server.ProviderServerId &&
                (s.AppId != context.AppId || s.OrganizationId != context.OrganizationId), ct))
            {
                throw new PipelineStepException("runtime_agent_install_failed", "Runtime server belongs to another app.");
            }
            var binaryPath = await RuntimeAgentBundle.SelectBinaryAsync(options.Value.BundlePath, probe);
            var uploads = BundleFiles.Select(name =>
            {
                var path = name == "minicloud-agent" ? binaryPath : Path.Combine(options.Value.BundlePath, name);
                if (!File.Exists(path))
                {
                    throw new PipelineStepException("runtime_agent_install_failed", $"Runtime agent bundle is missing {name}.");
                }
                return new SshDeploymentArtifactUpload("runtime-agent", name,
                    _ => Task.FromResult<Stream>(File.OpenRead(path)), new FileInfo(path).Length);
            }).ToArray();
            var fingerprints = RuntimeAgentVerification.Fingerprints(options.Value.BundlePath, binaryPath);
            var now = timeProvider.GetUtcNow();
            var runtime = await db.RuntimeServers.SingleOrDefaultAsync(s => s.AppId == context.AppId &&
                s.ProviderAccountId == server.ProviderAccountId && s.Provider.ToLower() == server.Provider.ToLower() && s.ProviderServerId == server.ProviderServerId, ct);
            if (runtime is null)
            {
                runtime = new RuntimeServer
                {
                    Id = $"rtsrv_{Guid.NewGuid():N}",
                    OrganizationId = context.OrganizationId,
                    AppId = context.AppId,
                    ProviderAccountId = server.ProviderAccountId,
                    Provider = server.Provider,
                    ProviderServerId = server.ProviderServerId,
                    CreatedAt = now
                };
                db.RuntimeServers.Add(runtime);
            }
            runtime.Hostname = context.ServerIp;
            runtime.UpdatedAt = now;
            var configuration = new
            {
                serverId = runtime.Id,
                organizationId = context.OrganizationId,
                appId = context.AppId,
                apiBaseUrl = apiUri.AbsoluteUri.TrimEnd('/'),
                composeProject = $"minicloud-{context.AppSlug}",
                composeFile = $"/opt/minicloud/apps/{context.AppSlug}/app.compose.json",
                tokenPath = "/opt/minicloud/agent/runtime-token",
                statePath = "/var/lib/minicloud-agent/state.json"
            };
            var candidates = await db.RuntimeTokens.Where(t => t.RuntimeServerId == runtime.Id && t.RevokedAt == null).ToListAsync(ct);
            var previous = candidates.OrderByDescending(t => t.LastUsedAt).FirstOrDefault();
            var verification = RuntimeAgentVerification.Encode(configuration, fingerprints, previous?.TokenHash);
            var observation = await probe(RuntimeAgentVerification.Command(verification));
            if (observation is not (0 or 10 or 11 or 12 or 13))
            {
                throw new PipelineStepException("runtime_agent_install_failed", "Runtime agent host observation failed.");
            }
            if (!await db.Deployments.AnyAsync(d => d.Id == context.DeploymentId && d.AppId == context.AppId &&
                d.OrganizationId == context.OrganizationId && d.OwnershipToken == context.OwnershipToken && d.Status == DeploymentStatus.Deploying, ct) ||
                !await db.ServerInstances.AnyAsync(s => s.Id == context.ServerInstanceId && s.AppId == context.AppId &&
                    s.OrganizationId == context.OrganizationId && s.ProviderAccountId == server.ProviderAccountId && s.Provider.ToLower() == server.Provider.ToLower() && s.ProviderServerId == server.ProviderServerId && s.IpAddress == context.ServerIp, ct))
            { throw new PipelineStepException("runtime_agent_install_failed", "Deployment target or ownership changed during agent observation."); }
            var fresh = previous is not null && previous.LastUsedAt >= now.AddMinutes(-2) &&
                runtime.LastSeenAt >= now.AddMinutes(-2) && previous.LastUsedAt <= now.AddSeconds(30) && runtime.LastSeenAt <= now.AddSeconds(30) && (previous.ExpiresAt is null || previous.ExpiresAt > now.AddDays(1));
            if (fresh && observation is 0 or 10 or 13)
            {
                package = new RuntimeAgentPackage(previous!.Id, configuration, string.Empty, [])
                {
                    Action = observation == 0 ? "reuse" : observation == 10 ? "configure" : "restart",
                    Reason = observation == 0 ? "verified_unchanged" : observation == 10 ? "configuration_changed" : "verified_files_service_stopped",
                    Verification = verification,
                    FinalVerification = verification,
                    CreatesCredential = false,
                    ContactAfter = observation == 0 ? null : now
                };
                return;
            }
            var tokens = new RuntimeTokenService(db, timeProvider);
            var value = tokens.Generate();
            var token = new RuntimeToken
            {
                Id = $"rttok_{Guid.NewGuid():N}",
                RuntimeServer = runtime,
                TokenPrefix = value.Prefix,
                TokenHash = tokens.Hash(value.Token),
                CreatedAt = now,
                UpdatedAt = now
            };
            db.RuntimeTokens.Add(token);
            package = new RuntimeAgentPackage(token.Id, configuration, value.Token, observation == 12 ? uploads : [])
            {
                Action = observation == 12 ? "install" : "rotate",
                Reason = observation == 12 ? "host_repair_required" : "credential_refresh_required",
                Verification = verification,
                FinalVerification = RuntimeAgentVerification.Encode(configuration, fingerprints, token.TokenHash),
                ContactAfter = now
            };
        }, cancellationToken);
        return package!;
    }

    public async Task CompleteAsync(PipelineStepContext context, RuntimeAgentPackage package, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var now = timeProvider.GetUtcNow();
            if (!await db.Deployments.AnyAsync(d => d.Id == context.DeploymentId && d.AppId == context.AppId &&
                d.OrganizationId == context.OrganizationId && d.OwnershipToken == context.OwnershipToken && d.Status == DeploymentStatus.Deploying, cancellationToken))
            {
                throw new PipelineStepException("runtime_agent_install_failed", "Deployment ownership is no longer current.");
            }
            var contacted = await db.RuntimeTokens.Include(t => t.RuntimeServer).SingleOrDefaultAsync(t => t.Id == package.TokenId, cancellationToken);
            var target = await db.ServerInstances.SingleOrDefaultAsync(t => t.Id == context.ServerInstanceId &&
                t.AppId == context.AppId && t.OrganizationId == context.OrganizationId, cancellationToken);
            if (contacted is not null && target is not null && contacted.RevokedAt is null &&
                (contacted.ExpiresAt is null || contacted.ExpiresAt > now) && contacted.LastUsedAt >= now.AddMinutes(-2) &&
                (package.ContactAfter is null || contacted.LastUsedAt >= package.ContactAfter) &&
                contacted.RuntimeServer.AppId == context.AppId && contacted.RuntimeServer.OrganizationId == context.OrganizationId &&
                contacted.RuntimeServer.ProviderAccountId == target.ProviderAccountId && contacted.RuntimeServer.Provider == target.Provider && contacted.RuntimeServer.ProviderServerId == target.ProviderServerId &&
                target.IpAddress == context.ServerIp && contacted.RuntimeServer.LastSeenAt >= now.AddMinutes(-2))
            {
                break;
            }
            if (timer.Elapsed >= TimeSpan.FromSeconds(45))
            {
                throw new PipelineStepException("runtime_agent_install_failed", "Installed runtime agent did not contact the control API within 45 seconds.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        if (!package.CreatesCredential) { return; }
        await PipelineDb.ExecuteInTransactionAsync(scopeFactory, async (db, ct) =>
        {
            if (!await db.Deployments.AnyAsync(d => d.Id == context.DeploymentId && d.AppId == context.AppId &&
                d.OrganizationId == context.OrganizationId && d.OwnershipToken == context.OwnershipToken, ct))
            {
                throw new PipelineStepException("runtime_agent_install_failed", "Deployment ownership is no longer current.");
            }
            var current = await db.RuntimeTokens.SingleAsync(t => t.Id == package.TokenId, ct);
            var now = timeProvider.GetUtcNow();
            if (current.LastUsedAt is null || current.RevokedAt is not null || (current.ExpiresAt is not null && current.ExpiresAt <= now) ||
                current.LastUsedAt < package.ContactAfter || current.LastUsedAt < now.AddMinutes(-2))
            { throw new PipelineStepException("runtime_agent_install_failed", "Replacement runtime credential is no longer healthy."); }
            var old = await db.RuntimeTokens.Where(t => t.RuntimeServerId == current.RuntimeServerId &&
                t.Id != current.Id && t.RevokedAt == null).ToListAsync(ct);
            foreach (var token in old)
            {
                token.RevokedAt = now;
                token.UpdatedAt = now;
            }
        }, cancellationToken);
    }
}
