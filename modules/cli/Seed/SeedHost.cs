// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minicloud.Cli.Seed;

internal sealed partial class SeedHost(string? userKey = null, bool useAgent = false,
    Func<string, IEnumerable<string>, byte[]?, CancellationToken, bool, Task<SeedProcessResult>>? process = null,
    bool localSource = false) : ISeedHost
{
    private static string Remote(SeedState state) => "/var/lib/minicloud-seed/" + state.Id;
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private static string NullFile => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
    private IEnumerable<string> SshOptions(SeedState state, string directory) =>
    [
        "-F", NullFile, "-T", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=yes", "-o", "CheckHostIP=yes", "-o", "ConnectTimeout=20",
        "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
        "-o", "ForwardAgent=no", "-o", "ClearAllForwardings=yes", "-o", "HostKeyAlgorithms=ssh-ed25519",
        "-o", "GlobalKnownHostsFile=" + NullFile, "-o", "UserKnownHostsFile=\"" + Path.Combine(directory, "known_hosts").Replace('\\', '/').Replace("\"", "\\\"") + "\"",
        "-i", userKey ?? Path.Combine(directory, "id_ed25519"), "root@" + state.Server!.Address
    ];

    private Task<SeedProcessResult> SshAsync(SeedState state, string directory, string command, byte[]? input, CancellationToken ct) =>
        (process ?? SeedProcess.RunAsync)("ssh", SshOptions(state, directory).Append("env -i HOME=/root PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin " + command), input, ct, useAgent);

    private const string MutationLockScript = """
import fcntl, os, pathlib, stat
for parent in (pathlib.Path("/"), pathlib.Path("/opt")):
 info=parent.lstat()
 assert stat.S_ISDIR(info.st_mode) and info.st_uid == 0 and not info.st_mode & 0o022
lock_fd=os.open("/opt/minicloud.installation.lock",os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW,0o600)
info=os.fstat(lock_fd)
assert stat.S_ISREG(info.st_mode) and info.st_uid == 0 and stat.S_IMODE(info.st_mode) == 0o600
fcntl.flock(lock_fd,fcntl.LOCK_EX|fcntl.LOCK_NB)

""";

    private string MutationGuard(SeedState state)
    {
        if (userKey is null) return MutationLockScript;
        var context = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            seedId = state.Id, release = state.Plan.Selection.ServerVersion, source = state.Plan.ReleaseSource,
            previousSource = state.Plan.PreviousLocalSourceRevision,
            consoleUrl = "https://" + state.Plan.Selection.Domain, apiUrl = "https://api." + state.Plan.Selection.Domain,
            appDomain = state.Plan.Selection.AppDomain, address = state.Server!.Address,
            identity = new
            {
                provider = state.Plan.Selection.Provider, providerAccount = state.ProviderAccountId,
                serverId = state.Server.Id, region = state.Plan.Selection.Region, size = state.Plan.Selection.Size,
                image = state.Plan.Quote.Image, architecture = state.Plan.Quote.Architecture
            }
        }));
        return MutationLockScript + "\nimport base64, json\nnamespace={'__name__':'seed_inspection'}\nexec("
            + JsonSerializer.Serialize(InspectionScript) + ", namespace)\ncontext=json.loads(base64.b64decode("
            + Quote(context) + "))\n" + """
observation=namespace["inspect_host"](context["seedId"],acquire_lock=False)
assert observation["state"] in ("preparing","installed")
if observation.get("identity") is not None:
 assert observation["identity"] == context["identity"]
if observation["state"] == "installed":
 assert observation["progress"] in ("initializing","prepared","initialized")
 assert observation["release"] == context["release"]
 assert observation["source"] == context["source"] or (context["previousSource"] is not None
     and observation["source"] == context["previousSource"] and observation["progress"] == "prepared")
 assert observation["configuration"] == {key:context[key] for key in ("consoleUrl","apiUrl","appDomain")}
 assert observation["publicAddresses"] == [context["address"]]

""";
    }

    public async Task CheckPrerequisitesAsync(CancellationToken ct)
    {
        await SeedProcess.RequireAsync("ssh", ["-V"], null, ct);
        await SeedProcess.RunAsync("ssh-keygen", ["-?"], null, ct);
        if (!localSource)
        {
            var cosign = await SeedProcess.RequireAsync("cosign", ["version", "--json"], null, ct);
            SeedGuard.Require(cosign.Contains("v2.6.5"), "Install Cosign 2.6.5 independently before seed.");
        }
        await SeedProcess.RunAsync("ssh-keyscan", ["-h"], null, ct);
    }

    public async Task GenerateKeyAsync(string directory, CancellationToken ct)
    {
        var key = Path.Combine(directory, "id_ed25519");
        if (!File.Exists(key))
        {
            SeedGuard.Require(!File.Exists(key + ".pub"), "Partial generated SSH identity; preserve and reconcile it.");
            await SeedProcess.RequireAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", "minicloud-seed", "-f", key], null, ct);
        }
        SeedFiles.Check(key);
        SeedFiles.NoLinks(key + ".pub");
        var derived = await SeedProcess.RequireAsync("ssh-keygen", ["-y", "-f", key], null, ct);
        SeedGuard.Require(File.ReadAllText(key + ".pub").StartsWith(derived.Trim(), StringComparison.Ordinal), "Generated SSH identity/public key mismatch.");
        foreach (var path in new[] { key, key + ".pub" })
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Flush(true);
        }
    }

    public async Task TrustAsync(SeedState state, string directory, string? independentFingerprint, CancellationToken ct)
    {
        SeedFiles.Check(userKey ?? Path.Combine(directory, "id_ed25519"));
        if (userKey is null) SeedFiles.NoLinks(Path.Combine(directory, "id_ed25519.pub"));
        var expected = state.HostFingerprint ?? independentFingerprint!;
        SeedGuard.Require(Regex.IsMatch(expected, @"^SHA256:[A-Za-z0-9+/]{43}$"), "Confirm an exact independently obtained ed25519 SHA256 console fingerprint.");
        SeedGuard.Require(independentFingerprint is null || state.HostFingerprint is null || independentFingerprint == state.HostFingerprint,
            "An established SSH identity cannot be replaced by resume.");
        var path = Path.Combine(directory, "known_hosts");
        if (File.Exists(path))
        {
            SeedFiles.Check(path);
            SeedGuard.Require(Fingerprint(File.ReadAllText(path), state.Server!.Address!) == expected, "Pinned SSH host identity mismatch.");
            state.HostFingerprint = expected;
            return;
        }
        var candidate = await SeedProcess.RequireAsync("ssh-keyscan", ["-T", "10", "-t", "ed25519", state.Server!.Address!], null, ct);
        var lines = candidate.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => !x.StartsWith('#')).Distinct().ToArray();
        SeedGuard.Require(lines.Length == 1 && Fingerprint(lines[0], state.Server.Address!) == expected,
            "SSH candidate differs from the independent console fingerprint; no key pinned and no host secrets transmitted.");
        // The supplied independent fingerprint, not ssh-keyscan, establishes trust.
        state.HostFingerprint = expected;
        SeedFiles.WriteNew(path, Encoding.UTF8.GetBytes(lines[0] + "\n"));
    }

    internal static string Fingerprint(string line, string address)
    {
        var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        SeedGuard.Require(fields.Length == 3 && fields[0] == address && fields[1] == "ssh-ed25519", "Invalid pinned ed25519 host key.");
        byte[] key;
        try { key = Convert.FromBase64String(fields[2]); }
        catch (FormatException) { throw new Commands.CliCommandException(CliExitCodes.ValidationError, "Invalid SSH host key encoding."); }
        SeedGuard.Require(key.Length == 51 && Encoding.ASCII.GetString(key, 4, 11) == "ssh-ed25519"
            && key[0] == 0 && key[1] == 0 && key[2] == 0 && key[3] == 11
            && key[15] == 0 && key[16] == 0 && key[17] == 0 && key[18] == 32, "Invalid SSH ed25519 wire key.");
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
    }

    public async Task PrepareAsync(SeedState state, string directory, CancellationToken ct)
    {
        using var resource = typeof(SeedHost).Assembly.GetManifestResourceStream("Minicloud.Cli.Seed.prepare-host.sh")!;
        using var script = new MemoryStream(); await resource.CopyToAsync(script, ct);
        var identity = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider = state.Plan.Selection.Provider, providerAccount = state.ProviderAccountId ?? state.Plan.Quote.Account,
            serverId = state.Server!.Id, region = state.Plan.Selection.Region, size = state.Plan.Selection.Size,
            image = state.Plan.Quote.Image, architecture = state.Plan.Quote.Architecture
        }));
        var result = await SshAsync(state, directory, $"bash -s -- {Quote(state.Id)} {Quote(state.Plan.Quote.Architecture)} {Quote(identity)} "
            + Quote(localSource ? "local-source" : "github-sigstore"), script.ToArray(), ct);
        SeedGuard.Require(result.ExitCode == 0, "Dedicated Ubuntu host preparation failed. Preserve resources; inspect through pinned SSH. No host output was logged.");
    }

    internal const string OperatorKeysScript = """
import base64, json, os, pathlib, stat, sys, uuid
root = pathlib.Path("/root")
folder = root / ".ssh"
for path in (root, folder):
    if path == folder and not path.exists() and not path.is_symlink():
        path.mkdir(mode=0o700)
    info = path.lstat()
    assert stat.S_ISDIR(info.st_mode) and info.st_uid == 0 and not info.st_mode & 0o022
target = folder / "authorized_keys"
original = b""
if target.exists() or target.is_symlink():
    info = target.lstat()
    assert stat.S_ISREG(info.st_mode) and info.st_uid == 0 and not info.st_mode & 0o022
    assert info.st_size <= 1024 * 1024
    original = target.read_bytes()
keys = json.load(sys.stdin)
assert isinstance(keys, list) and 0 < len(keys) <= 16
existing = {tuple(line.split()[:2]) for line in original.splitlines()}
additions = []
for key in keys:
    assert isinstance(key, str) and "\n" not in key and "\r" not in key
    fields = key.encode("ascii").split()
    assert len(fields) == 2 and fields[0] in (b"ssh-ed25519", b"ssh-rsa")
    base64.b64decode(fields[1], validate=True)
    if tuple(fields) not in existing:
        additions.append(key.encode("ascii") + b"\n")
        existing.add(tuple(fields))
if additions:
    data = original + (b"\n" if original and not original.endswith(b"\n") else b"") + b"".join(additions)
    temporary = folder / (".minicloud-keys-" + uuid.uuid4().hex)
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, target)
    fd = os.open(folder, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
""";

    public async Task AddOperatorKeysAsync(SeedState state, string directory, CancellationToken ct)
    {
        SeedOperatorKeys.Validate(state.Plan.OperatorPublicKeys);
        var result = await SshAsync(state, directory, "python3 -B -c " + Quote(MutationGuard(state) + OperatorKeysScript),
            JsonSerializer.SerializeToUtf8Bytes(state.Plan.OperatorPublicKeys), ct);
        SeedGuard.Require(result.ExitCode == 0,
            "Operator public-key installation failed. Bootstrap access is retained; inspect and resume through pinned SSH.");
    }

    public async Task<bool> DnsReadyAsync(SeedState state, CancellationToken ct)
    {
        foreach (var hostname in new[] { state.Plan.Selection.Domain, "api." + state.Plan.Selection.Domain })
        {
            IPAddress[] addresses;
            try { addresses = await Dns.GetHostAddressesAsync(hostname, ct); }
            catch (System.Net.Sockets.SocketException) { return false; }
            if (addresses.Length == 0 || addresses.Any(x => x.ToString() != state.Server!.Address)) return false;
        }
        return true;
    }

    private async Task PutAsync(SeedState state, string directory, string path, byte[] bytes, CancellationToken ct)
    {
        const string script = """
import os, pathlib, stat, sys
p=pathlib.Path(sys.argv[1])
for parent in p.parents:
 s=parent.lstat()
 assert stat.S_ISDIR(s.st_mode) and s.st_uid == 0 and not s.st_mode & 0o022
data=sys.stdin.buffer.read()
pending=p.with_name(p.name+".pending")
assert not pending.exists() and not pending.is_symlink()
if p.exists() or p.is_symlink():
 s=p.lstat()
 assert stat.S_ISREG(s.st_mode) and s.st_uid == 0 and stat.S_IMODE(s.st_mode) in (0o400,0o600)
 assert p.read_bytes() == data
else:
 fd=os.open(pending,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
 with os.fdopen(fd,"wb") as target:
  target.write(data); target.flush(); os.fsync(target.fileno())
 os.rename(pending,p)
 fd=os.open(p.parent,os.O_RDONLY|os.O_DIRECTORY)
 try: os.fsync(fd)
 finally: os.close(fd)
""";
        var result = await SshAsync(state, directory, "python3 -B -c " + Quote(MutationGuard(state) + script) + " " + Quote(path), bytes, ct);
        SeedGuard.Require(result.ExitCode == 0, "Protected remote input conflicts or upload interrupted. Do not overwrite; reconcile exact partial input through pinned SSH.");
    }

    private static string InstallerCommand(SeedState state, string command) =>
        $"sh {Quote(Remote(state) + "/assets/install-server.sh")} --tag {Quote(state.Plan.Selection.ServerVersion)} --assets-dir {Quote(Remote(state) + "/assets")} "
        + command + $" --root /opt/minicloud --config {Quote(Remote(state) + "/configuration.json")} --recovery-dir {Quote(Remote(state) + "/recovery")} --public-address {Quote(state.Server!.Address!)}";

    public async Task<string> InstallAsync(SeedState state, string directory, bool statusOnly, CancellationToken ct)
    {
        var existingMetadata = await MetadataAsync(state, directory, ct);
        var basePath = Remote(state);
        var s = state.Plan.Selection;
        var config = JsonSerializer.SerializeToUtf8Bytes(new { consoleUrl = "https://" + s.Domain, apiUrl = "https://api." + s.Domain,
            consoleHostname = s.Domain, apiHostname = "api." + s.Domain, appDomain = s.AppDomain, manageDnsRecords = false,
            googleOidc = new { enabled = false }, sourceUrl = "https://github.com/muniventures/minicloud/tree/" + state.Plan.ReleaseSource,
            recovery = new { openBaoUnsealMaterialPath = basePath + "/recovery", backupDestination = "explicit protected workstation export required" } });
        if (!localSource || existingMetadata is null)
            await PutAsync(state, directory, basePath + "/configuration.json", config, ct);
        if (localSource)
        {
            SeedGuard.Require(state.Plan.ArtifactTrust == SeedLocalSource.Trust && !statusOnly,
                "Local source installation requires an explicit reviewed local-source plan.");
            var localAssets = basePath + "/assets/" + state.Plan.ReleaseDigest;
            var mkdir = await SshAsync(state, directory, "python3 -B -c " + Quote(MutationGuard(state)
                + "\np=pathlib.Path(" + JsonSerializer.Serialize(localAssets) + ")\n"
                + "if not p.exists() and not p.is_symlink(): p.mkdir(mode=0o700)\n"
                + "s=p.lstat()\nassert stat.S_ISDIR(s.st_mode) and s.st_uid == 0 and stat.S_IMODE(s.st_mode) == 0o700\n"), null, ct);
            SeedGuard.Require(mkdir.ExitCode == 0, "Protected source asset directory conflicts.");
            foreach (var name in new[] { "source.tar.gz", "local-source.py" })
            {
                var path = Path.Combine(directory, "release", name);
                SeedFiles.Check(path);
                await PutAsync(state, directory, localAssets + "/" + name, await File.ReadAllBytesAsync(path, ct), ct);
            }
            // Pinned SSH plus the plan-bound archive and runner hashes replace GitHub's release authority.
            var runnerDigest = Convert.ToHexStringLower(SHA256.HashData(
                await File.ReadAllBytesAsync(Path.Combine(directory, "release", "local-source.py"), ct)));
            var verify = $"cd {Quote(localAssets)} && printf '%s  %s\\n' {Quote(state.Plan.ReleaseDigest)} source.tar.gz "
                + $"{Quote(runnerDigest)} local-source.py | sha256sum --check --strict --status && "
                + $"python3 -B local-source.py install --archive {Quote(localAssets + "/source.tar.gz")} "
                + $"--sha256 {Quote(state.Plan.ReleaseDigest)} --tag {Quote(s.ServerVersion)} "
                + $"--config {Quote(basePath + "/configuration.json")} --recovery-dir {Quote(basePath + "/recovery")} "
                + $"--public-address {Quote(state.Server!.Address!)}"
                + (state.Plan.PreviousLocalSourceDigest is { } previous ? " --previous-source-sha256 " + Quote(previous) : "");
            var installed = await SshAsync(state, directory, "bash -c " + Quote(verify), null, ct);
            await MetadataAsync(state, directory, ct);
            SeedGuard.Require(installed.ExitCode is 0 or 2,
                "Local source build/installation failed; preserve this target, source hash, build logs and recovery material.");
            using var localResult = JsonDocument.Parse(installed.Output);
            return localResult.RootElement.GetProperty("state").GetString()!;
        }
        foreach (var name in SeedRelease.Names(s.ServerVersion))
        {
            var local = Path.Combine(directory, "release", name); SeedFiles.Check(local);
            await PutAsync(state, directory, basePath + "/assets/" + name, await File.ReadAllBytesAsync(local, ct), ct);
        }
        // Independent verification on the host precedes any downloaded installer/verifier execution.
        var verification = $"cd {Quote(basePath + "/assets")} && cosign verify-blob --new-bundle-format --bundle checksums.txt.sigstore.json --certificate-identity "
            + Quote($"https://github.com/muniventures/minicloud/.github/workflows/server-release.yml@refs/tags/{s.ServerVersion}")
            + " --certificate-oidc-issuer https://token.actions.githubusercontent.com checksums.txt >/dev/null 2>&1 && sha256sum --check --strict checksums.txt >/dev/null";
        var verified = await SshAsync(state, directory, "bash -c " + Quote(verification), null, ct);
        SeedGuard.Require(verified.ExitCode == 0, "Host independent release signature/checksum verification failed; installer was not executed.");
        var metadata = await MetadataAsync(state, directory, ct);
        if (statusOnly && metadata is null) return "incomplete";
        var command = metadata is not null && (metadata.Value.GetProperty("phase").GetString() is "ready" or "awaiting_operator" or "awaiting_dns_tls") ? "status" : "install";
        var result = await SshAsync(state, directory, InstallerCommand(state, command), null, ct);
        // Read exact installation identities even on an interrupted installer failure.
        await MetadataAsync(state, directory, ct);
        SeedGuard.Require(result.ExitCode is 0 or 2, "Signed installer failed; original identities/volumes are retained. Resume after protected host reconciliation.");
        using var json = JsonDocument.Parse(result.Output);
        var phase = json.RootElement.GetProperty("state").GetString()!;
        SeedGuard.Require(phase is "ready" or "awaiting_operator" or "awaiting_dns_tls", "Unexpected signed installer readiness state.");
        return phase;
    }

    private async Task<JsonElement?> MetadataAsync(SeedState state, string directory, CancellationToken ct)
    {
        const string script = """
import json, pathlib, stat, sys
binding=pathlib.Path("/opt/minicloud.seed-operation")
for parent in binding.parents:
 s=parent.lstat()
 assert stat.S_ISDIR(s.st_mode) and s.st_uid == 0 and not s.st_mode & 0o022
s=binding.lstat()
assert stat.S_ISREG(s.st_mode) and s.st_uid == 0 and stat.S_IMODE(s.st_mode) == 0o600
assert binding.read_text().strip() == sys.argv[1]
p=pathlib.Path("/opt/minicloud/installation.json")
if p.exists():
 for parent in p.parents:
  s=parent.lstat()
  assert stat.S_ISDIR(s.st_mode) and s.st_uid == 0 and not s.st_mode & 0o022
 s=p.lstat()
 assert stat.S_ISREG(s.st_mode) and s.st_uid == 0 and stat.S_IMODE(s.st_mode) in (0o400,0o600)
 print(p.read_text())
else:
 assert not p.is_symlink()
 print("null")
""";
        var result = await SshAsync(state, directory, "python3 -c " + Quote(script) + " " + Quote(state.Id), null, ct);
        SeedGuard.Require(result.ExitCode == 0, "Protected installation metadata is unavailable; preserve state.");
        using var json = JsonDocument.Parse(result.Output);
        if (json.RootElement.ValueKind == JsonValueKind.Null) return null;
        var value = json.RootElement;
        var id = value.GetProperty("installationId").GetString()!;
        var operation = value.GetProperty("operationId").GetString()!;
        SeedGuard.Require(value.GetProperty("schemaVersion").GetInt32() == 1 && value.GetProperty("product").GetString() == "minicloud"
            && Guid.TryParse(id, out _) && Guid.TryParse(operation, out _)
            && (state.InstallationId is null || state.InstallationId == id)
            && (state.InstallerOperationId is null || state.InstallerOperationId == operation)
            && value.GetProperty("releaseTag").GetString() == state.Plan.Selection.ServerVersion
            && (value.GetProperty("sourceRevision").GetString() == state.Plan.ReleaseSource
                || state.Plan.PreviousLocalSourceRevision is { } previousSource
                    && value.GetProperty("sourceRevision").GetString() == previousSource
                    && value.GetProperty("phase").GetString() == "prepared")
            && value.GetProperty("root").GetString() == "/opt/minicloud"
            && value.GetProperty("recoveryDir").GetString() == Remote(state) + "/recovery"
            && value.GetProperty("publicAddresses").EnumerateArray().Select(x => x.GetString()).SequenceEqual([state.Server!.Address])
            && value.GetProperty("configuration").GetProperty("consoleUrl").GetString() == "https://" + state.Plan.Selection.Domain
            && value.GetProperty("configuration").GetProperty("apiUrl").GetString() == "https://api." + state.Plan.Selection.Domain
            && value.GetProperty("configuration").GetProperty("appDomain").GetString() == state.Plan.Selection.AppDomain,
            "Remote installation identity/configuration differs; never adopt it.");
        state.InstallationId = id; state.InstallerOperationId = operation;
        return value.Clone();
    }

    public async Task ExportAsync(SeedState state, string directory, string destination, bool setupToken, CancellationToken ct)
    {
        var metadata = await MetadataAsync(state, directory, ct);
        SeedGuard.Require(metadata is not null && state.InstallationId is not null, "Installation recovery is not available yet.");
        destination = Path.GetFullPath(destination);
        SeedGuard.Require(state.RecoveryDirectory is null || state.RecoveryDirectory == destination, "Resume recovery export at its original local path.");
        if (state.RecoveryDirectory is null && userKey is null)
            SeedGuard.Require(!Directory.Exists(destination) && !File.Exists(destination), "Choose a new independent local recovery directory.");
        if (userKey is not null && Directory.Exists(destination)) SeedFiles.Check(destination, true);
        SeedFiles.Directory(destination);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["openbao-recovery.json"] = Remote(state) + "/recovery/" + state.InstallationId + "/openbao-recovery.json",
            ["openbao-root-token"] = Remote(state) + "/recovery/" + state.InstallationId + "/openbao-root-token",
            ["openbao-ca.key"] = Remote(state) + "/recovery/" + state.InstallationId + "/openbao-ca.key",
            ["openbao-ca.crt"] = "/opt/minicloud/secrets/openbao-ca.crt",
            ["installation.json"] = "/opt/minicloud/installation.json"
        };
        if (setupToken)
        {
            var name = metadata!.Value.GetProperty("setupTokenFile").GetString()!;
            SeedGuard.Require(Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$"), "Invalid host token output name.");
            paths[name] = "/opt/minicloud/secrets/bootstrap/output/" + name;
            state.SetupTokenExportPath = Path.Combine(destination, name);
        }
        foreach (var (name, remote) in paths)
        {
            const string script = """
import base64, pathlib, stat, sys
p=pathlib.Path(sys.argv[1])
for parent in p.parents:
 s=parent.lstat()
 assert stat.S_ISDIR(s.st_mode) and s.st_uid == 0 and not s.st_mode & 0o022
s=p.lstat()
assert stat.S_ISREG(s.st_mode) and s.st_uid == 0 and stat.S_IMODE(s.st_mode) in (0o400,0o600) and 0 < s.st_size < 1048576
print(base64.b64encode(p.read_bytes()).decode())
""";
            var result = await SshAsync(state, directory, "python3 -c " + Quote(script) + " " + Quote(remote), null, ct);
            SeedGuard.Require(result.ExitCode == 0, "Protected recovery export failed; no completion claim. Preserve host material.");
            var bytes = Convert.FromBase64String(result.Output.Trim());
            var local = Path.Combine(destination, name);
            if (File.Exists(local))
            {
                SeedFiles.Check(local);
                if (name == "installation.json")
                {
                    using var original = JsonDocument.Parse(File.ReadAllBytes(local));
                    using var current = JsonDocument.Parse(bytes);
                    SeedGuard.Require(new[] { "installationId", "operationId", "releaseTag", "sourceRevision", "configuration" }
                        .All(key => JsonElement.DeepEquals(original.RootElement.GetProperty(key), current.RootElement.GetProperty(key))),
                        "Exported installation snapshot belongs to another configuration/identity.");
                }
                else SeedGuard.Require(File.ReadAllBytes(local).SequenceEqual(bytes), "Recovery export conflicts with existing bytes; never overwrite.");
            }
            else SeedFiles.WriteNew(local, bytes);
        }
        state.RecoveryDirectory = destination;
        state.RecoveryExported = true;
    }

    public async Task RotateTokenAsync(SeedState state, string directory, string outputName, CancellationToken ct)
    {
        SeedGuard.Require(Regex.IsMatch(outputName, "^seed-token-[0-9a-f]{32}$"), "Invalid pending setup-token output identity.");
        var metadata = await MetadataAsync(state, directory, ct);
        SeedGuard.Require(metadata is not null, "No installed token producer to rotate.");
        if (metadata!.Value.GetProperty("setupTokenFile").GetString() == outputName) return;
        SeedGuard.Require(await InstallAsync(state, directory, true, ct) == "awaiting_operator", "Only the existing unclaimed operator setup may rotate its token.");
        var result = await SshAsync(state, directory, InstallerCommand(state, "rotate-token --token-output-name " + outputName), null, ct);
        SeedGuard.Require(result.ExitCode is 0 or 2, "Shared token rotation is incomplete/uncertain. Preserve the pending output name; reconcile on host rather than issuing another token.");
        metadata = await MetadataAsync(state, directory, ct);
        SeedGuard.Require(metadata!.Value.GetProperty("setupTokenFile").GetString() == outputName, "Token producer output identity is uncertain; preserve pending output.");
    }

    public async Task VerifyHttpsAsync(SeedState state, CancellationToken ct)
    {
        SeedGuard.Require(await DnsReadyAsync(state, ct), "Dashboard/API DNS no longer resolves exclusively to the recorded control-plane host.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var s = state.Plan.Selection; var api = "https://api." + s.Domain;
        using var response = await http.GetAsync(api + "/api/instance", ct);
        SeedGuard.Require(response.StatusCode == HttpStatusCode.OK, "Exact-origin trusted HTTPS discovery failed.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var value = json.RootElement;
        SeedGuard.Require(value.GetProperty("product").GetString() == "minicloud" && value.GetProperty("protocolVersion").GetInt32() == 1
            && value.GetProperty("serverVersion").GetString() == s.ServerVersion[8..]
            && value.GetProperty("sourceRevision").GetString() == state.Plan.ReleaseSource
            && value.GetProperty("appDomain").GetString() == s.AppDomain, "HTTPS instance product/release/origin contract mismatch.");
        SeedRelease.RequireCompatible(value.GetProperty("cliCompatibility"));
        using var health = await http.GetAsync(api + "/health", ct);
        SeedGuard.Require(health.StatusCode == HttpStatusCode.OK && await health.Content.ReadAsStringAsync(ct) == "Healthy", "HTTPS health unavailable.");
        using var setup = await http.GetAsync("https://" + s.Domain + "/setup", ct);
        SeedGuard.Require(setup.StatusCode == HttpStatusCode.OK && setup.Content.Headers.ContentType?.MediaType == "text/html", "HTTPS browser setup route unavailable.");
    }
}
