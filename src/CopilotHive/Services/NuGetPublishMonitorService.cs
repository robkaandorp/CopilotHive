using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

using CopilotHive.Configuration;
using CopilotHive.Goals;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NuGet.Versioning;

namespace CopilotHive.Services;

/// <summary>
/// Polls the NuGet registration API to verify that packages have landed after a release,
/// publishing <see cref="EventType.PackagePublished"/> / <see cref="EventType.PackagePublishTimedOut"/>
/// events on the event bus.
/// </summary>
/// <remarks>
/// The public entry points are <c>virtual</c> so tests can substitute the monitor via a
/// subclass when verifying how callers launch and isolate monitoring.
/// </remarks>
public class NuGetPublishMonitorService
{
    /// <summary>Outcome of a single NuGet registration probe.</summary>
    internal enum ProbeResult
    {
        /// <summary>The package version was found — monitoring can stop.</summary>
        Found,
        /// <summary>The package version is not registered yet — keep polling.</summary>
        NotFound,
        /// <summary>The probe was inconclusive (transient failure) — retry.</summary>
        Retry,
        /// <summary>The probe can never succeed — monitoring can stop.</summary>
        Terminal,
    }

    /// <summary>
    /// Result of one <see cref="ProbePackageAsync"/> iteration.
    /// <see cref="RetryAfter"/> is only non-null and positive for HTTP 429 responses.
    /// </summary>
    /// <param name="Result">The probe outcome.</param>
    /// <param name="RetryAfter">Optional delay hint; only set for 429 responses.</param>
    internal sealed record ProbeOutcome(ProbeResult Result, TimeSpan? RetryAfter = null);

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private readonly HiveConfigFile? _config;
    private readonly IEventBus? _eventBus;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<NuGetPublishMonitorService> _logger;
    private readonly IGoalStore? _goalStore;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Monitoring runs currently in flight, keyed by (repo, packageId, normalized version)
    /// so the same package is never monitored twice concurrently.
    /// </summary>
    private readonly ConcurrentDictionary<(string Repo, string PackageId, string Version), bool> _inFlight = new();

    /// <summary>
    /// Initialises a new <see cref="NuGetPublishMonitorService"/> with optional dependencies.
    /// All parameters are optional so the service can be registered via DI.
    /// </summary>
    /// <param name="config">Optional hive configuration.</param>
    /// <param name="eventBus">Optional event bus for publishing package events.</param>
    /// <param name="httpClientFactory">Optional HTTP client factory for NuGet API calls.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="goalStore">Optional goal store, used by the startup release scan.</param>
    /// <param name="pollInterval">Polling interval between probes; defaults to 30 seconds.</param>
    /// <param name="timeoutOverride">Optional overall monitoring timeout; defaults to 30 minutes.</param>
    public NuGetPublishMonitorService(
        HiveConfigFile? config = null,
        IEventBus? eventBus = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<NuGetPublishMonitorService>? logger = null,
        IGoalStore? goalStore = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeoutOverride = null)
    {
        _config = config;
        _eventBus = eventBus;
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<NuGetPublishMonitorService>.Instance;
        _goalStore = goalStore;
        _pollInterval = pollInterval is { } p && p > TimeSpan.Zero ? p : DefaultPollInterval;
        _timeout = timeoutOverride is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
    }

