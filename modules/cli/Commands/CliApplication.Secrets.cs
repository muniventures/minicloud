using System.Text;
using Minicloud.Cli.Api;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunSecretsAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _console.WriteError("Usage: minicloud secrets <list|set|remove>");
            return CliExitCodes.ValidationError;
        }

        return args[0] switch
        {
            "list" => await RunSecretsListAsync(args.Skip(1).ToArray(), cancellationToken),
            "set" => await RunSecretsSetAsync(args.Skip(1).ToArray(), cancellationToken),
            "remove" or "delete" => await RunSecretsRemoveAsync(args.Skip(1).ToArray(), cancellationToken),
            _ => UnknownCommand($"secrets {args[0]}")
        };
    }

    private async Task<int> RunSecretsListAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var service = GetOption(args, "--service");
        var secrets = await _apiClient.GetSecretsAsync(app.Id, service, cancellationToken);
        if (secrets.Count == 0)
        {
            _console.WriteLine($"No secrets for {app.Name}.");
            return CliExitCodes.Success;
        }

        foreach (var secret in secrets.OrderBy(x => x.ServiceName, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            var scope = string.IsNullOrWhiteSpace(secret.ServiceName) ? "app" : secret.ServiceName;
            _console.WriteLine($"{scope}  {secret.Name}  status={secret.Status}  updated={secret.UpdatedAt:O}");
        }

        return CliExitCodes.Success;
    }

    private async Task<int> RunSecretsSetAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var service = GetOption(args, "--service");
        var name = FirstPositional(args);
        var value = GetOption(args, "--value");
        if (string.IsNullOrWhiteSpace(name))
        {
            _console.WriteError("Usage: minicloud secrets set [--app <app>] <NAME> [--value value]");
            return CliExitCodes.ValidationError;
        }

        if (value is null)
        {
            value = ReadSecretValue($"Value for {app.Slug}/{name}: ");
        }

        var secret = await _apiClient.SetSecretAsync(app.Id, new SetAppServiceSecretRequest(service, name, value), cancellationToken);
        _console.WriteLine($"Secret saved: {secret.Name}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunSecretsRemoveAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var service = GetOption(args, "--service");
        var name = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(name))
        {
            _console.WriteError("Usage: minicloud secrets remove [--app <app>] <NAME>");
            return CliExitCodes.ValidationError;
        }

        var secrets = await _apiClient.GetSecretsAsync(app.Id, service, cancellationToken);
        var matches = secrets
            .Where(x => string.Equals(x.Name, name, StringComparison.Ordinal) || string.Equals(x.Id, name, StringComparison.Ordinal))
            .ToArray();
        var secret = matches.Length switch
        {
            0 => throw new CliCommandException(CliExitCodes.ValidationError, $"Secret '{name}' was not found."),
            1 => matches[0],
            _ => matches.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.ServiceName))
                ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Secret '{name}' exists in multiple service scopes. Pass --service <service> to remove a service-scoped secret.")
        };
        await _apiClient.DeleteSecretAsync(app.Id, secret.Id, cancellationToken);
        _console.WriteLine($"Secret removed: {secret.Name}");
        return CliExitCodes.Success;
    }

    private string ReadSecretValue(string prompt)
    {
        _console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = _console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                _console.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }
}
