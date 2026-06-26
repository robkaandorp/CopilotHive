using System.Net.Http.Headers;
using System.Text.Json;

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
    private readonly ILogger<ModelDiscoveryService> _logger;
    private readonly IHttpClientFactory? _httpClientFactory;

    /// <summary>
    /// Initialises a new <see cref="ModelDiscoveryService"/>.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="httpClientFactory">Optional HTTP client factory. When null, a new <see cref="HttpClient"/> is created per request.</param>
    public ModelDiscoveryService(
        ILogger<ModelDiscoveryService> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    private HttpClient CreateClient() =>
        _httpClientFactory is not null ? _httpClientFactory.CreateClient() : new HttpClient();

    /// <summary>
    /// Discovers models available via the GitHub Copilot models API.
    /// Returns an empty list if no token is configured or on any failure.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<DiscoveredModel>> DiscoverCopilotModelsAsync(CancellationToken ct = default)
    {
        var token = Environment.GetEnvironmentVariable("GH_TOKEN")
                    ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("No GH_TOKEN or GITHUB_TOKEN set — skipping Copilot model discovery.");
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

                    results.Add(new DiscoveredModel(
                        Id: name,
                        Name: name,
                        Vendor: "ollama",
                        ContextWindow: null,
                        Enabled: true));
                }
            }
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
