// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minicloud.Cli.Seed;

internal static class SeedFiles
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void NoLinks(string path)
    {
        SeedGuard.Require(!path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Contains("..")
            && !(OperatingSystem.IsWindows() && Path.GetFullPath(path).StartsWith(@"\\")), "Use local paths without parent traversal.");
        for (var entry = Path.GetFullPath(path); entry is not null; entry = Path.GetDirectoryName(entry))
        {
            SeedGuard.Require(!File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint), "Seed paths must not contain links or reparse points.");
            if (!OperatingSystem.IsWindows() && System.IO.Directory.Exists(entry))
                SeedGuard.Require((File.GetUnixFileMode(entry) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
                    "Seed path ancestors must not be group/world writable.");
        }
    }

    public static void Directory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!System.IO.Directory.Exists(full))
        {
            var parent = Path.GetDirectoryName(full)!;
            if (!System.IO.Directory.Exists(parent)) Directory(parent);
            NoLinks(parent);
            if (OperatingSystem.IsWindows())
            {
                System.IO.Directory.CreateDirectory(full);
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                var sid = WindowsIdentity.GetCurrent().User!;
                acl.SetOwner(sid);
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(full).SetAccessControl(acl);
            }
            else System.IO.Directory.CreateDirectory(full, DirectoryMode);
        }
        Check(full, true);
    }

    public static void Check(string path, bool directory = false)
    {
        NoLinks(path);
        SeedGuard.Require(System.IO.Directory.Exists(path) == directory, "Expected a protected regular file/directory.");
        if (OperatingSystem.IsWindows())
        {
            var acl = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var sid = WindowsIdentity.GetCurrent().User!;
            SeedGuard.Require(acl.GetOwner(typeof(SecurityIdentifier)).Equals(sid), "Protected inputs must be owned by the current operator.");
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                SeedGuard.Require(rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Equals(sid)
                    || rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))
                    || rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)),
                    "Protected inputs must have an owner-only Windows ACL (disable inherited grants).");
        }
        else
        {
            var mode = File.GetUnixFileMode(path);
            SeedGuard.Require(directory ? mode == DirectoryMode : mode is FileModeBits or UnixFileMode.UserRead,
                "Seed directories require 0700; secret inputs require 0600 or 0400.");
            var owner = SeedProcess.RunAsync("stat", OperatingSystem.IsMacOS() ? ["-f", "%u:%HT", path] : ["-c", "%u:%F", "--", path], null, default).GetAwaiter().GetResult();
            var current = SeedProcess.RunAsync("id", ["-u"], null, default).GetAwaiter().GetResult();
            var fields = owner.Output.Trim().Split(':', 2);
            SeedGuard.Require(owner.ExitCode == 0 && current.ExitCode == 0 && fields.Length == 2 && fields[0] == current.Output.Trim()
                && (directory ? fields[1].Equals("directory", StringComparison.OrdinalIgnoreCase)
                    : Regex.IsMatch(fields[1], "^regular (empty )?file$", RegexOptions.IgnoreCase)),
                "Protected inputs must be regular files/directories owned by the current operator.");
        }
    }

    public static FileStream Create(string path)
    {
        Check(Path.GetDirectoryName(Path.GetFullPath(path))!, true);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
        var stream = new FileStream(path, options);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var acl = new FileSecurity();
                var sid = WindowsIdentity.GetCurrent().User!;
                acl.SetOwner(sid);
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                stream.SetAccessControl(acl);
            }
            catch { stream.Dispose(); throw; }
        }
        return stream;
    }

    public static void WriteNew(string path, byte[] bytes)
    {
        using var stream = Create(path);
        stream.Write(bytes);
        stream.Flush(true);
    }

    public static string Credential(string? path, IConsole console)
    {
        string value;
        if (path is not null)
        {
            Check(path);
            SeedGuard.Require(new FileInfo(path).Length is > 0 and <= 16384, "Credential file must contain one bounded API credential.");
            value = File.ReadAllText(path).Trim();
        }
        else
        {
            SeedGuard.Require(!Console.IsInputRedirected, "Noninteractive seed requires --credential-file <owner-only path>.");
            console.Write("Provider API credential (hidden): ");
            var secret = new StringBuilder();
            while (true)
            {
                var key = console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Escape) throw new Commands.CliCommandException(CliExitCodes.ValidationError, "Credential entry canceled; no credential retained.");
                if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
                else if (!char.IsControl(key.KeyChar) && secret.Length < 16384) secret.Append(key.KeyChar);
            }
            console.WriteLine();
            value = secret.ToString().Trim();
        }
        SeedGuard.Require(value.Length > 0 && !value.Any(char.IsWhiteSpace), "Credential must be one nonempty value.");
        return value;
    }
}

