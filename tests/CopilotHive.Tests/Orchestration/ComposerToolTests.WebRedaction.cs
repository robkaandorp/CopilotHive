using System.Net;
using System.Text;
using System.Text.Json;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Credential-redaction tests for the Composer's web tools (<c>web_search</c> / <c>web_fetch</c>).
/// </summary>
/// <remarks>
/// <para>
/// The web tools are model-visible output surfaces built from REMOTE data — a hostile or merely
/// sloppy search/fetch backend controls the title, url, content, links and reason phrase — so the
/// synthetic credentials below are planted in EVERY remote field and in every failure channel and
/// then asserted absent from both the tool output and the captured orchestrator log.
/// </para>
/// <para>
/// The suite is a partial of <see cref="ComposerToolTests"/> so it shares
/// <c>[Collection(GitOutputInventoryCollection.Name)]</c>: that collection disables parallelization
/// for every test that reads or writes the credential-bearing process environment variables, which
/// is exactly what the rotation/removal/short-value rows do (each restores the original value in a
/// <c>finally</c>).
/// </para>
/// </remarks>
public sealed partial class ComposerToolTests
{
    /// <summary>One line break, as <see cref="System.Text.StringBuilder.AppendLine()"/> emits it.</summary>
    private static string Nl => Environment.NewLine;

    /// <summary>The synthetic API key captured by the Composer constructor (part of the inventory).</summary>
    /// <remarks>
    /// 16 characters, so its standard-base64 spelling ends in <c>==</c> and the padded and unpadded
    /// forms are genuinely different strings.
    /// </remarks>
    private const string WebKey = "FAKE_WEB_KEY_a12";

    /// <summary>A structural-only credential: short, in NO inventory, recognizable by shape alone.</summary>
    private const string WebSecret = "FAKE_PASSWORD";

    /// <summary>A second value used to prove the inventory FOLLOWS the current environment value.</summary>
    private const string EnvFirst = "FAKE_ENV_ON_1";

    /// <summary>The rotated-to value: once it is current, <see cref="EnvFirst"/> must stop being redacted.</summary>
    private const string EnvSecond = "FAKE_ENV_TW_2";

    /// <summary>Below <see cref="GitToolOutputRedactor.MinimumInventoryValueLength"/>: never exact-replaced.</summary>
    private const string EnvShort = "FAKEzz";

    /// <summary>A value that CONTAINS a line break, so a redaction merges two text lines into one.</summary>
    private const string MultiLineSecret = "FAKE_MULTI_1\nFAKE_MULTI_2";

