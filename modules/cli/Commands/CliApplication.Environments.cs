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
        var app = await _apiClient.GetAppAsync(result.Config.AppId!, ct);
        if (app.BranchName is not null) throw new CliCommandException(CliExitCodes.ValidationError, "Use a main application config to manage environments.");
        if (app.ParentAppId is not null) app = await _apiClient.GetAppAsync(app.ParentAppId, ct);
        if (action == "create")
        {
            var created = await _apiClient.CreateEnvironmentAsync(app.Id, new(name!, GetOption(args, "--plan")!, GetOption(args, "--database")), ct);
            _console.WriteLine($"Environment created: {created.EnvironmentName} ({created.Plan}), runtime {created.Id}");
        }
        else
        {
            var environments = app.Environments ?? [new AppEnvironmentResponse(app.Id, app.EnvironmentName ?? "production", app.Plan, app.Database, app.DeletionStatus)];
            if (action == "rename")
            {
                var selected = environments.SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Unknown environment '{name}'.");
                await _apiClient.RenameEnvironmentAsync(selected.Id, new(args[2]), ct);
                _console.WriteLine($"Environment renamed: {name} -> {args[2]}");
            }
            else foreach (var environment in environments)
                _console.WriteLine($"{environment.Name}\t{environment.Plan}\t{environment.Database}\t{environment.Id}");
        }
        return CliExitCodes.Success;
    }
}
