using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using SharpCoder;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    [Description("Search the web for information. Returns titles, URLs, and content snippets.")]
    internal async Task<string> WebSearchAsync(
        [Description("Search query")] string query,
        [Description("Maximum results to return (1-10, default 5)")] int max_results = 5)
    {
        if (_ollamaApiKey is null)
            return "❌ Web search is not available — no OLLAMA_API_KEY configured.";

        if (string.IsNullOrWhiteSpace(query))
            return "❌ query is required.";

        max_results = Math.Clamp(max_results, 1, 10);

        var client = _httpClientFactory!.CreateClient("ollama-web");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/web_search");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _ollamaApiKey);
            request.Content = JsonContent.Create(new { query, max_results });

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return $"❌ Web search failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            const int MaxContentChars = 500;
            var sb = new System.Text.StringBuilder();
            var results = doc.RootElement.GetProperty("results");
            foreach (var result in results.EnumerateArray())
            {
                var title = result.TryGetProperty("title", out var t) ? t.GetString() : "";
                var url = result.TryGetProperty("url", out var u) ? u.GetString() : "";
                var content = result.TryGetProperty("content", out var c) ? c.GetString() : "";
                if (content is not null && content.Length > MaxContentChars)
                    content = content[..MaxContentChars] + "…";
                sb.AppendLine($"### {title}");
                sb.AppendLine(url);
                sb.AppendLine(content);
                sb.AppendLine();
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : "No results found.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "WebSearch failed for query '{Query}'", query);
            return $"❌ Web search error: {ex.Message}";
        }
    }

    [Description("Fetch a web page and return its content. Use after web_search to read full pages.")]
    internal async Task<string> WebFetchAsync(
        [Description("URL to fetch")] string url,
        [Description("Maximum lines of content to return. Default: 100")] int max_lines = 100)
    {
        if (_ollamaApiKey is null)
            return "❌ Web fetch is not available — no OLLAMA_API_KEY configured.";

        if (string.IsNullOrWhiteSpace(url))
            return "❌ url is required.";

        var client = _httpClientFactory!.CreateClient("ollama-web");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/web_fetch");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _ollamaApiKey);
            request.Content = JsonContent.Create(new { url });

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return $"❌ Web fetch failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var links = doc.RootElement.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToList()
                : new List<string?>();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.Append(content);

            if (links.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("## Links");
                foreach (var link in links)
                    sb.AppendLine($"- {link}");
            }

            var output = sb.ToString();
            var lines = output.Split('\n');
            if (lines.Length > max_lines)
            {
                output = string.Join('\n', lines.Take(max_lines));
                output += $"\n...(truncated, {lines.Length} lines total)";
            }

            return output;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "WebFetch failed for url '{Url}'", url);
            return $"❌ Web fetch error: {ex.Message}";
        }
    }
}
