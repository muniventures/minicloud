// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Seed;

namespace Minicloud.Tests;

public sealed class SeedLifecycleTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(".local", "artifacts", "seed-tests-" + Guid.NewGuid().ToString("N")));
    private readonly FakeProvider provider = new();
    private readonly FakeHost host = new();
    private readonly List<string> snapshots = [];
    private readonly List<string> output = [];
    private SeedLifecycle Lifecycle => new(provider, host, state => snapshots.Add(JsonSerializer.Serialize(state, SeedJournal.Json)), output.Add);
    private SeedState State() => new() { Id = new string('a', 32), Plan = new(new("hetzner", "region", "size", "cloud.example.com", "apps.example.com", "server-v1.2.3"),
        provider.Quote, new string('b', 64), new string('c', 40)) };
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public async Task Refusing_exact_plan_never_generates_keys_or_mutates_provider()
    {
        var state = State();
        await Lifecycle.ResumeAsync(state, directory, "yes", null, default);
        Assert.Equal(0, provider.Creates);
        Assert.Equal(0, provider.KeyCreates);
        Assert.False(host.KeyGenerated);
        Assert.Equal("planned", state.Phase);
    }

    [Fact]
    public async Task Changed_cost_invalidates_previous_approval()
    {
        var state = State(); var approval = state.Plan.Approval;
        provider.Quote = provider.Quote with { Costs = [provider.Quote.Costs[0] with { Monthly = 999 }] };
        await Lifecycle.ResumeAsync(state, directory, approval, null, default);
        Assert.NotEqual(approval, state.Plan.Approval);
        Assert.Equal(0, provider.Creates);
    }

    [Fact]
    public async Task Create_intent_is_persisted_before_dispatch_and_timeout_cannot_create_again()
    {
        var state = State();
        provider.CreateError = new IOException("synthetic response lost");
        await Assert.ThrowsAsync<IOException>(() => Lifecycle.ResumeAsync(state, directory, state.Plan.Approval, null, default));
        Assert.Contains(snapshots, x => JsonDocument.Parse(x).RootElement.GetProperty("serverCreateStarted").GetBoolean());
        Assert.True(state.ServerCreateStarted);
        await Lifecycle.ResumeAsync(state, directory, state.Plan.Approval, null, default);
        Assert.Equal(1, provider.Creates);
        Assert.Equal("reconciliation_required", state.Phase);
    }

    [Fact]
    public async Task Timeout_reconciles_exact_tagged_resource_without_duplicate_or_untrusted_ssh()
    {
        var state = State(); state.ServerCreateStarted = true;
        provider.Server = provider.NewServer(state.Id);
        await Lifecycle.ResumeAsync(state, directory, null, null, default);
        Assert.Equal(0, provider.Creates);
        Assert.Equal("server", state.Server!.Id);
        Assert.Equal("awaiting_ssh_trust", state.Phase);
        Assert.False(host.Prepared);
    }

    [Fact]
    public async Task Changed_account_or_placement_cannot_be_adopted()
    {
        var state = State(); state.ServerCreateStarted = true;
        provider.Server = provider.NewServer(state.Id) with { Region = "unrelated" };
        await Assert.ThrowsAsync<CliCommandException>(() => Lifecycle.ResumeAsync(state, directory, null, null, default));
        Assert.False(host.Prepared);
        Assert.Equal(0, provider.Creates);
    }

    [Fact]
    public async Task Host_identity_mismatch_never_prepares_or_installs()
    {
        var state = State(); state.ServerCreateStarted = true;
        provider.Server = provider.NewServer(state.Id); host.TrustError = true;
        await Assert.ThrowsAsync<CliCommandException>(() => Lifecycle.ResumeAsync(state, directory, null, "SHA256:independent", default));
        Assert.False(host.Prepared);
        Assert.False(state.InstallStarted);
    }

    [Fact]
    public async Task Dns_wait_retains_billable_host_and_separates_app_routing()
    {
        var state = State(); state.ServerCreateStarted = true;
        provider.Server = provider.NewServer(state.Id);
        host.DnsReady = false;
        await Lifecycle.ResumeAsync(state, directory, null, "SHA256:independent", default);
        Assert.Equal("awaiting_dns", state.Phase);
        Assert.False(state.InstallStarted);
        Assert.Contains(output, x => x.Contains("api.cloud.example.com. A 203.0.113.1"));
        Assert.Contains(output, x => x.Contains("Do NOT point *.apps.example.com"));
    }

    [Fact]
    public async Task Installer_intent_precedes_remote_call_and_ready_requires_independent_recovery_ack()
    {
        var state = State(); state.ServerCreateStarted = true;
        provider.Server = provider.NewServer(state.Id); host.Result = "ready";
        host.BeforeInstall = () => Assert.Contains(snapshots, x => JsonDocument.Parse(x).RootElement.GetProperty("installStarted").GetBoolean());
        await Lifecycle.ResumeAsync(state, directory, null, "SHA256:independent", default);
        Assert.NotEqual("complete", state.Phase);
        state.RecoveryExported = true; state.RecoveryAcknowledged = true;
        await Lifecycle.ResumeAsync(state, directory, null, null, default);
        Assert.Equal("complete", state.Phase);
        Assert.Equal(0, provider.Creates);
    }

    [Fact]
    public async Task Https_failure_is_not_completion_or_permission_to_cleanup()
    {
        var state = State(); state.ServerCreateStarted = true; state.RecoveryExported = true; state.RecoveryAcknowledged = true;
        provider.Server = provider.NewServer(state.Id); host.Result = "ready"; host.HttpsError = true;
        await Assert.ThrowsAsync<CliCommandException>(() => Lifecycle.ResumeAsync(state, directory, null, "SHA256:independent", default));
        Assert.NotEqual("complete", state.Phase);
        await Assert.ThrowsAsync<CliCommandException>(() => Lifecycle.CleanupAsync(state, directory, "anything", default));
        Assert.Equal(0, provider.Deletes);
    }

    [Fact]
    public async Task Cleanup_needs_distinct_resource_bound_approval_and_never_uses_catalog()
    {
        var state = State(); state.ServerCreateStarted = true; state.Server = provider.NewServer(state.Id); provider.Server = state.Server;
        await Lifecycle.CleanupAsync(state, directory, state.Plan.Approval, default);
        Assert.Equal(0, provider.Deletes);
        Assert.Equal(0, provider.Quotes);
        var digest = output.Single(x => x.StartsWith("Cleanup approval: ")).Split(": ")[1];
        await Lifecycle.CleanupAsync(state, directory, digest, default);
        Assert.Equal(1, provider.Deletes);
        Assert.Equal("cleaned", state.Phase);
    }

    [Fact]
    public async Task Cleanup_refuses_unresolved_creation_even_when_inventory_is_empty()
    {
        var state = State(); state.ServerCreateStarted = true;
        await Assert.ThrowsAsync<CliCommandException>(() => Lifecycle.CleanupAsync(state, directory, "anything", default));
        Assert.Equal(0, provider.Deletes);
    }

    [Fact]
    public void Approval_and_journal_do_not_contain_provider_credential()
    {
        const string credential = "synthetic-secret-provider-token";
        var state = State();
        state.Plan = state.Plan with { Quote = state.Plan.Quote with { CredentialScope = SeedGuard.Scope(credential, state.Id) } };
        Assert.DoesNotContain(credential, JsonSerializer.Serialize(state, SeedJournal.Json));
        Assert.NotEqual(state.Plan.Approval, (state.Plan with { Selection = state.Plan.Selection with { Region = "other" } }).Approval);
        Assert.Equal(state.Plan.Approval, (state.Plan with { Quote = state.Plan.Quote with { RetrievedAt = DateTimeOffset.UtcNow } }).Approval);
    }

    [Theory]
    [InlineData("127.0.0.1", "apps.example.com")]
    [InlineData("http://cloud.example.com", "apps.example.com")]
    [InlineData("cloud.example.com", "example.com")]
    [InlineData("cloud.example.com", "api.cloud.example.com")]
    [InlineData("cloud.localhost", "apps.example.com")]
    public void Invalid_or_overlapping_origins_fail_before_creation(string domain, string appDomain) =>
        Assert.Throws<CliCommandException>(() => (State().Plan.Selection with { Domain = domain, AppDomain = appDomain }).Validate());

    [Theory]
    [InlineData("../minicloud")]
    [InlineData("/minicloud")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("C:minicloud")]
    public void Unsafe_release_members_are_rejected(string name) => Assert.False(SeedRelease.SafeMember(name));

    [Fact]
    public void Ssh_fingerprint_binds_the_wire_key_and_exact_address()
    {
        byte[] key = [0, 0, 0, 11, .. Encoding.ASCII.GetBytes("ssh-ed25519"), 0, 0, 0, 32, .. Enumerable.Repeat((byte)7, 32)];
        var encoded = Convert.ToBase64String(key);
        Assert.Equal("SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('='),
            SeedHost.Fingerprint("203.0.113.1 ssh-ed25519 " + encoded, "203.0.113.1"));
        Assert.Throws<CliCommandException>(() => SeedHost.Fingerprint("203.0.113.2 ssh-ed25519 " + encoded, "203.0.113.1"));
    }

    private sealed class FakeProvider : ISeedProvider
    {
        internal SeedQuote Quote = new("synthetic-account", "scope", "image", "linux/amd64", "root", 2, 2048, 40,
            [new("synthetic fixture VPS", 12, 0.02m, "USD", "synthetic monthly", "synthetic exclusions", "https://provider.invalid/pricing")], DateTimeOffset.UnixEpoch, "synthetic");
        internal int Creates, KeyCreates, Deletes, Quotes;
        internal Exception? CreateError;
        internal SeedResource? Server;
        internal SeedResource NewServer(string operation) => new("server", operation, "region", "size", "image", "203.0.113.1");
        public string CredentialScope(string operation) => Quote.CredentialScope;
        public Task<SeedQuote> QuoteAsync(SeedSelection s, string operation, CancellationToken ct) { Quotes++; return Task.FromResult(Quote); }
        public Task<string?> FindKeyAsync(string operation, string key, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string> CreateKeyAsync(string operation, string key, CancellationToken ct) { KeyCreates++; return Task.FromResult("key"); }
        public Task<SeedResource?> FindServerAsync(string operation, CancellationToken ct) => Task.FromResult(Server);
        public Task<SeedResource> CreateServerAsync(SeedState state, CancellationToken ct)
        {
            Creates++;
            if (CreateError is not null) throw CreateError;
            Server = NewServer(state.Id);
            return Task.FromResult(Server);
        }
        public Task<bool> DeleteServerAsync(SeedState state, CancellationToken ct) { Deletes++; Server = null; return Task.FromResult(true); }
        public Task DeleteKeyAsync(string id, string operation, string key, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeHost : ISeedHost
    {
        internal bool KeyGenerated, Prepared, TrustError, HttpsError, DnsReady = true;
        internal string Result = "awaiting_operator";
        internal Action? BeforeInstall;
        public Task CheckPrerequisitesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task GenerateKeyAsync(string directory, CancellationToken ct)
        {
            KeyGenerated = true; Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "id_ed25519.pub"), "synthetic-public-key"); return Task.CompletedTask;
        }
        public Task TrustAsync(SeedState state, string directory, string? fingerprint, CancellationToken ct)
        {
            if (TrustError) throw new CliCommandException(2, "synthetic SSH mismatch");
            state.HostFingerprint = fingerprint ?? state.HostFingerprint; return Task.CompletedTask;
        }
        public Task PrepareAsync(SeedState state, string directory, CancellationToken ct) { Prepared = true; return Task.CompletedTask; }
        public Task<bool> DnsReadyAsync(SeedState state, CancellationToken ct) => Task.FromResult(DnsReady);
        public Task<string> InstallAsync(SeedState state, string directory, bool statusOnly, CancellationToken ct) { BeforeInstall?.Invoke(); return Task.FromResult(Result); }
        public Task ExportAsync(SeedState state, string directory, string destination, bool token, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyHttpsAsync(SeedState state, CancellationToken ct)
        {
            if (HttpsError) throw new CliCommandException(2, "synthetic TLS mismatch");
            return Task.CompletedTask;
        }
    }
}
