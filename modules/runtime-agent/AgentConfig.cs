using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minicloud.RuntimeAgent;

public sealed record AgentConfig(
    string ServerId,
    string OrganizationId,
    string AppId,
    string ApiBaseUrl,
    string ComposeProject,
    string ComposeFile,
    string TokenPath,
    string StatePath)
{
    public static AgentConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        var config = JsonSerializer.Deserialize<AgentConfig>(stream, JsonDefaults.Options)
            ?? throw new InvalidOperationException("Agent config file is empty.");

        if (string.IsNullOrWhiteSpace(config.ServerId) ||
            string.IsNullOrWhiteSpace(config.ApiBaseUrl) ||
            string.IsNullOrWhiteSpace(config.TokenPath) ||
            string.IsNullOrWhiteSpace(config.StatePath) ||
            string.IsNullOrWhiteSpace(config.AppId) ||
            string.IsNullOrWhiteSpace(config.ComposeProject) ||
            !Regex.IsMatch(config.ComposeProject, @"\Aminicloud-[a-z0-9][a-z0-9-]{0,62}\z"))
        {
            throw new InvalidOperationException("Agent config is missing required fields.");
        }

        return config;
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
