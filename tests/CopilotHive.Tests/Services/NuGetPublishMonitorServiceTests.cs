using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using CopilotHive;
using CopilotHive.Configuration;
using CopilotHive.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="NuGetPublishMonitorService"/>: NuGet registration polling,
/// inline-first page resolution, @id validation, HTTP status handling, Retry-After
/// parsing, timeout/cancellation boundaries, dedup, and event message formats.
/// </summary>
public sealed class NuGetPublishMonitorServiceTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    // ── Test infrastructure ────────────────────────────────────────────────

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

    private static NuGetPublishMonitorService CreateService(
        ScriptedHttpMessageHandler handler,
        HiveConfigFile? config = null,
        IEventBus? eventBus = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeoutOverride = null)
    {
        return new NuGetPublishMonitorService(
            config: config,
            eventBus: eventBus,
            httpClientFactory: CreateFactory(handler),
            logger: NullLogger<NuGetPublishMonitorService>.Instance,
            pollInterval: pollInterval ?? PollInterval,
            timeoutOverride: timeoutOverride ?? Timeout);
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

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is not null)
            Assert.True(response.Headers.TryAddWithoutValidation("Retry-After", retryAfter));
        return response;
    }

    // ── JSON builders ──────────────────────────────────────────────────────

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

    private static string PageJsonWithMatch(string version) =>
        JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new { catalogEntry = new { version } }
            }
        });

    private static string IndexJsonWithPage(string pageUrl) =>
        JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new Dictionary<string, string> { ["@id"] = pageUrl }
            }
        });

    private static string IndexJsonWithInlineMatchAndPage(string version, string pageUrl) =>
        JsonSerializer.Serialize(new
        {
            items = new object[]
            {
                new
                {
                    items = new[]
                    {
                        new { catalogEntry = new { version } }
                    }
                },
                new Dictionary<string, string> { ["@id"] = pageUrl }
            }
        });

    // ── Found / timeout ────────────────────────────────────────────────────

    [Fact]
    public async Task MonitorPackageAsync_FoundInline_PublishesPackagePublished()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, evt.Type);
        Assert.Equal("Package My.Package 1.2.3 published on NuGet (release v1.2.3)", evt.Message);
        Assert.Equal("test-repo", evt.Repository);
        Assert.Null(evt.ReleaseId);
    }

    [Fact]
    public async Task MonitorPackageAsync_FoundInPage_PublishesPackagePublished()
    {
        var eventBus = new RecordingEventBus();
        var pageUrl = "https://api.nuget.org/v3/registration5-gz-semver2/mypackage/page.json";
        var handler = new ScriptedHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString() == pageUrl)
                return OkResponse(PageJsonWithMatch("1.2.3"));
            return OkResponse(IndexJsonWithPage(pageUrl));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, evt.Type);
        Assert.Equal("Package My.Package 1.2.3 published on NuGet (release v1.2.3)", evt.Message);
        Assert.Equal("test-repo", evt.Repository);
        Assert.Null(evt.ReleaseId);
    }

    [Fact]
    public async Task MonitorPackageAsync_Timeout_PublishesPackagePublishTimedOut()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.NotFound));
        var service = CreateService(handler, eventBus: eventBus, timeoutOverride: TimeSpan.FromMilliseconds(200));

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublishTimedOut, evt.Type);
        Assert.Matches(@"^Package My\.Package 1\.2\.3 not found on NuGet after \d+s \(release v1\.2\.3\)$", evt.Message);
        Assert.Equal("test-repo", evt.Repository);
        Assert.Null(evt.ReleaseId);
    }

    // ── Version handling ───────────────────────────────────────────────────

    [Fact]
    public async Task MonitorPackageAsync_VersionNormalization_1_0_Matches_1_0_0()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.0.0")));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.0", "v1.0", TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, eventBus.Published[0].Type);
    }

    /// <summary>
    /// <see cref="NuGetPublishMonitorService.MonitorPackageAsync"/> must NOT strip a leading
    /// <c>v</c>/<c>V</c> — it receives an already-stripped version. <c>NuGetVersion.TryParse("v1.2.3")</c>
    /// fails, so the call must return without any HTTP request or event.
    /// </summary>
    [Fact]
    public async Task MonitorPackageAsync_VPrefixedVersion_ReturnsWithoutRequest()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "v1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("1.2.3")]
    public async Task MonitorReleaseAsync_StripsLeadingV_Publishes(string releaseTag)
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("test-repo", publishNuGet: new NuGetPublishConfig
        {
            Packages = [new NuGetPackageEntry { PackageId = "My.Package" }]
        }));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorReleaseAsync("test-repo", releaseTag, TestContext.Current.CancellationToken);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, evt.Type);
        Assert.Equal($"Package My.Package 1.2.3 published on NuGet (release {releaseTag})", evt.Message);
    }

    [Fact]
    public async Task MonitorReleaseAsync_InvalidVersion_ReturnsWithoutEvents()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("test-repo", publishNuGet: new NuGetPublishConfig
        {
            Packages = [new NuGetPackageEntry { PackageId = "My.Package" }]
        }));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorReleaseAsync("test-repo", "vnot-a-version", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task MonitorReleaseAsync_BlankAfterStrip_ReturnsWithoutEvents()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("test-repo", publishNuGet: new NuGetPublishConfig
        {
            Packages = [new NuGetPackageEntry { PackageId = "My.Package" }]
        }));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorReleaseAsync("test-repo", "v", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    // ── Dedup / multi-package ──────────────────────────────────────────────

    [Fact]
    public async Task MonitorPackageAsync_Dedup_SecondConcurrentCallSkipped()
    {
        var eventBus = new RecordingEventBus();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(async _ =>
        {
            callCount++;
            if (callCount == 1)
                await gate.Task;
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        var first = service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);
        gate.SetResult(true);
        await first;

        Assert.Equal(1, callCount);
        Assert.Single(eventBus.Published);
    }

    [Fact]
    public async Task MonitorReleaseAsync_MultiplePackages_MonitorsAll()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("test-repo", publishNuGet: new NuGetPublishConfig
        {
            Packages =
            [
                new NuGetPackageEntry { PackageId = "My.Package" },
                new NuGetPackageEntry { PackageId = "Other.Package" }
            ]
        }));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorReleaseAsync("test-repo", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, eventBus.Published.Count);
        Assert.All(eventBus.Published, e => Assert.Equal(EventType.PackagePublished, e.Type));
    }

    // ── Config / dependency validation ─────────────────────────────────────

    [Fact]
    public async Task MonitorReleaseAsync_NullConfig_ReturnsWithoutEvents()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = new NuGetPublishMonitorService(
            config: null,
            eventBus: eventBus,
            httpClientFactory: CreateFactory(handler),
            pollInterval: PollInterval,
            timeoutOverride: Timeout);

        await service.MonitorReleaseAsync("test-repo", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task MonitorReleaseAsync_RepoNotFound_ReturnsWithoutEvents()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("other-repo", publishNuGet: new NuGetPublishConfig
        {
            Packages = [new NuGetPackageEntry { PackageId = "My.Package" }]
        }));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorReleaseAsync("test-repo", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task MonitorReleaseAsync_RepoNameCaseInsensitive_ResolvesAndPublishes()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("Test-Repo", publishNuGet: new NuGetPublishConfig
        {
            Packages = [new NuGetPackageEntry { PackageId = "My.Package" }]
        }));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorReleaseAsync("test-repo", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, eventBus.Published[0].Type);
    }

    [Fact]
    public async Task MonitorReleaseAsync_NullPublishNuGet_ReturnsWithoutEvents()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("test-repo", publishNuGet: null));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorReleaseAsync("test-repo", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task MonitorReleaseAsync_EmptyPackagesList_ReturnsWithoutEvents()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("test-repo", publishNuGet: new NuGetPublishConfig { Packages = [] }));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorReleaseAsync("test-repo", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task MonitorPackageAsync_NullEventBus_ReturnsWithoutRequest()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = new NuGetPublishMonitorService(
            eventBus: null,
            httpClientFactory: CreateFactory(handler),
            pollInterval: PollInterval,
            timeoutOverride: Timeout);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task MonitorPackageAsync_NullHttpClientFactory_ReturnsWithoutRequest()
    {
        var eventBus = new RecordingEventBus();
        var service = new NuGetPublishMonitorService(
            eventBus: eventBus,
            httpClientFactory: null,
            pollInterval: PollInterval,
            timeoutOverride: Timeout);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
    }

    [Theory]
    [InlineData("", "My.Package", "1.2.3", "v1.2.3")]
    [InlineData("test-repo", "", "1.2.3", "v1.2.3")]
    [InlineData("test-repo", "My.Package", "", "v1.2.3")]
    [InlineData("test-repo", "My.Package", "1.2.3", "")]
    [InlineData("   ", "My.Package", "1.2.3", "v1.2.3")]
    [InlineData("test-repo", "   ", "1.2.3", "v1.2.3")]
    [InlineData("test-repo", "My.Package", "   ", "v1.2.3")]
    [InlineData("test-repo", "My.Package", "1.2.3", "   ")]
    [InlineData(null!, "My.Package", "1.2.3", "v1.2.3")]
    [InlineData("test-repo", null!, "1.2.3", "v1.2.3")]
    [InlineData("test-repo", "My.Package", null!, "v1.2.3")]
    [InlineData("test-repo", "My.Package", "1.2.3", null!)]
    public async Task MonitorPackageAsync_BlankInputs_ReturnsWithoutRequest(
        string? repoName, string? packageId, string? version, string? releaseTag)
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync(
            repoName!, packageId!, version!, releaseTag!, TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    [Theory]
    [InlineData("", "v1.2.3")]
    [InlineData("test-repo", "")]
    [InlineData("   ", "v1.2.3")]
    [InlineData("test-repo", "   ")]
    [InlineData(null!, "v1.2.3")]
    [InlineData("test-repo", null!)]
    public async Task MonitorReleaseAsync_BlankInputs_ReturnsWithoutEvents(string? repoName, string? releaseTag)
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var config = CreateConfig(CreateRepo("test-repo", publishNuGet: new NuGetPublishConfig
        {
            Packages = [new NuGetPackageEntry { PackageId = "My.Package" }]
        }));
        var service = CreateService(handler, config: config, eventBus: eventBus);

        await service.MonitorReleaseAsync(repoName!, releaseTag!, TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    // ── HTTP status handling ───────────────────────────────────────────────

    [Fact]
    public async Task MonitorPackageAsync_404_RetriesThenFinds()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.NotFound);
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, eventBus.Published[0].Type);
    }

    [Fact]
    public async Task MonitorPackageAsync_429WithRetryAfterZero_UsesPollInterval()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.TooManyRequests, retryAfter: "0");
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
    }

    [Fact]
    public async Task MonitorPackageAsync_429WithRetryAfterDeltaSeconds_DelaysThenFinds()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.TooManyRequests, retryAfter: "1");
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus, timeoutOverride: TimeSpan.FromSeconds(5));

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
    }

    [Fact]
    public async Task MonitorPackageAsync_429WithRetryAfterPastDate_UsesPollInterval()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var pastDate = DateTimeOffset.UtcNow.AddSeconds(-10).ToString("R", CultureInfo.InvariantCulture);
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.TooManyRequests, retryAfter: pastDate);
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
    }

    [Fact]
    public async Task MonitorPackageAsync_429WithRetryAfterFutureDate_DelaysThenFinds()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var futureDate = DateTimeOffset.UtcNow.AddSeconds(1).ToString("R", CultureInfo.InvariantCulture);
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.TooManyRequests, retryAfter: futureDate);
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus, timeoutOverride: TimeSpan.FromSeconds(5));

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
    }

    [Fact]
    public async Task MonitorPackageAsync_429WithoutRetryAfter_UsesPollInterval()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.TooManyRequests);
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
    }

    [Fact]
    public async Task MonitorPackageAsync_Other4xx_TerminalReturn()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.BadRequest));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Empty(eventBus.Published);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task MonitorPackageAsync_5xx_RetriesThenFinds()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return ErrorResponse(HttpStatusCode.InternalServerError);
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
    }

    [Fact]
    public async Task MonitorPackageAsync_MalformedJson_RetriesThenFinds()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return OkResponse("not json");
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
    }

    [Fact]
    public async Task MonitorPackageAsync_TransportError_RetriesThenFinds()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("Simulated transport failure");
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
    }

    [Theory]
    [InlineData("http://api.nuget.org/v3/page.json")] // not HTTPS
    [InlineData("https://evil.example.com/v3/page.json")] // wrong host
    [InlineData("https://api.nuget.org:8080/v3/page.json")] // wrong port
    public async Task MonitorPackageAsync_InvalidPageUrl_SkippedAndRetried(string pageUrl)
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return OkResponse(IndexJsonWithPage(pageUrl));
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, eventBus.Published[0].Type);
    }

    /// <summary>
    /// A page fetch that fails (HTTP error) must be skipped — the probe continues to the
    /// next page or the next poll iteration. A page failure must never fault the whole probe.
    /// </summary>
    [Fact]
    public async Task MonitorPackageAsync_PageError_SkippedAndRetried()
    {
        var eventBus = new RecordingEventBus();
        var pageUrl = "https://api.nuget.org/v3/registration5-gz-semver2/mypackage/page.json";
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(req =>
        {
            callCount++;
            if (req.RequestUri!.ToString() == pageUrl)
                return ErrorResponse(HttpStatusCode.InternalServerError);
            if (callCount == 1)
                return OkResponse(IndexJsonWithPage(pageUrl));
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(3, callCount); // index + failed page + index again
        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, eventBus.Published[0].Type);
    }

    // ── Removal-proof: timeout boundary ───────────────────────────────────

    /// <summary>
    /// The handler blocks on a TCS; the timeout fires first, then the caller cancels.
    /// When the handler is released, the service must observe the caller's cancellation
    /// and publish NO event. Removing the <c>ct.IsCancellationRequested</c> check in the
    /// timeout handler would publish <c>PackagePublishTimedOut</c> and fail this test.
    /// </summary>
    [Fact]
    public async Task MonitorPackageAsync_CallerCancelledAfterTimeout_NoEvent()
    {
        var eventBus = new RecordingEventBus();
        using var requestStarted = new SemaphoreSlim(0, 1);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHttpMessageHandler(async _ =>
        {
            requestStarted.Release();
            await release.Task;
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus, timeoutOverride: TimeSpan.FromSeconds(1));

        using var cts = new CancellationTokenSource();
        var monitorTask = service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", cts.Token);
        try
        {
            // Rendezvous: proves the implementation actually issued a request (rules out a no-op).
            Assert.True(await requestStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
                "The service never issued a request — cancellation would be vacuous.");

            // Let the 1s timeout fire, then cancel the caller.
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            await cts.CancelAsync();
            release.SetResult(true);
            await monitorTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult(true);
        }

        Assert.Empty(eventBus.Published);
    }

    // ── Removal-proof: page-result precedence ──────────────────────────────

    /// <summary>
    /// The index carries BOTH an inline match and a paged <c>@id</c>. The inline match must
    /// win: <c>PackagePublished</c> is emitted and the page URL is never requested.
    /// Fetching pages before checking inline entries would request the page and fail this test.
    /// </summary>
    [Fact]
    public async Task MonitorPackageAsync_InlineMatch_PageNeverRequested()
    {
        var eventBus = new RecordingEventBus();
        var pageUrl = "https://api.nuget.org/v3/registration5-gz-semver2/mypackage/page.json";
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatchAndPage("1.2.3", pageUrl)));
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, eventBus.Published[0].Type);
        Assert.DoesNotContain(handler.Urls, u => u == pageUrl);
    }

    // ── Removal-proof: malformed @id ───────────────────────────────────────

    /// <summary>
    /// A relative <c>@id</c> (e.g. <c>"./page.json"</c>) must be skipped by URL validation —
    /// not fetched (which would throw <c>InvalidOperationException</c> on a relative URI).
    /// The second probe finds the package inline. Removing the @id validation would fault
    /// the first probe and fail this test.
    /// </summary>
    [Fact]
    public async Task MonitorPackageAsync_MalformedPageId_SkippedAndRetried()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return OkResponse(IndexJsonWithPage("./page.json"));
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, eventBus.Published[0].Type);
    }

    // ── Removal-proof: HTTP timeout retry ──────────────────────────────────

    /// <summary>
    /// A <see cref="TaskCanceledException"/> that is NOT caused by the caller token or the
    /// overall timeout is an HTTP client timeout and must be retried. Treating it as
    /// cancellation would publish <c>PackagePublishTimedOut</c> (or propagate) and fail this test.
    /// </summary>
    [Fact]
    public async Task MonitorPackageAsync_HttpTimeout_RetriesAndFinds()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                throw new TaskCanceledException("The request timed out.");
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, eventBus.Published[0].Type);
    }

    // ── Named client ───────────────────────────────────────────────────────

    /// <summary>
    /// The service must explicitly request the <c>nuget-api</c> named client (which carries
    /// gzip decompression). A different name (or an un-named client) fails this test.
    /// </summary>
    [Fact]
    public async Task MonitorPackageAsync_UsesNamedClient_NugetApi()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var factory = new Mock<IHttpClientFactory>();
        var requestedNames = new List<string>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Callback<string>(name => requestedNames.Add(name))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var service = new NuGetPublishMonitorService(
            eventBus: eventBus,
            httpClientFactory: factory.Object,
            pollInterval: PollInterval,
            timeoutOverride: Timeout);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Contains("nuget-api", requestedNames);
        Assert.Single(eventBus.Published);
    }

    // ── ProbePackageAsync: single-iteration probe ─────────────────────────

    [Fact]
    public async Task ProbePackageAsync_2xxInlineMatch_ReturnsFoundAndPublishes()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Found, outcome.Result);
        Assert.Null(outcome.RetryAfter);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, evt.Type);
    }

    [Fact]
    public async Task ProbePackageAsync_NonObjectRoot_ReturnsRetry()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse("[1, 2, 3]"));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Retry, outcome.Result);
        Assert.Empty(eventBus.Published);
    }

    [Theory]
    [InlineData("{}")] // items missing
    [InlineData("{\"items\": 42}")] // items non-array
    public async Task ProbePackageAsync_ItemsMissingOrNonArray_ReturnsRetry(string body)
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(body));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Retry, outcome.Result);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task ProbePackageAsync_EmptyItems_ReturnsNotFound()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse("{\"items\": []}"));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.NotFound, outcome.Result);
        Assert.Empty(eventBus.Published);
    }

    /// <summary>
    /// Malformed index entries must be skipped: non-object items, non-array nested
    /// <c>items</c>, and non-string <c>@id</c> are all ignored. The probe completes with
    /// <see cref="NuGetPublishMonitorService.ProbeResult.NotFound"/> — never faults and never fetches garbage pages.
    /// </summary>
    [Fact]
    public async Task ProbePackageAsync_ShapeErrors_SkippedAndNotFound()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse("""
            {
              "items": [
                42,
                { "items": "not-an-array", "@id": "./relative.json" },
                { "@id": 123 },
                { "items": [ { "catalogEntry": { "version": "9.9.9" } } ] }
              ]
            }
            """));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.NotFound, outcome.Result);
        Assert.Empty(eventBus.Published);
        Assert.All(handler.Urls, u => Assert.DoesNotContain("./relative.json", u));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Continue)] // 1xx
    [InlineData(HttpStatusCode.Redirect)] // 3xx
    public async Task ProbePackageAsync_RetryStatuses_ReturnRetry(HttpStatusCode status)
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(status));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Retry, outcome.Result);
        Assert.Null(outcome.RetryAfter);
        Assert.Empty(eventBus.Published);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task ProbePackageAsync_Other4xx_ReturnsTerminal(HttpStatusCode status)
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(status));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Terminal, outcome.Result);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task ProbePackageAsync_429WithRetryAfter_ReturnsRetryWithPositiveRetryAfter()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.TooManyRequests, retryAfter: "5"));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Retry, outcome.Result);
        Assert.NotNull(outcome.RetryAfter);
        Assert.True(outcome.RetryAfter > TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(5), outcome.RetryAfter);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task ProbePackageAsync_429WithoutRetryAfter_ReturnsRetryWithNullRetryAfter()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.TooManyRequests));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Retry, outcome.Result);
        Assert.Null(outcome.RetryAfter);
    }

    [Fact]
    public async Task ProbePackageAsync_429WithZeroRetryAfter_NullRetryAfter()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => ErrorResponse(HttpStatusCode.TooManyRequests, retryAfter: "0"));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Retry, outcome.Result);
        Assert.Null(outcome.RetryAfter);
    }

    [Fact]
    public async Task ProbePackageAsync_NullDependencies_ReturnsTerminal()
    {
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));

        var noBus = new NuGetPublishMonitorService(
            httpClientFactory: CreateFactory(handler),
            pollInterval: PollInterval,
            timeoutOverride: Timeout);
        var noBusOutcome = await noBus.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);
        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Terminal, noBusOutcome.Result);

        var noFactory = new NuGetPublishMonitorService(
            eventBus: new RecordingEventBus(),
            pollInterval: PollInterval,
            timeoutOverride: Timeout);
        var noFactoryOutcome = await noFactory.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);
        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Terminal, noFactoryOutcome.Result);

        Assert.Empty(handler.Urls);
    }

    [Theory]
    [InlineData("", "My.Package", "1.2.3", "v1.2.3")]
    [InlineData("test-repo", "", "1.2.3", "v1.2.3")]
    [InlineData("test-repo", "My.Package", "", "v1.2.3")]
    [InlineData("test-repo", "My.Package", "1.2.3", "")]
    [InlineData("test-repo", "My.Package", "v1.2.3", "v1.2.3")]
    [InlineData("test-repo", "My.Package", "not-a-version", "v1.2.3")]
    public async Task ProbePackageAsync_BlankOrInvalidInputs_ReturnsTerminal(
        string? repoName, string? packageId, string? version, string? releaseTag)
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            repoName!, packageId!, version!, releaseTag!, TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Terminal, outcome.Result);
        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task ProbePackageAsync_CancellationRequested_Throws()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse(IndexJsonWithInlineMatch("1.2.3")));
        var service = CreateService(handler, eventBus: eventBus);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ProbePackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", cts.Token));

        Assert.Empty(eventBus.Published);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task ProbePackageAsync_PageError_Skipped_NotFound()
    {
        var eventBus = new RecordingEventBus();
        var pageUrl = "https://api.nuget.org/v3/registration5-gz-semver2/mypackage/page.json";
        var handler = new ScriptedHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString() == pageUrl)
                return ErrorResponse(HttpStatusCode.InternalServerError);
            return OkResponse(IndexJsonWithPage(pageUrl));
        });
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.NotFound, outcome.Result);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task ProbePackageAsync_PageMalformedJson_Skipped_NotFound()
    {
        var eventBus = new RecordingEventBus();
        var pageUrl = "https://api.nuget.org/v3/registration5-gz-semver2/mypackage/page.json";
        var handler = new ScriptedHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString() == pageUrl)
                return OkResponse("not json");
            return OkResponse(IndexJsonWithPage(pageUrl));
        });
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.NotFound, outcome.Result);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task ProbePackageAsync_PageCancellation_Propagates()
    {
        var eventBus = new RecordingEventBus();
        var pageUrl = "https://api.nuget.org/v3/registration5-gz-semver2/mypackage/page.json";
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHttpMessageHandler(async req =>
        {
            if (req.RequestUri!.ToString() == pageUrl)
            {
                await release.Task;
                return OkResponse(PageJsonWithMatch("1.2.3"));
            }
            return OkResponse(IndexJsonWithPage(pageUrl));
        });
        var service = CreateService(handler, eventBus: eventBus);
        using var cts = new CancellationTokenSource();

        var probe = service.ProbePackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        release.SetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task ProbePackageAsync_PageMatch_ReturnsFoundAndPublishes()
    {
        var eventBus = new RecordingEventBus();
        var pageUrl = "https://api.nuget.org/v3/registration5-gz-semver2/mypackage/page.json";
        var handler = new ScriptedHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString() == pageUrl)
                return OkResponse(PageJsonWithMatch("1.2.3"));
            return OkResponse(IndexJsonWithPage(pageUrl));
        });
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Found, outcome.Result);
        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, evt.Type);
    }

    // ── ProbePackageAsync: transport / timeout / malformed JSON ──────────────

    [Fact]
    public async Task ProbePackageAsync_TransportError_ReturnsRetry()
    {
        var eventBus = new RecordingEventBus();
        Func<HttpRequestMessage, HttpResponseMessage> responder = _ => throw new HttpRequestException("Simulated transport failure");
        var handler = new ScriptedHttpMessageHandler(responder);
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Retry, outcome.Result);
        Assert.Null(outcome.RetryAfter);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task ProbePackageAsync_HttpTimeout_ReturnsRetry()
    {
        var eventBus = new RecordingEventBus();
        Func<HttpRequestMessage, HttpResponseMessage> responder = _ => throw new TaskCanceledException("HTTP timeout");
        var handler = new ScriptedHttpMessageHandler(responder);
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Retry, outcome.Result);
        Assert.Null(outcome.RetryAfter);
        Assert.Empty(eventBus.Published);
    }

    [Fact]
    public async Task ProbePackageAsync_MalformedJson_ReturnsRetry()
    {
        var eventBus = new RecordingEventBus();
        var handler = new ScriptedHttpMessageHandler(_ => OkResponse("not valid json"));
        var service = CreateService(handler, eventBus: eventBus);

        var outcome = await service.ProbePackageAsync(
            "test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(NuGetPublishMonitorService.ProbeResult.Retry, outcome.Result);
        Assert.Null(outcome.RetryAfter);
        Assert.Empty(eventBus.Published);
    }

    // ── MonitorPackageAsync: NotFound delay then find ───────────────────────

    /// <summary>
    /// A <see cref="NuGetPublishMonitorService.ProbeResult.NotFound"/> (empty items array)
    /// must trigger a delay (not terminal) so the next iteration can find the package.
    /// </summary>
    [Fact]
    public async Task MonitorPackageAsync_NotFound_DelayedThenFinds()
    {
        var eventBus = new RecordingEventBus();
        var callCount = 0;
        var handler = new ScriptedHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                return OkResponse("{\"items\": []}");
            return OkResponse(IndexJsonWithInlineMatch("1.2.3"));
        });
        var service = CreateService(handler, eventBus: eventBus);

        await service.MonitorPackageAsync("test-repo", "My.Package", "1.2.3", "v1.2.3", TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Single(eventBus.Published);
        Assert.Equal(EventType.PackagePublished, eventBus.Published[0].Type);
    }

    // ── Handler factory: gzip decompression ────────────────────────────────

    /// <summary>
    /// <see cref="Program.CreateNuGetApiHandler"/> must produce a handler with both
    /// <see cref="DecompressionMethods.GZip"/> and <see cref="DecompressionMethods.Deflate"/>
    /// enabled so the gzip-compressed registration5-gz-semver2 endpoint is transparently
    /// decompressed.
    /// </summary>
    [Fact]
    public void CreateNuGetApiHandler_HasGzipAndDeflateDecompression()
    {
        var handler = Program.CreateNuGetApiHandler();
        var clientHandler = Assert.IsType<HttpClientHandler>(handler);
        Assert.True(clientHandler.AutomaticDecompression.HasFlag(DecompressionMethods.GZip));
        Assert.True(clientHandler.AutomaticDecompression.HasFlag(DecompressionMethods.Deflate));
    }
}
