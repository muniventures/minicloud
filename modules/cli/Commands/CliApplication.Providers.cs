// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunProvidersAsync(string[] args, CancellationToken ct)
    {
        if (args.FirstOrDefault() == "import-legacy")
        {
            var path = GetOption(args, "--manifest");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new CliCommandException(CliExitCodes.ValidationError, "Provide an existing JSON manifest with --manifest <path>.");
            ProviderLegacyImportRequest manifest;
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !HasString(root, "organizationId") || !HasString(root, "providerAccountId")
                    || !root.TryGetProperty("appBindings", out var apps) || apps.ValueKind != JsonValueKind.Array
                    || !root.TryGetProperty("retainedVolumeIds", out var volumes) || volumes.ValueKind != JsonValueKind.Array
                    || apps.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Object || !HasString(x, "appId"))
                    || volumes.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())))
                    throw new JsonException();
                manifest = root.Deserialize<ProviderLegacyImportRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new CliCommandException(CliExitCodes.ValidationError, "Manifest requires organizationId, providerAccountId, appBindings with appId, and retainedVolumeIds arrays. Use an empty array explicitly when there are no matching resources.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliCommandException(CliExitCodes.ValidationError, "The import manifest could not be read.");
            }
            var result = await _apiClient.ImportProviderLegacyAsync(manifest, ct);
            _console.WriteLine($"Imported legacy bindings: plans={result.CreatedPlans}, apps={result.BoundApps}, servers={result.BoundServers}, runtimes={result.BoundRuntimes}, volumes={result.BoundVolumes}.");
            return CliExitCodes.Success;
        }
        if (args.FirstOrDefault() == "ssh-keys")
            return await RunProviderSshKeysAsync(args, ct);
        if (args.FirstOrDefault() != "add")
            throw new CliCommandException(CliExitCodes.ValidationError, "Usage: minicloud providers add --organization <id> --name <name> --provider <type> | import-legacy --manifest <path> | ssh-keys <provider-account-id> | --plan <plan> [--organization <id>]");
        var organizationId = GetOption(args, "--organization");
        var name = GetOption(args, "--name");
        var provider = GetOption(args, "--provider")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(name) || provider is not ("hetzner" or "digital-ocean" or "digitalocean" or "vultr"))
            throw new CliCommandException(CliExitCodes.ValidationError, "Specify --organization, --name and --provider hetzner|digital-ocean|vultr.");
        if (args.Any(x => x.StartsWith("--token", StringComparison.Ordinal) || x.StartsWith("--credential", StringComparison.Ordinal) || x.StartsWith("--api-key", StringComparison.Ordinal)))
            throw new CliCommandException(CliExitCodes.ValidationError, "Cloud credentials must be supplied through provider environment variables, never command arguments.");
        var credential = provider switch
        {
            "hetzner" => FirstEnvironmentValue("HCLOUD_TOKEN", "HETZNER_API_TOKEN"),
            "vultr" => FirstEnvironmentValue("VULTR_API_KEY", "VULTR_KEY"),
            _ => FirstEnvironmentValue("DIGITALOCEAN_ACCESS_TOKEN", "DIGITALOCEAN_TOKEN")
        };
        if (credential is null)
            throw new CliCommandException(CliExitCodes.ValidationError, provider switch
            {
                "hetzner" => "Set HCLOUD_TOKEN or HETZNER_API_TOKEN before adding this account.",
                "vultr" => "Set VULTR_API_KEY or VULTR_KEY before adding this account.",
                _ => "Set DIGITALOCEAN_ACCESS_TOKEN or DIGITALOCEAN_TOKEN before adding this account."
            });
        var osValue = GetOption(args, "--os-id");
        if (args.Any(x => x == "--os-id" || x.StartsWith("--os-id=", StringComparison.Ordinal)) && string.IsNullOrWhiteSpace(osValue))
            throw new CliCommandException(CliExitCodes.ValidationError, "--os-id requires a positive integer.");
        int? osId = null;
        if (osValue is not null)
        {
            if (!int.TryParse(osValue, out var parsed) || parsed <= 0)
                throw new CliCommandException(CliExitCodes.ValidationError, "--os-id requires a positive integer.");
            osId = parsed;
        }
        var account = await _apiClient.CreateProviderAsync(new(organizationId, name, provider == "digitalocean" ? "digital-ocean" : provider,
            credential, GetOption(args, "--project-id") ?? (provider is "digital-ocean" or "digitalocean" ? FirstEnvironmentValue("DIGITALOCEAN_PROJECT_ID") : null), osId), ct);
        _console.WriteLine($"Provider added: {account.Name} ({account.Id}, {account.ProviderType}).");
        return CliExitCodes.Success;
    }

    private async Task<int> RunProviderSshKeysAsync(string[] args, CancellationToken ct)
    {
        const string usage = "Usage: minicloud providers ssh-keys <provider-account-id> | --plan <plan> [--organization <id>]";
        var accountId = args.ElementAtOrDefault(1) is { } positional && !positional.StartsWith('-') ? positional : null;
        var planName = GetOption(args, "--plan");
        if ((accountId is null) == (planName is null) || (planName is not null && string.IsNullOrWhiteSpace(planName)))
            throw new CliCommandException(CliExitCodes.ValidationError, usage);
        var me = await _apiClient.GetMeAsync(ct);
        var organizationId = GetOption(args, "--organization");
        if (organizationId is null)
        {
            if (me.Organizations.Count != 1)
                throw new CliCommandException(CliExitCodes.ValidationError, "Select an organization with --organization <id>.");
            organizationId = me.Organizations[0].Id;
        }
        if (!me.Organizations.Any(x => x.Id == organizationId))
            throw new CliCommandException(CliExitCodes.AuthError, "The selected organization is not available to this token.");
        if (planName is not null)
        {
            var plan = (await _apiClient.GetPlansAsync(organizationId, ct)).SingleOrDefault(x => string.Equals(x.Name, planName, StringComparison.Ordinal))
                ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Plan '{planName}' was not found. Run 'minicloud plans list' to see current names.");
            accountId = plan.ProviderAccountId;
            _console.WriteLine($"Provider account: {plan.ProviderName} ({accountId})");
        }
        IReadOnlyList<ProviderSshKeyResponse> keys;
        try
        {
            keys = await _apiClient.GetProviderSshKeysAsync(accountId!, organizationId, ct);
        }
        catch (Api.ApiException ex) when (ex.StatusCode == 403)
        {
            throw new CliCommandException(CliExitCodes.AuthError, $"Auth error: {ex.Message} Listing SSH keys requires an API key with providers:read for this organization.");
        }
        catch (Api.ApiException ex) when (MapSshApiError(ex, null) is { } mapped)
        {
            throw mapped;
        }
        if (keys.Count == 0)
            _console.WriteLine("No selectable SSH keys in this provider account. Use --ssh-public-key-file to add one.");
        foreach (var key in keys)
            _console.WriteLine($"{key.Id}\t{key.Name}\t{key.KeyType}\t{key.Fingerprint}\t{key.RegistrationState}");
        return CliExitCodes.Success;
    }

    private static bool HasString(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

    private static string? FirstEnvironmentValue(params string[] names) => names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
}
