namespace Minicloud.Api.Services;

public sealed class DatabaseBackupDownload(HttpResponseMessage response, string filename, long expectedLength) : IDisposable
{
    public string Filename { get; } = filename;
    public async Task CopyToAsync(Stream destination, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, timeout.Token);
            if (count == 0) break;
            total += count;
            if (total > expectedLength) throw new IOException("Backup artifact exceeded its declared length.");
            await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
        if (total != expectedLength) throw new IOException("Backup artifact transfer was incomplete.");
    }
    public void Dispose() => response.Dispose();
}
