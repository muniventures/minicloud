using Minicloud.Cli;
using Minicloud.Cli.Rendering;

namespace Minicloud.Tests;

public sealed class DeploymentRendererTests
{
    private sealed class TestConsole : IConsole
    {
        public bool SupportsAnsi { get; init; } = true;
        public int WindowWidth { get; init; } = 80;
        public List<string> Writes { get; } = [];

        public string Output => string.Join("", Writes);

        public void Write(string message) => Writes.Add(message);
        public void WriteLine(string message = "") => Writes.Add(message + "\n");
        public void WriteError(string message) => Writes.Add(message + "\n");
        public string? ReadLine() => null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset time, TimeSpan? elapsed = null) : TimeProvider
    {
        private readonly long _elapsedTicks = (long)((elapsed ?? TimeSpan.FromSeconds(12)).TotalSeconds * 1000);
        private readonly long _startTimestamp = 1000;
        private bool _started;

        public override DateTimeOffset GetUtcNow() => time;
        public override long GetTimestamp()
        {
            if (!_started)
            {
                _started = true;
                return _startTimestamp;
            }
            return _startTimestamp + _elapsedTicks;
        }
        public override long TimestampFrequency => 1000;
    }

    [Fact]
    public void ProgressTracker_computes_equal_weight_across_included_stages()
    {
        // 3 included stages: Secrets (2 secrets), Artifacts (1 service -> 2 ops), Deploy (2 ops)
        var plan = new DeploymentPlan(
            HasSecretsStage: true,
            TotalSecrets: 2,
            HasArtifactsStage: true,
            SourceServices: ["web"],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        var tracker = new DeploymentProgressTracker(plan);
        Assert.Equal(0, tracker.CurrentPercentage);

        // Secrets: 1 of 2 -> 50% of 1/3 = 16.6% -> 16%
        tracker.OnSecretCompleted();
        Assert.Equal(16, tracker.CurrentPercentage);

        // Secrets: 2 of 2 -> 100% of 1/3 = 33.3% -> 33%
        tracker.OnSecretCompleted();
        Assert.Equal(33, tracker.CurrentPercentage);

        // Artifacts: 1 of 2 -> 50% of 1/3 = 16.6% -> 33 + 16 = 50%
        tracker.OnArtifactOpCompleted();
        Assert.Equal(50, tracker.CurrentPercentage);

        // Artifacts: 2 of 2 -> 100% of 1/3 = 33.3% -> 33 + 33 = 66%
        tracker.OnArtifactOpCompleted();
        Assert.Equal(66, tracker.CurrentPercentage);

        // Deploy: created does not advance artificial percentage (deploy stage is indeterminate while active)
        tracker.OnDeploymentCreated();
        Assert.Equal(66, tracker.CurrentPercentage);

        // Succeeded -> 100%
        tracker.OnDeploymentSucceeded();
        Assert.Equal(100, tracker.CurrentPercentage);
    }

    [Fact]
    public void ProgressTracker_failure_never_reaches_100_percent()
    {
        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        var tracker = new DeploymentProgressTracker(plan);
        // Deploy created -> does not advance percentage
        tracker.OnDeploymentCreated();
        Assert.Equal(0, tracker.CurrentPercentage);

        // Deploy failed -> frozen at reached percentage, strictly < 100
        tracker.OnDeploymentFailed();
        Assert.Equal(0, tracker.CurrentPercentage);
        Assert.True(tracker.CurrentPercentage < 100);
    }

    [Fact]
    public void ProgressTracker_is_strictly_monotonic()
    {
        var plan = new DeploymentPlan(
            HasSecretsStage: true,
            TotalSecrets: 4,
            HasArtifactsStage: true,
            SourceServices: ["web", "api"],
            HasDeployStage: true,
            SelectedServicesCount: 2);

        var tracker = new DeploymentProgressTracker(plan);
        var last = tracker.CurrentPercentage;

        for (var i = 0; i < 4; i++)
        {
            tracker.OnSecretCompleted();
            Assert.True(tracker.CurrentPercentage >= last);
            last = tracker.CurrentPercentage;
        }

        for (var i = 0; i < 4; i++)
        {
            tracker.OnArtifactOpCompleted();
            Assert.True(tracker.CurrentPercentage >= last);
            last = tracker.CurrentPercentage;
        }

        tracker.OnDeploymentCreated();
        Assert.True(tracker.CurrentPercentage >= last);
        last = tracker.CurrentPercentage;

        tracker.OnDeploymentSucceeded();
        Assert.Equal(100, tracker.CurrentPercentage);
    }

    [Fact]
    public void ProgressTracker_handles_no_publish_and_no_secrets()
    {
        // Only deploy stage included
        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        var tracker = new DeploymentProgressTracker(plan);
        Assert.Equal(0, tracker.CurrentPercentage);

        tracker.OnDeploymentCreated();
        Assert.Equal(0, tracker.CurrentPercentage);

        tracker.OnDeploymentSucceeded();
        Assert.Equal(100, tracker.CurrentPercentage);
    }

    [Fact]
    public void TerminalTextHelper_VisualWidth_and_StripAnsi_ignore_color_and_links()
    {
        var text = "\x1b[32mhello\x1b[0m world";
        Assert.Equal("hello world", TerminalTextHelper.StripAnsi(text));
        Assert.Equal(11, TerminalTextHelper.VisualWidth(text));

        var linked = "\x1b]8;;https://example.com\x1b\\link-text\x1b]8;;\x1b\\";
        Assert.Equal("link-text", TerminalTextHelper.StripAnsi(linked));
        Assert.Equal(9, TerminalTextHelper.VisualWidth(linked));
    }

    [Fact]
    public void TerminalTextHelper_Wrap_wraps_long_lines_at_word_boundaries()
    {
        var longText = "The quick brown fox jumps over the lazy dog";
        var wrapped = TerminalTextHelper.Wrap(longText, 20);
        Assert.All(wrapped, line => Assert.True(TerminalTextHelper.VisualWidth(line) <= 20));
        Assert.Equal("The quick brown fox", wrapped[0]);
        Assert.Equal("jumps over the lazy", wrapped[1]);
        Assert.Equal("dog", wrapped[2]);
    }

    [Fact]
    public void TerminalTextHelper_FormatBox_renders_content_sized_borders()
    {
        var lines = new[] { "Deployment complete", "1 service(s) deployed successfully" };
        var box = TerminalTextHelper.FormatBox(lines, isSuccess: true, terminalWidth: 80, isInteractive: true, supportsAnsi: true, noColor: true);

        Assert.Equal(4, box.Count);
        Assert.StartsWith("┌", box[0]);
        Assert.EndsWith("┐", box[0]);
        Assert.StartsWith("│", box[1]);
        Assert.Contains("Deployment complete", box[1]);
        Assert.StartsWith("│", box[2]);
        Assert.Contains("1 service(s) deployed successfully", box[2]);
        Assert.StartsWith("└", box[3]);
        Assert.EndsWith("┘", box[3]);

        // Verify equal visual width
        var expectedWidth = TerminalTextHelper.VisualWidth(box[0]);
        Assert.All(box, line => Assert.Equal(expectedWidth, TerminalTextHelper.VisualWidth(line)));
    }

    [Fact]
    public void PlainTextRenderer_emits_no_ansi_and_is_deterministic()
    {
        var console = new TestConsole { SupportsAnsi = false };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new PlainTextDeploymentRenderer(console, time);

        var plan = new DeploymentPlan(
            HasSecretsStage: true,
            TotalSecrets: 2,
            HasArtifactsStage: true,
            SourceServices: ["web"],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        renderer.Initialize(plan);
        renderer.SecretStarted("web", "KEY1");
        renderer.SecretCompleted("web", "KEY1");
        renderer.SecretStarted("web", "KEY2");
        renderer.SecretCompleted("web", "KEY2");

        renderer.ArtifactPackageStarted("web");
        renderer.ArtifactPackageCompleted("web", 1024 * 500);
        renderer.ArtifactUploadStarted("web", 1024 * 500);
        renderer.ArtifactUploadCompleted("web", 1024 * 500);

        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_123", "dispatching");
        renderer.DeploymentStatusUpdated("dep_123", "deploying");
        renderer.DeploymentSucceeded(new DeploymentSuccessResult(
            ServicesCount: 1,
            ConsoleUrl: "https://console.example",
            ServiceUrls: [("web", "https://web.example")]));

        var output = console.Output;

        // Must contain NO ANSI escape sequences
        Assert.False(output.Contains('\u001b'));
        Assert.DoesNotContain("⠋", output);

        // Contains chronological plain-text lines
        Assert.Contains("Secrets: syncing local secrets...", output);
        Assert.Contains("Secrets: synced web/KEY1", output);
        Assert.Contains("Secrets: synced web/KEY2", output);
        Assert.Contains("Secrets: 2 synced", output);
        Assert.Contains("Artifacts: packaging and uploading artifacts...", output);
        Assert.Contains("Artifacts: bundled web", output);
        Assert.Contains("Artifacts: uploaded web (500.0 KB)", output);
        Assert.Contains("Artifacts: 1 uploaded", output);
        Assert.Contains("Deploy: created deployment dep_123 (status: dispatching)", output);
        Assert.Contains("Deploy: status deploying", output);
        Assert.Contains("Deploy: succeeded", output);
        Assert.Contains("Overall: 100% (12s)", output);
        Assert.Contains("Deployment complete", output);
        Assert.Contains("Console: https://console.example", output);
        Assert.Contains("Service URL (web): https://web.example", output);
    }

    [Fact]
    public void PlainTextRenderer_failure_box_and_no_raw_logs()
    {
        var console = new TestConsole { SupportsAnsi = false };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new PlainTextDeploymentRenderer(console, time);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        renderer.Initialize(plan);
        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_failed_1", "deploying");
        renderer.DeploymentFailed(new DeploymentFailureResult(
            DeploymentId: "dep_failed_1",
            FailureCode: "build_failed",
            FailureMessage: "Docker build failed with code 1",
            ConsoleUrl: "https://console.example"));

        var output = console.Output;

        Assert.False(output.Contains('\u001b'));
        Assert.Contains("Deploy: failed", output);
        Assert.Contains("Deployment failed", output);
        Assert.Contains("build_failed", output);
        Assert.Contains("Docker build failed with code 1", output);
        Assert.Contains("Logs: minicloud logs dep_failed_1", output);
        Assert.Contains("Console: https://console.example", output);
        Assert.DoesNotContain("100%", output);
    }

    [Fact]
    public void InteractiveRenderer_hides_and_restores_cursor_on_dispose()
    {
        var console = new TestConsole { SupportsAnsi = true };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, enableSpinnerTimer: false);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        renderer.Initialize(plan);
        Assert.Contains("\x1b[?25l", console.Output);

        renderer.Dispose();
        Assert.Contains("\x1b[?25h", console.Output);
    }

    [Fact]
    public void InteractiveRenderer_success_snapshot_contains_green_box_and_service_urls()
    {
        var console = new TestConsole { SupportsAnsi = true };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, noColor: false, enableSpinnerTimer: false);

        var plan = new DeploymentPlan(
            HasSecretsStage: true,
            TotalSecrets: 1,
            HasArtifactsStage: true,
            SourceServices: ["web"],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        renderer.Initialize(plan);
        renderer.SecretStarted("web", "API_KEY");
        renderer.SecretCompleted("web", "API_KEY");

        renderer.ArtifactPackageStarted("web");
        renderer.ArtifactPackageCompleted("web", 1024 * 1024);
        renderer.ArtifactUploadStarted("web", 1024 * 1024);
        renderer.ArtifactUploadCompleted("web", 1024 * 1024);

        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_succ", "deploying");
        renderer.DeploymentSucceeded(new DeploymentSuccessResult(
            ServicesCount: 1,
            ConsoleUrl: "https://console.example",
            ServiceUrls: [("web", "https://web.example")]));

        var output = console.Output;
        var stripped = TerminalTextHelper.StripAnsi(output);

        Assert.Contains("Overall", stripped);
        Assert.Contains("100%", stripped);
        Assert.Contains("✓ Secrets  1 synced", stripped);
        Assert.Contains("✓ Artifacts  1 uploaded", stripped);
        Assert.Contains("✓ Deploy", stripped);
        Assert.Contains("Deployment complete", stripped);
        Assert.Contains("1 service(s) deployed successfully", stripped);
        Assert.Contains("Service URL (web)", stripped);
        Assert.Contains("https://web.example", stripped);
        // Green box border ANSI
        Assert.Contains("\x1b[32m┌", output);
        Assert.Contains("\x1b[32m└", output);
    }

    [Fact]
    public void InteractiveRenderer_failure_snapshot_contains_red_box_and_no_100_percent()
    {
        var console = new TestConsole { SupportsAnsi = true };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, noColor: false, enableSpinnerTimer: false);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: true,
            SourceServices: ["web"],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        renderer.Initialize(plan);
        renderer.ArtifactPackageStarted("web");
        renderer.ArtifactPackageCompleted("web", 2048);
        renderer.ArtifactUploadStarted("web", 2048);
        renderer.ArtifactUploadCompleted("web", 2048);

        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_fail", "deploying");
        renderer.DeploymentFailed(new DeploymentFailureResult(
            DeploymentId: "dep_fail",
            FailureCode: "healthcheck_timeout",
            FailureMessage: "Container did not respond to /health within 60s",
            ConsoleUrl: "https://console.example"));

        var output = console.Output;
        var stripped = TerminalTextHelper.StripAnsi(output);

        Assert.DoesNotContain("100%", stripped);
        Assert.Contains("✗ Deploy", stripped);
        Assert.Contains("Deployment failed", stripped);
        Assert.Contains("healthcheck_timeout", stripped);
        Assert.Contains("Container did not respond to /health within 60s", stripped);
        Assert.Contains("Logs: minicloud logs dep_fail", stripped);
        // Red box border ANSI
        Assert.Contains("\x1b[31m┌", output);
        Assert.Contains("\x1b[31m└", output);
    }

