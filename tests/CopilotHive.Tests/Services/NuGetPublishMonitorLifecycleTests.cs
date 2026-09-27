using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using CopilotHive;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="NuGetPublishMonitorService.StartupScanAsync"/> (startup release
/// reconciliation), <see cref="NuGetPublishMonitorService.LaunchBackgroundMonitor"/>
/// (fire-and-forget monitoring), and <see cref="ApiEndpoints.LaunchNuGetMonitors"/>
/// (release-completion trigger).
/// <para>
/// <b>Determinism contract.</b> Every test here that reaches a poll delay, the overall timeout, the
/// 1-second startup probe timeout or the 60-minute cutoff drives a
/// <see cref="ControlledTimeProvider"/>: the clock advances ONLY when the test advances it, and
/// each advance is preceded by a <see cref="ControlledTimeProvider.WaitForPendingTimerAsync"/>
/// rendezvous on the EXACT due time the service is parked on (7 s poll vs 10 min overall timeout vs
/// 1 s probe, so the timers cannot be confused). No test sleeps, polls a real deadline, or races a
/// short real timeout.
/// </para>
/// <para>
/// <b>No background task or timer is left behind.</b> Tests that only assert WHETHER a background
/// monitor is launched override the <c>internal virtual</c>
/// <see cref="NuGetPublishMonitorService.LaunchBackgroundMonitor"/> with a recording double, which
/// removes the background task entirely. The one test that deliberately runs a REAL background
/// monitor owns its cancellation token, rendezvouses on the monitor's pending poll timer, cancels
/// in a <c>finally</c>, and observes the monitor's completion before asserting
/// <see cref="ControlledTimeProvider.PendingTimerCount"/> is zero.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class NuGetPublishMonitorLifecycleTests
{
    /// <summary>
    /// Poll interval requested on the CONTROLLED clock — 7 seconds, deliberately distinct from
    /// <see cref="OverallTimeout"/> and from the 1-second startup probe window.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(7);

    /// <summary>
    /// Overall monitoring timeout requested on the CONTROLLED clock — 10 minutes, unmistakably
    /// different from the 7-second poll interval.
    /// </summary>
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The startup scan's per-package probe window, mirrored from production.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    private sealed class RecordingEventBus : IEventBus
    {
        public List<SystemEvent> Published { get; } = [];
        public event Action<SystemEvent>? OnEvent;

        public void Publish(SystemEvent evt)
        {
            Published.Add(evt);
            OnEvent?.Invoke(evt);
        }
    }

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
        private readonly List<string> _urls = [];
        private readonly object _lock = new();

        public IReadOnlyList<string> Urls
        {
            get { lock (_lock) return _urls.ToList(); }
        }

        public ScriptedHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            : this((req, _) => responder(req))
        {
        }

        public ScriptedHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this(req => Task.FromResult(responder(req)))
        {
        }

        public ScriptedHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
                _urls.Add(request.RequestUri!.ToString());
            return _responder(request, cancellationToken);
        }
    }

    private static IHttpClientFactory CreateFactory(ScriptedHttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory.Object;
    }

    private static HiveConfigFile CreateConfig(params RepositoryConfig[] repos) => new()
    {
        Repositories = [.. repos],
        Orchestrator = new OrchestratorConfig(),
    };

    private static RepositoryConfig CreateRepo(
        string name = "test-repo",
        NuGetPublishConfig? publishNuGet = null) => new()
        {
            Name = name,
            Url = $"https://github.com/org/{name}",
            PublishNuGet = publishNuGet,
        };

    private static NuGetPublishConfig PkgConfig(params string[] packageIds) => new()
    {
        Packages = [.. packageIds.Select(id => new NuGetPackageEntry { PackageId = id })],
    };

    private static HttpResponseMessage OkResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status) => new(status);

    private static string IndexJsonWithInlineMatch(string version) =>
        JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    items = new[]
                    {
                        new { catalogEntry = new { version } }
                    }
                }
            }
        });

    /// <summary>
    /// An index listing several versions inline, so a scan over MULTIPLE candidate releases can
    /// find each release's own version and therefore launch no background monitor at all.
    /// </summary>
    private static string IndexJsonWithInlineMatches(params string[] versions) =>
        JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    items = versions.Select(v => new { catalogEntry = new { version = v } }).ToArray()
                }
            }
        });

    /// <summary>
    /// A Released release whose <c>ReleasedAt</c> is an exact offset from the CONTROLLED clock's
    /// <c>GetUtcNow()</c>, so the 60-minute cutoff boundary is expressed in absolute terms rather
    /// than relative to real elapsed time.
    /// </summary>
    private static Release ReleasedRelease(
        ControlledTimeProvider clock,
        TimeSpan releasedAgo,
        string tag = "v1.2.3",
        params string[] repos) => new()
        {
            Id = tag,
            Tag = tag,
            Status = ReleaseStatus.Released,
            ReleasedAt = clock.GetUtcNow().UtcDateTime - releasedAgo,
            RepositoryNames = [.. repos],
        };

    private static NuGetPublishMonitorService CreateService(
        ScriptedHttpMessageHandler handler,
        HiveConfigFile? config = null,
        IEventBus? eventBus = null,
        IGoalStore? goalStore = null,
        ILogger<NuGetPublishMonitorService>? logger = null,
        ControlledTimeProvider? clock = null) => new(
            config: config,
            eventBus: eventBus,
            httpClientFactory: CreateFactory(handler),
            logger: logger ?? NullLogger<NuGetPublishMonitorService>.Instance,
            goalStore: goalStore,
            pollInterval: PollInterval,
            timeoutOverride: OverallTimeout,
            timeProvider: clock);

    /// <summary>
    /// Rendezvous on the pending delay timer that still has exactly <paramref name="delay"/> to run,
    /// then advance the manual clock by that window so the producer resumes. The rendezvous — not a
    /// sleep — guarantees the producer is parked on that exact timer before the advance.
    /// </summary>
    private static async Task AdvanceAsync(
        ControlledTimeProvider clock, TimeSpan delay, CancellationToken ct)
    {
        await clock.WaitForPendingTimerAsync(delay, ct);
        clock.Advance(delay);
    }

    /// <summary>
    /// Joins a task the test started, for use exclusively in <c>finally</c> blocks. The await is
    /// bounded and its outcome is deliberately discarded: the purpose is only to prove the task
    /// ENDED before the test returned, so an intermediate rendezvous or assertion failure can never
    /// leak a live background monitor. Swallowing here is what preserves the ORIGINAL failure — an
    /// exception escaping a <c>finally</c> would replace it.
    /// </summary>
    private static async Task JoinQuietlyAsync(Task task)
    {
        try
        {
            await task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
        }
        catch (Exception)
        {
            // Joining only: the test's own assertions decide the verdict.
        }
    }

    // ── StartupScanAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task StartupScanAsync_NullGoalStore_ReturnsImmediately()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, clock: clock,
            config: CreateConfig(CreateRepo("test-repo", publishNuGet: PkgConfig("My.Package"))));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Urls);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task StartupScanAsync_NullConfig_ReturnsImmediately()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler, goalStore: new ReleaseStore(), clock: clock);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Urls);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// Removal-proof for M4. The 60-minute window is measured on the INJECTED clock: a release
    /// exactly 60 minutes old is at the boundary and must be EXCLUDED (the comparison is strictly
    /// greater), 59 minutes and 1 second is inside and included, and a Planning release with a
    /// recent <c>ReleasedAt</c> is never scanned. Reverting the cutoff to
    /// wall-clock cutoff (mutant M4) makes every fixture fall outside the window and the probe count
    /// assertions fail.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_ExclusiveCutoff_FiltersReleases()
    {
        var ct = TestContext.Current.CancellationToken;

        // Every candidate's version is present inline, so each probe resolves to Found and NO
        // background monitor is launched — this test is purely about the cutoff.
        var handler = new ScriptedHttpMessageHandler(
            _ => OkResponse(IndexJsonWithInlineMatches("1.0.0", "1.1.0", "1.2.3")));
        var clock = new ControlledTimeProvider();
        var store = new ReleaseStore();

        // Exactly 60 minutes → EXCLUDED (the comparison is strictly greater).
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(60), "v1.0.0", "pkg-repo"));
        // One second inside the window → included.
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(60) - TimeSpan.FromSeconds(1), "v1.1.0", "pkg-repo"));
        // 59 minutes → included.
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(59), "v1.2.3", "pkg-repo"));

        var config = CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package")));
        var service = CreateService(handler, config: config, eventBus: new RecordingEventBus(),
            goalStore: store, clock: clock);

        await service.StartupScanAsync(ct);

        // EXACTLY two candidates were probed: the 61-minute-old release is out, the release at the
        // exact 60-minute boundary is out (exclusive), and the two inside are in.
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains(handler.Urls, u => u.Contains("my.package", StringComparison.OrdinalIgnoreCase));
        // Both probes found their version, so nothing was left to monitor and nothing is pending.
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// A <see cref="ReleaseStatus.Planning"/> release with a recent <c>ReleasedAt</c> must be
    /// skipped entirely — only <see cref="ReleaseStatus.Released"/> releases are reconciled.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_NonReleasedStatus_Skips()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var clock = new ControlledTimeProvider();
        var store = new ReleaseStore();
        var release = ReleasedRelease(clock, TimeSpan.FromMinutes(5), "v1.2.3", "pkg-repo");
        release.Status = ReleaseStatus.Planning;
        store.Releases.Add(release);

        var config = CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package")));
        var service = CreateService(handler, config: config, eventBus: new RecordingEventBus(),
            goalStore: store, clock: clock);

        await service.StartupScanAsync(ct);

        Assert.Empty(handler.Urls);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task StartupScanAsync_TagStripping_ProbesStrippedVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var clock = new ControlledTimeProvider();
        var eventBus = new RecordingEventBus();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(1), "v1.2.3", "pkg-repo"));

        var service = CreateService(handler,
            config: CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package"))),
            eventBus: eventBus, goalStore: store, clock: clock);

        await service.StartupScanAsync(ct);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, evt.Type);
        Assert.Contains("1.2.3", evt.Message);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task StartupScanAsync_BlankOrInvalidTag_Skips()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var clock = new ControlledTimeProvider();
        var eventBus = new RecordingEventBus();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(1), "v", "pkg-repo"));
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(1), "vnot-a-version", "pkg-repo"));

        var service = CreateService(handler,
            config: CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package"))),
            eventBus: eventBus, goalStore: store, clock: clock);

        await service.StartupScanAsync(ct);

        Assert.Empty(handler.Urls);
        Assert.Empty(eventBus.Published);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task StartupScanAsync_GetReleasesFailure_LogsWarningAndReturns()
    {
        var logger = new CapturingLogger<NuGetPublishMonitorService>();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var clock = new ControlledTimeProvider();
        var service = CreateService(handler,
            config: CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package"))),
            goalStore: new ThrowingGetReleasesStore(), logger: logger, clock: clock);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Urls);
        Assert.Contains(logger.Entries, e => e.Contains("failed to load releases", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task StartupScanAsync_CallerCancellation_Returns()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var clock = new ControlledTimeProvider();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(1), "v1.2.3", "pkg-repo"));

        var service = CreateService(handler,
            config: CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package"))),
            goalStore: store, clock: clock);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await service.StartupScanAsync(cts.Token);

        Assert.Empty(handler.Urls);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// Per-package isolation: a probe failure for one package must not stop the scan — the next
    /// package is still probed.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_ProbeException_LogsAndContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<NuGetPublishMonitorService>();
        Func<HttpRequestMessage, HttpResponseMessage> responder = _ =>
            throw new InvalidOperationException("Simulated probe failure");
        var handler = new ScriptedHttpMessageHandler(responder);
        var clock = new ControlledTimeProvider();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(1), "v1.2.3", "pkg-repo"));

        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: PkgConfig("Broken.Package", "My.Package")));
        var service = CreateService(handler, config: config, eventBus: new RecordingEventBus(),
            goalStore: store, logger: logger, clock: clock);

        await service.StartupScanAsync(ct);

        // The first package's probe threw — logged. The scan continued and probed the
        // second package (which also threw against this handler).
        Assert.Contains(logger.Entries, e => e.Contains("scan probe failed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, handler.Urls.Count);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    // ── StartupScanAsync: background-monitor launches ──────────────────────

    /// <summary>
    /// A <see cref="NuGetPublishMonitorService.ProbeResult.NotFound"/> result must launch a
    /// background monitor — the package may not be registered yet but could land shortly.
    /// <para>
    /// Uses the recording override of <see cref="NuGetPublishMonitorService.LaunchBackgroundMonitor"/>:
    /// the launch decision is the behaviour under test here, and the real monitor's polling loop is
    /// covered by <c>NuGetPublishMonitorServiceTests</c>. That removes the background task entirely,
    /// so this test can never leave a timer or a task behind.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_NotFound_LaunchesBackgroundMonitor()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.NotFound));
        var clock = new ControlledTimeProvider();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(5), "v1.2.3", "pkg-repo"));

        var service = new RecordingBackgroundMonitorService(clock, handler,
            CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package"))), store);

        await service.StartupScanAsync(ct);

        var launch = Assert.Single(service.Launches);
        Assert.Equal("pkg-repo", launch.Repo);
        Assert.Equal("My.Package", launch.PackageId);
        Assert.Equal("1.2.3", launch.Version);
        Assert.Equal("v1.2.3", launch.ReleaseTag);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// A <see cref="NuGetPublishMonitorService.ProbeResult.Retry"/> result (e.g. 404 on the
    /// registration index) must launch a background monitor. Same recording override as above.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_Retry_LaunchesBackgroundMonitor()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.ServiceUnavailable));
        var clock = new ControlledTimeProvider();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(5), "v1.2.3", "pkg-repo"));

        var service = new RecordingBackgroundMonitorService(clock, handler,
            CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package"))), store);

        await service.StartupScanAsync(ct);

        var launch = Assert.Single(service.Launches);
        Assert.Equal("My.Package", launch.PackageId);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task StartupScanAsync_Found_DoesNotLaunchMonitor()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(1), "v1.2.3", "pkg-repo"));

        var config = CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package")));
        var service = CreateService(handler, config: config, eventBus: eventBus, goalStore: store, clock: clock);

        await service.StartupScanAsync(ct);

        Assert.Single(handler.Urls); // exactly one probe, no background monitor loop
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, evt.Type);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    [Fact]
    public async Task StartupScanAsync_Terminal_DoesNotLaunchMonitor()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.BadRequest));
        var eventBus = new RecordingEventBus();
        var clock = new ControlledTimeProvider();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(1), "v1.2.3", "pkg-repo"));

        var config = CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package")));
        var service = CreateService(handler, config: config, eventBus: eventBus, goalStore: store, clock: clock);

        await service.StartupScanAsync(ct);

        Assert.Single(handler.Urls); // probe once, Terminal → no background monitor
        Assert.Empty(eventBus.Published);
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// Removal-proof for M3 and proof of deterministic background-monitor cleanup. The startup
    /// probe's 1-second window is a TimeProvider-backed CTS: the probe transport parks until
    /// released and honours cancellation, so ONLY the manual clock can expire it. Restoring a real
    /// <c>CancelAfter(TimeSpan.FromSeconds(1))</c> (M3) means no 1-second timer is ever registered
    /// and the rendezvous below times out.
    /// <para>
    /// This is the one test that runs a REAL background monitor. Its lifecycle is fully owned: the
    /// test supplies the cancellation token, rendezvouses on the monitor's pending POLL timer
    /// (proving the monitor is running and parked on the controlled clock), cancels in
    /// <c>finally</c>, awaits the monitor's completion — observed through the subclass's
    /// <c>finally</c> — and then asserts that no timer is left pending.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_ProbeTimeout_LaunchesRealBackgroundMonitor_AndCleansUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease(clock, TimeSpan.FromMinutes(5), "v1.2.3", "pkg-repo"));

        // First request (the startup probe) parks until the test cancels it; later requests (the
        // background monitor's own probes) return NotFound immediately so it parks on its poll timer.
        var probeParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkForever = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var handler = new ScriptedHttpMessageHandler(async (req, token) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                probeParked.TrySetResult();
                // No real delay: the request is released ONLY by cancellation, which is exactly the
                // behaviour a hung transport exhibits. Awaiting cancellation directly keeps this
                // independent of any clock.
                await parkForever.Task.WaitAsync(token);
            }
            return ErrorResponse(HttpStatusCode.NotFound);
        });

        using var monitorLifetime = new CancellationTokenSource();
        var service = new TrackedBackgroundMonitorService(clock, handler,
            CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package"))), store);

        // Declared OUTSIDE the try so the finally can join whatever actually started, even when a
        // rendezvous or assertion failed before the later lines ran.
        Task? scan = null;
        try
        {
            scan = service.StartupScanAsync(monitorLifetime.Token);

            // The probe is provably in flight, so the 1-second window is the only thing that can
            // release it. Rendezvous on that exact due time — a poll or overall timeout could not
            // satisfy it.
            await probeParked.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);
            await AdvanceAsync(clock, ProbeTimeout, ct);
            await scan.WaitAsync(ControlledTimeProvider.HangGuard, ct);

            // The probe timed out → the package was handed to a REAL background monitor.
            await service.MonitorStarted.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);

            // Prove the monitor is genuinely running and parked on the controlled clock: its
            // 7-second poll delay, distinct from both the 10-minute timeout and the 1-second probe.
            await clock.WaitForPendingTimerAsync(PollInterval, ct);
            Assert.Contains(PollInterval, clock.RequestedDelays);

            // Release the monitor with the test-owned lifetime. No clock advance is involved, so no
            // timer is left armed.
            await monitorLifetime.CancelAsync();

            // SUCCESS-path cleanup, asserted with full strength: MonitorPackageAsync's own finally
            // has run, so the monitor provably ended and nothing is pending.
            await service.MonitorFinished.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);
            Assert.True(service.MonitorStarted.Task.IsCompleted,
                "The 1-second startup probe timeout must have handed the package to a background monitor.");
            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            // ANY failure above must not leave a live background task behind: cancel the
            // test-owned lifetime FIRST, then join the scan and — only if it ever started — the
            // monitor. Joining is bounded and exception-swallowing, so the ORIGINAL failure is
            // preserved (an exception escaping this finally would replace it). `scan` is null only
            // when the failure happened before the scan was started.
            await monitorLifetime.CancelAsync();
            if (scan is not null)
                await JoinQuietlyAsync(scan);
            if (service.MonitorStarted.Task.IsCompleted)
                await JoinQuietlyAsync(service.MonitorFinished.Task);
        }
    }

    // ── LaunchBackgroundMonitor ────────────────────────────────────────────

    [Fact]
    public async Task LaunchBackgroundMonitor_LogsExceptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<NuGetPublishMonitorService>();
        var clock = new ControlledTimeProvider();
        var service = new ThrowingMonitorService(null, logger, clock);

        service.LaunchBackgroundMonitor("test-repo", "My.Package", "1.2.3", "v1.2.3", ct);

        // Deterministic signal from the logger itself — no polling loop, no real deadline.
        await logger.FirstWrite.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);
        Assert.Contains(logger.Entries, e => e.Contains("NuGet monitor failed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>Subclass whose <see cref="NuGetPublishMonitorService.MonitorPackageAsync"/> always throws.</summary>
    private sealed class ThrowingMonitorService : NuGetPublishMonitorService
    {
        public ThrowingMonitorService(
            HiveConfigFile? config, ILogger<NuGetPublishMonitorService> logger, TimeProvider clock)
            : base(config: config, logger: logger, timeProvider: clock)
        {
        }

        public override Task MonitorPackageAsync(
            string repoName, string packageId, string version, string releaseTag, CancellationToken ct)
            => throw new InvalidOperationException("Simulated monitor failure");
    }

    /// <summary>Subclass that records <see cref="NuGetPublishMonitorService.MonitorReleaseAsync"/> invocations.</summary>
    private sealed class RecordingMonitorService : NuGetPublishMonitorService
    {
        public List<(string Repo, string Tag, CancellationToken Ct)> Calls { get; } = [];

        /// <summary>Completes on the first recorded invocation, so callers never poll.</summary>
        public TaskCompletionSource FirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task MonitorReleaseAsync(string repoName, string releaseTag, CancellationToken ct)
        {
            lock (Calls)
                Calls.Add((repoName, releaseTag, ct));
            FirstCall.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records <see cref="NuGetPublishMonitorService.LaunchBackgroundMonitor"/> calls instead of
    /// starting a background task, so launch-decision tests leave no task or timer behind.
    /// </summary>
    private sealed class RecordingBackgroundMonitorService : NuGetPublishMonitorService
    {
        public List<(string Repo, string PackageId, string Version, string ReleaseTag)> Launches { get; } = [];

        public RecordingBackgroundMonitorService(
            TimeProvider clock, HttpMessageHandler handler, HiveConfigFile config, IGoalStore store)
            : base(config: config, eventBus: new RecordingEventBus(), httpClientFactory: CreateFactory(
                handler as ScriptedHttpMessageHandler
                    ?? throw new ArgumentException("handler must be a ScriptedHttpMessageHandler", nameof(handler))),
                goalStore: store, pollInterval: PollInterval, timeoutOverride: OverallTimeout,
                timeProvider: clock)
        {
        }

        internal override void LaunchBackgroundMonitor(
            string repoName, string packageId, string version, string releaseTag, CancellationToken ct)
            => Launches.Add((repoName, packageId, version, releaseTag));
    }

    /// <summary>
    /// Runs a REAL background monitor and reports its lifecycle: <see cref="MonitorStarted"/> when
    /// <see cref="NuGetPublishMonitorService.MonitorPackageAsync"/> begins, and
    /// <see cref="MonitorFinished"/> when its <c>finally</c> has run — the deterministic cleanup
    /// signal, taken without polling.
    /// </summary>
    private sealed class TrackedBackgroundMonitorService : NuGetPublishMonitorService
    {
        public TrackedBackgroundMonitorService(
            TimeProvider clock, HttpMessageHandler handler, HiveConfigFile config, IGoalStore store)
            : base(config: config, eventBus: new RecordingEventBus(), httpClientFactory: CreateFactory(
                handler as ScriptedHttpMessageHandler
                    ?? throw new ArgumentException("handler must be a ScriptedHttpMessageHandler", nameof(handler))),
                goalStore: store, pollInterval: PollInterval, timeoutOverride: OverallTimeout,
                timeProvider: clock)
        {
        }

        public TaskCompletionSource MonitorStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource MonitorFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task MonitorPackageAsync(
            string repoName, string packageId, string version, string releaseTag, CancellationToken ct)
        {
            MonitorStarted.TrySetResult();
            try
            {
                await base.MonitorPackageAsync(repoName, packageId, version, releaseTag, ct);
            }
            finally
            {
                MonitorFinished.TrySetResult();
            }
        }
    }

    // ── LaunchNuGetMonitors (release trigger) ──────────────────────────────

    /// <summary>
    /// Missing monitor or missing config must skip SYNCHRONOUSLY: no request is issued, and because
    /// both guards precede the <c>Task.Run</c>, nothing is ever started. Proven with a real handler
    /// (so a would-be launch is observable) rather than a sleep.
    /// </summary>
    [Fact]
    public async Task LaunchNuGetMonitors_RequiredServicesMissing_Skips()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var realMonitor = CreateService(handler);
        var recording = new RecordingMonitorService();
        var release = new Release
        {
            Id = "v1.2.3", Tag = "v1.2.3", Status = ReleaseStatus.Released,
            RepositoryNames = ["pkg-repo"],
        };

        // No config → the guard returns BEFORE Task.Run, so the real monitor is never invoked and
        // the request handler is never reached. Both guards are synchronous, so no waiting is
        // needed to observe the skip: the recording monitor proves the invocation count directly.
        ApiEndpoints.LaunchNuGetMonitors(realMonitor, null, null, null, release);

        // No monitor → the other guard, same synchronous skip. Passing the recording double as the
        // monitor here makes the assertion meaningful: if the guard were removed, the double WOULD
        // have been called for the PublishNuGet repo.
        ApiEndpoints.LaunchNuGetMonitors(null, CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package"))), null, null, release);
        ApiEndpoints.LaunchNuGetMonitors(recording, null, null, null, release);

        // A synchronous no-op cannot have issued a request — the real handler was never reached.
        Assert.Empty(handler.Urls);
        // And neither guarded call invoked the monitor.
        Assert.Empty(recording.Calls);
        Assert.False(recording.FirstCall.Task.IsCompleted,
            "LaunchNuGetMonitors must skip synchronously when the monitor or config is missing.");

        // POSITIVE CONTROL: the same recording double IS invoked once both inputs are supplied, so
        // the empty-Calls assertions above are a real skip rather than a double that never fires.
        ApiEndpoints.LaunchNuGetMonitors(
            recording, CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package"))),
            null, null, release);

        await recording.FirstCall.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);
        Assert.Single(recording.Calls);
    }

    [Fact]
    public async Task LaunchNuGetMonitors_PrefiltersRepos()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = CreateConfig(
            CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package")),
            CreateRepo("no-pkg-repo"), // PublishNuGet null
            CreateRepo("empty-pkg-repo", publishNuGet: new NuGetPublishConfig { Packages = [] }));
        var monitor = new RecordingMonitorService();
        var release = new Release
        {
            Id = "v1.2.3",
            Tag = "v1.2.3",
            Status = ReleaseStatus.Released,
            RepositoryNames = ["pkg-repo", "no-pkg-repo", "empty-pkg-repo"],
        };

        ApiEndpoints.LaunchNuGetMonitors(monitor, config, null, null, release);

        // Deterministic signal from the recording double — no polling loop, no real deadline.
        await monitor.FirstCall.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);

        // Only the PublishNuGet repo with packages is monitored; the other two are skipped.
        var call = Assert.Single(monitor.Calls);
        Assert.Equal("pkg-repo", call.Repo);
        Assert.Equal("v1.2.3", call.Tag);
    }

    /// <summary>
    /// When no application lifetime is registered the monitor must fall back to
    /// <see cref="CancellationToken.None"/> (never tied to the request token). Asserted on the
    /// token the recorder was actually handed — not by waiting for the monitor to finish.
    /// </summary>
    [Fact]
    public async Task LaunchNuGetMonitors_NoLifetime_FallsBackToCancellationTokenNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package")));
        var monitor = new RecordingMonitorService();
        var release = new Release
        {
            Id = "v1.2.3",
            Tag = "v1.2.3",
            Status = ReleaseStatus.Released,
            RepositoryNames = ["pkg-repo"],
        };

        ApiEndpoints.LaunchNuGetMonitors(monitor, config, null, null, release);

        await monitor.FirstCall.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);

        var call = Assert.Single(monitor.Calls);
        Assert.Equal(CancellationToken.None, call.Ct);
        Assert.False(call.Ct.CanBeCanceled,
            "A lifetime-less launch must bind the monitor to a token that can never be cancelled.");
    }

    [Fact]
    public async Task LaunchNuGetMonitors_WithLifetime_UsesApplicationStoppingToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package")));
        var monitor = new RecordingMonitorService();
        var release = new Release
        {
            Id = "v1.2.3",
            Tag = "v1.2.3",
            Status = ReleaseStatus.Released,
            RepositoryNames = ["pkg-repo"],
        };
        using var stopping = new CancellationTokenSource();
        var lifetime = new FakeHostApplicationLifetime(stopping.Token);

        ApiEndpoints.LaunchNuGetMonitors(monitor, config, lifetime, null, release);

        await monitor.FirstCall.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);

        // The test-owned lifetime token is handed through verbatim.
        var call = Assert.Single(monitor.Calls);
        Assert.Equal(stopping.Token, call.Ct);
    }

    [Fact]
    public async Task LaunchNuGetMonitors_Failure_LogsAndDoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<Program>();
        var clock = new ControlledTimeProvider();
        var config = CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package")));
        var monitor = new ThrowingMonitorService(config, NullLogger<NuGetPublishMonitorService>.Instance, clock);
        var release = new Release
        {
            Id = "v1.2.3",
            Tag = "v1.2.3",
            Status = ReleaseStatus.Released,
            RepositoryNames = ["pkg-repo"],
        };

        ApiEndpoints.LaunchNuGetMonitors(monitor, config, null, logger, release);

        await logger.FirstWrite.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);
        Assert.Contains(logger.Entries, e => e.Contains("NuGet publish monitor failed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, clock.PendingTimerCount);
    }

    /// <summary>
    /// End-to-end: PATCH /api/releases/{id}/status → Released triggers the NuGet monitor
    /// for every PublishNuGet repo in the release via <see cref="ApiEndpoints.LaunchNuGetMonitors"/>.
    /// </summary>
    [Fact]
    public async Task PatchReleaseStatus_Released_TriggersNuGetMonitor()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = new RecordingMonitorService();
        var config = CreateConfig(CreateRepo("pkg-repo", publishNuGet: PkgConfig("My.Package")));
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };

        var baseFactory = new HiveTestFactory { MockRepoManager = fake };
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existingConfig = services.SingleOrDefault(d => d.ServiceType == typeof(HiveConfigFile));
                if (existingConfig is not null)
                    services.Remove(existingConfig);
                services.AddSingleton(config);

                var existingMonitor = services.SingleOrDefault(d => d.ServiceType == typeof(NuGetPublishMonitorService));
                if (existingMonitor is not null)
                    services.Remove(existingMonitor);
                services.AddSingleton<NuGetPublishMonitorService>(monitor);

                services.AddSingleton(sp => new ReleaseExecutionService(
                    sp.GetRequiredService<IGoalStore>(),
                    config,
                    sp.GetRequiredService<IBrainRepoManager>(),
                    sp.GetRequiredService<ILogger<ReleaseExecutionService>>()));
            });
        });
        using var client = factory.CreateClient();

        var releaseId = "test-rel-" + Guid.NewGuid().ToString("N")[..10];
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId,
                Tag = "v1.0.0",
                RepositoryNames = ["pkg-repo"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = "goal-" + Guid.NewGuid().ToString("N")[..10], Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent.Create(new { status = "Released" }),
            ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Deterministic signal from the recording double — the monitor was invoked for the
        // PublishNuGet repo with the release tag.
        await monitor.FirstCall.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);
        var call = Assert.Single(monitor.Calls);
        Assert.Equal("pkg-repo", call.Repo);
        Assert.Equal("v1.0.0", call.Tag);
    }

    /// <summary>
    /// Minimal <see cref="IHostApplicationLifetime"/> whose <c>ApplicationStopping</c> the TEST owns,
    /// so a launch-time token can be observed and cancelled without a real host.
    /// </summary>
    private sealed class FakeHostApplicationLifetime(CancellationToken stopping) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    // ── In-memory doubles ──────────────────────────────────────────────────

    /// <summary>
    /// In-memory <see cref="IGoalStore"/> that returns a configurable release list from
    /// <see cref="GetReleasesAsync"/>.
    /// </summary>
    private sealed class ReleaseStore : IGoalStore
    {
        public List<Release> Releases { get; } = [];

        public string Name => "ReleaseStore";
        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult<Goal?>(null);
        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) => Task.FromResult(goal);
        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IterationSummary>>([]);
        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) => Task.FromResult(release);
        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult<Release?>(null);
        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Release>>(Releases.ToList().AsReadOnly());
        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConversationEntry>>([]);
        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }

    /// <summary>Goal store whose <see cref="IGoalStore.GetReleasesAsync"/> always throws.</summary>
    private sealed class ThrowingGetReleasesStore : IGoalStore
    {
        public string Name => "ThrowingGetReleasesStore";
        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult<Goal?>(null);
        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) => Task.FromResult(goal);
        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IterationSummary>>([]);
        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) => Task.FromResult(release);
        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult<Release?>(null);
        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated store failure");
        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Goal>>([]);
        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConversationEntry>>([]);
        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }

    /// <summary>
    /// Logger that records formatted messages AND signals the first write, so tests can rendezvous
    /// on a diagnostic existing instead of polling for it.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        /// <summary>Completes on the first recorded entry.</summary>
        public TaskCompletionSource FirstWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add(formatter(state, exception));
            FirstWrite.TrySetResult();
        }
    }
}
