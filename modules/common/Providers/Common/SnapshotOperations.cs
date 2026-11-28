using System.Buffers.Text;
using System.Text.Json;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.Common;

internal static class SnapshotOperations
{
    public static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PowerTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SnapshotTimeout = TimeSpan.FromHours(1);
    private const string LabelMarker = "--selfrun-v1--";

    public static async Task<T?> PollAsync<T>(Func<CancellationToken, Task<T?>> probe, TimeSpan timeout, CancellationToken ct)
        where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (await probe(deadline.Token) is { } result)
                    return result;
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public static bool Matches(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> required) =>
        required.All(label => actual.TryGetValue(label.Key, out var value) && value == label.Value);

    public static ProviderSnapshot VerifyOwnership(ProviderSnapshot snapshot, IReadOnlyDictionary<string, string> labels, CloudProvider provider) =>
        Matches(snapshot.Labels, labels) ? snapshot
            : throw new ProviderApiException($"Snapshot {snapshot.Id} did not preserve the requested ownership labels.", provider: provider);

    // Droplet snapshots and Vultr compute snapshots cannot be tagged at creation.
    public static string EncodeLabels(string description, IReadOnlyDictionary<string, string> labels) =>
        labels.Count == 0 ? description : description + LabelMarker +
            Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
                new SortedDictionary<string, string>(labels.ToDictionary(), StringComparer.Ordinal)));

    public static (string Description, Dictionary<string, string> Labels) DecodeLabels(string name)
    {
        var markerIndex = name.LastIndexOf(LabelMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            // Unmanaged snapshots may have arbitrary names, including the marker.
            try
            {
                var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    Base64Url.DecodeFromChars(name.AsSpan(markerIndex + LabelMarker.Length)));
                if (labels is { Count: > 0 })
                    return (name[..markerIndex], labels);
            }
            catch (FormatException) { }
            catch (JsonException) { }
        }
        return (name, []);
    }

    public static void ValidateRestore(string snapshotId, string? retainedAddressId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        if (retainedAddressId is not null)
            throw new NotSupportedException("Restoring a provider snapshot with a retained address is not supported by this adapter.");
    }
}