    [Fact]
    public void InteractiveRenderer_reconnecting_shows_amber_message()
    {
        var console = new TestConsole { SupportsAnsi = true };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, noColor: false, enableSpinnerTimer: false);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        renderer.Initialize(plan);
        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_rec", "deploying");
        renderer.DeploymentReconnecting();

        var output = console.Output;
        Assert.Contains("Network connection lost. Waiting to reconnect...", output);
        // Amber ANSI
        Assert.Contains("\x1b[33mNetwork connection lost", output);

        renderer.DeploymentReconnected("dep_rec", "deploying");
        var restoredOutput = console.Output;
        Assert.Contains("dep_rec  deploying", restoredOutput);
    }

    [Fact]
    public void InteractiveRenderer_no_color_omits_color_escapes()
    {
        var console = new TestConsole { SupportsAnsi = true };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, noColor: true, enableSpinnerTimer: false);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        renderer.Initialize(plan);
        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_nc", "deploying");
        renderer.DeploymentSucceeded(new DeploymentSuccessResult(
            ServicesCount: 1,
            ConsoleUrl: null,
            ServiceUrls: [("web", "https://web.example")]));

        var output = console.Output;

        // No color codes (31m, 32m, 33m, 36m, 90m)
        Assert.DoesNotContain("\x1b[31m", output);
        Assert.DoesNotContain("\x1b[32m", output);
        Assert.DoesNotContain("\x1b[33m", output);
        Assert.DoesNotContain("\x1b[36m", output);
        Assert.DoesNotContain("\x1b[90m", output);

        // Box characters still present
        Assert.Contains("┌", output);
        Assert.Contains("└", output);
        Assert.Contains("Deployment complete", output);
    }

    [Fact]
    public void InteractiveRenderer_narrow_terminal_wraps_long_lines_without_breaking_box()
    {
        var console = new TestConsole { SupportsAnsi = true, WindowWidth = 40 };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, noColor: false, enableSpinnerTimer: false);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        renderer.Initialize(plan);
        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_narrow", "failed");
        renderer.DeploymentFailed(new DeploymentFailureResult(
            DeploymentId: "dep_narrow",
            FailureCode: "very_long_failure_code_that_exceeds_normal_line_length",
            FailureMessage: "This is a very long error message that will definitely wrap multiple times within forty columns without breaking the box border.",
            ConsoleUrl: null));

        var output = console.Output;
        Assert.Contains("Deployment failed", output);
        Assert.Contains("┌", output);
        Assert.Contains("└", output);
    }

    [Fact]
    public void InteractiveRenderer_narrow_40_columns_success_box_preserves_urls_and_renders_box()
    {
        var console = new TestConsole { SupportsAnsi = true, WindowWidth = 40 };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, noColor: false, enableSpinnerTimer: false);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 1);

        renderer.Initialize(plan);
        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_long", "deploying");
        renderer.DeploymentSucceeded(new DeploymentSuccessResult(
            ServicesCount: 1,
            ConsoleUrl: "https://console.example.com/organizations/my-org/apps/my-app/deployments/dep_long_long_long_identifier",
            ServiceUrls: [("very-long-service-name-exceeding-width", "https://very-long-service-name-exceeding-width.example.com/subpath/to/app")]));

        var output = console.Output;
        Assert.Contains("Deployment complete", output);
        Assert.Contains("┌", output);
        Assert.Contains("└", output);
        // Verify URLs are preserved intact without being sliced
        Assert.Contains("https://console.example.com/organizations/my-org/apps/my-app/deployments/dep_long_long_long_identifier", output);
        Assert.Contains("https://very-long-service-name-exceeding-width.example.com/subpath/to/app", output);
        // No OSC 8 escape sequence leakage
        Assert.DoesNotContain("]8;;", output);
    }

    [Fact]
    public async Task InteractiveRenderer_concurrent_thread_updates_never_corrupt_or_throw()
    {
        var console = new TestConsole { SupportsAnsi = true, WindowWidth = 80 };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, noColor: false, enableSpinnerTimer: false);

        var services = Enumerable.Range(1, 10).Select(i => $"service_{i}").ToArray();
        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: true,
            SourceServices: services,
            HasDeployStage: true,
            SelectedServicesCount: services.Length);

        renderer.Initialize(plan);

        // Run concurrent packaging and uploading from 10 tasks
        var tasks = services.Select(async svc =>
        {
            await Task.Yield();
            renderer.ArtifactPackageStarted(svc);
            await Task.Delay(1);
            renderer.ArtifactPackageCompleted(svc, 1024 * 100);
            renderer.ArtifactUploadStarted(svc, 1024 * 100);
            await Task.Delay(1);
            renderer.ArtifactUploadCompleted(svc, 1024 * 100);
        });

        await Task.WhenAll(tasks);

        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_conc", "deploying");
        renderer.DeploymentSucceeded(new DeploymentSuccessResult(
            ServicesCount: services.Length,
            ConsoleUrl: "https://console.example",
            ServiceUrls: services.Select(s => (s, $"https://{s}.example")).ToArray()));

        var output = console.Output;
        Assert.Contains("Deployment complete", output);
        Assert.Contains("10 uploaded", output);
    }

    [Fact]
    public async Task InteractiveRenderer_disposal_on_unhandled_exception_restores_cursor()
    {
        var console = new TestConsole { SupportsAnsi = true };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);

        await using (var renderer = new InteractiveDeploymentRenderer(console, time, noColor: false, enableSpinnerTimer: false))
        {
            var plan = new DeploymentPlan(
                HasSecretsStage: false,
                TotalSecrets: 0,
                HasArtifactsStage: false,
                SourceServices: [],
                HasDeployStage: true,
                SelectedServicesCount: 1);

            renderer.Initialize(plan);
            // Simulate sudden crash or exception without calling DeploymentFailed
        }

        // Disposal must emit cursor restore code (\x1b[?25h) and a trailing newline
        Assert.Contains("\x1b[?25h", console.Output);
    }

    [Fact]
    public void DeploymentRendererFactory_creates_plaintext_when_ansi_unsupported()
    {
        var nonAnsiConsole = new TestConsole { SupportsAnsi = false };
        var renderer = DeploymentRendererFactory.Create(nonAnsiConsole);
        Assert.IsType<PlainTextDeploymentRenderer>(renderer);

        var ansiConsole = new TestConsole { SupportsAnsi = true };
        var interactiveRenderer = DeploymentRendererFactory.Create(ansiConsole, enableSpinnerTimer: false);
        Assert.IsType<InteractiveDeploymentRenderer>(interactiveRenderer);
    }

    [Fact]
    public void InteractiveRenderer_deploy_stage_renders_indeterminate_spinner_and_service_count()
    {
        var console = new TestConsole { SupportsAnsi = true };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, noColor: false, enableSpinnerTimer: false);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 6);

        renderer.Initialize(plan);
        renderer.DeploymentCreationStarted();
        renderer.DeploymentCreated("dep_test_123", "deploying");

        var output = console.Output;
        var stripped = TerminalTextHelper.StripAnsi(output);

        // Header shows indeterminate Deploying with service count
        Assert.Contains("Deploying (6 services)", stripped);
        Assert.DoesNotContain("83%", stripped);
        Assert.DoesNotContain("50%", stripped);

        // Stage row shows service count and deployment ID
        Assert.Contains("Deploy  (6 services)", stripped);
        Assert.Contains("dep_test_123", stripped);
    }

    [Fact]
    public void InteractiveRenderer_activity_updated_displays_phase_and_detail()
    {
        var console = new TestConsole { SupportsAnsi = true };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new InteractiveDeploymentRenderer(console, time, noColor: false, enableSpinnerTimer: false);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 6);

        renderer.Initialize(plan);
        renderer.DeploymentCreated("dep_test_456", "deploying");
        renderer.DeploymentActivityUpdated("dep_test_456", "Building Docker images (2/6)", "agency-ui: Step 4/8 RUN npm run build");

        var output = console.Output;
        var stripped = TerminalTextHelper.StripAnsi(output);

        // Header and stage show the active phase
        Assert.Contains("Building Docker images (2/6)", stripped);
        Assert.Contains("agency-ui: Step 4/8 RUN npm run build", stripped);
        Assert.DoesNotContain("83%", stripped);
    }

    [Fact]
    public void PlainTextRenderer_activity_updated_emits_milestones()
    {
        var console = new TestConsole { SupportsAnsi = false };
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var renderer = new PlainTextDeploymentRenderer(console, time);

        var plan = new DeploymentPlan(
            HasSecretsStage: false,
            TotalSecrets: 0,
            HasArtifactsStage: false,
            SourceServices: [],
            HasDeployStage: true,
            SelectedServicesCount: 2);

        renderer.Initialize(plan);
        renderer.DeploymentCreated("dep_plain_1", "deploying");
        renderer.DeploymentActivityUpdated("dep_plain_1", "Preparing host environment");
        renderer.DeploymentActivityUpdated("dep_plain_1", "Building Docker images (1/2)", "web: Step 1/5");
        // Repeating same phase should not duplicate milestone output
        renderer.DeploymentActivityUpdated("dep_plain_1", "Building Docker images (1/2)", "web: Step 2/5");
        renderer.DeploymentActivityUpdated("dep_plain_1", "Starting application services");

        var output = console.Output;
        Assert.Contains("Deploy: Preparing host environment", output);
        Assert.Contains("Deploy: Building Docker images (1/2): web: Step 1/5", output);
        Assert.Contains("Deploy: Starting application services", output);
    }
}

