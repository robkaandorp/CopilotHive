using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

using CopilotHive.Configuration;

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
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private readonly HiveConfigFile? _config;
    private readonly IEventBus? _eventBus;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<NuGetPublishMonitorService> _logger;
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
    /// <param name="pollInterval">Polling interval between probes; defaults to 30 seconds.</param>
    /// <param name="timeoutOverride">Optional overall monitoring timeout; defaults to 30 minutes.</param>
    public NuGetPublishMonitorService(
        HiveConfigFile? config = null,
        IEventBus? eventBus = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<NuGetPublishMonitorService>? logger = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeoutOverride = null)
    {
        _config = config;
        _eventBus = eventBus;
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<NuGetPublishMonitorService>.Instance;
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
            var client = _httpClientFactory.CreateClient("nuget-api");
            var registrationUrl =
                $"https://api.nuget.org/v3/registration5-gz-semver2/{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json";

            while (true)
            {
                var delay = _pollInterval;
                try
                {
                    using var response = await client.GetAsync(registrationUrl, timeoutCts.Token);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        // Package not registered yet — delay and retry.
                    }
                    else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var retryAfter = ParseRetryAfter(response);
                        delay = retryAfter is { } r && r > TimeSpan.Zero ? r : _pollInterval;
                    }
                    else if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                    {
                        // Other 4xx — terminal: no amount of retrying will help.
                        return;
                    }
                    else if ((int)response.StatusCode >= 500)
                    {
                        // 5xx — delay and retry.
                    }
                    else if (!response.IsSuccessStatusCode)
                    {
                        // Unexpected status — delay and retry.
                    }
                    else
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
                        var root = doc.RootElement;

                        // Inline-first: check items[].items[].catalogEntry.version before any page fetch.
                        if (TryFindInlineMatch(root, parsedVersion))
                        {
                            PublishPublished(repoName, packageId, version, releaseTag);
                            return;
                        }

                        // Collect @id from null-items entries and fetch each page.
                        foreach (var pageId in CollectPageIds(root))
                        {
                            if (!IsValidPageUrl(pageId))
                                continue;

                            try
                            {
                                using var pageResponse = await client.GetAsync(pageId, timeoutCts.Token);
                                if (!pageResponse.IsSuccessStatusCode)
                                    continue; // page error — skip page

                                await using var pageStream = await pageResponse.Content.ReadAsStreamAsync(timeoutCts.Token);
                                using var pageDoc = await JsonDocument.ParseAsync(pageStream, cancellationToken: timeoutCts.Token);
                                if (TryFindMatchInPage(pageDoc.RootElement, parsedVersion))
                                {
                                    PublishPublished(repoName, packageId, version, releaseTag);
                                    return;
                                }
                            }
                            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                            {
                                throw; // linked token fired — outer handler decides
                            }
                            catch (Exception)
                            {
                                // Page error — skip page.
                            }
                        }
                    }
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested && !timeoutCts.IsCancellationRequested)
                {
                    // HTTP client timeout — delay and retry.
                }
                catch (HttpRequestException) when (!ct.IsCancellationRequested && !timeoutCts.IsCancellationRequested)
                {
                    // Transport error — delay and retry.
                }
                catch (JsonException) when (!ct.IsCancellationRequested && !timeoutCts.IsCancellationRequested)
                {
                    // Malformed response — delay and retry.
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    PublishTimedOut(repoName, packageId, version, releaseTag, sw);
                    return;
                }

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
    /// (i.e. page references).
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
            // Entries with inline items were already checked inline-first.
            if (item.TryGetProperty("items", out var inline) && inline.ValueKind == JsonValueKind.Array)
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
