using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minicloud.Worker.Pipeline.Steps;

internal static class RuntimeAgentVerification
{
    // Exit codes describe bounded repair actions. Credentials stay on the target host.
    internal const string Script = """
import base64, hashlib, hmac, json, os, pathlib, pwd, stat, subprocess, sys
expected = json.loads(base64.b64decode(sys.argv[1]))
root = pathlib.Path('/opt/minicloud/agent')
try:
    agent = pwd.getpwnam('minicloud-agent')
    if root.is_symlink() or root.stat().st_uid != 0 or root.stat().st_mode & 0o022:
        sys.exit(12)
    for name, digest in expected['files'].items():
        path = root / name
        mode = path.lstat()
        if not stat.S_ISREG(mode.st_mode) or mode.st_uid != 0 or mode.st_mode & 0o022:
            sys.exit(12)
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            sys.exit(12)
    unit = pathlib.Path('/etc/systemd/system/minicloud-agent.service')
    if unit.is_symlink() or unit.read_bytes() != (root / 'minicloud-agent.service').read_bytes():
        sys.exit(12)
    running = subprocess.run(['systemctl', 'is-active', '--quiet', 'minicloud-agent.service']).returncode == 0
    for field, wanted in [('FragmentPath', '/etc/systemd/system/minicloud-agent.service'), ('User', 'minicloud-agent'), ('DropInPaths', '')]:
        if subprocess.check_output(['systemctl', 'show', '-p', field, '--value', 'minicloud-agent.service'], text=True).strip() != wanted:
            sys.exit(12)
    sudoers = pathlib.Path('/etc/sudoers.d/minicloud-agent-routing')
    if sudoers.is_symlink() or sudoers.stat().st_uid != 0 or stat.S_IMODE(sudoers.stat().st_mode) != 0o440 or sudoers.read_text().strip() != 'minicloud-agent ALL=(root) NOPASSWD: /opt/minicloud/agent/apply-minicloud-routing ""':
        sys.exit(12)
    if subprocess.check_output(['systemctl', 'show', '-p', 'NeedDaemonReload', '--value', 'minicloud-agent.service'], text=True).strip() != 'no':
        sys.exit(12)
    pid = subprocess.check_output(['systemctl', 'show', '-p', 'MainPID', '--value', 'minicloud-agent.service'], text=True).strip()
    if running and (not pid.isdigit() or pathlib.Path('/proc/' + pid + '/exe').resolve() != root / 'minicloud-agent'):
        sys.exit(12)
    configuration = root / 'config.json'
    token = root / 'runtime-token'
    if configuration.is_symlink() or token.is_symlink() or configuration.stat().st_mode & 0o027 or token.stat().st_mode & 0o077 or configuration.stat().st_uid != 0 or token.stat().st_uid != agent.pw_uid or configuration.stat().st_gid != agent.pw_gid:
        sys.exit(12)
    credential = expected.get('credentialHash')
    if not credential:
        sys.exit(11)
    kind, rounds, salt, wanted = credential.split('$')
    if kind != 'pbkdf2_sha256' or not 1 <= int(rounds) <= 1000000:
        sys.exit(11)
    actual = hashlib.pbkdf2_hmac('sha256', token.read_text().strip().encode(), base64.b64decode(salt), int(rounds))
    if not hmac.compare_digest(actual, base64.b64decode(wanted)):
        sys.exit(11)
    if json.loads(configuration.read_text()) != expected['config']:
        sys.exit(10)
    if not running:
        sys.exit(13)
except (OSError, ValueError, KeyError):
    sys.exit(12)
sys.exit(0)
""";

    internal static IReadOnlyDictionary<string, string> Fingerprints(string bundlePath) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minicloud-agent"] = Digest(bundlePath, "minicloud-agent"),
            ["apply-minicloud-routing"] = Digest(bundlePath, "apply-minicloud-routing.py"),
            ["minicloud-agent.service"] = Digest(bundlePath, "minicloud-agent.service"),
            ["install-minicloud-agent.sh"] = Digest(bundlePath, "install-minicloud-agent.sh")
        };

    private static string Digest(string root, string name)
    {
        using var input = File.OpenRead(Path.Combine(root, name));
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    internal static string Encode(object config, IReadOnlyDictionary<string, string> files, string? credentialHash) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { config, files, credentialHash }));

    internal static string Command(string verification) =>
        "sudo python3 -c '" + Script.Replace("'", "'\\''", StringComparison.Ordinal) + "' '" + verification + "'";
}
