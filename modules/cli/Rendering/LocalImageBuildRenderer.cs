// SPDX-License-Identifier: MPL-2.0

using System.Text.RegularExpressions;

namespace Minicloud.Cli.Rendering;

internal sealed class LocalImageBuildRenderer : IDisposable
{
    private sealed class Service(string name)
    {
        public string Name { get; } = name;
        public string Status { get; set; } = "waiting";
        public string Activity { get; set; } = "waiting";
        public long? Started { get; set; }
        public TimeSpan? Duration { get; set; }
        public Queue<string> Recent { get; } = new();
        public int Warnings { get; set; }
        public string? LastWarning { get; set; }
    }

    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private readonly IConsole _console;
    private readonly string _app;
    private readonly Service[] _services;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly object _lock = new();
    private readonly StreamWriter _log;
    private readonly bool _noColor;
    private readonly bool _interactive;
    private readonly Timer? _timer;
    private Service? _focused;
    private bool _uploading;
    private int _renderedLines;
    private int _frame;
    private bool _disposed;
    private bool _cursorHidden;

    public string LogPath { get; }

    public LocalImageBuildRenderer(IConsole console, string app, IReadOnlyList<string> services,
        TimeProvider? timeProvider = null, bool? noColor = null, bool enableTimer = true, string? logPath = null)
    {
        _console = console;
        _app = Clean(app);
        _services = services.Select(name => new Service(Clean(name))).ToArray();
        _time = timeProvider ?? TimeProvider.System;
        _started = _time.GetTimestamp();
        _noColor = noColor ?? Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        _interactive = console.SupportsAnsi && console.WindowWidth >= 30 && console.WindowHeight >= 10;
        if (logPath is null)
        {
            var directory = Directory.CreateTempSubdirectory("minicloud-build-logs-");
            if (!OperatingSystem.IsWindows()) { directory.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute; }
            logPath = Path.Combine(directory.FullName, "build.log");
        }
        LogPath = logPath;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        _log = new StreamWriter(new FileStream(LogPath, options)) { AutoFlush = true };
        if (Interactive && enableTimer)
        {
            _timer = new Timer(_ => { lock (_lock) { if (!_disposed) { _frame++; Render(); } } }, null, 125, 125);
        }
    }

    private bool Interactive => _interactive;

    public void Start(string name, string platform)
    {
        lock (_lock)
        {
            var service = _services.Single(x => x.Name == name);
            service.Status = "active";
            service.Activity = "Preparing image";
            service.Started = _time.GetTimestamp();
            if (_focused?.Status is not ("active" or "failed")) { _focused = service; }
            Record(service, $"Building for {platform}");
            if (!Interactive) { _console.WriteLine($"Images: {_services.Count(x => x.Status == "ready")}/{_services.Length} ready; building {name} for {platform}"); }
            Render();
        }
    }

    public void Activity(string name, string text)
    {
        lock (_lock)
        {
            var service = _services.Single(x => x.Name == name);
            text = Clean(text);
            if (service.Activity == text) { return; }
            service.Activity = text;
            Record(service, text);
            if (!Interactive) { _console.WriteLine($"[{service.Name}] {text}"); }
        }
    }

    public void Output(string name, string text)
    {
        lock (_lock)
        {
            var service = _services.Single(x => x.Name == name);
            foreach (var line in text.Replace("\r", "\n").Split('\n'))
            {
                var clean = Clean(line);
                if (string.IsNullOrWhiteSpace(clean)) { continue; }
                Record(service, clean);
                if (Regex.IsMatch(clean, @"\bwarning(?:\s+[A-Z]+\d+)?\s*:", RegexOptions.IgnoreCase)) { service.Warnings++; service.LastWarning = clean; }
                else
                {
                    service.Recent.Enqueue(clean);
                    while (service.Recent.Count > 5) { service.Recent.Dequeue(); }
                }
                if (!Interactive) { _console.WriteLine($"[{service.Name}:build] {clean}"); }
            }
        }
    }

    public void Complete(string name)
    {
        lock (_lock)
        {
            var service = _services.Single(x => x.Name == name);
            service.Status = "ready";
            service.Activity = "ready";
            service.Duration = _time.GetElapsedTime(service.Started!.Value);
            Record(service, "Image verified and ready");
            if (_focused == service) { _focused = _services.FirstOrDefault(x => x.Status == "active") ?? service; }
            if (!Interactive) { _console.WriteLine($"Images: {_services.Count(x => x.Status == "ready")}/{_services.Length} ready; {name} ready"); }
            Render();
        }
    }

    public void Built(string name)
    {
        lock (_lock)
        {
            var service = _services.Single(x => x.Name == name);
            service.Status = "built";
            service.Activity = "built · waiting to upload";
            Record(service, "Image built");
            if (!Interactive) { _console.WriteLine($"[{service.Name}] Image built"); }
            if (_focused == service) { _focused = _services.FirstOrDefault(x => x.Status == "active") ?? service; }
            Render();
        }
    }

    public void StartUpload(string name)
    {
        lock (_lock)
        {
            _uploading = true;
            var service = _services.Single(x => x.Name == name);
            if (_focused?.Status is not ("active" or "failed")) { _focused = service; }
            service.Status = "active";
            Activity(name, "Uploading image");
            Render();
        }
    }

