using Minicloud.Cli.Api;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunDomainsAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _console.WriteError("Usage: minicloud domains <list|add-subdomain|disable|delete>");
            return CliExitCodes.ValidationError;
        }

        return args[0] switch
        {
            "list" => await RunDomainsListAsync(args.Skip(1).ToArray(), cancellationToken),
            "add-subdomain" => await RunDomainsAddSubdomainAsync(args.Skip(1).ToArray(), cancellationToken),
            "disable" => await RunDomainsDisableAsync(args.Skip(1).ToArray(), cancellationToken),
            "delete" => await RunDomainsDeleteAsync(args.Skip(1).ToArray(), cancellationToken),
            _ => UnknownCommand($"domains {args[0]}")
        };
    }

    private async Task<int> RunDomainsListAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var domains = await _apiClient.GetDomainsAsync(app.Id, cancellationToken);
        if (domains.Count == 0)
        {
            _console.WriteLine($"No domains for {app.Name}.");
            return CliExitCodes.Success;
        }

        foreach (var domain in domains.OrderBy(x => x.ServiceName, StringComparer.Ordinal).ThenBy(x => x.Hostname, StringComparer.Ordinal))
        {
            var lastApplied = domain.LastAppliedAt is null ? "-" : domain.LastAppliedAt.Value.ToString("O");
            _console.WriteLine($"{domain.Hostname}  service={domain.ServiceName}  status={domain.Status}  apply={domain.ApplyStatus}  ssl={domain.SslStatus}  lastApplied={lastApplied}  updated={domain.UpdatedAt:O}");
        }

        return CliExitCodes.Success;
    }

    private async Task<int> RunDomainsAddSubdomainAsync(string[] args, CancellationToken cancellationToken)
    {
        var service = GetOption(args, "--service");
        if (string.IsNullOrWhiteSpace(service))
        {
            _console.WriteError("Usage: minicloud domains add-subdomain --app <app> --service <service> [--label <label>]");
            return CliExitCodes.ValidationError;
        }

        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var label = GetOption(args, "--label");
        var domain = await _apiClient.CreateDomainAsync(app.Id, new CreateDomainBindingRequest(service, label), cancellationToken);
        _console.WriteLine($"Created {domain.Hostname}");
        _console.WriteLine($"Service: {domain.ServiceName}");
        _console.WriteLine($"Status: {domain.Status}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunDomainsDisableAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var domain = await ResolveDomainOptionAsync(app.Id, args, cancellationToken);
        var updated = await _apiClient.UpdateDomainAsync(app.Id, domain.Id, new UpdateDomainBindingRequest(true), cancellationToken);
        _console.WriteLine($"Disabled {updated.Hostname}");
        _console.WriteLine($"Status: {updated.Status}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunDomainsDeleteAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var domain = await ResolveDomainOptionAsync(app.Id, args, cancellationToken);
        await _apiClient.DeleteDomainAsync(app.Id, domain.Id, cancellationToken);
        _console.WriteLine($"Deleted {domain.Hostname}");
        return CliExitCodes.Success;
    }

    private async Task<DomainBindingResponse> ResolveDomainOptionAsync(string appId, string[] args, CancellationToken cancellationToken)
    {
        var hostname = GetOption(args, "--hostname");
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "Missing --hostname <host>.");
        }

        var domains = await _apiClient.GetDomainsAsync(appId, cancellationToken);
        return domains.FirstOrDefault(x => string.Equals(x.Hostname, hostname, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Id, hostname, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Domain '{hostname}' was not found.");
    }

    private static string FormatDomainSummary(DomainBindingResponse domain)
    {
        var lastApplied = domain.LastAppliedAt is null ? "-" : domain.LastAppliedAt.Value.ToString("O");
        return $"{domain.Hostname}({domain.Status},apply={domain.ApplyStatus},ssl={domain.SslStatus},lastApplied={lastApplied})";
    }
}