internal sealed class SeedJournal : IDisposable
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public string DirectoryPath { get; }
    private readonly FileStream operationLock;

    public SeedJournal(string home, string id, bool create = false)
    {
        SeedGuard.Require(Regex.IsMatch(id, "^[0-9a-f]{32}$"), "Invalid seed operation ID.");
        DirectoryPath = Path.Combine(home, "seed", id);
        if (create) SeedFiles.Directory(DirectoryPath);
        else SeedFiles.Check(DirectoryPath, true);
        var path = Path.Combine(DirectoryPath, "operation.lock");
        try
        {
            if (!File.Exists(path))
            {
                SeedGuard.Require(create, "Persistent operation lock is missing. Reconcile; never replace a lock for an existing operation.");
                using var created = SeedFiles.Create(path);
            }
            SeedFiles.Check(path);
            operationLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { throw new Commands.CliCommandException(CliExitCodes.ValidationError, "Seed operation is locked; do not remove its persistent lock."); }
    }

    public SeedState Load()
    {
        SeedGuard.Require(!File.Exists(Path.Combine(DirectoryPath, "operation.pending")),
            "An interrupted journal write remains. Preserve operation.pending and operation.json; reconcile before resume. No mutation is safe.");
        var path = Path.Combine(DirectoryPath, "operation.json");
        SeedFiles.Check(path);
        var state = JsonSerializer.Deserialize<SeedState>(File.ReadAllText(path), Json)
            ?? throw new JsonException();
        SeedGuard.Require(state.Schema == 1 && state.Id == Path.GetFileName(DirectoryPath), "Unsupported or conflicting seed journal.");
        SeedGuard.Require(state.Plan is not null && state.Plan.Quote is not null && state.Plan.Selection is not null
            && Regex.IsMatch(state.Plan.ReleaseDigest ?? "", "^[0-9a-f]{64}$")
            && Regex.IsMatch(state.Plan.ReleaseSource ?? "", "^[0-9a-f]{40}$")
            && Regex.IsMatch(state.Plan.Quote.CredentialScope ?? "", "^[0-9a-f]{64}$")
            && state.Plan.Quote.SshUser == "root" && (state.Plan.Quote.Architecture is "linux/amd64" or "linux/arm64")
            && state.Plan.Quote.Costs is { Length: > 0 }
            && state.Plan.Quote.Costs.All(x => x is not null && x.Monthly > 0 && !string.IsNullOrWhiteSpace(x.Currency)),
            "Incomplete or corrupt seed plan; never treat it as a new provisioning operation.");
        SeedGuard.Require(state.Phase is "planned" or "creating" or "awaiting_ssh_trust" or "preparing_host" or "awaiting_dns"
            or "installing" or "awaiting_browser_setup" or "complete" or "reconciliation_required" or "failed" or "cleanup_pending" or "cleaned",
            "Unknown durable seed phase.");
        SeedGuard.Require((state.Server is null || state.ServerCreateStarted)
            && (!state.ServerCreateStarted || state.SshKeyId is not null)
            && (!state.InstallStarted || state.HostPrepared && state.Server?.Address is not null && state.HostFingerprint is not null)
            && (!state.RecoveryAcknowledged || state.RecoveryExported)
            && (!state.RecoveryExported || state.RecoveryDirectory is not null && state.InstallationId is not null)
            && (state.Phase != "complete" || state.InstallerState == "ready" && state.RecoveryAcknowledged && state.InstallStarted),
            "Seed journal lifecycle flags conflict. Reconcile original state; do not reset ownership or creation intent.");
        state.Plan.Selection.Validate();
        SeedGuard.Require(state.Plan.OperatorPublicKeys is not null, "Operator public-key selection is corrupt.");
        SeedOperatorKeys.Validate(state.Plan.OperatorPublicKeys);
        return state;
    }

    public void Save(SeedState state)
    {
        var path = Path.Combine(DirectoryPath, "operation.json");
        var pending = Path.Combine(DirectoryPath, "operation.pending");
        SeedGuard.Require(!File.Exists(pending), "An interrupted journal write remains. Preserve it and reconcile operation.json before resuming.");
        SeedFiles.WriteNew(pending, JsonSerializer.SerializeToUtf8Bytes(state, Json));
        if (OperatingSystem.IsWindows())
            SeedGuard.Require(MoveFileEx(pending, path, 0x1 | 0x8), "Atomic write-through journal replacement failed. Preserve operation.pending; no mutation is safe.");
        else File.Move(pending, path, true);
        // Flush the rename on Unix before allowing any provider mutation.
        if (!OperatingSystem.IsWindows())
        {
            var result = SeedProcess.RunAsync("sync", OperatingSystem.IsLinux() ? ["-f", DirectoryPath] : [], null, default).GetAwaiter().GetResult();
            SeedGuard.Require(result.ExitCode == 0, "Could not durably flush seed journal; no provider mutation is safe.");
        }
    }
    public void Dispose() => operationLock.Dispose();
}

internal sealed record SeedProcessResult(int ExitCode, string Output);
internal static class SeedProcess
{
    public static async Task<SeedProcessResult> RunAsync(string executable, IEnumerable<string> args, byte[]? input, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var inherited = new Dictionary<string, string?>();
        foreach (var name in new[] { "PATH", "HOME", "SystemRoot", "WINDIR", "USERPROFILE", "LOCALAPPDATA", "SSL_CERT_FILE", "SSL_CERT_DIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) inherited[name] = value;
        start.Environment.Clear();
        foreach (var pair in inherited) start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new Commands.CliCommandException(CliExitCodes.ValidationError, $"Required tool unavailable: {executable}. See docs/seed.md."); }
        using var registration = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            await error; // Tool/provider output may contain secrets; never relay stderr.
            return new(process.ExitCode, await output);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    public static async Task<string> RequireAsync(string executable, IEnumerable<string> args, byte[]? input, CancellationToken ct)
    {
        var result = await RunAsync(executable, args, input, ct);
        SeedGuard.Require(result.ExitCode == 0, $"{executable} failed; preserve resources and inspect the protected host through verified SSH. No output was logged.");
        return result.Output;
    }
}
