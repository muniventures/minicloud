// SPDX-License-Identifier: MPL-2.0

using System.Text;
using System.Text.Json;
using Minicloud.Cli;
using Minicloud.Cli.Rendering;

namespace Minicloud.Tests;

public sealed class LocalImageBuildRendererTests
{
    [Fact]
    public void Panel_stays_bounded_and_counts_only_verified_images()
    {
        var console = new TestConsole();
        var path = NewLog();
        try
        {
            using (var renderer = new LocalImageBuildRenderer(console, "SampleApp", ["backend", "worker", "portal"], enableTimer: false, logPath: path))
            {
                renderer.Start("backend", "linux/amd64");
                var height = renderer.Frame().Count;
                for (var i = 0; i < 1000; i++) { renderer.Output("backend", $"Compiler output {i}: " + new string('x', 300)); }
                renderer.Activity("backend", "Pushing image layers");
                Assert.Contains("0/3 ready", Plain(renderer));
                Assert.Equal(height, renderer.Frame().Count);
                Assert.All(renderer.Frame(), line => Assert.True(TerminalTextHelper.VisualWidth(line) < console.WindowWidth));
                Assert.DoesNotContain("Compiler output 0:", Plain(renderer));
                Assert.Contains("Compiler output 999:", Plain(renderer));
                renderer.Complete("backend");
                renderer.Start("worker", "linux/amd64");
                Assert.Contains("1/3 ready", Plain(renderer));
                renderer.Fail("worker", cancelled: false);
                Assert.Contains("stopped", Plain(renderer));
                Assert.Contains("failed", Plain(renderer));
                Assert.DoesNotContain("100%", console.Output);
            }
            var log = File.ReadAllText(path);
            Assert.Contains("Compiler output 0:", log);
            Assert.Contains("Compiler output 999:", log);
            Assert.EndsWith("\x1b[?25h", console.Output);
            Assert.Contains(path, console.Output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Overlapping_builds_keep_separate_activity_and_logs()
    {
        var console = new TestConsole();
        var path = NewLog();
        try
        {
            using (var renderer = new LocalImageBuildRenderer(console, "Demo", ["api", "web"], enableTimer: false, logPath: path))
            {
                renderer.Start("api", "linux/amd64");
                renderer.Start("web", "linux/amd64");
                renderer.Activity("api", "Compiling API");
                renderer.Activity("web", "Bundling web");
                renderer.Output("web", "Web output");
                renderer.Output("api", "API output");

                var frame = Plain(renderer);
                Assert.Contains("Compiling API", frame);
                Assert.Contains("Bundling web", frame);
                Assert.Contains("API output", frame);
                Assert.DoesNotContain("Web output", frame);

                renderer.Complete("api");
                Assert.Contains("1/2 ready", Plain(renderer));
                Assert.Contains("Web output", Plain(renderer));
                renderer.Fail("web", cancelled: false);
            }

            var log = File.ReadAllText(path);
            Assert.Contains("[api] API output", log);
            Assert.Contains("[api] Image verified and ready", log);
            Assert.Contains("[web] Web output", log);
            Assert.Contains("[web] failed", log);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Colors_identify_ready_active_warning_failure_and_waiting_states()
    {
        var console = new TestConsole();
        var path = NewLog();
        try
        {
            using var renderer = new LocalImageBuildRenderer(console, "Demo", ["api", "web", "worker"], noColor: false, enableTimer: false, logPath: path);
            renderer.Start("api", "linux/amd64");
            renderer.Complete("api");
            renderer.Start("web", "linux/amd64");
            renderer.Output("web", "project.csproj: warning NU1903: package warning");
            renderer.Output("web", "project.csproj: warning NU1903: package warning");
            renderer.Output("web", "Build finished: 2 Warning(s)");
            var frame = string.Join('\n', renderer.Frame());
            Assert.Contains("\x1b[32m✓", frame);
            Assert.Contains("\x1b[36m", frame);
            Assert.Contains("\x1b[90m○", frame);
            Assert.Contains("\x1b[33mproject.csproj:", frame);
            Assert.Contains("2 warnings", frame);
            renderer.Fail("web", cancelled: false);
            Assert.Contains("\x1b[31m✗", string.Join('\n', renderer.Frame()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void No_color_keeps_structure_and_plain_output_has_no_terminal_controls()
    {
        foreach (var ansi in new[] { true, false })
        {
            var console = new TestConsole { SupportsAnsi = ansi };
            var path = NewLog();
            try
            {
                using (var renderer = new LocalImageBuildRenderer(console, "Demo", ["api"], noColor: true, enableTimer: false, logPath: path))
                {
                    renderer.Start("api", "linux/amd64");
                    renderer.Output("api", "\x1b[31mmessage\x1b[0m\x1b]0;untrusted\a");
                    renderer.Fail("api", cancelled: true);
                    Assert.Contains("cancelled", Plain(renderer));
                    Assert.True(!Plain(renderer).Contains('\u001b'), JsonSerializer.Serialize(Plain(renderer)));
                    Assert.DoesNotContain("\x1b[31m", console.Output);
                }
                if (!ansi) { Assert.False(console.Output.Contains('\u001b')); }
                Assert.False(File.ReadAllText(path).Contains('\u001b'));
            }
            finally { File.Delete(path); }
        }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(40, 12)]
    [InlineData(24, 8)]
    public void Resize_keeps_active_service_visible_and_lines_within_terminal(int width, int height)
    {
        var console = new TestConsole();
        var path = NewLog();
        try
        {
            using var renderer = new LocalImageBuildRenderer(console, "Demo", ["api", "web", "worker", "admin", "auth"], enableTimer: false, logPath: path);
            renderer.Start("admin", "linux/amd64");
            renderer.Activity("admin", "RUN dotnet publish " + new string('x', 200));
            console.WindowWidth = width;
            console.WindowHeight = height;
            Assert.Contains("admin", Plain(renderer));
            Assert.True(renderer.Frame().Count <= height - 2);
            Assert.All(renderer.Frame(), line => Assert.True(TerminalTextHelper.VisualWidth(line) < width));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Structured_logs_join_utf8_chunks_redact_credentials_and_decode_buildkit_warnings()
    {
        var activity = new List<string>();
        var logs = new List<string>();
        var progress = new DockerBuildProgress(activity.Add, logs.Add, text => text.Replace("secret-password", "[redacted]"));
        progress.Process("""{"vertexes":[{"digest":"sha256:a","name":"RUN dotnet publish","started":"2026-10-27T00:00:00Z"}]}""");
        var bytes = Encoding.UTF8.GetBytes("ação secret-password\nlast line");
        progress.Process(Log(bytes[..2]));
        progress.Process(Log(bytes[2..12]));
        progress.Process(Log(bytes[12..]));
        progress.Process(JsonSerializer.Serialize(new { warnings = new[] { new { @short = Convert.ToBase64String(Encoding.UTF8.GetBytes("FromAsCasing: mixed case")) } } }));
        progress.Process("""{"statuses":[{"id":"pushing layers"}]}""");
        progress.Flush();
        Assert.Equal(["RUN dotnet publish", "Pushing image layers"], activity);
        Assert.Equal(["ação [redacted]", "Warning: FromAsCasing: mixed case", "last line"], logs);
        Assert.DoesNotContain("secret-password", string.Join('\n', logs));
    }

    [Fact]
    public void Unstructured_errors_and_invalid_log_events_do_not_hide_failure_output()
    {
        var logs = new List<string>();
        var progress = new DockerBuildProgress(_ => { }, logs.Add, text => text);
        progress.Process("ERROR: Docker daemon connection lost");
        progress.Process("""{"logs":[{"vertex":"a","stream":1,"data":"invalid!"}]}""");
        Assert.Contains("ERROR: Docker daemon connection lost", logs);
        Assert.Contains("Docker emitted an unreadable log event.", logs);
    }

    private static string Log(byte[] bytes) => JsonSerializer.Serialize(new { logs = new[] { new { vertex = "sha256:a", stream = 1, data = Convert.ToBase64String(bytes) } } });
    private static string Plain(LocalImageBuildRenderer renderer) => TerminalTextHelper.StripAnsi(string.Join('\n', renderer.Frame()));
    private static string NewLog() => Path.Combine(Path.GetTempPath(), $"minicloud-build-test-{Guid.NewGuid():N}.log");

    private sealed class TestConsole : IConsole
    {
        public bool SupportsAnsi { get; init; } = true;
        public int WindowWidth { get; set; } = 80;
        public int WindowHeight { get; set; } = 24;
        public StringBuilder Buffer { get; } = new();
        public string Output => Buffer.ToString();
        public void Write(string message) => Buffer.Append(message);
        public void WriteLine(string message = "") => Buffer.AppendLine(message);
        public void WriteError(string message) => Buffer.AppendLine(message);
        public string? ReadLine() => null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
