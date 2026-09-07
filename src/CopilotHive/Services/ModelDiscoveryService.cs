using System.Net.Http.Headers;
using System.Text.Json;

using CopilotHive.Shared;

namespace CopilotHive.Services;

/// <summary>
/// Represents a model discovered from a provider's API.
/// </summary>
/// <param name="Id">Provider-prefixed identifier (e.g. "copilot/claude-sonnet-4.6").</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Vendor">Vendor name, or <c>null</c> if not reported.</param>
/// <param name="ContextWindow">Maximum context window in tokens, or <c>null</c> if not reported.</param>
/// <param name="Enabled">Whether the model is enabled by provider policy.</param>
public sealed record DiscoveredModel(
    string Id,
    string Name,
    string? Vendor,
    int? ContextWindow,
    bool Enabled);

/// <summary>
/// Queries GitHub Copilot and Ollama for the list of available models.
/// </summary>
public sealed class ModelDiscoveryService
{
    /// <summary>
    /// Fixed, credential-free warning used whenever the stored-OAuth token lookup fails
    /// for a non-cancellation reason. Exception details are deliberately never logged
    /// because a lookup exception message can carry credential-bearing data.
    /// </summary>
    internal const string OAuthLookupFailedWarning =
        "Stored GitHub OAuth token lookup failed — falling back to GH_TOKEN/GITHUB_TOKEN environment variables for Copilot model discovery.";

    private readonly ILogger<ModelDiscoveryService> _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<CancellationToken, Task<string?>>? _storedTokenLookup;

    /// <summary>
    /// Initialises a new <see cref="ModelDiscoveryService"/>.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="httpClientFactory">Optional HTTP client factory. When null, a new <see cref="HttpClient"/> is created per request.</param>
    /// <param name="getStoredAccessTokenAsync">
    /// Optional async lookup of the stored GitHub OAuth access token (e.g.
    /// <see cref="UserService.GetActiveAccessTokenAsync"/>). When null, only the
    /// GH_TOKEN/GITHUB_TOKEN environment variables are considered. The lookup is invoked
    /// PER DISCOVERY CALL — never at construction — so token rotation/removal between calls
    /// is always observed.
    /// </param>
    public ModelDiscoveryService(
        ILogger<ModelDiscoveryService> logger,
        IHttpClientFactory? httpClientFactory = null,
        Func<CancellationToken, Task<string?>>? getStoredAccessTokenAsync = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _storedTokenLookup = getStoredAccessTokenAsync;
    }

    private HttpClient CreateClient() =>
        _httpClientFactory is not null ? _httpClientFactory.CreateClient() : new HttpClient();

    /// <summary>
    /// Resolves the Copilot credential for ONE discovery invocation, per call:
    /// first non-whitespace of the stored OAuth token (via the injected lookup),
    /// then <c>GH_TOKEN</c>, then <c>GITHUB_TOKEN</c>. Selection goes through
    /// <see cref="GitCredentialResolver.Resolve"/>, which returns the chosen candidate
    /// UNCHANGED (never trimmed). Null when every source is absent or whitespace.
    /// </summary>
    /// <param name="ct">Cancellation token propagated into the stored-token lookup.</param>
    private async Task<string?> ResolveCopilotCredentialAsync(CancellationToken ct)
    {
        string? stored = null;
        if (_storedTokenLookup is not null)
        {
            try
            {
                stored = await _storedTokenLookup(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A non-cancellation lookup failure degrades to the environment chain. The
                // exception itself is never logged — its message could contain credentials.
                _logger.LogWarning(OAuthLookupFailedWarning);
            }
        }

        return GitCredentialResolver.Resolve(
            stored,
            Environment.GetEnvironmentVariable("GH_TOKEN"),
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    }

    /// <summary>
    /// Discovers models available via the GitHub Copilot models API.
    /// The credential is resolved per call (stored OAuth, then GH_TOKEN, then GITHUB_TOKEN).
    /// Returns an empty list if no token is configured or on any failure.
    /// A caller cancellation is ALWAYS propagated — never swallowed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<DiscoveredModel>> DiscoverCopilotModelsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var token = await ResolveCopilotCredentialAsync(ct);

        // Cancellation observed DURING the lookup must terminate the caller even when the
        // lookup itself answered "no token" (returned null/blank, or failed for a
        // non-cancellation reason after the token was cancelled). Without this check a
        // cancelled call with no environment fallback would silently return an empty list.
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("No stored GitHub OAuth token, GH_TOKEN or GITHUB_TOKEN set — skipping Copilot model discovery.");
            return [];
        }

        var results = new List<DiscoveredModel>();
        try
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.githubcopilot.com/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-GitHub-Api-Version", "2025-04-01");

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in data.EnumerateArray())
                {
                    var id = model.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var name = model.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString() ?? id
                        : id;

                    string? vendor = model.TryGetProperty("vendor", out var vendorEl) && vendorEl.ValueKind == JsonValueKind.String
                        ? vendorEl.GetString()
                        : null;

                    int? contextWindow = null;
                    if (model.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
                        && caps.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Object
                        && limits.TryGetProperty("max_context_window_tokens", out var cw)
                        && cw.ValueKind == JsonValueKind.Number)
                    {
                        contextWindow = cw.GetInt32();
                    }

                    var enabled = model.TryGetProperty("policy", out var policy) && policy.ValueKind == JsonValueKind.Object
                        && policy.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                        && string.Equals(state.GetString(), "enabled", StringComparison.OrdinalIgnoreCase);

                    results.Add(new DiscoveredModel(
                        Id: $"copilot/{id}",
                        Name: name,
                        Vendor: vendor,
                        ContextWindow: contextWindow,
                        Enabled: enabled));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation is never a "discovery failed" answer — it must terminate
            // the caller rather than be swallowed into an empty list.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover Copilot models.");
            return [];
        }

        return results;
    }

    /// <summary>
    /// Discovers models available via the Ollama API (cloud or local).
    /// Returns an empty list if no endpoint is configured or on any failure.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<DiscoveredModel>> DiscoverOllamaModelsAsync(CancellationToken ct = default)
    {
        var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
        string baseUrl;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            baseUrl = "https://ollama.com";
        }
        else
        {
            var url = Environment.GetEnvironmentVariable("OLLAMA_URL");
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("No OLLAMA_API_KEY or OLLAMA_URL set — skipping Ollama model discovery.");
                return [];
            }
            baseUrl = url;
        }

        var results = new List<DiscoveredModel>();
        try
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/tags");
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in models.EnumerateArray())
                {
                    var name = model.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var providerPrefix = !string.IsNullOrWhiteSpace(apiKey) ? "ollama-cloud" : "ollama-local";
                    results.Add(new DiscoveredModel(
                        Id: $"{providerPrefix}/{name}",
                        Name: name,
                        Vendor: "ollama",
                        ContextWindow: null,
                        Enabled: true));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation is never a "discovery failed" answer — it must terminate
            // the caller rather than be swallowed into an empty list.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover Ollama models.");
            return [];
        }

        return results;
    }

    /// <summary>
    /// Discovers models from all supported providers, Copilot first then Ollama.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<DiscoveredModel>> DiscoverAllAsync(CancellationToken ct = default)
    {
        var copilot = await DiscoverCopilotModelsAsync(ct);
        var ollama = await DiscoverOllamaModelsAsync(ct);
        var all = new List<DiscoveredModel>(copilot.Count + ollama.Count);
        all.AddRange(copilot);
        all.AddRange(ollama);
        return all;
    }
}
