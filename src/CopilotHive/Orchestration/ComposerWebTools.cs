using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotHive.Services;
using SharpCoder;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    /// <summary>
    /// Searches the web through the Ollama web-search API and returns the formatted results.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything this method returns is model-visible, so every remote and caller-derived string
    /// passes through the ONE credential-redaction transform the Composer already owns
    /// (<see cref="RedactGitOutput"/>, whose inventory is the constructor API key plus the FRESH
    /// values of the credential environment variables): each result's title, url and content, the
    /// failure reason phrase, and the message of a classified exception.
    /// </para>
    /// <para>
    /// The transform runs BEFORE the per-result content clip and before the builder is assembled.
    /// That ordering is the point: a credential that straddles (or lies beyond) the clip boundary is
    /// removed WHOLE, instead of being cut into an unmatched fragment that no longer resembles the
    /// value it came from, and the clip length is measured on the already-redacted text.
    /// </para>
    /// <para>
    /// The catch filter is deliberately narrow — an HTTP failure, a request timeout, and a malformed
    /// response — so every OTHER cancellation class keeps propagating with its identity intact. The
    /// raw <see cref="Exception"/> object is never handed to the logger (no stack trace, no request
    /// or response headers, no bodies): only the exception TYPE name — which cannot carry a
    /// credential — and the redacted, log-sanitized message are written, so neither a credential nor
    /// a forged log line from a hostile remote can reach the orchestrator log.
    /// </para>
    /// </remarks>
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
                return RedactGitOutput($"❌ Web search failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            const int MaxContentChars = 500;
            var sb = new System.Text.StringBuilder();
            var results = doc.RootElement.GetProperty("results");
            foreach (var result in results.EnumerateArray())
            {
                // Redact FIRST, clip second: see the remarks above. RedactGitOutput maps a missing
                // (or JSON null) field to the empty string, so no null handling is needed after it.
                var title = RedactGitOutput(result.TryGetProperty("title", out var t) ? t.GetString() : "");
                var url = RedactGitOutput(result.TryGetProperty("url", out var u) ? u.GetString() : "");
                var content = RedactGitOutput(result.TryGetProperty("content", out var c) ? c.GetString() : "");
                if (content.Length > MaxContentChars)
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
            // Two redaction stages, then log-shape safety: the inventory/structural transform removes
            // credentials, LogSanitizer collapses newlines and other control characters so a hostile
            // remote cannot forge extra log lines. The exception OBJECT is never passed as the
            // exception argument, so no stack trace, header or body can reach the log.
            _logger.LogWarning(
                "WebSearch failed ({ExceptionType}) for query '{Query}': {RedactedMessage}",
                ex.GetType().Name,
                LogSanitizer.SanitizeText(RedactGitOutput(query)),
                LogSanitizer.SanitizeText(RedactGitOutput(ex.Message)));

            return RedactGitOutput($"❌ Web search error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches a web page through the Ollama web-fetch API and returns its formatted content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same boundary as <see cref="WebSearchAsync"/>: the remote title, content and EVERY link pass
    /// through <see cref="RedactGitOutput"/> before the output builder is assembled, the line split
    /// and the <c>max_lines</c> truncation run, so truncation operates on the redacted text and the
    /// reported total is the redacted output's own line count. The failure reason phrase and the
    /// classified exception message are redacted too.
    /// </para>
    /// <para>
    /// The catch filter is unchanged (HTTP failure, request timeout, malformed response) so other
    /// cancellation classes still propagate, and the logger receives the exception TYPE name plus
    /// the redacted, log-sanitized url and message — never the exception object, its stack trace,
    /// or any request/response header or body.
    /// </para>
    /// </remarks>
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
                return RedactGitOutput($"❌ Web fetch failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Redact before assembling: the StringBuilder, the line split and the truncation notice
            // therefore all describe the REDACTED text (RedactGitOutput maps a missing or null field
            // to the empty string, preserving the empty/missing-field behaviour).
            var title = RedactGitOutput(doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : "");
            var content = RedactGitOutput(doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : "");
            var links = doc.RootElement.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Select(link => RedactGitOutput(link)).ToList()
                : new List<string>();

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
            _logger.LogWarning(
                "WebFetch failed ({ExceptionType}) for url '{Url}': {RedactedMessage}",
                ex.GetType().Name,
                LogSanitizer.SanitizeText(RedactGitOutput(url)),
                LogSanitizer.SanitizeText(RedactGitOutput(ex.Message)));

            return RedactGitOutput($"❌ Web fetch error: {ex.Message}");
        }
    }
}
