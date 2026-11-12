// SPDX-License-Identifier: MPL-2.0
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;
using Minicloud.Cli.Seed;

namespace Minicloud.Tests;

public sealed class SeedJournalTests : IDisposable
{
    private readonly string home = Path.GetFullPath(Path.Combine(".local", "artifacts", "seed-journal-tests-" + Guid.NewGuid().ToString("N")));
    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, true); }

    [Fact]
    public void Persistent_lock_excludes_a_second_invocation()
    {
        using var first = new SeedJournal(home, new string('a', 32), true);
        Assert.Throws<CliCommandException>(() => new SeedJournal(home, new string('a', 32)));
    }

    [Fact]
    public void Interrupted_write_is_not_interpreted_as_a_fresh_operation()
    {
        using var journal = new SeedJournal(home, new string('a', 32), true);
        SeedFiles.WriteNew(Path.Combine(journal.DirectoryPath, "operation.pending"), "{}"u8.ToArray());
        Assert.Throws<CliCommandException>(() => journal.Load());
    }

    [Fact]
    public void Credential_input_rejects_links_and_public_permissions()
    {
        if (OperatingSystem.IsWindows()) return;
        SeedFiles.Directory(home);
        var path = Path.Combine(home, "credential");
        SeedFiles.WriteNew(path, "synthetic-secret"u8.ToArray());
        var link = Path.Combine(home, "credential-link");
        File.CreateSymbolicLink(link, path);
        Assert.Throws<CliCommandException>(() => SeedFiles.Check(link));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Assert.Throws<CliCommandException>(() => SeedFiles.Check(path));
    }

    [Fact]
    public void Protected_export_sink_never_replaces_existing_recovery_bytes()
    {
        SeedFiles.Directory(home);
        var path = Path.Combine(home, "openbao-recovery.json");
        SeedFiles.WriteNew(path, "synthetic-original-recovery"u8.ToArray());
        Assert.Throws<IOException>(() => SeedFiles.WriteNew(path, "synthetic-replacement"u8.ToArray()));
        SeedFiles.Check(path);
        Assert.Equal("synthetic-original-recovery", File.ReadAllText(path));
    }

    [Fact]
    public void Seed_instance_addition_preserves_selection_and_rejects_conflicting_profiles()
    {
        var store = new InstanceStore(home);
        var state = new InstanceStore.State { Active = "original", Instances = [new("original", "https://old.example.com", "apps.old.example.com")] };
        store.Save(state);
        var seed = new InstanceProfile("seed", "https://cloud.example.com", "apps.example.com");
        store.AddWithoutSelecting(seed);
        Assert.Equal("original", store.Load().Active);
        Assert.Contains(seed, store.Load().Instances);
        Assert.Throws<CliCommandException>(() => store.AddWithoutSelecting(seed with { Url = "https://different.example.com" }));
        Assert.Equal(2, store.Load().Instances.Count);
    }

    [Fact]
    public void Signed_inventory_contract_rejects_tampering_and_duplicates_before_execution()
    {
        SeedFiles.Directory(home);
        var path = Path.Combine(home, "server-manifest.json");
        SeedFiles.WriteNew(path, "{}"u8.ToArray());
        var digest = SeedGuard.Hash("{}");
        SeedFiles.WriteNew(Path.Combine(home, "checksums.txt"), System.Text.Encoding.UTF8.GetBytes($"{digest}  server-manifest.json\n"));
        SeedRelease.VerifyInventory(home, new HashSet<string> { "server-manifest.json" });
        File.WriteAllText(path, "{\"tampered\":true}");
        Assert.Throws<CliCommandException>(() => SeedRelease.VerifyInventory(home, new HashSet<string> { "server-manifest.json" }));
        File.WriteAllText(path, "{}");
        File.AppendAllText(Path.Combine(home, "checksums.txt"), $"{digest}  server-manifest.json\n");
        Assert.Throws<CliCommandException>(() => SeedRelease.VerifyInventory(home, new HashSet<string> { "server-manifest.json" }));
    }

    [Theory]
    [InlineData("seed", "--api-key", "synthetic-secret")]
    [InlineData("seed", "--domain", "127.0.0.1")]
    [InlineData("seed", "resume", "not-an-operation")]
    public async Task Seed_validation_bypasses_instance_discovery_and_does_not_echo_secret(params string[] args)
    {
        var environment = CliEnvironment.ForTests("", home);
        var console = new ConsoleFixture();
        var token = new TokenStore(environment);
        var application = new CliApplication(console, environment, token,
            new MinicloudApiClient(environment, token, new HttpClient(new NoRequests())));
        Assert.Equal(CliExitCodes.ValidationError, await application.RunAsync(args, default));
        Assert.DoesNotContain("synthetic-secret", console.Output);
        Assert.Equal("", environment.ApiBaseUrl);
        Assert.False(File.Exists(Path.Combine(home, "instances.json")));
    }

    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Instance API must not be used by offline seed.");
    }

    private sealed class ConsoleFixture : IConsole
    {
        public string Output = "";
        public bool SupportsAnsi => false;
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message;
        public void WriteError(string message) => Output += message;
        public string? ReadLine() => throw new InvalidOperationException("No input expected.");
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException("No hidden input expected.");
    }
}
