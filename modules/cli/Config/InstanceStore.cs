// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Minicloud.Cli.Commands;

namespace Minicloud.Cli.Config;

public sealed record InstanceProfile(string Name, string Url, string AppDomain);
public sealed class InstanceStore(string configHome)
{
    private string FilePath => Path.Combine(configHome, "instances.json");
    public sealed class State
    {
        public string? Active { get; set; }
        public List<InstanceProfile> Instances { get; set; } = [];
    }

    public State Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? throw new JsonException();
            if (state.Instances is null || state.Instances.Any(x => x is null || string.IsNullOrWhiteSpace(x.Name) ||
                string.IsNullOrWhiteSpace(x.Url) || string.IsNullOrWhiteSpace(x.AppDomain)) ||
                state.Instances.Select(x => x.Name).Distinct().Count() != state.Instances.Count ||
                state.Instances.Select(x => x.Url).Distinct().Count() != state.Instances.Count ||
                (state.Active is not null && !state.Instances.Any(x => x.Name == state.Active)))
                throw new JsonException();
            return state;
        }
        catch (JsonException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Invalid instance configuration: {FilePath}");
        }
    }

    public void Save(State state)
    {
        Directory.CreateDirectory(configHome);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string NormalizeUrl(string value)
    {
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.HostNameType != UriHostNameType.Dns ||
            (uri.Scheme != "https" && uri.Scheme != "http") ||
            (uri.Scheme == "http" && !uri.IsLoopback && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Enter the main Minicloud hostname, e.g. cloud.example.com (HTTPS; no paths or credentials). HTTP is allowed only for localhost.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static string DeriveApiUrl(string instanceUrl)
    {
        var uri = new Uri(NormalizeUrl(instanceUrl));
        return new UriBuilder(uri) { Host = "api." + uri.Host }.Uri.GetLeftPart(UriPartial.Authority);
    }
}