    /// <summary>One-level percent escapes of every UTF-8 byte, lower-case hex.</summary>
    private static string PercentForm(string value) =>
        string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:x2}"));

    /// <summary>The standard (padded) base64 spelling of <paramref name="value"/>.</summary>
    private static string PaddedBase64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>A raw value followed by its percent, padded-base64 and unpadded-base64 spellings.</summary>
    private static string InventoryBlock(string value) =>
        string.Join('\n', value, PercentForm(value), PaddedBase64(value), PaddedBase64(value).TrimEnd('='));

    private static string SearchBody(string title, string url, string content) =>
        JsonSerializer.Serialize(new { results = new[] { new { title, url, content } } });

    private static string FetchBody(string title, string content) =>
        JsonSerializer.Serialize(new { title, content });

    private static string FetchBody(string title, string content, IReadOnlyList<string> links) =>
        JsonSerializer.Serialize(new { title, content, links });

    private static string FetchBodyWithNullLinks(string title, string content) =>
        JsonSerializer.Serialize(new { title, content, links = (string[]?)null });

    /// <summary>
    /// The runtime type name of the exception the tool's own JSON parse raises for a malformed payload
    /// — obtained by performing that parse rather than by naming a non-public BCL type.
    /// </summary>
    private static string ParseFailureTypeName()
    {
        var thrown = Assert.ThrowsAny<Exception>(() => JsonDocument.Parse("invalid json"));
        Assert.IsAssignableFrom<JsonException>(thrown);
        return thrown.GetType().Name;
    }

    /// <summary>
    /// Builds a Composer wired to <paramref name="handler"/> with the synthetic constructor API key
    /// and a capturing logger, reusing the shared goal store.
    /// </summary>
    private (Composer Composer, TestLogger<Composer> Logger) NewWebComposer(
        HttpMessageHandler handler, string? apiKey = WebKey)
    {
        var factory = new Mock<IHttpClientFactory>();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://ollama.com/") };
        factory.Setup(f => f.CreateClient("ollama-web")).Returns(client);

        var logger = new TestLogger<Composer>();
        var composer = new Composer(
            "test-model",
            logger,
            _store,
            stateDir: Path.GetTempPath(),
            httpClientFactory: factory.Object,
            ollamaApiKey: apiKey);
        return (composer, logger);
    }

    private static Task<string> RunWebToolAsync(Composer composer, string tool, string queryOrUrl) => tool switch
    {
        "search" => composer.WebSearchAsync(queryOrUrl),
        "fetch" => composer.WebFetchAsync(queryOrUrl),
        _ => throw new InvalidOperationException($"Unknown web tool '{tool}'."),
    };

    /// <summary>
    /// Asserts that neither the raw value nor its bounded encoded forms, and not the structural
    /// credential, survived in <paramref name="text"/>.
    /// </summary>
    private static void NoWebSecret(string text, string value)
    {
        var padded = PaddedBase64(value);
        foreach (var form in new[] { value, PercentForm(value), padded, padded.TrimEnd('=') })
            Assert.False(text.Contains(form, StringComparison.Ordinal), "Synthetic credential reached model-visible web tool output.");
        Assert.False(text.Contains(WebSecret, StringComparison.Ordinal), "Structural credential reached model-visible web tool output.");
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that answers with a chosen status AND reason phrase, so a
    /// remote-controlled reason phrase can be exercised (the shared
    /// <see cref="FakeHttpMessageHandler"/> derives its reason phrase from the status code).
    /// </summary>
    private sealed class ReasonPhraseHandler(string reasonPhrase) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = reasonPhrase };
            response.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    // ── remote fields, constructor inventory ─────────────────────────────────

    [Fact]
    public async Task WebSearch_RemoteFields_RedactConstructorInventory()
    {
        var body = SearchBody($"title {WebKey}", $"https://h.invalid/{WebKey}/x", $"prefix {WebKey} suffix");
        var (composer, logger) = NewWebComposer(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var output = await composer.WebSearchAsync("ordinary query");

        Assert.Contains("### title [redacted]", output);
        Assert.Contains("https://h.invalid/[redacted]/x", output);
        Assert.Contains("prefix [redacted] suffix", output);
        NoWebSecret(output, WebKey);
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(WebKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebFetch_RemoteFields_RedactConstructorInventory()
    {
        var body = FetchBody(
            $"title {WebKey}",
            $"body {WebKey} body",
            [$"https://h.invalid/{WebKey}", $"ssh://u:{WebKey}@h.invalid/r"]);
        var (composer, logger) = NewWebComposer(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var output = await composer.WebFetchAsync("https://h.invalid/page");

        Assert.Contains("# title [redacted]", output);
        Assert.Contains("body [redacted] body", output);
        Assert.Contains("## Links", output);
        Assert.Contains("- https://h.invalid/[redacted]", output);
        Assert.Contains("- ssh://u:[redacted]@h.invalid/r", output);
        NoWebSecret(output, WebKey);
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(WebKey, StringComparison.Ordinal));
    }

    // ── remote fields, environment inventory (read fresh per operation) ──────

    [Theory]
    [InlineData("search", "GH_TOKEN")]
    [InlineData("search", "GITHUB_TOKEN")]
    [InlineData("search", "GITHUB_CONFIG_REPO_TOKEN")]
    [InlineData("search", "GITHUB_ORCHESTRATOR_TOKEN")]
    [InlineData("fetch", "GH_TOKEN")]
    [InlineData("fetch", "GITHUB_TOKEN")]
    [InlineData("fetch", "GITHUB_CONFIG_REPO_TOKEN")]
    [InlineData("fetch", "GITHUB_ORCHESTRATOR_TOKEN")]
    public async Task WebRemoteFields_EnvironmentInventory_FollowsRotationRemovalAndShortValues(string tool, string variable)
    {
        Assert.EndsWith("==", PaddedBase64(EnvFirst), StringComparison.Ordinal);
        Assert.EndsWith("==", PaddedBase64(EnvSecond), StringComparison.Ordinal);
        Assert.True(EnvShort.Length < GitToolOutputRedactor.MinimumInventoryValueLength);

        var content = $"ordinary-web-line\n{InventoryBlock(EnvFirst)}\n{InventoryBlock(EnvSecond)}\n{InventoryBlock(EnvShort)}\nend-marker";
        // Well inside the 500-character search content window, so nothing is clipped away here.
        Assert.True(content.Length < 500);
        var body = tool == "search"
            ? SearchBody("Env Title", "https://h.invalid/env", content)
            : FetchBody("Env Title", content, ["https://h.invalid/link"]);
        var (composer, _) = NewWebComposer(new FakeHttpMessageHandler(HttpStatusCode.OK, body));
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            // The collection disables parallelization, so this write is serialized against all other
            // tests that read or write the credential environment variables.
            foreach (var current in new string?[] { EnvFirst, EnvSecond, null, EnvShort })
            {
                Environment.SetEnvironmentVariable(variable, current);
                var output = await RunWebToolAsync(composer, tool, tool == "search" ? "ordinary query" : "https://h.invalid/page");

                var expected = content;
                if (current is EnvFirst or EnvSecond)
                {
                    expected = expected
                        .Replace(current, GitToolOutputRedactor.Placeholder, StringComparison.Ordinal)
                        .Replace(PercentForm(current), GitToolOutputRedactor.Placeholder, StringComparison.Ordinal)
                        .Replace(PaddedBase64(current), GitToolOutputRedactor.Placeholder, StringComparison.Ordinal)
                        .Replace(PaddedBase64(current).TrimEnd('='), GitToolOutputRedactor.Placeholder, StringComparison.Ordinal);
                }

                Assert.Contains(expected, output);
                Assert.Contains("ordinary-web-line", output);
                Assert.Contains("end-marker", output);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    // ── environment inventory across every field ──────────────────────────────

    /// <summary>Every (credential environment variable name, tool) pair, from the inventory.</summary>
    public static IEnumerable<object[]> InventoryVariableToolPairs =>
        GitToolOutputRedactor.InventoryEnvironmentVariableNames
            .SelectMany(name => new[] { "search", "fetch" }, (name, tool) => new object[] { name, tool });

    /// <summary>
    /// Asserts none of the four matchable forms of <paramref name="value"/> survives in
    /// <paramref name="text"/>. Used for values read fresh from the process environment.
    /// </summary>
    private static void NoInventoryForms(string text, string value)
    {
        var padded = PaddedBase64(value);
        foreach (var form in new[] { value, PercentForm(value), padded, padded.TrimEnd('=') })
            Assert.False(text.Contains(form, StringComparison.Ordinal),
                "An inventory form of the environment value reached model-visible output or a captured log.");
    }

    [Theory]
    [MemberData(nameof(InventoryVariableToolPairs))]
    public async Task WebRemoteFields_EnvironmentInventory_IsRedactedInTitleUrlAndLinks(string variable, string tool)
    {
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, EnvFirst);
            Assert.True(EnvFirst.Length >= GitToolOutputRedactor.MinimumInventoryValueLength);

            var content = $"ordinary-content {PaddedBase64(EnvFirst)} tail {PaddedBase64(EnvFirst).TrimEnd('=')}";
            Assert.True(content.Length < 500, "The content fixture must fit inside the search clip window.");
            var body = tool == "search"
                ? SearchBody($"title {EnvFirst}", $"https://h.invalid/{EnvFirst}/{PercentForm(EnvFirst)}", content)
                : FetchBody($"title {EnvFirst}", content,
                    [$"https://h.invalid/{EnvFirst}", $"ssh://u:{EnvFirst}@h.invalid/r"]);
            var (composer, logger) = NewWebComposer(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

            var output = await RunWebToolAsync(composer, tool, tool == "search" ? "ordinary query" : "https://h.invalid/page");

            Assert.Contains("[redacted]", output, StringComparison.Ordinal);
            NoInventoryForms(output, EnvFirst);
            Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(EnvFirst, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Theory]
    [MemberData(nameof(InventoryVariableToolPairs))]
    public async Task WebTools_NonSuccessReasonPhrase_EnvironmentInventory_IsRedacted(string variable, string tool)
    {
        // A single-line reason phrase carrying the raw value and two of its encoded forms.
        var reasonPhrase = $"upstream {EnvFirst} {PercentForm(EnvFirst)} {PaddedBase64(EnvFirst)}";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, EnvFirst);
            var (composer, _) = NewWebComposer(new ReasonPhraseHandler(reasonPhrase));

            var output = await RunWebToolAsync(composer, tool, tool == "search" ? "q" : "https://h.invalid/p");

            Assert.StartsWith(tool == "search" ? "❌ Web search failed: HTTP 500 " : "❌ Web fetch failed: HTTP 500 ",
                output, StringComparison.Ordinal);
            Assert.Contains("[redacted]", output, StringComparison.Ordinal);
            NoInventoryForms(output, EnvFirst);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Theory]
    [MemberData(nameof(InventoryVariableToolPairs))]
    public async Task WebTools_ClassifiedFailure_EnvironmentInventory_IsRedactedInReturnedAndLoggedText(string variable, string tool)
    {
        var forged = "forged-log-line";
        var exceptionMessage = $"request failed ssh://u:{EnvFirst}@h.invalid/r\n{forged} GH_TOKEN={EnvFirst} {PaddedBase64(EnvFirst)}";
        var queryOrUrl = tool == "search"
            ? $"ordinary-query {EnvFirst}\n{forged}"
            : $"https://h.invalid/{EnvFirst}\n{forged}";

        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, EnvFirst);
            var (composer, logger) = NewWebComposer(
                new FakeHttpMessageHandler(_ => throw new HttpRequestException(exceptionMessage)));

            var output = await RunWebToolAsync(composer, tool, queryOrUrl);

            Assert.StartsWith(tool == "search" ? "❌ Web search error: " : "❌ Web fetch error: ",
                output, StringComparison.Ordinal);
            NoInventoryForms(output, EnvFirst);

            var entry = Assert.Single(
                logger.LogEntries,
                e => e.LogLevel == LogLevel.Warning
                    && e.Message.Contains(tool == "search" ? "WebSearch failed" : "WebFetch failed",
                        StringComparison.Ordinal));
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(entry.Message, char.IsControl);
            NoInventoryForms(entry.Message, EnvFirst);
            Assert.Contains("[redacted]", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(EnvFirst, StringComparison.Ordinal));
            Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(PaddedBase64(EnvFirst), StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    // ── structural and encoded payloads outside the content field ─────────────

    [Theory]
    [InlineData("search")]
    [InlineData("fetch")]
    public async Task WebRemoteFields_StructuralAndEncodedPayloads_AreRedactedInTitleUrlAndLinks(string tool)
    {
        var body = tool == "search"
            ? SearchBody(
                "Authorization: Bearer FAKE_PASSWORD",
                "https://h.invalid/r?token=FAKE_PASSWORD&ref=topic",
                "ordinary content")
            : FetchBody(
                "Authorization: Bearer FAKE_PASSWORD",
                "ordinary content",
                ["ssh://u:FAKE_PASSWORD@h.invalid/r", PercentForm(WebKey), PaddedBase64(WebKey).TrimEnd('=')]);
        var (composer, _) = NewWebComposer(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var output = await RunWebToolAsync(composer, tool, tool == "search" ? "ordinary query" : "https://h.invalid/page");

        Assert.Contains("Authorization: Bearer [redacted]", output, StringComparison.Ordinal);
        Assert.Contains(tool == "search" ? "https://h.invalid/r?ref=topic" : "ssh://u:[redacted]@h.invalid/r",
            output, StringComparison.Ordinal);
        if (tool == "fetch")
        {
            Assert.Contains($"- {GitToolOutputRedactor.Placeholder}", output, StringComparison.Ordinal);
        }
        Assert.False(output.Contains("FAKE_PASSWORD", StringComparison.Ordinal),
            "A structural credential survived in the output.");
        NoWebSecret(output, WebKey);
    }

    // ── redaction precedes max_lines truncation for the links section ───────

    [Fact]
    public async Task WebFetch_LinksRedactedBeforeTruncation_TotalLineCountUsesRedactedOutput()
    {
        var original = Environment.GetEnvironmentVariable("GH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", MultiLineSecret);

            // The link carries the multi-line environment value; redacting it merges three raw
            // physical lines into one, so the reported total must be the redacted count (8),
            // never the raw count (9).
            var body = FetchBody("T", "c", [$"a\n{MultiLineSecret} b"]);
            var (composer, _) = NewWebComposer(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

            var output = await composer.WebFetchAsync("https://h.invalid/page", max_lines: 7);

            Assert.Contains("[redacted] b", output, StringComparison.Ordinal);
            Assert.EndsWith("...(truncated, 8 lines total)", output, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE_MULTI_1", output, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE_MULTI_2", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", original);
        }
    }

    // ── cancellation that is NOT caught by the tool propagates ───────────────

    [Theory]
    [InlineData("search")]
    [InlineData("fetch")]
    public async Task WebTools_NonCaughtCancellation_PropagatesWithoutLogging(string tool)
    {
        var (composer, logger) = NewWebComposer(
            new FakeHttpMessageHandler(_ => throw new OperationCanceledException($"cancelled {WebKey}")));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunWebToolAsync(composer, tool, tool == "search" ? "query" : "https://h.invalid/page"));

        // It is NOT a TaskCanceledException, so the tool's catch filter must not classify it;
        // it propagates untouched and nothing reaches the log.
        Assert.IsNotType<TaskCanceledException>(ex);
        Assert.Empty(logger.LogEntries);
    }

    // ── max_results lower clamp ──────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task WebSearch_MaxResultsBelowRange_IsClampedUpToOne(int maxResults)
    {
        string? sentBody = null;
        var (composer, _) = NewWebComposer(new FakeHttpMessageHandler(
            HttpStatusCode.OK, """{"results":[]}""", null,
            async req => sentBody = await req.Content!.ReadAsStringAsync()));

        await composer.WebSearchAsync("ordinary query", maxResults);

        Assert.NotNull(sentBody);
        Assert.Contains("\"max_results\":1", sentBody, StringComparison.Ordinal);
    }

    // ── structural shapes and bounded encodings ─────────────────────────────

    private static readonly (string Input, string Expected)[] StructuralPayloads =
    [
        ("http://u:FAKE_PASSWORD@h.invalid/r", "http://h.invalid/r"),
        ("https://h.invalid/r?token=FAKE_PASSWORD&ref=topic", "https://h.invalid/r?ref=topic"),
        ("ssh://u:FAKE_PASSWORD@h.invalid/r", "ssh://u:[redacted]@h.invalid/r"),
        ("u:FAKE_PASSWORD@h.invalid:org/r", "u:[redacted]@h.invalid:org/r"),
        // The indented entry below is the FOLDED continuation of this Authorization header.
        ("Authorization: Bearer FAKE_PASSWORD", "Authorization: Bearer [redacted]"),
        ("\tFAKE_PASSWORD_TAIL", "\t[redacted]"),
        ("Proxy-Authorization: Basic FAKE_PASSWORD==", "Proxy-Authorization: Basic [redacted]"),
        ("GH_TOKEN=FAKE_PASSWORD", "GH_TOKEN=[redacted]"),
        ("token: FAKE_PASSWORD", "token: [redacted]"),
        ("credential = 'FAKE_PASSWORD'", "credential = '[redacted]'"),
    ];

    private static readonly (string Input, string Expected)[] EncodedPayloads =
    [
        (WebKey, GitToolOutputRedactor.Placeholder),
        (PercentForm(WebKey), GitToolOutputRedactor.Placeholder),
        (PaddedBase64(WebKey), GitToolOutputRedactor.Placeholder),
        (PaddedBase64(WebKey).TrimEnd('='), GitToolOutputRedactor.Placeholder),
    ];

    [Theory]
    [InlineData("search", "structural")]
    [InlineData("search", "encoded")]
    [InlineData("fetch", "structural")]
    [InlineData("fetch", "encoded")]
    public async Task WebRemoteFields_StructuralPatternsAndBoundedEncodings_AreRedacted(string tool, string shape)
    {
        var payloads = shape == "structural" ? StructuralPayloads : EncodedPayloads;
        Assert.NotEmpty(payloads);
        Assert.True(payloads.Length == payloads.DistinctBy(p => p.Input).Count(), "Payload rows must be distinct.");

        var content = "ordinary-marker\n" + string.Join('\n', payloads.Select(p => p.Input)) + "\nend-marker";
        var expected = "ordinary-marker\n" + string.Join('\n', payloads.Select(p => p.Expected)) + "\nend-marker";
        Assert.True(content.Length < 500, "The payload block must fit inside the search content window.");

        var body = tool == "search"
            ? SearchBody("Shape Title", "https://h.invalid/shape", content)
            : FetchBody("Shape Title", content, ["https://h.invalid/link"]);
        var (composer, _) = NewWebComposer(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var output = await RunWebToolAsync(composer, tool, tool == "search" ? "ordinary query" : "https://h.invalid/page");

        Assert.Contains(expected, output);
        NoWebSecret(output, WebKey);
    }

    // ── redaction precedes the 500-character search clip ────────────────────

    [Fact]
    public async Task WebSearch_ClipBoundary_SecretInsideWindow_IsRedactedWholeAndLengthMeasuredOnRedactedText()
    {
        // The secret ends at index 496 — inside the clip window — so the measured clip length only
        // works out (500 characters + the ellipsis) if the clip is applied to the REDACTED text.
        var content = new string('x', 480) + WebKey + new string('y', 100);
        var (composer, _) = NewWebComposer(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SearchBody("Clip Title", "https://h.invalid/clip", content)));

        var output = await composer.WebSearchAsync("clip query");

        var line = Assert.Single(output.Split('\n'), l => l.StartsWith("xxxxxxxxxxx", StringComparison.Ordinal)).TrimEnd('\r');
        Assert.Equal(new string('x', 480) + GitToolOutputRedactor.Placeholder + new string('y', 10) + "…", line);
        Assert.Equal(501, line.Length);
        NoWebSecret(output, WebKey);
    }

    [Fact]
    public async Task WebSearch_ClipBoundary_SecretStraddlingWindow_LeavesNoRawOrPartialSecret()
    {
        // The secret starts at index 495 and runs past the clip window: clipping first would leave
        // the partial fragment "FAKE_" of the raw value in the window.
        var content = new string('x', 495) + WebKey + "tail";
        var (composer, _) = NewWebComposer(
            new FakeHttpMessageHandler(HttpStatusCode.OK, SearchBody("Clip Title", "https://h.invalid/clip", content)));

        var output = await composer.WebSearchAsync("clip query");

        var line = Assert.Single(output.Split('\n'), l => l.StartsWith("xxxxxxxxxxx", StringComparison.Ordinal)).TrimEnd('\r');
        Assert.Equal(new string('x', 495) + "[reda…", line);
        Assert.Equal(501, line.Length);
        for (var length = 4; length <= WebKey.Length; length++)
            Assert.False(output.Contains(WebKey[..length], StringComparison.Ordinal), "A partial fragment of the credential survived the clip.");
        NoWebSecret(output, WebKey);
    }

    // ── redaction precedes the max_lines truncation ─────────────────────────

    [Fact]
    public async Task WebFetch_Truncation_ReportsLineCountOfTheRedactedOutput()
    {
        var original = Environment.GetEnvironmentVariable("GH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", MultiLineSecret);

            var (composer, _) = NewWebComposer(new FakeHttpMessageHandler(
                HttpStatusCode.OK, FetchBody("Truncated Page", $"line-1\n{MultiLineSecret}\nline-2")));

            var output = await composer.WebFetchAsync("https://h.invalid/page", max_lines: 4);

            // The secret spans two RAW lines; the redacted output has 5 lines, so the notice can only
            // read "5 lines total" (raw: 6) if the count is taken AFTER redaction.
            Assert.EndsWith("...(truncated, 5 lines total)", output, StringComparison.Ordinal);
            Assert.Contains($"line-1\n{GitToolOutputRedactor.Placeholder}", output);
            Assert.DoesNotContain("line-2", output, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE_MULTI_1", output, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE_MULTI_2", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", original);
        }
    }

    [Fact]
    public async Task WebFetch_Truncation_WindowIsRedactedAndTotalCountIsExact()
    {
        var content = string.Join('\n', Enumerable.Range(1, 40).Select(i => i switch
        {
            3 => $"GH_TOKEN={WebSecret}",
            39 => $"https://u:{WebSecret}@h.invalid/r",
            _ => $"Line {i}",
        }));
        var (composer, _) = NewWebComposer(
            new FakeHttpMessageHandler(HttpStatusCode.OK, FetchBody("Window Page", content)));

        var output = await composer.WebFetchAsync("https://h.invalid/page", max_lines: 10);

        // The credential on content line 3 sits inside the retained window and is already redacted.
        Assert.Contains("GH_TOKEN=[redacted]", output);
        Assert.Contains("Line 1", output);
        Assert.Contains("Line 8", output);
        Assert.DoesNotContain("Line 9", output, StringComparison.Ordinal);
        // "# Window Page" + blank + 40 content lines = 42, counted on the REDACTED output.
        Assert.EndsWith("...(truncated, 42 lines total)", output, StringComparison.Ordinal);
        Assert.DoesNotContain(WebSecret, output, StringComparison.Ordinal);
    }

    // ── non-success responses ───────────────────────────────────────────────

    [Theory]
    [InlineData("search")]
    [InlineData("fetch")]
    public async Task WebTools_NonSuccessReasonPhrase_IsRedacted(string tool)
    {
        const string reasonPhrase = "FAKE_WEB_KEY_a12 ssh://u:FAKE_PASSWORD@h.invalid/r";
        var (composer, logger) = NewWebComposer(new ReasonPhraseHandler(reasonPhrase));

        var output = await RunWebToolAsync(composer, tool, tool == "search" ? "q" : "https://h.invalid/p");

        var expected = (tool == "search" ? "❌ Web search failed: HTTP 500 " : "❌ Web fetch failed: HTTP 500 ")
            + "[redacted] ssh://u:[redacted]@h.invalid/r";
        Assert.Equal(expected, output);
        NoWebSecret(output, WebKey);
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(WebKey, StringComparison.Ordinal));
    }

    // ── classified failures: returned error + captured log ──────────────────

    [Theory]
    [InlineData("search", "http")]
    [InlineData("search", "timeout")]
    [InlineData("search", "json")]
    [InlineData("fetch", "http")]
    [InlineData("fetch", "timeout")]
    [InlineData("fetch", "json")]
    public async Task WebTools_ClassifiedFailure_LogsRedactedSanitizedReasonWithoutExceptionObject(string tool, string failure)
    {
        const string forged = "forged-log-line";
        var exceptionMessage = $"request failed ssh://u:{WebSecret}@h.invalid/r\n{forged} GH_TOKEN={WebKey}";
        var queryOrUrl = tool == "search"
            ? $"ordinary-query {WebKey}\n{forged}"
            : $"https://h.invalid/{WebKey}\n{forged}";

        var handler = failure switch
        {
            "http" => new FakeHttpMessageHandler(_ => throw new HttpRequestException(exceptionMessage)),
            "timeout" => new FakeHttpMessageHandler(_ => throw new TaskCanceledException(exceptionMessage)),
            "json" => new FakeHttpMessageHandler(HttpStatusCode.OK, "invalid json"),
            _ => throw new InvalidOperationException($"Unknown failure '{failure}'."),
        };
        var expectedType = failure switch
        {
            "http" => nameof(HttpRequestException),
            "timeout" => nameof(TaskCanceledException),
            // Derived from the SAME parse call the tool makes: the malformed-payload exception's
            // runtime type is not a public BCL type name, so it is captured rather than guessed. The
            // logged name must be the actual type, which is what keeps the log diagnosable.
            _ => ParseFailureTypeName(),
        };
        var logMarker = tool == "search" ? "WebSearch failed" : "WebFetch failed";

        var (composer, logger) = NewWebComposer(handler);

        var output = await RunWebToolAsync(composer, tool, queryOrUrl);

        // The returned error string carries the REDACTED failure text.
        Assert.StartsWith(tool == "search" ? "❌ Web search error: " : "❌ Web fetch error: ", output, StringComparison.Ordinal);
        NoWebSecret(output, WebKey);
        Assert.False(output.Contains("Bearer", StringComparison.Ordinal));

        // Exactly one Warning mentions this tool, and it carries an exception TYPE NAME — never the
        // exception object, so a stack trace, header or body cannot reach the log.
        var entry = Assert.Single(
            logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning && e.Message.Contains(logMarker, StringComparison.Ordinal));
        Assert.Null(entry.Exception);
        Assert.Contains(expectedType, entry.Message, StringComparison.Ordinal);

        var redactedQueryOrUrl = tool == "search"
            ? "ordinary-query [redacted]?forged-log-line"
            : "https://h.invalid/[redacted]?forged-log-line";
        Assert.Contains(redactedQueryOrUrl, entry.Message, StringComparison.Ordinal);

        if (failure != "json")
            Assert.Contains(
                "ssh://u:[redacted]@h.invalid/r?forged-log-line GH_TOKEN=[redacted]",
                entry.Message,
                StringComparison.Ordinal);

        // The message is already log-sanitized: the newlines it contained cannot forge a log line.
        Assert.DoesNotContain(entry.Message, c => char.IsControl(c));
        Assert.Equal(LogSanitizer.SanitizeText(entry.Message), entry.Message);

        // Nothing anywhere in the captured log carries a raw secret, an encoded form, or the header.
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(WebKey, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(WebSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains("Bearer", StringComparison.Ordinal));
    }

    // ── outbound authorization is unchanged and never leaks ─────────────────

    [Theory]
    [InlineData("search")]
    [InlineData("fetch")]
    public async Task WebTools_SendBearerAuthorizationWithoutLeakingItIntoOutputOrLog(string tool)
    {
        HttpRequestMessage? captured = null;
        var body = tool == "search"
            ? SearchBody("Auth Page", "https://h.invalid/auth", "ordinary content")
            : FetchBody("Auth Page", "ordinary content", ["https://h.invalid/link"]);
        var (composer, logger) = NewWebComposer(
            new FakeHttpMessageHandler(HttpStatusCode.OK, body, req => captured = req));

        var output = await RunWebToolAsync(composer, tool, tool == "search" ? "auth query" : "https://h.invalid/page");

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal(WebKey, captured.Headers.Authorization?.Parameter);
        Assert.Contains("Auth Page", output);
        Assert.Contains("ordinary content", output);
        Assert.False(output.Contains(WebKey, StringComparison.Ordinal));
        Assert.False(output.Contains("Bearer", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(WebKey, StringComparison.Ordinal));
    }

    // ── ordinary behaviour preserved ────────────────────────────────────────

    [Fact]
    public async Task WebSearch_OrdinaryResponses_KeepFormattingClampingAndEmptyFieldBehaviour()
    {
        string? sentBody = null;
        const string json = """
            {"results":[
              {"title":"First","url":"https://a.invalid","content":"Content A"},
              {"url":"https://b.invalid","content":"Content B"},
              {"title":"Third","content":"Content C"}]}
            """;
        var (composer, logger) = NewWebComposer(new FakeHttpMessageHandler(
            HttpStatusCode.OK, json, null, async req => sentBody = await req.Content!.ReadAsStringAsync()));

        var output = await composer.WebSearchAsync("ordinary query", 50);

        Assert.Contains("### First", output);
        Assert.Contains("https://a.invalid", output);
        Assert.Contains("Content A", output);
        // A result without a title still renders its heading (empty) plus its url and content.
        Assert.Contains($"### {Nl}https://b.invalid{Nl}Content B", output);
        Assert.Contains("### Third", output);
        Assert.Contains("Content C", output);
        // max_results=50 is still clamped to 10.
        Assert.NotNull(sentBody);
        Assert.Contains("\"max_results\":10", sentBody);
        Assert.False(output.Contains('…'), "Short content must not be clipped.");
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel >= LogLevel.Warning);

        var (emptyComposer, _) = NewWebComposer(
            new FakeHttpMessageHandler(HttpStatusCode.OK, """{"results":[]}"""));
        Assert.Equal("No results found.", await emptyComposer.WebSearchAsync("nothing"));
    }

    [Fact]
    public async Task WebFetch_OrdinaryResponses_KeepHeadingsLineBreaksAndMissingLinkBehaviour()
    {
        var body = FetchBody("Simple Page", "line one\nline two\nline three", ["https://h.invalid/a", "https://h.invalid/b"]);
        var (composer, logger) = NewWebComposer(new FakeHttpMessageHandler(HttpStatusCode.OK, body));

        var output = await composer.WebFetchAsync("https://h.invalid/page");

        Assert.Equal(
            $"# Simple Page{Nl}{Nl}line one\nline two\nline three{Nl}{Nl}## Links{Nl}- https://h.invalid/a{Nl}- https://h.invalid/b{Nl}",
            output);
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel >= LogLevel.Warning);

        var (missingLinks, _) = NewWebComposer(
            new FakeHttpMessageHandler(HttpStatusCode.OK, FetchBody("No Links", "content")));
        var missingOutput = await missingLinks.WebFetchAsync("https://h.invalid/page");
        Assert.Contains("# No Links", missingOutput);
        Assert.False(missingOutput.Contains("## Links", StringComparison.Ordinal));

        var (nullLinks, _) = NewWebComposer(
            new FakeHttpMessageHandler(HttpStatusCode.OK, FetchBodyWithNullLinks("Null Links", "content")));
        var nullOutput = await nullLinks.WebFetchAsync("https://h.invalid/page");
        Assert.Contains("# Null Links", nullOutput);
        Assert.Contains("content", nullOutput);
        Assert.False(nullOutput.Contains("## Links", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("fetch")]
    public async Task WebTools_MissingApiKeyAndBlankInput_KeepValidationMessages(string tool)
    {
        var (composer, logger) = NewWebComposer(new ReasonPhraseHandler("unused"), apiKey: null);

        var noKey = await RunWebToolAsync(composer, tool, tool == "search" ? "query" : "https://h.invalid/page");
        Assert.Contains("❌", noKey);
        Assert.Contains("OLLAMA_API_KEY", noKey);
        Assert.DoesNotContain(logger.LogEntries, e => e.LogLevel >= LogLevel.Warning);

        var (blankComposer, _) = NewWebComposer(
            new FakeHttpMessageHandler(HttpStatusCode.OK, tool == "search" ? """{"results":[]}""" : """{"title":"","content":""}"""));
        // The key check still runs first, and a blank query/url is still rejected before any request.
        var blank = await RunWebToolAsync(blankComposer, tool, "   ");
        Assert.Contains("❌", blank);
        Assert.Contains(tool == "search" ? "query is required" : "url is required", blank);
    }
}
