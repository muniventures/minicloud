// SPDX-License-Identifier: MPL-2.0
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task EnsureInstanceAsync(CancellationToken ct)
    {
        _environment.LoadActiveInstance();
        if (!string.IsNullOrWhiteSpace(_environment.ApiBaseUrl))
        {
            if (string.IsNullOrWhiteSpace(_environment.AppDomain))
            {
                var info = await _apiClient.GetInstanceInfoAsync(_environment.ApiBaseUrl, ct);
                _environment.SetAppDomain(info.AppDomain);
            }
            return;
        }
        _console.Write("Minicloud instance URL (e.g. cloud.example.com): ");
        var url = _console.ReadLine();
        if (string.IsNullOrWhiteSpace(url))
            throw new CliCommandException(CliExitCodes.ValidationError,
                "An instance URL is required. Run 'minicloud instance add <name> <url>' or set MINICLOUD_API_URL for CI.");
        var normalized = InstanceStore.NormalizeUrl(url);
        await AddInstanceAsync(new Uri(normalized).Authority, normalized, ct);
    }

    private async Task AddInstanceAsync(string name, string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
            throw new CliCommandException(CliExitCodes.ValidationError, "Instance name must be nonempty and cannot start with '-'.");
        var normalized = InstanceStore.NormalizeUrl(url);
        var state = _environment.Instances.Load();
        if (state.Instances.Any(x => x.Name == name || x.Url == normalized))
            throw new CliCommandException(CliExitCodes.ValidationError, "Instance name or URL already exists. Use 'minicloud instance use <name>'.");
        var info = await _apiClient.GetInstanceInfoAsync(InstanceStore.DeriveApiUrl(normalized), ct);
        var profile = new InstanceProfile(name, normalized, info.AppDomain);
        state.Instances.Add(profile);
        state.Active = name;
        _environment.Instances.Save(state);
        _environment.SelectInstance(profile);
        _console.WriteLine($"Active instance: {name} ({normalized})");
    }

    private async Task<int> RunInstanceAsync(string[] args, CancellationToken ct)
    {
        switch (args.ElementAtOrDefault(0))
        {
            case "add" when args.Length == 3:
                await AddInstanceAsync(args[1], args[2], ct);
                return CliExitCodes.Success;
            case "list" when args.Length == 1:
                var state = _environment.Instances.Load();
                if (state.Instances.Count == 0) _console.WriteLine("No saved instances.");
                foreach (var profile in state.Instances)
                    _console.WriteLine($"{(profile.Name == state.Active ? "*" : " ")} {profile.Name}  {profile.Url}");
                if (_environment.HasApiOverride) _console.WriteLine($"MINICLOUD_API_URL override: {_environment.ApiBaseUrl}");
                return CliExitCodes.Success;
            case "use" when args.Length == 2:
                var saved = _environment.Instances.Load();
                var selected = saved.Instances.SingleOrDefault(x => x.Name == args[1]);
                if (selected is null) throw new CliCommandException(CliExitCodes.ValidationError, $"Unknown instance '{args[1]}'. Run 'minicloud instance list'.");
                var info = await _apiClient.GetInstanceInfoAsync(InstanceStore.DeriveApiUrl(selected.Url), ct);
                var refreshed = selected with { AppDomain = info.AppDomain };
                saved.Instances[saved.Instances.IndexOf(selected)] = refreshed;
                selected = refreshed;
                saved.Active = selected.Name;
                _environment.Instances.Save(saved);
                _console.WriteLine($"Active instance: {selected.Name} ({selected.Url})");
                if (_environment.HasApiOverride) _console.WriteLine("Unset MINICLOUD_API_URL to use the saved active instance in subsequent commands.");
                else _environment.SelectInstance(selected);
                return CliExitCodes.Success;
            default:
                PrintCommandHelp("instance");
                return CliExitCodes.ValidationError;
        }
    }
}
