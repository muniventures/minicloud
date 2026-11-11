// SPDX-License-Identifier: MPL-2.0
using Minicloud.Cli.Api;
using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunEnvironmentsAsync(string[] args, CancellationToken ct)
    {
        var action = args.FirstOrDefault();
        var name = args.ElementAtOrDefault(1);
        var usage = $"Usage: minicloud environments list | create <name> --plan <plan> {SshOptionsUsage} | update <name> {SshOptionsUsage} | rename <name> <new-name> [--config path]";
        if (action is not ("list" or "create" or "update" or "rename") ||
            (action != "list" && (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))) ||
            (action == "create" && string.IsNullOrWhiteSpace(GetOption(args, "--plan"))) ||
            (action == "rename" && (args.Length < 3 || args[2].StartsWith('-'))))
            throw new CliCommandException(CliExitCodes.ValidationError, usage);
        if (action is "list" or "rename" && HasSshAccessOptions(args))
            throw new CliCommandException(CliExitCodes.ValidationError, $"SSH options apply only to 'environments create' and 'environments update'. {usage}");
        // Validate SSH flags and read the public key file before any API call.
        var sshAccess = action is "create" or "update" ? ParseSshAccessOptions(args) : null;
        if (action == "update" && sshAccess is null)
            throw new CliCommandException(CliExitCodes.ValidationError, $"Nothing to update. Supply {SshKeyOption} <key-id> or {SshPublicKeyFileOption} <path>; omitting both keeps the current SSH selection.");
        var result = MinicloudConfigLoader.Load(GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath());
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
            var selected = application.Environments.SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Unknown environment '{name}'. Available: {string.Join(", ", application.Environments.Select(x => x.Name))}");
            if (selected.DeletionStatus is not null)
                throw new CliCommandException(CliExitCodes.ValidationError, $"Environment '{selected.Name}' is being deleted or requires deletion recovery.");
            var current = await _apiClient.GetEnvironmentAsync(selected.Id, ct);
            if (current.ApplicationId != application.Id)
                throw new CliCommandException(CliExitCodes.ValidationError, "The selected environment does not belong to this application.");
            var currentAccess = current.SshAccess
                ?? throw new CliCommandException(CliExitCodes.ValidationError, "This Minicloud server does not report SSH access for environments. Upgrade the API before selecting SSH keys.");
            if (currentAccess.SelectionLocked)
                throw MapSshApiError(new ApiException(409, "ssh_selection_locked", string.Empty), sshAccess)!;
            var request = sshAccess! with { ExpectedRevision = currentAccess.Revision };
            var updated = await WithSshErrorsAsync(request, () => _apiClient.UpdateEnvironmentAsync(application.Id, selected.Id, new(SshAccess: request), ct));
            _console.WriteLine($"App: {application.Name} · Environment updated: {updated.Name}");
            WriteSshSelectionNote(request, updated.SshAccess);
        }
        else if (action == "rename")
        {
            var selected = application.Environments.SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Unknown environment '{name}'.");
            await _apiClient.UpdateEnvironmentAsync(application.Id, selected.Id, new(Name: args[2]), ct);
            _console.WriteLine($"Environment renamed: {name} -> {args[2]}");
        }
        else foreach (var environment in application.Environments)
            _console.WriteLine($"{environment.Name}\t{environment.Plan}\t{environment.Database}\t{environment.Id}");
        return CliExitCodes.Success;
    }
}
