// SPDX-License-Identifier: MPL-2.0
using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunEnvironmentsAsync(string[] args, CancellationToken ct)
    {
        var action = args.FirstOrDefault();
        var name = args.ElementAtOrDefault(1);
        if (action is not ("list" or "create" or "rename") ||
            (action != "list" && (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))) ||
            (action == "create" && string.IsNullOrWhiteSpace(GetOption(args, "--plan"))) ||
            (action == "rename" && (args.Length < 3 || args[2].StartsWith('-'))))
            throw new CliCommandException(CliExitCodes.ValidationError, "Usage: minicloud environments list | create <name> --plan <plan> | rename <name> <new-name> [--config path]");
        var result = MinicloudConfigLoader.Load(GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath());
        if (!result.IsValid || result.Config is null) { PrintDiagnostics(result.Diagnostics); return CliExitCodes.ValidationError; }
        var application = await GetApplicationContextAsync(result.Config.AppId!, ct);
        if (action == "create")
        {
            var created = await _apiClient.CreateEnvironmentAsync(application.Id,
                new(name!, GetOption(args, "--plan")!, GetOption(args, "--database") ?? "none"), ct);
            _console.WriteLine($"App: {application.Name} · Environment created: {created.Name} ({created.Plan}), runtime {created.Id}");
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
