using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Minicloud.Common.Providers.Hetzner;

public sealed partial class HetznerClient : IStorageVolumeProvider
{
    // Mutation POSTs are not blindly retried: the next reconciliation reads provider state.
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("minicloud.no-retry");

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        using var doc = await VolumeRequestAsync(HttpMethod.Get, $"volumes?name={Uri.EscapeDataString(name)}", null, ct);
        var matches = doc!.RootElement.GetProperty("volumes").EnumerateArray().ToArray();
        if (matches.Length > 1) { throw new InvalidOperationException("Multiple provider volumes match the storage identity."); }
        return matches.Length == 0 ? null : ReadVolume(matches[0]);
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        using var doc = await VolumeRequestAsync(HttpMethod.Get, $"volumes/{Uri.EscapeDataString(id)}", null, ct);
        return doc is null ? null : ReadVolume(doc.RootElement.GetProperty("volume"));
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        using var doc = await VolumeRequestAsync(HttpMethod.Post, "volumes", new { name, size = sizeGb, location = region, format = "ext4", automount = false, labels }, ct);
        var volume = ReadVolume(doc!.RootElement.GetProperty("volume"));
        if (doc.RootElement.TryGetProperty("action", out var action) && action.ValueKind != JsonValueKind.Null)
        { await WaitForVolumeActionAsync(doc, ct); }
        if (doc.RootElement.TryGetProperty("next_actions", out var nextActions))
        {
            foreach (var next in nextActions.EnumerateArray())
            {
                using var pending = JsonDocument.Parse("{\"action\":" + next.GetRawText() + "}");
                await WaitForVolumeActionAsync(pending, ct);
            }
        }
        return volume;
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        using var doc = await VolumeRequestAsync(HttpMethod.Post, $"volumes/{Uri.EscapeDataString(id)}/actions/attach", new { server = long.Parse(serverId), automount = false }, ct);
        await WaitForVolumeActionAsync(doc!, ct);
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        using var doc = await VolumeRequestAsync(HttpMethod.Post, $"volumes/{Uri.EscapeDataString(id)}/actions/resize", new { size = sizeGb }, ct);
        await WaitForVolumeActionAsync(doc!, ct);
    }

    private async Task<JsonDocument?> VolumeRequestAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        request.Options.Set(NoRetry, method != HttpMethod.Get);
        if (payload is not null) { request.Content = JsonContent.Create(payload); }
        using var response = await SendAsync(request, ct);
        if (method == HttpMethod.Get && response.StatusCode == HttpStatusCode.NotFound) { return null; }
        await EnsureSuccessAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task WaitForVolumeActionAsync(JsonDocument doc, CancellationToken ct)
    {
        if (!doc.RootElement.TryGetProperty("action", out var action)) { throw new InvalidOperationException("Hetzner did not return a volume action."); }
        var id = action.GetProperty("id").GetInt64();
        var status = action.GetProperty("status").GetString();
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (status == "success") { return; }
            if (status == "error") { throw new InvalidOperationException($"Hetzner volume action {id} failed."); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var poll = await VolumeRequestAsync(HttpMethod.Get, $"volumes/actions/{id}", null, ct);
            status = poll!.RootElement.GetProperty("action").GetProperty("status").GetString();
        }
        throw new TimeoutException($"Hetzner volume action {id} did not finish within five minutes.");
    }

    private static ProviderStorageVolume ReadVolume(JsonElement volume) => new(
        volume.GetProperty("id").GetInt64().ToString(), volume.GetProperty("name").GetString()!, volume.GetProperty("size").GetInt32(),
        volume.GetProperty("location").GetProperty("name").GetString()!,
        volume.TryGetProperty("server", out var server) && server.ValueKind != JsonValueKind.Null ? server.GetInt64().ToString() : null,
        volume.GetProperty("labels").Deserialize<Dictionary<string, string>>()!);
}
