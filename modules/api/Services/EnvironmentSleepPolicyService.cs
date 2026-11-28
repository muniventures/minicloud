using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Selfrun.Common.Auth;
using Selfrun.Common.Contracts;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Dns;
using Selfrun.Common.Providers.Extensibility;
using Selfrun.Common.Services;

namespace Selfrun.Api.Services;

public sealed class EnvironmentSleepException(string code, string message, int statusCode = StatusCodes.Status409Conflict)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class EnvironmentSleepPolicyService(
    SelfrunDbContext db,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ProviderAccountClientFactory providerClients,
    ICloudflareDnsClient dns,
    ICustomDomainDnsResolver customDomainDns,
    IVpsProviderRegistry providers,
    IOptions<ManagedDomainOptions> managedDomains,
    IOptions<CloudflareOptions> cloudflare)
{
    public const int DefaultSleepAfterMinutes = 120;
    public const int MinimumSleepAfterMinutes = 30;
    public const int MaximumSleepAfterMinutes = 43_200;
    public const int DefaultKeepAwakeMinutes = 240;

    private readonly ManagedDomainOptions _managedDomains = managedDomains.Value;
    private readonly CloudflareOptions _cloudflare = cloudflare.Value;

    public async Task<EnvironmentSleepResponse> BuildResponseAsync(string environmentId, CancellationToken ct)
    {
        var env = await LoadEnvironmentAsync(environmentId, ct);
        var settings = await db.EnvironmentSleepSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EnvironmentId == environmentId, ct);
        var domains = await ActiveDomainsAsync(environmentId, ct);
        var retainedAddress = await db.EnvironmentRetainedAddresses.AsNoTracking()
            .Where(x => x.EnvironmentId == environmentId && x.State != EnvironmentRetainedAddressStates.Released)
            .SingleOrDefaultAsync(ct);
        var mode = GetAddressMode(domains, retainedAddress);
        var eligibility = await GetEligibilityAsync(env, mode, checkSleepReadiness: true, ct);
        var state = settings?.PowerState ?? EnvironmentPowerStates.Awake;
        var stateChangedAt = settings?.StateChangedAt ?? env.CreatedAt;
        var lastActivityAt = settings?.LastActivityAt;
        var sleepsAt = settings?.SleepAfterMinutes is { } minutes && lastActivityAt is { } activity
            ? Max(activity.AddMinutes(minutes), settings.KeepAwakeUntil)
            : null;

        var operations = await db.EnvironmentPowerOperations.AsNoTracking()
            .Where(x => x.EnvironmentId == environmentId)
            .ToListAsync(ct);
        var operation = operations.OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
        var snapshots = await db.EnvironmentSnapshots.AsNoTracking()
            .Where(x => x.EnvironmentId == environmentId &&
                (x.Status == EnvironmentSnapshotStatuses.Current || x.Status == EnvironmentSnapshotStatuses.Deleting))
            .ToListAsync(ct);
        var snapshot = snapshots.OrderByDescending(x => x.CreatedAt).FirstOrDefault();

        return new EnvironmentSleepResponse(
            new EnvironmentSleepPolicyResponse(settings?.SleepAfterMinutes, settings?.HttpTrafficIsActivity ?? true, settings?.Revision ?? 0),
            state,
            stateChangedAt,
            lastActivityAt,
            settings?.LastHttpRequestAt,
            settings?.KeepAwakeUntil,
            sleepsAt,
            eligibility.IsEligible,
            eligibility.Reason,
            mode,
            retainedAddress is null ? null : new EnvironmentRetainedAddressResponse(
                retainedAddress.Ipv4, retainedAddress.State, retainedAddress.PreviousIpv4),
            snapshot is null ? null : new EnvironmentSnapshotResponse(
                snapshot.CreatedAt, snapshot.SizeGb, snapshot.Status == EnvironmentSnapshotStatuses.Deleting),
            operation is null ? null : ToResponse(operation),
            settings?.LastErrorCode is null ? null : new EnvironmentSleepErrorResponse(
                settings.LastErrorCode, settings.LastErrorMessage ?? string.Empty));
    }

    public async Task ApplyPolicyPatchAsync(
        SelfrunApp env,
        EnvironmentSleepPatchRequest? request,
        CancellationToken ct)
    {
        if (request is null)
            return;

        if (request.Policy is not { } patch)
            throw InvalidPolicy("Sleep policy must be provided.");
        if (patch.Revision is null)
            throw InvalidPolicy("Sleep policy revision is required.");

        var settings = await db.EnvironmentSleepSettings
            .SingleOrDefaultAsync(x => x.EnvironmentId == env.Id, ct);
        var revision = settings?.Revision ?? 0;
        if (patch.Revision != revision)
            throw new EnvironmentSleepException("sleep_policy_revision_conflict",
                "Sleep policy changed since it was read. Refresh the environment and retry.");

        int? requestedMinutes = settings?.SleepAfterMinutes;
        if (patch.SleepAfterMinutes.ValueKind != JsonValueKind.Undefined)
        {
            if (patch.SleepAfterMinutes.ValueKind == JsonValueKind.Null)
            {
                requestedMinutes = null;
            }
            else if (patch.SleepAfterMinutes.ValueKind == JsonValueKind.Number &&
                patch.SleepAfterMinutes.TryGetInt32(out var minutes))
            {
                requestedMinutes = minutes;
            }
            else
            {
                throw InvalidPolicy("sleepAfterMinutes must be null or a whole number of minutes.");
            }
        }

        if (patch.Enabled is false)
            requestedMinutes = null;
        else if (patch.Enabled is true && requestedMinutes is null &&
            patch.SleepAfterMinutes.ValueKind == JsonValueKind.Undefined)
            requestedMinutes = settings?.SleepAfterMinutes ?? DefaultSleepAfterMinutes;

        if (requestedMinutes is not null &&
            requestedMinutes is < MinimumSleepAfterMinutes or > MaximumSleepAfterMinutes)
            throw InvalidPolicy($"sleepAfterMinutes must be between {MinimumSleepAfterMinutes} and {MaximumSleepAfterMinutes}.");

        var httpTrafficIsActivity = patch.HttpTrafficIsActivity ?? settings?.HttpTrafficIsActivity ?? true;
        var changed = settings is null
            ? requestedMinutes is not null || !httpTrafficIsActivity
            : settings.SleepAfterMinutes != requestedMinutes ||
                settings.HttpTrafficIsActivity != httpTrafficIsActivity;
        if (!changed)
            return;

        var domains = await ActiveDomainsAsync(env.Id, ct);
        var retainedAddress = await db.EnvironmentRetainedAddresses
            .Where(x => x.EnvironmentId == env.Id && x.State != EnvironmentRetainedAddressStates.Released)
            .SingleOrDefaultAsync(ct);
        var addressMode = GetAddressMode(domains, retainedAddress);

        if (requestedMinutes is not null)
        {
            var eligibility = await GetEligibilityAsync(env, addressMode, checkSleepReadiness: false, ct);
            if (!eligibility.IsEligible)
                throw new EnvironmentSleepException(eligibility.Reason!, EligibilityMessage(eligibility.Reason!));

            if (addressMode == EnvironmentAddressModes.Retained)
                await EnsureRetainedAddressAsync(env, domains, ct);
        }

        var now = timeProvider.GetUtcNow();
        if (settings is null)
        {
            settings = new EnvironmentSleepSettings
            {
                EnvironmentId = env.Id,
                Revision = 0,
                PowerState = EnvironmentPowerStates.Awake,
                StateChangedAt = now
            };
            db.EnvironmentSleepSettings.Add(settings);
        }

        settings.SleepAfterMinutes = requestedMinutes;
        settings.HttpTrafficIsActivity = httpTrafficIsActivity;
        settings.AddressMode = addressMode;
        settings.Revision++;
        settings.LastActivityAt = now;
        settings.StateChangedAt = now;
        settings.LastErrorCode = null;
        settings.LastErrorMessage = null;
    }

    public async Task EnsureRetainedAddressForCustomDomainAsync(SelfrunApp env, CancellationToken ct)
    {
        var settings = await db.EnvironmentSleepSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EnvironmentId == env.Id, ct);
        if (settings?.SleepAfterMinutes is null)
            return;

        var domains = await ActiveDomainsAsync(env.Id, ct);
        await EnsureRetainedAddressAsync(env, domains, ct);
        settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == env.Id, ct);
        settings.AddressMode = EnvironmentAddressModes.Retained;
        settings.Revision++;
        settings.LastActivityAt = timeProvider.GetUtcNow();
    }

    public async Task EnsureAwakeAsync(string environmentId, CancellationToken ct)
    {
        var state = await db.EnvironmentSleepSettings.AsNoTracking()
            .Where(x => x.EnvironmentId == environmentId)
            .Select(x => x.PowerState)
            .SingleOrDefaultAsync(ct) ?? EnvironmentPowerStates.Awake;
        if (state == EnvironmentPowerStates.Asleep)
            throw new EnvironmentSleepException("environment_asleep",
                "This environment is asleep. Wake it before running this operation.");
        if (state == EnvironmentPowerStates.Waking)
            throw new EnvironmentSleepException("environment_waking",
                "This environment is waking. Retry the operation after it is awake.");
        if (state == EnvironmentPowerStates.Sleeping)
            throw new EnvironmentSleepException("environment_power_busy",
                "This environment is entering sleep. Retry the operation after it is awake.");
    }

    public async Task<EnvironmentPowerOperation?> RequestSleepAsync(
        SelfrunApp env, ActorIdentity actor, CancellationToken ct)
    {
        env = await LoadEnvironmentAsync(env.Id, ct);
        var settings = await db.EnvironmentSleepSettings.SingleOrDefaultAsync(x => x.EnvironmentId == env.Id, ct);
        if (settings?.PowerState == EnvironmentPowerStates.Asleep)
            throw new EnvironmentSleepException("environment_asleep", "This environment is already asleep. Wake it before requesting sleep.");
        if (settings?.PowerState is EnvironmentPowerStates.Sleeping or EnvironmentPowerStates.Waking)
            throw new EnvironmentSleepException("environment_power_busy", "A power operation is already in progress for this environment.");

        var domains = await ActiveDomainsAsync(env.Id, ct);
        var retainedAddress = await db.EnvironmentRetainedAddresses.AsNoTracking()
            .Where(x => x.EnvironmentId == env.Id && x.State != EnvironmentRetainedAddressStates.Released)
            .SingleOrDefaultAsync(ct);
        var mode = GetAddressMode(domains, retainedAddress);
        var eligibility = await GetEligibilityAsync(env, mode, checkSleepReadiness: true, ct);
        if (!eligibility.IsEligible)
            throw new EnvironmentSleepException("environment_sleep_ineligible",
                EligibilityMessage(eligibility.Reason ?? "environment_ineligible"));

        var currentServer = await db.ServerInstances.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == env.Id && x.OrganizationId == env.OrganizationId &&
                x.Status == "active", ct);
        if (mode == EnvironmentAddressModes.Retained &&
            Enum.TryParse<CloudProvider>(currentServer?.Provider, true, out var provider) &&
            provider == CloudProvider.DigitalOcean)
        {
            var address = await db.EnvironmentRetainedAddresses.AsNoTracking().SingleOrDefaultAsync(x =>
                x.EnvironmentId == env.Id && x.State == EnvironmentRetainedAddressStates.Assigned, ct);
            foreach (var domain in domains.Where(x => x.Kind == DomainBindingService.KindCustomDomain &&
                x.DnsStatus == DomainBindingService.DnsVerified))
            {
                if (address is null || !await CustomDomainPointsToAsync(domain.Hostname, address.Ipv4, ct))
                    throw new EnvironmentSleepException("custom_domain_not_repointed",
                        $"Point {domain.Hostname} to the retained address {address?.Ipv4 ?? "shown in the environment settings"} before sleeping.");
            }
        }

        var managedDatabaseIds = db.ManagedDatabases
            .Where(x => x.AppId == env.Id)
            .Select(x => x.Id);
        var managedDatabaseServerIds = db.ManagedDatabaseServers
            .Where(x => x.RuntimeAppId == env.Id)
            .Select(x => x.Id);
        if (await HasActivePowerOperationAsync(env.Id, ct) ||
            await db.Deployments.AnyAsync(x => x.AppId == env.Id &&
                (x.OwnershipToken != null || x.Status == DeploymentStatus.Queued ||
                 x.Status == DeploymentStatus.WaitingForBuild || x.Status == DeploymentStatus.Dispatching ||
                 x.Status == DeploymentStatus.Provisioning || x.Status == DeploymentStatus.Deploying ||
                 x.Status == DeploymentStatus.Verifying), ct) ||
            await db.DatabaseOperations.AnyAsync(x =>
                (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling") &&
                ((x.DatabaseId != null && managedDatabaseIds.Contains(x.DatabaseId)) ||
                 (x.ServerId != null && managedDatabaseServerIds.Contains(x.ServerId))), ct))
            throw new EnvironmentSleepException("environment_power_busy",
                "Finish or cancel active environment work before requesting sleep.");

        return AddOperation(env, "sleep", actor);
    }

    public async Task<EnvironmentPowerOperation?> RequestWakeAsync(
        SelfrunApp env, ActorIdentity actor, int? keepAwakeMinutes, CancellationToken ct)
    {
        var keepAwake = keepAwakeMinutes ?? DefaultKeepAwakeMinutes;
        if (keepAwake is < 30 or > 1_440)
            throw InvalidPolicy("keepAwakeMinutes must be between 30 and 1440.");

        var settings = await db.EnvironmentSleepSettings.SingleOrDefaultAsync(x => x.EnvironmentId == env.Id, ct);
        var current = await db.EnvironmentPowerOperations
            .Where(x => x.EnvironmentId == env.Id &&
                x.Status != EnvironmentPowerOperationStatuses.Completed &&
                x.Status != EnvironmentPowerOperationStatuses.Failed &&
                x.Status != EnvironmentPowerOperationStatuses.Cancelled)
            .SingleOrDefaultAsync(ct);
        if (current?.Kind == "wake")
            return current;
        if (settings is null || settings.PowerState == EnvironmentPowerStates.Awake)
        {
            var now = timeProvider.GetUtcNow();
            if (settings is null)
            {
                settings = new EnvironmentSleepSettings
                {
                    EnvironmentId = env.Id,
                    PowerState = EnvironmentPowerStates.Awake,
                    HttpTrafficIsActivity = true,
                    StateChangedAt = now
                };
                db.EnvironmentSleepSettings.Add(settings);
            }
            settings.LastActivityAt = now;
            settings.KeepAwakeUntil = now.AddMinutes(keepAwake);
            return null;
        }

        if (settings?.PowerState is EnvironmentPowerStates.Sleeping or EnvironmentPowerStates.Waking)
        {
            throw new EnvironmentSleepException("environment_power_busy",
                "A sleep operation is still in progress. Retry wake after it completes.");
        }
        if (current is not null)
            throw new EnvironmentSleepException("environment_power_busy",
                "A power operation is already in progress for this environment.");

        if (settings?.PowerState is not (EnvironmentPowerStates.Asleep or EnvironmentPowerStates.SleepFailed or EnvironmentPowerStates.WakeFailed))
            throw new EnvironmentSleepException("environment_wake_failed", "The environment is not in a state that can be woken.");

        var snapshotExists = await db.EnvironmentSnapshots.AnyAsync(x => x.EnvironmentId == env.Id &&
            x.Status == EnvironmentSnapshotStatuses.Current && x.ProviderImageId != null, ct);
        if (!snapshotExists)
            throw new EnvironmentSleepException("snapshot_failed", "The current environment snapshot is unavailable.");

        if (await HasActivePowerOperationAsync(env.Id, ct))
            throw new EnvironmentSleepException("environment_power_busy",
                "A power operation is already in progress for this environment.");

        return AddOperation(env, "wake", actor);
    }

    public static EnvironmentPowerOperationResponse ToResponse(EnvironmentPowerOperation operation) =>
        new(operation.Id, operation.Kind, operation.Status, operation.Step, operation.Trigger);

    private async Task EnsureRetainedAddressAsync(SelfrunApp env, IReadOnlyList<Domain> domains, CancellationToken ct)
    {
        var current = await db.EnvironmentRetainedAddresses
            .SingleOrDefaultAsync(x => x.EnvironmentId == env.Id &&
                x.State != EnvironmentRetainedAddressStates.Released, ct);
        var server = await db.ServerInstances
            .SingleOrDefaultAsync(x => x.AppId == env.Id && x.OrganizationId == env.OrganizationId && x.Status == "active", ct);

        if (current is null)
        {
            if (server is null)
                throw ReservationFailed("An active server is required before Selfrun can reserve a retained address.");
            if (string.IsNullOrWhiteSpace(server.ProviderServerId))
                throw ReservationFailed("The environment's provider server ID is unavailable.");

            var providerAccountId = server.ProviderAccountId ?? env.SelectedPlan?.ProviderAccountId;
            if (providerAccountId is null)
                throw ReservationFailed("The environment has no provider account for address reservation.");

            try
            {
                var provider = await providerClients.CreateAsync(env.OrganizationId, providerAccountId, ct);
                if (provider is not IVpsProviderAddressClient addressClient)
                    throw ReservationFailed("The selected provider does not support retained addresses.");

                var address = await addressClient.RetainServerAddressAsync(server.ProviderServerId, ct);
                current = new EnvironmentRetainedAddress
                {
                    Id = ids.NewId("retained"),
                    EnvironmentId = env.Id,
                    OrganizationId = env.OrganizationId,
                    Provider = server.Provider,
                    ProviderAccountId = providerAccountId,
                    ProviderAddressId = address.Id,
                    Ipv4 = address.Ipv4Address,
                    Region = address.Region ?? server.Region,
                    Datacenter = address.Datacenter,
                    PreviousIpv4 = !string.Equals(server.IpAddress, address.Ipv4Address, StringComparison.Ordinal)
                        ? server.IpAddress : null,
                    State = EnvironmentRetainedAddressStates.Assigned,
                    CreatedAt = timeProvider.GetUtcNow()
                };
                db.EnvironmentRetainedAddresses.Add(current);
                server.IpAddress = address.Ipv4Address;
            }
            catch (SecretStoreUnavailableException)
            {
                throw new EnvironmentSleepException("address_reservation_failed",
                    "Provider credentials are temporarily unavailable; the retained address was not reserved.",
                    StatusCodes.Status503ServiceUnavailable);
            }
            catch (ProviderCatalogException ex)
            {
                throw new EnvironmentSleepException("address_reservation_failed", ex.Message,
                    ex.Code == "provider_disabled" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
            }
            catch (HttpRequestException ex)
            {
                throw new EnvironmentSleepException("address_reservation_failed",
                    $"The provider could not reserve an address: {ex.Message}");
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new EnvironmentSleepException("address_reservation_failed",
                    $"The provider address request timed out: {ex.Message}", StatusCodes.Status503ServiceUnavailable);
            }
            catch (InvalidOperationException ex)
            {
                throw new EnvironmentSleepException("address_reservation_failed",
                    $"The provider could not reserve an address: {ex.Message}");
            }
        }

        if (server is not null)
        {
            server.IpAddress = current.Ipv4;
            var managed = domains.Where(x => _managedDomains.ManageDnsRecords && _managedDomains.Contains(x.Hostname)).ToArray();
            if (managed.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(_cloudflare.ApiToken) || string.IsNullOrWhiteSpace(_cloudflare.ZoneId))
                    throw ReservationFailed("Managed DNS is not configured, so managed hostnames could not be repointed.");
                try
                {
                    foreach (var domain in managed)
                    {
                        await dns.UpsertDnsRecordAsync(domain.Hostname, current.Ipv4, ct);
                        await dns.WaitForPropagationAsync(domain.Hostname, current.Ipv4, TimeSpan.FromMinutes(2), ct);
                    }
                }
                catch (HttpRequestException ex)
                {
                    throw ReservationFailed($"Managed DNS could not be repointed to the retained address: {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    throw ReservationFailed($"Managed DNS could not be repointed to the retained address: {ex.Message}");
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new EnvironmentSleepException("address_reservation_failed",
                        $"Managed DNS did not propagate the retained address in time: {ex.Message}",
                        StatusCodes.Status503ServiceUnavailable);
                }
            }

            foreach (var domain in domains)
            {
                domain.TargetIp = current.Ipv4;
                domain.UpdatedAt = timeProvider.GetUtcNow();
            }
        }
    }

    private async Task<Eligibility> GetEligibilityAsync(
        SelfrunApp env,
        string addressMode,
        bool checkSleepReadiness,
        CancellationToken ct)
    {
        if (env.EnvironmentKind == EnvironmentKinds.Internal)
            return Eligibility.No("environment_internal");
        if (env.DeletionStatus is not null || env.Application?.DeletionStatus is not null)
            return Eligibility.No("app_deleting");
        if (env.SelectedPlan is null)
            return Eligibility.No("provider_plan_missing");
        var providerAccount = await db.ProviderAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == env.SelectedPlan.ProviderAccountId &&
                x.OrganizationId == env.OrganizationId, ct);
        if (providerAccount is null)
            return Eligibility.No("provider_plan_missing");
        if (!providers.TryGet(providerAccount.ProviderType, out var registration))
            return Eligibility.No("provider_type_unsupported");
        if (!registration.Descriptor.Capabilities.SupportsSnapshots)
            return Eligibility.No("provider_snapshots_unsupported");
        try
        {
            ProviderQualificationGate.EnsureQualified(providerAccount.ProviderType);
        }
        catch (ProviderCatalogException ex)
        {
            return Eligibility.No(ex.Code);
        }
        if (!providerAccount.Enabled)
            return Eligibility.No("provider_disabled");
        if (await db.ManagedDatabaseServers.AnyAsync(x => x.RuntimeAppId == env.Id, ct))
            return Eligibility.No("managed_database_host");

        if (!checkSleepReadiness)
            return Eligibility.Yes;

        var activeServer = await db.ServerInstances.AnyAsync(x => x.AppId == env.Id &&
            x.OrganizationId == env.OrganizationId && x.Status == "active", ct);
        if (!activeServer)
            return Eligibility.No("environment_server_unavailable");
        if (addressMode == EnvironmentAddressModes.Retained)
        {
            var address = await db.EnvironmentRetainedAddresses.AsNoTracking().SingleOrDefaultAsync(x =>
                x.EnvironmentId == env.Id && x.State != EnvironmentRetainedAddressStates.Released, ct);
            if (address is null || address.State != EnvironmentRetainedAddressStates.Assigned)
                return Eligibility.No("retained_address_unavailable");
        }
        if (addressMode == EnvironmentAddressModes.Dynamic &&
            (string.IsNullOrWhiteSpace(_cloudflare.ApiToken) || string.IsNullOrWhiteSpace(_cloudflare.ZoneId)))
        {
            var hostnames = await db.Domains.AsNoTracking()
                .Where(x => x.AppId == env.Id && x.Status != DomainBindingService.StatusDisabled)
                .Select(x => x.Hostname)
                .ToListAsync(ct);
            var managedDomainExists = hostnames.Any(_managedDomains.Contains);
            if (managedDomainExists)
                return Eligibility.No("managed_dns_unavailable");
        }
        return Eligibility.Yes;
    }

    private async Task<SelfrunApp> LoadEnvironmentAsync(string environmentId, CancellationToken ct) =>
        await db.Apps.Include(x => x.SelectedPlan).ThenInclude(x => x!.ProviderAccount)
            .Include(x => x.Application)
            .SingleAsync(x => x.Id == environmentId, ct);

    private Task<List<Domain>> ActiveDomainsAsync(string environmentId, CancellationToken ct) =>
        db.Domains.Where(x => x.AppId == environmentId && x.Status != DomainBindingService.StatusDisabled)
            .OrderBy(x => x.Hostname).ToListAsync(ct);

    private string GetAddressMode(IReadOnlyList<Domain> domains, EnvironmentRetainedAddress? retainedAddress)
    {
        if (retainedAddress is not null)
            return EnvironmentAddressModes.Retained;
        return !_managedDomains.ManageDnsRecords || domains.Any(x =>
            x.Kind == DomainBindingService.KindCustomDomain || !_managedDomains.Contains(x.Hostname))
            ? EnvironmentAddressModes.Retained
            : EnvironmentAddressModes.Dynamic;
    }

    private async Task<bool> HasActivePowerOperationAsync(string environmentId, CancellationToken ct) =>
        await db.EnvironmentPowerOperations.AnyAsync(x => x.EnvironmentId == environmentId &&
            x.Status != EnvironmentPowerOperationStatuses.Completed &&
            x.Status != EnvironmentPowerOperationStatuses.Failed &&
            x.Status != EnvironmentPowerOperationStatuses.Cancelled, ct);

    private async Task<bool> CustomDomainPointsToAsync(string hostname, string expectedIpv4, CancellationToken ct)
    {
        try
        {
            var aRecords = await customDomainDns.QueryAsync(hostname, 1, ct);
            var aaaaRecords = await customDomainDns.QueryAsync(hostname, 28, ct);
            if (!IPAddress.TryParse(expectedIpv4, out var expectedAddress))
                return false;
            return aRecords.Count > 0 && aRecords.All(record =>
                    IPAddress.TryParse(record, out var address) &&
                    address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    address.Equals(expectedAddress)) &&
                aaaaRecords.Count == 0;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private EnvironmentPowerOperation AddOperation(SelfrunApp env, string kind, ActorIdentity actor)
    {
        var now = timeProvider.GetUtcNow();
        var operation = new EnvironmentPowerOperation
        {
            Id = ids.NewId("power"),
            OrganizationId = env.OrganizationId,
            EnvironmentId = env.Id,
            Kind = kind,
            Trigger = actor.IsApiKey ? "manual_cli" : "manual_dashboard",
            ActorJson = JsonSerializer.Serialize(new { actor.UserId, actor.ApiKeyId, actor.OrganizationId }),
            Status = EnvironmentPowerOperationStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.EnvironmentPowerOperations.Add(operation);
        return operation;
    }

    private static DateTimeOffset? Max(DateTimeOffset first, DateTimeOffset? second) =>
        second is { } value && value > first ? value : first;

    private static EnvironmentSleepException InvalidPolicy(string message) =>
        new("sleep_policy_invalid", message, StatusCodes.Status400BadRequest);

    private static EnvironmentSleepException ReservationFailed(string message) =>
        new("address_reservation_failed", message);

    private static string EligibilityMessage(string reason) => reason switch
    {
        "provider_plan_missing" => "Select a supported provider plan before enabling environment sleep.",
        "provider_disabled" => "Enable the environment's provider account before using environment sleep.",
        "provider_snapshots_unsupported" => "The selected provider does not support snapshots.",
        "provider_not_qualified" => "The selected provider is not qualified for environment sleep.",
        "managed_database_host" => "This environment hosts a managed database and cannot sleep.",
        "retained_address_unavailable" => "Reserve and assign the retained address before sleeping this environment.",
        "managed_dns_unavailable" => "Managed DNS is unavailable, so the environment cannot safely sleep.",
        "environment_server_unavailable" => "The environment has no active server to sleep.",
        "app_deleting" => "The environment or application is being deleted.",
        "environment_internal" => "Internal environments cannot use sleep operations.",
        _ => "The environment is not eligible for this power operation."
    };

    private sealed record Eligibility(bool IsEligible, string? Reason)
    {
        public static Eligibility Yes { get; } = new(true, null);
        public static Eligibility No(string reason) => new(false, reason);
    }
}