    public void Fail(string name, bool cancelled)
    {
        lock (_lock)
        {
            var service = _services.Single(x => x.Name == name);
            service.Status = cancelled ? "cancelled" : "failed";
            service.Activity = service.Status;
            service.Duration = _time.GetElapsedTime(service.Started!.Value);
            Record(service, service.Status);
            if (!cancelled) { _focused = service; }
            Render();
        }
    }

    private void Record(Service service, string text) => _log.WriteLine($"{_time.GetUtcNow():O} [{service.Name}] {text}");

    private string Paint(string text, string color) => _noColor || !Interactive ? text : $"\x1b[{color}m{text}\x1b[0m";

    internal IReadOnlyList<string> Frame()
    {
        lock (_lock)
        {
            var width = Math.Max(1, _console.WindowWidth - 1);
            if (width < 30 || _console.WindowHeight < 10)
            {
                return [TerminalTextHelper.Truncate($"Images: {_services.Count(x => x.Status == "ready")}/{_services.Length} ready", width),
                    TerminalTextHelper.Truncate($"{_focused?.Name} · {_focused?.Activity}", width)];
            }
            var height = Math.Min(Math.Max(8, _console.WindowHeight - 2), _services.Length + 11);
            var slots = Math.Min(_services.Length, Math.Max(1, height - 9));
            var logRows = Math.Max(1, height - slots - 6);
            string Fit(string text, int? limit = null) => TerminalTextHelper.Truncate(text, limit ?? width);
            string Columns(string left, string right)
            {
                left = Fit(left, Math.Max(1, width - right.Length - 2));
                return left + new string(' ', Math.Max(1, width - left.Length - right.Length)) + right;
            }
            var ready = _services.Count(x => x.Status == "ready");
            var failed = _services.Any(x => x.Status is "failed" or "cancelled");
            var lines = new List<string>
            {
                Paint(Columns($"Minicloud · {_app}", Elapsed(_time.GetElapsedTime(_started))), "1"),
                Paint(Columns((_uploading ? "Upload images" : "Build images") + (failed ? " · stopped" : ""), $"{ready}/{_services.Length} ready"), failed ? "31" : "36"),
                ""
            };
            var focusIndex = _focused is null ? 0 : Array.IndexOf(_services, _focused);
            var first = Math.Clamp(focusIndex - slots / 2, 0, Math.Max(0, _services.Length - slots));
            foreach (var service in _services.Skip(first).Take(slots))
            {
                var (icon, color) = service.Status switch
                {
                    "ready" => ("✓", "32"),
                    "active" => (Spinner[_frame % Spinner.Length], "36"),
                    "built" => ("○", "36"),
                    "failed" => ("✗", "31"),
                    "cancelled" => ("!", "33"),
                    _ => ("○", "90")
                };
                var elapsed = service.Duration ?? (service.Started is { } start ? _time.GetElapsedTime(start) : (TimeSpan?)null);
                var time = elapsed is { } duration ? Elapsed(duration) : "";
                lines.Add(Paint(Columns($"{icon} {Fit(service.Name, 16),-16} {service.Activity}", time), color));
            }
            lines.Add("");
            var inner = width - 4;
            var warnings = _focused?.Warnings ?? 0;
            var title = $"{_focused?.Name ?? "images"} · recent output" + (warnings > 0 ? $" · {warnings} {(warnings == 1 ? "warning" : "warnings")}" : "");
            var label = Fit(title, Math.Max(1, width - 4));
            lines.Add(Paint("┌ " + label + " " + new string('─', Math.Max(0, width - label.Length - 4)) + "┐", "90"));
            var warning = _focused?.LastWarning;
            var output = _focused?.Recent.TakeLast(warning is null ? logRows : logRows - 1).ToList() ?? [];
            if (warning is not null) { output.Add(warning); }
            while (output.Count < logRows) { output.Insert(0, ""); }
            foreach (var line in output)
            {
                var content = Fit(line, inner);
                var colored = Paint(content, line == warning ? "33" : "90");
                lines.Add(Paint("│ ", "90") + colored + new string(' ', Math.Max(0, inner - content.Length)) + Paint(" │", "90"));
            }
            lines.Add(Paint("└" + new string('─', width - 2) + "┘", "90"));
            return lines;
        }
    }

    private void Render()
    {
        if (!Interactive) { return; }
        if (!_cursorHidden) { _console.Write("\x1b[?25l"); _cursorHidden = true; }
        var lines = Frame();
        var clear = _renderedLines > 0 ? $"\r\x1b[{Math.Min(_renderedLines, Math.Max(1, _console.WindowHeight - 1))}A\x1b[0J" : "";
        _console.Write(clear + string.Join('\n', lines) + "\n");
        _renderedLines = lines.Count;
    }

    private static string Elapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
    }

    private static string Clean(string text) => new(TerminalTextHelper.StripAnsi(text).Replace("\t", "    ").Where(c => !char.IsControl(c)).ToArray());

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) { return; }
            _disposed = true;
            _timer?.Dispose();
            try
            {
                Render();
                var ready = _services.Count(x => x.Status == "ready");
                _console.WriteLine(Paint($"Images: {ready}/{_services.Length} ready ({Elapsed(_time.GetElapsedTime(_started))})", ready == _services.Length ? "32" : "31"));
                _console.WriteLine($"Build logs: {LogPath}");
            }
            finally
            {
                if (_cursorHidden) { _console.Write("\x1b[?25h"); }
                _log.Dispose();
            }
        }
    }
}
