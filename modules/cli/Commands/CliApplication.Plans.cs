// SPDX-License-Identifier: MPL-2.0
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunPlansAsync(string[] args, CancellationToken ct)
    {
        if (args.FirstOrDefault() != "list")
            throw new CliCommandException(CliExitCodes.ValidationError, "Usage: minicloud plans list [--organization <id>] [--config path]");
        var me = await _apiClient.GetMeAsync(ct);
        var organizationId = GetOption(args, "--organization");
        if (organizationId is null)
        {
            var path = GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath();
            if (File.Exists(path))
            {
                var result = MinicloudConfigLoader.Load(path);
                if (!result.IsValid || result.Config is null) { PrintDiagnostics(result.Diagnostics); return CliExitCodes.ValidationError; }
                organizationId = (await GetApplicationContextAsync(result.Config.AppId!, ct)).OrganizationId;
            }
            else if (me.Organizations.Count == 1) organizationId = me.Organizations[0].Id;
            else throw new CliCommandException(CliExitCodes.ValidationError, "Select an organization with --organization <id> or use an application config.");
        }
        if (!me.Organizations.Any(x => x.Id == organizationId))
            throw new CliCommandException(CliExitCodes.AuthError, "The selected organization is not available to this token.");
        var plans = await _apiClient.GetPlansAsync(organizationId, ct);
        foreach (var plan in plans.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            _console.WriteLine($"{plan.Name}\t{plan.ProviderName}\t{plan.Region}\t{plan.Sku}\t{(plan.Enabled && plan.AccountEnabled ? "enabled" : "disabled")}");
        if (plans.Count == 0) _console.WriteLine("No plans configured. Add a provider account and plan in the portal.");
        return CliExitCodes.Success;
    }
}
