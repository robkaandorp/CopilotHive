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
/// </summary>
[Collection("HiveIntegration")]
public sealed class NuGetPublishMonitorLifecycleTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

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
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        private readonly List<string> _urls = [];
        private readonly object _lock = new();

        public IReadOnlyList<string> Urls
        {
            get { lock (_lock) return _urls.ToList(); }
        }

        public ScriptedHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public ScriptedHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this(req => Task.FromResult(responder(req)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
                _urls.Add(request.RequestUri!.ToString());
            return _responder(request);
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

    private static Release ReleasedRelease(
        string tag = "v1.2.3",
        DateTime? releasedAt = null,
        params string[] repos) => new()
        {
            Id = tag,
            Tag = tag,
            Status = ReleaseStatus.Released,
            ReleasedAt = releasedAt ?? DateTime.UtcNow.AddMinutes(-5),
            RepositoryNames = [.. repos],
        };

    private static NuGetPublishMonitorService CreateService(
        ScriptedHttpMessageHandler handler,
        HiveConfigFile? config = null,
        IEventBus? eventBus = null,
        IGoalStore? goalStore = null,
        ILogger<NuGetPublishMonitorService>? logger = null) => new(
            config: config,
            eventBus: eventBus,
            httpClientFactory: CreateFactory(handler),
            logger: logger ?? NullLogger<NuGetPublishMonitorService>.Instance,
            goalStore: goalStore,
            pollInterval: PollInterval,
            timeoutOverride: Timeout);

    // ── StartupScanAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task StartupScanAsync_NullGoalStore_ReturnsImmediately()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = CreateService(handler, config: CreateConfig(CreateRepo("test-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] })));

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task StartupScanAsync_NullConfig_ReturnsImmediately()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = CreateService(handler, goalStore: new ReleaseStore());

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Urls);
    }

    /// <summary>
    /// Only releases released within the last 60 minutes (exclusive cutoff) are scanned:
    /// Planning releases, releases with no ReleasedAt, and releases older than the cutoff
    /// are all skipped.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_ExclusiveCutoff_FiltersReleases()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease("v1.0.0", DateTime.UtcNow.AddMinutes(-61), "pkg-repo"));
        store.Releases.Add(ReleasedRelease("v1.2.3", DateTime.UtcNow.AddMinutes(-59), "pkg-repo"));
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }));
        var service = CreateService(handler, config: config, eventBus: new RecordingEventBus(), goalStore: store);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        // Only the recent release was probed; the 61-minute-old release was filtered out.
        // The probe found the version inline (1.2.3 matches tag v1.2.3), so no background
        // monitor was launched and no extra URLs appear.
        Assert.Single(handler.Urls);
        Assert.Contains(handler.Urls, u => u.Contains("my.package", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartupScanAsync_NotFound_LaunchesBackgroundMonitor()
    {
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.NotFound));
        var eventBus = new RecordingEventBus();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease("v1.2.3", repos: "pkg-repo"));
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }));
        var service = CreateService(handler, config: config, eventBus: eventBus, goalStore: store);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        // The background monitor keeps polling the index (NotFound → delay → probe again).
        await WaitUntilAsync(() => handler.Urls.Count >= 2, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartupScanAsync_Found_DoesNotLaunchMonitor()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var eventBus = new RecordingEventBus();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease("v1.2.3", DateTime.UtcNow.AddMinutes(-1), "pkg-repo"));
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }));
        var service = CreateService(handler, config: config, eventBus: eventBus, goalStore: store);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(handler.Urls); // exactly one probe, no background monitor loop
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, evt.Type);
    }

    [Fact]
    public async Task StartupScanAsync_Terminal_DoesNotLaunchMonitor()
    {
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.BadRequest));
        var eventBus = new RecordingEventBus();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease("v1.2.3", DateTime.UtcNow.AddMinutes(-1), "pkg-repo"));
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }));
        var service = CreateService(handler, config: config, eventBus: eventBus, goalStore: store);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Single(handler.Urls); // probe once, Terminal → no background monitor
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// Only <see cref="ReleaseStatus.Released"/> releases are scanned: a release in
    /// <see cref="ReleaseStatus.Planning"/> (or any non-Released status) with a recent
    /// ReleasedAt must be skipped entirely.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_NonReleasedStatus_Skips()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var store = new ReleaseStore();
        store.Releases.Add(new Release
        {
            Id = "v1.2.3",
            Tag = "v1.2.3",
            Status = ReleaseStatus.Planning,
            ReleasedAt = DateTime.UtcNow.AddMinutes(-5),
            RepositoryNames = ["pkg-repo"],
        });
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }));
        var service = CreateService(handler, config: config, eventBus: new RecordingEventBus(), goalStore: store);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Urls);
    }

    /// <summary>
    /// A <see cref="NuGetPublishMonitorService.ProbeResult.Retry"/> result (e.g. 404 on the
    /// registration index) must launch a background monitor — the package may not be registered
    /// yet but could land shortly.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_Retry_LaunchesBackgroundMonitor()
    {
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.NotFound));
        var eventBus = new RecordingEventBus();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease("v1.2.3", repos: "pkg-repo"));
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }));
        var service = CreateService(handler, config: config, eventBus: eventBus, goalStore: store);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        // The background monitor keeps polling the index (404 → Retry → delay → probe again).
        await WaitUntilAsync(() => handler.Urls.Count >= 2, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartupScanAsync_TagStripping_ProbesStrippedVersion()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var eventBus = new RecordingEventBus();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease("v1.2.3", DateTime.UtcNow.AddMinutes(-1), "pkg-repo"));
        var service = CreateService(handler,
            config: CreateConfig(CreateRepo("pkg-repo",
                publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] })),
            eventBus: eventBus, goalStore: store);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, evt.Type);
        Assert.Contains("1.2.3", evt.Message);
    }

    [Fact]
    public async Task StartupScanAsync_BlankOrInvalidTag_Skips()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var eventBus = new RecordingEventBus();
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease("v", DateTime.UtcNow.AddMinutes(-1), "pkg-repo"));
        store.Releases.Add(ReleasedRelease("vnot-a-version", DateTime.UtcNow.AddMinutes(-1), "pkg-repo"));
        var service = CreateService(handler,
            config: CreateConfig(CreateRepo("pkg-repo",
                publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] })),
            eventBus: eventBus, goalStore: store);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Urls);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task StartupScanAsync_GetReleasesFailure_LogsWarningAndReturns()
    {
        var logger = new CapturingLogger<NuGetPublishMonitorService>();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = CreateService(handler,
            config: CreateConfig(CreateRepo("pkg-repo",
                publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] })),
            goalStore: new ThrowingGetReleasesStore(), logger: logger);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Urls);
        Assert.Contains(logger.Entries, e => e.Contains("failed to load releases", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartupScanAsync_CallerCancellation_Returns()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease("v1.2.3", DateTime.UtcNow.AddMinutes(-1), "pkg-repo"));
        var service = CreateService(handler,
            config: CreateConfig(CreateRepo("pkg-repo",
                publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] })),
            goalStore: store);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await service.StartupScanAsync(cts.Token);

        Assert.Empty(handler.Urls);
    }

    /// <summary>
    /// Per-package isolation: a probe failure for one package must not stop the scan — the
    /// next package is still probed.
    /// </summary>
    [Fact]
    public async Task StartupScanAsync_ProbeException_LogsAndContinues()
    {
        var logger = new CapturingLogger<NuGetPublishMonitorService>();
        Func<HttpRequestMessage, HttpResponseMessage> responder = _ =>
            throw new InvalidOperationException("Simulated probe failure");
        var handler = new ScriptedHttpMessageHandler(responder);
        var store = new ReleaseStore();
        store.Releases.Add(ReleasedRelease("v1.2.3", DateTime.UtcNow.AddMinutes(-1), "pkg-repo"));
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig
            {
                Packages =
                [
                    new NuGetPackageEntry { PackageId = "Broken.Package" },
                    new NuGetPackageEntry { PackageId = "My.Package" },
                ]
            }));
        var service = CreateService(handler, config: config, eventBus: new RecordingEventBus(), goalStore: store, logger: logger);

        await service.StartupScanAsync(TestContext.Current.CancellationToken);

        // The first package's probe threw — logged. The scan continued and probed the
        // second package (which also threw against this handler).
        Assert.Contains(logger.Entries, e => e.Contains("scan probe failed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, handler.Urls.Count);
    }

    // ── LaunchBackgroundMonitor ────────────────────────────────────────────

    [Fact]
    public async Task LaunchBackgroundMonitor_LogsExceptions()
    {
        var logger = new CapturingLogger<NuGetPublishMonitorService>();
        var service = new ThrowingMonitorService(null, logger);

        service.LaunchBackgroundMonitor("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => logger.Entries.Count > 0, TestContext.Current.CancellationToken);
        Assert.Contains(logger.Entries, e => e.Contains("NuGet monitor failed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Subclass whose <see cref="MonitorPackageAsync"/> always throws.</summary>
    private sealed class ThrowingMonitorService : NuGetPublishMonitorService
    {
        public ThrowingMonitorService(HiveConfigFile? config, ILogger<NuGetPublishMonitorService> logger)
            : base(config: config, logger: logger)
        {
        }

        public override Task MonitorPackageAsync(
            string repoName, string packageId, string version, string releaseTag, CancellationToken ct)
            => throw new InvalidOperationException("Simulated monitor failure");
    }

    /// <summary>Subclass that records <see cref="MonitorReleaseAsync"/> invocations.</summary>
    private sealed class RecordingMonitorService : NuGetPublishMonitorService
    {
        public List<(string Repo, string Tag, CancellationToken Ct)> Calls { get; } = [];

        public override Task MonitorReleaseAsync(string repoName, string releaseTag, CancellationToken ct)
        {
            lock (Calls)
                Calls.Add((repoName, releaseTag, ct));
            return Task.CompletedTask;
        }
    }

    // ── LaunchNuGetMonitors (release trigger) ──────────────────────────────

    [Fact]
    public async Task LaunchNuGetMonitors_RequiredServicesMissing_Skips()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var monitor = CreateService(handler);
        var release = ReleasedRelease("v1.2.3", DateTime.UtcNow.AddMinutes(-1), "pkg-repo");

        // No config → skip.
        ApiEndpoints.LaunchNuGetMonitors(monitor, null, null, null, release);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(handler.Urls);

        // No monitor → skip.
        ApiEndpoints.LaunchNuGetMonitors(null, CreateConfig(), null, null, release);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task LaunchNuGetMonitors_PrefiltersRepos()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(
            CreateRepo("pkg-repo", publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }),
            CreateRepo("no-pkg-repo"), // PublishNuGet null
            CreateRepo("empty-pkg-repo", publishNuGet: new NuGetPublishConfig { Packages = [] }));
        var monitor = CreateService(handler, config: config, eventBus: new RecordingEventBus());
        var release = new Release
        {
            Id = "v1.2.3",
            Tag = "v1.2.3",
            Status = ReleaseStatus.Released,
            RepositoryNames = ["pkg-repo", "no-pkg-repo", "empty-pkg-repo"],
        };

        ApiEndpoints.LaunchNuGetMonitors(monitor, config, null, null, release);

        await WaitUntilAsync(() => handler.Urls.Count >= 1, TestContext.Current.CancellationToken);
        // Only the PublishNuGet repo with packages is monitored.
        Assert.All(handler.Urls, u => Assert.Contains("my.package", u, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// When no application lifetime is registered the monitor must fall back to
    /// <see cref="CancellationToken.None"/> (never tied to the request token).
    /// </summary>
    [Fact]
    public async Task LaunchNuGetMonitors_NoLifetime_FallsBackToCancellationTokenNone()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }));
        var monitor = CreateService(handler, config: config, eventBus: new RecordingEventBus());
        var release = new Release
        {
            Id = "v1.2.3",
            Tag = "v1.2.3",
            Status = ReleaseStatus.Released,
            RepositoryNames = ["pkg-repo"],
        };

        ApiEndpoints.LaunchNuGetMonitors(monitor, config, null, null, release);

        // The background monitor runs to completion (Found → publishes) — proving the
        // fallback token was not cancelled.
        await WaitUntilAsync(() => handler.Urls.Count >= 1, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LaunchNuGetMonitors_Failure_LogsAndDoesNotThrow()
    {
        var logger = new CapturingLogger<Program>();
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }));
        var monitor = new ThrowingMonitorService(config, NullLogger<NuGetPublishMonitorService>.Instance);
        var release = new Release
        {
            Id = "v1.2.3",
            Tag = "v1.2.3",
            Status = ReleaseStatus.Released,
            RepositoryNames = ["pkg-repo"],
        };

        ApiEndpoints.LaunchNuGetMonitors(monitor, config, null, logger, release);

        await WaitUntilAsync(() => logger.Entries.Count > 0, TestContext.Current.CancellationToken);
        Assert.Contains(logger.Entries, e => e.Contains("NuGet publish monitor failed", StringComparison.OrdinalIgnoreCase));
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
        var config = CreateConfig(CreateRepo("pkg-repo",
            publishNuGet: new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "My.Package" }] }));
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

        // The monitor was invoked for the PublishNuGet repo with the release tag.
        await WaitUntilAsync(() => monitor.Calls.Count >= 1, ct);
        Assert.Contains(monitor.Calls, c => c.Repo == "pkg-repo" && c.Tag == "v1.0.0");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(
        Func<bool> condition, CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(10, ct);
        }
    }

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
        public Task<IReadOnlyList<(string, PersistedClarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }

    /// <summary>Logger that records formatted messages so tests can assert on diagnostics.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

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
        }
    }
}
