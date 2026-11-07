using System.Diagnostics;

namespace Minicloud.Worker.Tests;

public sealed class StorageRuntimeTests
{
    [Fact]
    public async Task RuntimeStorageRefusesMissingOrForeignDisksAndPreservesData()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Minicloud.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root!.FullName, "scripts", "github-actions", "minicloud-deploy", "test-storage-volume.py"));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
    }
}
