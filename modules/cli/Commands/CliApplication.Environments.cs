// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Selfrun.Cli.Api;
using Selfrun.Cli.Config;
using Selfrun.Common.Contracts;
using CliEnvironmentPowerOperationResponse = Selfrun.Cli.Api.EnvironmentPowerOperationResponse;
using CliEnvironmentSleepPolicyPatch = Selfrun.Cli.Api.EnvironmentSleepPolicyPatch;
using CliEnvironmentSleepResponse = Selfrun.Cli.Api.EnvironmentSleepResponse;

namespace Selfrun.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunEnvironmentsAsync(string[] args, CancellationToken ct)
    {
        var action = args.FirstOrDefault();
        var name = args.ElementAtOrDefault(1);
        var usage = $"Usage: selfrun environments list | create <name> --plan <plan> {SshOptionsUsage} | update <name> [--sleep-after on|off|<duration>] [--traffic-activity on|off] {SshOptionsUsage} | sleep|wake <name> [--no-wait]";
        if (action is not ("list" or "create" or "update" or "rename" or "sleep" or "wake") ||
            (action != "list" && (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))) ||
            (action == "create" && string.IsNullOrWhiteSpace(GetOption(args, "--plan"))) ||
            (action == "rename" && (args.Length < 3 || args[2].StartsWith('-'))))
            throw new CliCommandException(CliExitCodes.ValidationError, usage);
        if (action is "list" or "rename" or "sleep" or "wake" && HasSshAccessOptions(args))
            throw new CliCommandException(CliExitCodes.ValidationError, $"SSH options apply only to 'environments create' and 'environments update'. {usage}");

        var sleepAfterValue = ReadEnvironmentOption(args, "--sleep-after");
        var trafficActivityValue = ReadEnvironmentOption(args, "--traffic-activity");
        var hasSleepUpdate = sleepAfterValue is not null || trafficActivityValue is not null;
        if (hasSleepUpdate && action != "update")
            throw new CliCommandException(CliExitCodes.ValidationError, "--sleep-after and --traffic-activity apply only to 'environments update'.");
        if (HasEnvironmentOption(args, "--keep-awake") && action != "wake")
            throw new CliCommandException(CliExitCodes.ValidationError, "--keep-awake applies only to 'environments wake'.");
        if (args.Contains("--no-wait", StringComparer.Ordinal) && action is not ("sleep" or "wake"))
            throw new CliCommandException(CliExitCodes.ValidationError, "--no-wait applies only to 'environments sleep' and 'environments wake'.");

        var sshAccess = action is "create" or "update" ? ParseSshAccessOptions(args) : null;
        if (action == "update" && sshAccess is null && !hasSleepUpdate)
            throw new CliCommandException(CliExitCodes.ValidationError,
                $"Nothing to update. Supply {SshKeyOption} <key-id>, {SshPublicKeyFileOption} <path>, --sleep-after, or --traffic-activity; omitting both SSH options keeps the current SSH selection.");

        var result = SelfrunConfigLoader.Load(GetOption(args, "--config") ?? SelfrunConfigLoader.DefaultFileName);
        if (!result.IsValid || result.Config is null) { PrintDiagnostics(result.Diagnostics); return CliExitCodes.ValidationError; }
        var application = await GetApplicationContextAsync(result.Config.AppId!, ct);
        if (action == "create")
        {
            var created = await WithSshErrorsAsync(sshAccess, () => _apiClient.CreateEnvironmentAsync(application.Id,
                new(name!, GetOption(args, "--plan")!, GetOption(args, "--database") ?? "none", SshAccess: sshAccess), ct));
            _console.WriteLine($"App: {application.Name} · Environment created: {created.Name} ({created.Plan}), runtime {created.Id}");
            if (sshAccess is not null) WriteSshSelectionNote(sshAccess, created.SshAccess);
        }
        else if (action == "update")
        {
            var selected = FindEnvironment(application, name!);
            if (selected.DeletionStatus is not null)
                throw new CliCommandException(CliExitCodes.ValidationError, $"Environment '{selected.Name}' is being deleted or requires deletion recovery.");

            var sleepPolicy = hasSleepUpdate
                ? (await GetApplicationEnvironmentForSleepAsync(application.Id, selected.Id, ct)).Sleep?.Policy
                    ?? throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, "The Selfrun API did not return this environment's sleep policy.")
                : null;
            SshAccessRequest? sshUpdate = null;
            if (sshAccess is not null)
            {
                var current = await _apiClient.GetEnvironmentAsync(selected.Id, ct);
                if (current.ApplicationId != application.Id)
                    throw new CliCommandException(CliExitCodes.ValidationError, "The selected environment does not belong to this application.");
                var currentAccess = current.SshAccess
                    ?? throw new CliCommandException(CliExitCodes.ValidationError, "This Selfrun server does not report SSH access for environments. Upgrade the API before selecting SSH keys.");
                if (currentAccess.SelectionLocked)
                    throw MapSshApiError(new ApiException(409, "ssh_selection_locked", string.Empty), sshAccess)!;
                sshUpdate = sshAccess with { ExpectedRevision = currentAccess.Revision };
            }

            if (!hasSleepUpdate)
            {
                var updated = await WithSshErrorsAsync(sshUpdate, () => _apiClient.UpdateEnvironmentAsync(application.Id, selected.Id,
                    new(SshAccess: sshUpdate), ct));
                _console.WriteLine($"App: {application.Name} · Environment updated: {updated.Name}");
                WriteSshSelectionNote(sshUpdate!, updated.SshAccess);
            }
            else
            {
                var patch = BuildSleepPolicyPatch(sleepPolicy!.Revision, sleepAfterValue, trafficActivityValue);
                var request = new EnvironmentSleepUpdateRequest(
                    SshAccess: sshUpdate,
                    Sleep: new EnvironmentSleepPatchRequest(patch));
                ApplicationEnvironmentSleepResponse updated;
                try
                {
                    updated = await _apiClient.UpdateEnvironmentSleepAsync(application.Id, selected.Id, request, ct);
                }
                catch (ApiException ex) when (sshUpdate is not null && MapSshApiError(ex, sshUpdate) is { } mapped)
                {
                    throw mapped;
                }
                _console.WriteLine($"App: {application.Name} · Environment updated: {selected.Name}");
                if (sshUpdate is not null) WriteSshSelectionNote(sshUpdate, updated.SshAccess);
                await WriteEnvironmentAddressMessageAsync(selected.Id, updated.Sleep, ct);
            }
        }
        else if (action == "rename")
        {
            var selected = FindEnvironment(application, name!);
            await _apiClient.UpdateEnvironmentAsync(application.Id, selected.Id, new(Name: args[2]), ct);
            _console.WriteLine($"Environment renamed: {name} -> {args[2]}");
        }
        else if (action == "sleep" || action == "wake")
        {
            var selected = FindEnvironment(application, name!);
            if (selected.DeletionStatus is not null)
                throw new CliCommandException(CliExitCodes.ValidationError, $"Environment '{selected.Name}' is being deleted or requires deletion recovery.");
            return await RunEnvironmentPowerAsync(application.Id, selected, action, args, ct);
        }
        else
        {
            _console.WriteLine("NAME\tPLAN\tDATABASE\tSTATE\tSLEEPS AT\tID");
            foreach (var environment in application.Environments)
            {
                var sleep = _apiClient.SupportsEnvironmentSleepState(application.Id, environment.Id)
                    ? (await GetApplicationEnvironmentForSleepAsync(application.Id, environment.Id, ct)).Sleep
                    : null;
                var state = sleep?.State ?? "-";
                var sleepsAt = sleep?.SleepsAt is { } dueAt
                    ? dueAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                    : "-";
                _console.WriteLine($"{environment.Name}\t{environment.Plan}\t{environment.Database}\t{state}\t{sleepsAt}\t{environment.Id}");
            }
        }
        return CliExitCodes.Success;
    }

    internal static int ParseSleepDurationMinutes(string value, int minimum, int maximum, string option)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
            throw InvalidDuration(option, minimum, maximum);

        var unit = char.ToLowerInvariant(value[^1]);
        var multiplier = unit switch
        {
            'm' => 1,
            'h' => 60,
            'd' => 1_440,
            _ => 0
        };
        if (multiplier == 0 ||
            !int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
            amount <= 0)
            throw InvalidDuration(option, minimum, maximum);

        var minutes = (long)amount * multiplier;
        if (minutes < minimum || minutes > maximum)
            throw InvalidDuration(option, minimum, maximum);
        return (int)minutes;
    }

    private static CliCommandException InvalidDuration(string option, int minimum, int maximum) =>
        new(CliExitCodes.ValidationError, $"{option} must be a duration from {minimum} minutes to {maximum} minutes using m, h, or d (for example 2h).");

    private static CliEnvironmentSleepPolicyPatch BuildSleepPolicyPatch(long revision, string? sleepAfter, string? trafficActivity)
    {
        bool? enabled = null;
        int? sleepAfterMinutes = null;
        bool? httpTrafficIsActivity = null;
        if (sleepAfter is not null)
        {
            if (sleepAfter.Equals("on", StringComparison.OrdinalIgnoreCase))
                enabled = true;
            else if (sleepAfter.Equals("off", StringComparison.OrdinalIgnoreCase))
                enabled = false;
            else
            {
                enabled = true;
                sleepAfterMinutes = ParseSleepDurationMinutes(sleepAfter, 30, 43_200, "--sleep-after");
            }
        }
        if (trafficActivity is not null)
        {
            if (trafficActivity.Equals("on", StringComparison.OrdinalIgnoreCase))
                httpTrafficIsActivity = true;
            else if (trafficActivity.Equals("off", StringComparison.OrdinalIgnoreCase))
                httpTrafficIsActivity = false;
            else
                throw new CliCommandException(CliExitCodes.ValidationError, "--traffic-activity must be 'on' or 'off'.");
        }
        return new CliEnvironmentSleepPolicyPatch(enabled, sleepAfterMinutes, httpTrafficIsActivity, revision);
    }

    private async Task<int> RunEnvironmentPowerAsync(
        string applicationId,
        ApplicationEnvironmentResponse environment,
        string action,
        string[] args,
        CancellationToken ct)
    {
        var keepAwakeValue = ReadEnvironmentOption(args, "--keep-awake");
        int? keepAwakeMinutes = keepAwakeValue is null
            ? null
            : ParseSleepDurationMinutes(keepAwakeValue, 30, 1_440, "--keep-awake");
        EnvironmentPowerActionResponse requested;
        try
        {
            requested = action == "sleep"
                ? await _apiClient.SleepEnvironmentAsync(applicationId, environment.Id, ct)
                : await _apiClient.WakeEnvironmentAsync(applicationId, environment.Id, new(keepAwakeMinutes), ct);
        }
        catch (ApiException ex) when (MapEnvironmentPowerApiError(ex) is { } mapped)
        {
            _console.WriteError($"Environment {action} failed ({ex.Code}): {ex.Message}");
            return mapped.ExitCode;
        }

        if (requested.Operation is not { } operation)
        {
            if (action == "wake" && requested.State == "awake")
            {
                _console.WriteLine($"Environment '{environment.Name}' is already awake.");
                return CliExitCodes.Success;
            }
            if (requested.LastError is { } immediateError)
                return WriteEnvironmentPowerFailure(action, immediateError.Code, immediateError.Message);
            throw new ApiException(200, "empty_operation", $"The API did not return a queued {action} operation.");
        }

        if (args.Contains("--no-wait", StringComparer.Ordinal))
        {
            _console.WriteLine($"{Capitalize(action)} operation queued: {operation.Id}");
            return CliExitCodes.Success;
        }

        _console.WriteLine($"{Capitalize(action)} operation queued: {operation.Id}");
        WritePowerProgress(action, operation.Step);
        return await WaitForEnvironmentPowerAsync(applicationId, environment, action, operation, ct);
    }

    private async Task<int> WaitForEnvironmentPowerAsync(
        string applicationId,
        ApplicationEnvironmentResponse environment,
        string action,
        CliEnvironmentPowerOperationResponse operation,
        CancellationToken ct)
    {
        var targetState = action == "sleep" ? "asleep" : "awake";
        var lastStep = operation.Step;
        while (true)
        {
            var current = await GetApplicationEnvironmentForSleepAsync(applicationId, environment.Id, ct);
            var sleep = current.Sleep
                ?? throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, "The Selfrun API did not return this environment's sleep state.");
            var currentOperation = sleep.Operation;
            if (currentOperation is not null && currentOperation.Id == operation.Id && currentOperation.Step != lastStep)
            {
                lastStep = currentOperation.Step;
                WritePowerProgress(action, lastStep);
            }

            var operationCancelled = currentOperation?.Status is "cancelled" or "canceled";
            if (sleep.State is "sleep_failed" or "wake_failed" ||
                currentOperation?.Status == "failed" ||
                operationCancelled)
            {
                var error = sleep.LastError;
                return WriteEnvironmentPowerFailure(
                    action,
                    error?.Code ?? (operationCancelled ? "operation_cancelled" : $"{action}_failed"),
                    error?.Message ?? $"The {action} operation did not complete.");
            }

            if (sleep.State == targetState &&
                (currentOperation is null || currentOperation.Status is "succeeded" or "completed"))
            {
                _console.WriteLine($"Environment '{environment.Name}' is {targetState}.");
                return CliExitCodes.Success;
            }
            if (currentOperation is null || currentOperation.Id != operation.Id)
            {
                return WriteEnvironmentPowerFailure(
                    action,
                    "operation_lost",
                    $"The {action} operation is no longer active, but the environment did not reach {targetState}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private int WriteEnvironmentPowerFailure(string action, string code, string message)
    {
        _console.WriteError($"Environment {action} failed ({code}): {message}");
        return CliExitCodes.DeploymentFailed;
    }

    private static CliCommandException? MapEnvironmentPowerApiError(ApiException exception)
    {
        var exitCode = exception.Code is "environment_sleep_ineligible" or "environment_power_busy"
            ? CliExitCodes.ValidationError
            : CliExitCodes.DeploymentFailed;
        return exception.StatusCode is 400 or 409 or 422
            ? new CliCommandException(exitCode, $"API error ({exception.Code}): {exception.Message}")
            : null;
    }

    private void WritePowerProgress(string action, string? step)
    {
        if (!string.IsNullOrWhiteSpace(step))
            _console.WriteLine($"{Capitalize(action)}: {HumanizePowerStep(step)}");
    }

    private static string HumanizePowerStep(string step) =>
        string.Join(' ', step.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private async Task WriteEnvironmentAddressMessageAsync(
        string environmentId,
        CliEnvironmentSleepResponse? sleep,
        CancellationToken ct)
    {
        if (sleep is null)
            throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, "The Selfrun API did not return this environment's sleep settings.");

        if (sleep.AddressMode == "dynamic")
        {
            _console.WriteLine("Address mode: dynamic. Selfrun-managed hostnames follow the server automatically; no address is retained.");
            return;
        }
        if (sleep.AddressMode != "retained")
        {
            _console.WriteLine($"Address mode: {sleep.AddressMode}.");
            return;
        }

        var retained = sleep.RetainedAddress;
        if (retained is null)
        {
            _console.WriteLine("Address mode: retained. Selfrun will keep a public address across sleep and wake.");
            return;
        }

        _console.WriteLine($"Address mode: retained. Public IPv4 {retained.Ipv4} is kept across sleep and wake.");
        if (string.IsNullOrWhiteSpace(retained.PreviousIpv4) ||
            string.Equals(retained.PreviousIpv4, retained.Ipv4, StringComparison.Ordinal))
            return;

        var domains = await _apiClient.GetDomainsAsync(environmentId, ct);
        var customDomains = domains
            .Where(domain => domain.Kind == "custom_domain")
            .Select(domain => domain.Hostname)
            .Where(hostname => !string.IsNullOrWhiteSpace(hostname))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(hostname => hostname, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var domainList = customDomains.Length == 0 ? "any custom domains" : string.Join(", ", customDomains);
        _console.WriteError($"Address changed from {retained.PreviousIpv4} to {retained.Ipv4}. Manually repoint {domainList} to {retained.Ipv4}; Selfrun-managed hostnames follow the server automatically.");
    }

    private async Task<ApplicationEnvironmentSleepResponse> GetApplicationEnvironmentForSleepAsync(
        string applicationId,
        string environmentId,
        CancellationToken ct)
    {
        var response = await _apiClient.GetApplicationEnvironmentSleepAsync(applicationId, environmentId, ct);
        if (response.ApplicationId != applicationId || response.Id != environmentId)
            throw new CliCommandException(CliExitCodes.ValidationError, "The selected environment does not belong to this application.");
        return response;
    }

    private async Task WriteDeployWakeMessageAsync(string applicationId, string environmentId, CancellationToken ct)
    {
        if (!_apiClient.SupportsEnvironmentSleepState(applicationId, environmentId))
            return;
        var environment = await GetApplicationEnvironmentForSleepAsync(applicationId, environmentId, ct);
        var sleep = environment.Sleep
            ?? throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, "The Selfrun API did not return this environment's sleep state.");
        if (sleep.State == "asleep")
            _console.WriteLine("Environment is asleep; deployment will wake it automatically.");
        else if (sleep.State is "sleeping" or "waking")
            _console.WriteLine($"Environment is {sleep.State}; deployment will wait for it to become ready.");
        else if (sleep.State is "sleep_failed" or "wake_failed")
            _console.WriteLine($"Environment is in {sleep.State}; deployment will request a wake retry.");
    }

    private static ApplicationEnvironmentResponse FindEnvironment(ApplicationResponse application, string name) =>
        application.Environments.SingleOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new CliCommandException(CliExitCodes.ValidationError,
            $"Unknown environment '{name}'. Available: {string.Join(", ", application.Environments.Select(x => x.Name))}");

    private static string? ReadEnvironmentOption(IReadOnlyList<string> args, string name)
    {
        var matches = args.Count(arg => arg == name || arg.StartsWith(name + "=", StringComparison.Ordinal));
        if (matches == 0)
            return null;
        if (matches > 1)
            throw new CliCommandException(CliExitCodes.ValidationError, $"{name} can be specified only once.");
        var value = GetOption(args, name);
        if (string.IsNullOrWhiteSpace(value) || (value.StartsWith("--", StringComparison.Ordinal) && !args.Contains(name + "=" + value)))
            throw new CliCommandException(CliExitCodes.ValidationError, $"{name} requires a value.");
        return value;
    }

    private static bool HasEnvironmentOption(IReadOnlyList<string> args, string name) =>
        args.Any(arg => arg == name || arg.StartsWith(name + "=", StringComparison.Ordinal));

    private static string Capitalize(string value) =>
        char.ToUpperInvariant(value[0]) + value[1..];
}
