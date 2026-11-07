using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;

namespace Minicloud.Worker.Pipeline.Steps;

/// <summary>
/// Step 4: verify the public HTTPS health endpoints, then finalize the
/// deployment as succeeded. Failure code: <c>health_check_failed</c>.
/// </summary>
public sealed class HealthCheckFinalizeStep : IPipelineStep
{
    public const string FailureCode = "health_check_failed";

    public string Name => "health-finalize";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HttpClient _httpClient;
    private readonly DeploymentPipelineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HealthCheckFinalizeStep>? _logger;

    public HealthCheckFinalizeStep(
        IServiceScopeFactory scopeFactory,
        HttpClient httpClient,
        IOptions<DeploymentPipelineOptions> options,
        TimeProvider timeProvider,
        ILogger<HealthCheckFinalizeStep>? logger = null)
        : this(scopeFactory, httpClient, options.Value, timeProvider, logger)
    {
    }

    public HealthCheckFinalizeStep(
        IServiceScopeFactory scopeFactory,
        HttpClient httpClient,
        DeploymentPipelineOptions options,
        TimeProvider timeProvider,
        ILogger<HealthCheckFinalizeStep>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _httpClient = httpClient;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ExecuteAsync(PipelineStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var startedAt = _timeProvider.GetUtcNow();
        var timer = Stopwatch.StartNew();

        var targets = await ResolveHealthTargetsAsync(context, cancellationToken);
        if (targets.Count == 0)
        {
            _logger?.LogInformation("Deployment {DeploymentId} has no public health targets; finalizing", context.DeploymentId);
            await MarkSucceededAsync(context, startedAt, timer.Elapsed, cancellationToken);
            return;
        }

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(_options.MaxParallelNetworkOperations, targets.Count),
            },
            async (target, ct) =>
            {
                var outcome = await ProbeHealthAsync(target, ct);
                if (!outcome.Healthy)
                {
                    throw new PipelineStepException(FailureCode,
                        $"Health check failed for {target.Url} (timeout {_options.HealthCheckTimeout.TotalSeconds:F0}s, last status {outcome.LastStatus}, excerpt: {outcome.Excerpt}).");
                }

                _logger?.LogInformation("Health check passed for {Url} (status {Status})", target.Url, outcome.LastStatus);
            });

        await MarkSucceededAsync(context, startedAt, timer.Elapsed, cancellationToken);
    }

    private async Task<List<HealthTarget>> ResolveHealthTargetsAsync(
        PipelineStepContext context, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var rawServices = await db.DeploymentServices
            .AsNoTracking()
            .Where(s => s.DeploymentId == context.DeploymentId && s.Public)
            .ToListAsync(cancellationToken);

        var services = rawServices
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var targets = new List<HealthTarget>();
        foreach (var service in services)
        {
            var hosts = context.ServiceHostnames.GetValueOrDefault(service.Name)
                ?? (context.DnsHostnamesResolved ? [] : new[] { ResolveGeneratedServiceHost(context, service.Name) ?? context.PrimaryHostname });
            var healthPath = string.IsNullOrWhiteSpace(service.HealthPath) ? "/health" : service.HealthPath;
            if (!healthPath.StartsWith('/'))
            {
                healthPath = "/" + healthPath;
            }

            foreach (var host in hosts.Where(host => !string.IsNullOrWhiteSpace(host)))
            {
                targets.Add(new HealthTarget($"https://{host}{healthPath}"));
            }
        }

        return targets.DistinctBy(t => t.Url, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ResolveGeneratedServiceHost(PipelineStepContext context, string serviceName)
    {
        var prefix = $"{context.OrganizationSlug}-{context.AppSlug}-{serviceName}".ToLowerInvariant();
        return context.Hostnames.FirstOrDefault(h => h.StartsWith(prefix, StringComparison.Ordinal))
            ?? context.PrimaryHostname;
    }

    private async Task<HealthOutcome> ProbeHealthAsync(HealthTarget target, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.HealthCheckTimeout;
        string lastStatus = "none";
        string excerpt = string.Empty;

        while (DateTimeOffset.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var remainingBudget = deadline - DateTimeOffset.UtcNow;
                requestCts.CancelAfter(remainingBudget < TimeSpan.FromSeconds(20)
                    ? remainingBudget
                    : TimeSpan.FromSeconds(20));
                using var response = await _httpClient.GetAsync(target.Url, HttpCompletionOption.ResponseHeadersRead, requestCts.Token);
                lastStatus = ((int)response.StatusCode).ToString();
                if ((int)response.StatusCode is >= 200 and < 500)
                {
                    return new HealthOutcome(true, lastStatus, string.Empty);
                }

                excerpt = await SafeReadExcerptAsync(response, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastStatus = "timeout";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastStatus = "connection_error";
                excerpt = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var wait = remaining < _options.HealthCheckInterval ? remaining : _options.HealthCheckInterval;
            await Task.Delay(wait, cancellationToken);
        }

        return new HealthOutcome(false, lastStatus, excerpt);
    }

    private static async Task<string> SafeReadExcerptAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Length > 200 ? body[..200] : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    private Task MarkSucceededAsync(PipelineStepContext context, DateTimeOffset startedAt, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            var deployment = await DeploymentMutation.LockAsync(db, context.DeploymentId, ct);
            if (deployment is not null && deployment.OwnershipToken != context.OwnershipToken) { deployment = null; }
            if (deployment is null)
            {
                throw new PipelineStepException(FailureCode, "Deployment ownership is no longer current.");
            }

            if (!string.Equals(deployment.OrganizationId, context.OrganizationId, StringComparison.Ordinal))
            {
                throw new PipelineStepException(FailureCode, "Deployment organization scope mismatch.");
            }

            if (deployment.CancellationRequestedAt is not null) { throw new OperationCanceledException("Deployment cancellation requested."); }
            var websiteUrl = string.IsNullOrWhiteSpace(context.PrimaryHostname)
                ? deployment.WebsiteUrl
                : $"https://{context.PrimaryHostname}";

            deployment.Status = DeploymentStatus.Verifying;
            deployment.WebsiteUrl = websiteUrl;
            deployment.UpdatedAt = now;
            PipelineDb.Touch(deployment, now);

            // Finalize: verifying -> succeeded with completion metadata.
            deployment.Status = DeploymentStatus.Succeeded;
            deployment.WebsiteUrl = websiteUrl;
            deployment.FailureCode = null;
            deployment.FailureMessage = null;
            deployment.CompletedAt = now;
            deployment.UpdatedAt = now;
            deployment.WorkerId = null;
            deployment.OwnershipToken = null;
            deployment.OwnershipAcquiredAt = null;
            db.DeploymentEvents.Add(new DeploymentEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                DeploymentId = deployment.Id,
                Level = EventLevel.Info,
                Message = $"Deployment phase 'health-finalize' succeeded in {elapsed.TotalMilliseconds:F0} ms.",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    phase = "health-finalize",
                    started_at_utc = startedAt,
                    completed_at_utc = now,
                    duration_ms = Math.Round(elapsed.TotalMilliseconds, 3),
                    outcome = "succeeded",
                }),
                CreatedAt = now,
            });
            PipelineDb.AddEvent(db, deployment.Id, EventLevel.Info,
                $"Deployment succeeded. Public URL: {websiteUrl}.", now);

            await Task.CompletedTask;
        }, cancellationToken);
    }

    private sealed record HealthTarget(string Url);

    private sealed record HealthOutcome(bool Healthy, string LastStatus, string Excerpt);
}