    /// <summary>
    /// Monitors a single package on NuGet until it is found or the timeout elapses.
    /// The <paramref name="version"/> must already be stripped of any leading <c>v</c>/<c>V</c>.
    /// </summary>
    /// <param name="repoName">The repository name as configured in hive-config.yaml.</param>
    /// <param name="packageId">The NuGet package ID (original casing is preserved in events).</param>
    /// <param name="version">The package version, already stripped of a leading <c>v</c>/<c>V</c>.</param>
    /// <param name="releaseTag">The original release tag.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task MonitorPackageAsync(
        string repoName, string packageId, string version, string releaseTag, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoName) || string.IsNullOrWhiteSpace(packageId)
            || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(releaseTag))
            return;
        if (_eventBus is null || _httpClientFactory is null)
            return;
        if (!NuGetVersion.TryParse(version, out var parsedVersion))
            return;

        var dedupKey = (repoName.ToLowerInvariant(), packageId.ToLowerInvariant(), parsedVersion.ToNormalizedString());
        if (!_inFlight.TryAdd(dedupKey, true))
            return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            var sw = Stopwatch.StartNew();

            while (true)
            {
                ProbeOutcome outcome;
                try
                {
                    outcome = await ProbePackageAsync(repoName, packageId, version, releaseTag, timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    PublishTimedOut(repoName, packageId, version, releaseTag, sw);
                    return;
                }

                switch (outcome.Result)
                {
                    case ProbeResult.Found:
                    case ProbeResult.Terminal:
                        return;
                    case ProbeResult.NotFound:
                    case ProbeResult.Retry:
                        break;
                    default:
                        throw new InvalidOperationException($"Unhandled probe result: {outcome.Result}");
                }

                var delay = outcome.Result == ProbeResult.Retry && outcome.RetryAfter is { } r && r > TimeSpan.Zero
                    ? r
                    : _pollInterval;

                try
                {
                    await Task.Delay(delay, timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    PublishTimedOut(repoName, packageId, version, releaseTag, sw);
                    return;
                }
            }
        }
        finally
        {
            _inFlight.TryRemove(dedupKey, out _);
        }
    }

    /// <summary>
    /// Monitors all configured packages for a repository release. The leading <c>v</c>/<c>V</c>
    /// of the release tag is stripped before the version is handed to
    /// <see cref="MonitorPackageAsync(string, string, string, string, CancellationToken)"/>.
    /// </summary>
    /// <param name="repoName">The repository name as configured in hive-config.yaml.</param>
    /// <param name="releaseTag">The release tag (e.g. <c>v1.2.3</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task MonitorReleaseAsync(string repoName, string releaseTag, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoName) || string.IsNullOrWhiteSpace(releaseTag))
            return;
        if (_config is null)
            return;

        var repo = _config.Repositories.FirstOrDefault(
            r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase));
        if (repo is null)
            return;
        if (repo.PublishNuGet?.Packages is not { Count: > 0 })
            return;

        var version = releaseTag;
        if (version.StartsWith('v') || version.StartsWith('V'))
            version = version[1..];
        if (string.IsNullOrWhiteSpace(version))
            return;
        if (!NuGetVersion.TryParse(version, out _))
            return;

        var tasks = repo.PublishNuGet.Packages.Select(
            p => MonitorPackageAsync(repoName, p.PackageId, version, releaseTag, ct));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Performs one polling iteration for a single package: GETs the NuGet registration index,
    /// resolves the version inline or via registration pages, and reports the outcome.
    /// Emits <see cref="EventType.PackagePublished"/> when the version is found.
    /// </summary>
    /// <param name="repoName">The repository name as configured in hive-config.yaml.</param>
    /// <param name="packageId">The NuGet package ID.</param>
    /// <param name="version">The package version, already stripped of a leading <c>v</c>/<c>V</c>.</param>
    /// <param name="releaseTag">The original release tag.</param>
    /// <param name="ct">Cancellation token; cancellation propagates as
    /// <see cref="OperationCanceledException"/>.</param>
    /// <returns>The probe outcome; see <see cref="ProbeResult"/>.</returns>
    internal virtual async Task<ProbeOutcome> ProbePackageAsync(
        string repoName, string packageId, string version, string releaseTag, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        if (_httpClientFactory is null || _eventBus is null)
            return new ProbeOutcome(ProbeResult.Terminal);
        if (string.IsNullOrWhiteSpace(repoName) || string.IsNullOrWhiteSpace(packageId)
            || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(releaseTag))
            return new ProbeOutcome(ProbeResult.Terminal);
        if (!NuGetVersion.TryParse(version, out var parsedVersion))
            return new ProbeOutcome(ProbeResult.Terminal);

        var client = _httpClientFactory.CreateClient("nuget-api");
        var registrationUrl =
            $"https://api.nuget.org/v3/registration5-gz-semver2/{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json";

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(registrationUrl, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HTTP client timeout — retry.
            return new ProbeOutcome(ProbeResult.Retry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            // Transport error — retry.
            return new ProbeOutcome(ProbeResult.Retry);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new ProbeOutcome(ProbeResult.Retry);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = ParseRetryAfter(response);
                return new ProbeOutcome(
                    ProbeResult.Retry,
                    retryAfter is { } r && r > TimeSpan.Zero ? r : null);
            }
            if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                return new ProbeOutcome(ProbeResult.Terminal);
            if ((int)response.StatusCode >= 500)
                return new ProbeOutcome(ProbeResult.Retry);
            if (!response.IsSuccessStatusCode)
                return new ProbeOutcome(ProbeResult.Retry);

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return new ProbeOutcome(ProbeResult.Retry);
                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return new ProbeOutcome(ProbeResult.Retry);
                if (items.GetArrayLength() == 0)
                    return new ProbeOutcome(ProbeResult.NotFound);

                // Inline-first: check items[].items[].catalogEntry.version before any page fetch.
                if (TryFindInlineMatch(root, parsedVersion))
                {
                    PublishPublished(repoName, packageId, version, releaseTag);
                    return new ProbeOutcome(ProbeResult.Found);
                }

                // Collect @id from non-inline entries and fetch each page.
                foreach (var pageId in CollectPageIds(root))
                {
                    if (!IsValidPageUrl(pageId))
                        continue;

                    try
                    {
                        using var pageResponse = await client.GetAsync(pageId, ct);
                        if (!pageResponse.IsSuccessStatusCode)
                            continue; // page error — skip page

                        await using var pageStream = await pageResponse.Content.ReadAsStreamAsync(ct);
                        using var pageDoc = await JsonDocument.ParseAsync(pageStream, cancellationToken: ct);
                        if (TryFindMatchInPage(pageDoc.RootElement, parsedVersion))
                        {
                            PublishPublished(repoName, packageId, version, releaseTag);
                            return new ProbeOutcome(ProbeResult.Found);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw; // ct fired — propagate
                    }
                    catch (Exception)
                    {
                        // Page error (HTTP failure, client timeout, malformed JSON) — skip page.
                    }
                }

                return new ProbeOutcome(ProbeResult.NotFound);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // HTTP client timeout — retry.
                return new ProbeOutcome(ProbeResult.Retry);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested)
            {
                // Transport error — retry.
                return new ProbeOutcome(ProbeResult.Retry);
            }
            catch (JsonException) when (!ct.IsCancellationRequested)
            {
                // Malformed response — retry.
                return new ProbeOutcome(ProbeResult.Retry);
            }
        }
    }

    /// <summary>
    /// Scans releases marked Released while the orchestrator was down (within the last hour)
    /// and resumes background monitoring for packages that are not on NuGet yet.
    /// </summary>
    /// <param name="ct">Application-lifetime token, also handed to any background monitors.</param>
    public virtual async Task StartupScanAsync(CancellationToken ct)
    {
        if (_goalStore is null || _config is null)
            return;

        IReadOnlyList<Release> releases;
        try
        {
            releases = await _goalStore.GetReleasesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NuGet publish monitor startup scan failed to load releases");
            return;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-60);
        var candidates = releases
            .Where(r => r.Status == ReleaseStatus.Released
                        && r.ReleasedAt.HasValue
                        && r.ReleasedAt.Value > cutoff)
            .ToList();

        foreach (var release in candidates)
        {
            foreach (var repoName in release.RepositoryNames)
            {
                if (ct.IsCancellationRequested)
                    return;

                var repo = _config.Repositories.FirstOrDefault(
                    r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase));
                if (repo?.PublishNuGet?.Packages is not { Count: > 0 })
                    continue;

                var version = release.Tag;
                if (version.StartsWith('v') || version.StartsWith('V'))
                    version = version[1..];
                if (string.IsNullOrWhiteSpace(version))
                    continue;
                if (!NuGetVersion.TryParse(version, out _))
                    continue;

                foreach (var pkg in repo.PublishNuGet.Packages)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    try
                    {
                        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        probeCts.CancelAfter(TimeSpan.FromSeconds(1));

                        ProbeOutcome outcome;
                        try
                        {
                            outcome = await ProbePackageAsync(repoName, pkg.PackageId, version, release.Tag, probeCts.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // The 1-second probe timed out — the package may still be on its
                            // way; resume monitoring in the background.
                            LaunchBackgroundMonitor(repoName, pkg.PackageId, version, release.Tag, ct);
                            continue;
                        }
                        catch (OperationCanceledException)
                        {
                            // Caller cancellation — stop the scan.
                            return;
                        }

                        switch (outcome.Result)
                        {
                            case ProbeResult.Found:
                            case ProbeResult.Terminal:
                                // Already published (or can never succeed) — nothing to monitor.
                                break;
                            case ProbeResult.NotFound:
                            case ProbeResult.Retry:
                                LaunchBackgroundMonitor(repoName, pkg.PackageId, version, release.Tag, ct);
                                break;
                            default:
                                throw new InvalidOperationException($"Unhandled probe result: {outcome.Result}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "NuGet startup scan probe failed for {PackageId}", pkg.PackageId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Launches a fire-and-forget <see cref="MonitorPackageAsync"/> for a package, logging
    /// any unexpected failure instead of letting it escape into the caller.
    /// </summary>
    /// <param name="repoName">The repository name as configured in hive-config.yaml.</param>
    /// <param name="packageId">The NuGet package ID.</param>
    /// <param name="version">The package version, already stripped of a leading <c>v</c>/<c>V</c>.</param>
    /// <param name="releaseTag">The original release tag.</param>
    /// <param name="ct">Cancellation token handed to the background monitor.</param>
    internal virtual void LaunchBackgroundMonitor(
        string repoName, string packageId, string version, string releaseTag, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await MonitorPackageAsync(repoName, packageId, version, releaseTag, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "NuGet monitor failed for {PackageId}", packageId); }
        });
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private void PublishPublished(string repoName, string packageId, string version, string releaseTag)
    {
        _logger.LogInformation(
            "Package {PackageId} {Version} published on NuGet (release {ReleaseTag}) for repo {Repo}",
            packageId, version, releaseTag, repoName);
        _eventBus!.Publish(new SystemEvent(
            Type: EventType.PackagePublished,
            Message: $"Package {packageId} {version} published on NuGet (release {releaseTag})",
            Repository: repoName));
    }

    private void PublishTimedOut(string repoName, string packageId, string version, string releaseTag, Stopwatch sw)
    {
        _logger.LogWarning(
            "Package {PackageId} {Version} not found on NuGet after {Elapsed}s (release {ReleaseTag}) for repo {Repo}",
            packageId, version, (int)Math.Floor(sw.Elapsed.TotalSeconds), releaseTag, repoName);
        _eventBus!.Publish(new SystemEvent(
            Type: EventType.PackagePublishTimedOut,
            Message: $"Package {packageId} {version} not found on NuGet after {(int)Math.Floor(sw.Elapsed.TotalSeconds)}s (release {releaseTag})",
            Repository: repoName));
    }

    /// <summary>
    /// Checks the inline <c>items[].items[].catalogEntry.version</c> entries for a match.
    /// </summary>
    private static bool TryFindInlineMatch(JsonElement root, NuGetVersion target)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("items", out var inline)
                || inline.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var entry in inline.EnumerateArray())
            {
                if (TryGetCatalogVersion(entry, out var parsed) && NuGetVersion.Equals(parsed, target))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects <c>@id</c> values from <c>items[]</c> entries that have no inline <c>items</c>
    /// (i.e. page references). Non-object items, non-array nested <c>items</c>, and non-string
    /// <c>@id</c> entries are skipped.
    /// </summary>
    private static List<string> CollectPageIds(JsonElement root)
    {
        var pageIds = new List<string>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return pageIds;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            // Entries with inline items were already checked inline-first. A nested "items"
            // that is not an array is malformed — skip the whole entry.
            if (item.TryGetProperty("items", out var inline))
                continue;
            if (item.TryGetProperty("@id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var s = id.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    pageIds.Add(s);
            }
        }

        return pageIds;
    }

    /// <summary>
    /// Checks a registration page's <c>items[].catalogEntry.version</c> entries for a match.
    /// </summary>
    private static bool TryFindMatchInPage(JsonElement root, NuGetVersion target)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in items.EnumerateArray())
        {
            if (TryGetCatalogVersion(item, out var parsed) && NuGetVersion.Equals(parsed, target))
                return true;
        }

        return false;
    }

    /// <summary>Extracts and parses <c>catalogEntry.version</c> from a registration item.</summary>
    private static bool TryGetCatalogVersion(JsonElement item, out NuGetVersion version)
    {
        version = null!;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("catalogEntry", out var catalog)
            || catalog.ValueKind != JsonValueKind.Object)
            return false;
        if (!catalog.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String)
            return false;
        var versionString = v.GetString();
        if (string.IsNullOrWhiteSpace(versionString))
            return false;
        if (NuGetVersion.TryParse(versionString, out var parsed))
        {
            version = parsed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Validates a page URL: absolute HTTPS, host <c>api.nuget.org</c>, port 443.
    /// </summary>
    private static bool IsValidPageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "api.nuget.org", StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443;
    }

    /// <summary>
    /// Parses the <c>Retry-After</c> header: an integer delta-seconds value or an HTTP-date.
    /// Returns <c>null</c> when absent or unparseable.
    /// </summary>
    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;
        var value = values.FirstOrDefault();
        if (value is null)
            return null;

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date - DateTimeOffset.UtcNow;

        return null;
    }
}
