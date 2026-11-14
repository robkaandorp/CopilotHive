using System.Diagnostics;
using System.Globalization;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>Iteration-17 additive completed-reading budget probes. Generated synthetic values only.</summary>
public sealed class GitToolOutputReadingBudgetBoundaryTests(ITestOutputHelper output)
{
    private const int Budget = 4_000_000;
    private const string Other = "FAKE_OTHER_KEY";
    private static string Wrap(string body) => "file.txt\nordinary " + Other + "\n" + body + "\nend-marker";
    private static string Expected(string body) => "file.txt\nordinary [redacted]\n" + body + "\nend-marker";
    private static string EscapeAt(string value, int position) => value[..position] + $"%{(byte)value[position]:X2}" + value[(position + 1)..];

    private string Run(string label, string body, string value, bool reverse)
    {
        var input = Wrap(body);
        string[] inventory = reverse ? [Other, value] : [value, Other];
        var timer = Stopwatch.StartNew();
        var actual = GitToolOutputRedactor.Redact(input, inventory);
        timer.Stop();
        output.WriteLine($"READING_BOUNDARY_RUNTIME {label}: {timer.Elapsed.TotalMilliseconds:F3} ms; inputChars={input.Length}; valueChars={value.Length}");
        return actual;
    }

    private void NoInventory(string label, string body, string expected)
    {
        var timer = Stopwatch.StartNew();
        var actual = GitToolOutputRedactor.Redact(body);
        timer.Stop();
        output.WriteLine($"READING_BOUNDARY_RUNTIME control/{label}: {timer.Elapsed.TotalMilliseconds:F3} ms; inputChars={body.Length}");
        Assert.Equal(expected, actual);
    }

    private static string Url(string host, string spelling, string path = "/r") => $"https://{host}:{spelling}@{host}.invalid{path}";

    [Theory]
    [InlineData('a', false, false)]
    [InlineData('a', false, true)]
    [InlineData('f', false, false)]
    [InlineData('f', false, true)]
    [InlineData('0', false, false)]
    [InlineData('0', false, true)]
    [InlineData('9', false, false)]
    [InlineData('9', false, true)]
    [InlineData('a', true, false)]
    [InlineData('a', true, true)]
    [InlineData('f', true, false)]
    [InlineData('f', true, true)]
    [InlineData('0', true, false)]
    [InlineData('0', true, true)]
    [InlineData('9', true, false)]
    [InlineData('9', true, true)]
    public void BudgetHexHosts_MixedSpellings_UseOnlyConcreteMembership(char hostCharacter, bool genuine, bool reverse)
    {
        var value = "FAKE" + new string('K', Budget + 17) + (genuine ? hostCharacter : 'K');
        Assert.True(value.Length > Budget, "Fixture must exhaust the real reading budget.");
        var at = hostCharacter switch
        {
            'a' => 0,
            'f' => value.Length / 2,
            '0' => value.Length - 2,
            '9' => 3,
            _ => throw new InvalidOperationException("Unknown host alphabet case"),
        };
        var spelling = EscapeAt(value, at);
        if (hostCharacter == '9') spelling = EscapeAt(spelling, spelling.Length / 2);
        Assert.Equal(genuine, spelling.Contains(hostCharacter));
        Assert.Equal(value, Uri.UnescapeDataString(spelling));
        var host = new string(hostCharacter, 3);
        var path = hostCharacter is 'f' or '9' ? "/retained/path-prefix/r" : "/r";
        var body = Url(host, spelling, path);
        var safe = $"https://{(genuine ? "[redacted]" : host)}.invalid{path}";
        var label = $"hex-{(int)hostCharacter}/{genuine}/{reverse}";
        NoInventory(label, body, $"https://{host}.invalid{path}");
        Assert.Equal(Expected(safe), Run(label, body, value, reverse));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BudgetTwoCompletedReadings_BranchBeforeExhaustion_KeepOnlyGenuineCharacters(bool containsHost, bool reverse)
    {
        const int pairs = 2_000_004;
        var value = "FAKE%" + string.Concat(Enumerable.Repeat("25", pairs));
        var spelling = "FAKE%25" + string.Concat(Enumerable.Repeat("25", pairs));
        Assert.True(value.Length > Budget, "Both live reading paths must outlast the budget.");
        // Two COMPLETE readings, not merely a transient branch: the literal-% prefix is the raw
        // value, while decoding exactly one %25 produces the same entire value two chars later.
        Assert.Equal(value, spelling[..value.Length]);
        Assert.Equal(value, Uri.UnescapeDataString(spelling));
        Assert.Equal(value.Length + 2, spelling.Length);
        Assert.Equal(new[] { 9, 11 }, CompletedEnds("FAKE%2525", "FAKE%252525"));
        var hostCharacter = containsHost ? '2' : '0';
        Assert.Equal(containsHost, spelling.Contains(hostCharacter));
        var host = new string(hostCharacter, 3);
        var body = Url(host, spelling);
        var label = $"two-completed/{containsHost}/{reverse}";
        NoInventory(label, body, $"https://{host}.invalid/r");
        Assert.Equal(Expected($"https://{(containsHost ? "[redacted]" : host)}.invalid/r"), Run(label, body, value, reverse));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallSpans_ConcreteMembershipAndExactPlacementRemainUnchanged(bool reverse)
    {
        foreach (var value in new[] { "FAKE_KEY_K", "FAKE_KEY_a" })
        {
            var body = Url("aaa", EscapeAt(value, 0), "/retained/r");
            NoInventory("small", body, "https://aaa.invalid/retained/r");
            Assert.Equal(Expected("https://aaa.invalid/retained/r"), Run("small/" + reverse, body, value, reverse));
        }
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public void BudgetLines_MixedSpellingsPreserveExactLfAndCrLf(string newline, bool reverse)
    {
        var value = "FAKE" + new string('K', Budget + 17);
        Assert.True(value.Length > Budget);
        var spelling = EscapeAt(value, value.Length / 2);
        Assert.DoesNotContain('a', spelling);
        var body = Url("aaa", spelling) + newline + "retained-marker" + newline + Url("aaa", spelling, "/prefix/r");
        var safe = "https://aaa.invalid/r" + newline + "retained-marker" + newline + "https://aaa.invalid/prefix/r";
        NoInventory("lines/" + newline.Length, body, safe);
        var actual = Run("lines/" + newline.Length + "/" + reverse, body, value, reverse);
        Assert.Equal(Expected(safe), actual);
        Assert.Equal(Wrap(body).Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
    }

    // Small exact oracle for fixture reasoning only: consumes the FULL value and returns completed
    // endpoints. Dead partial paths are deliberately NOT candidates. No production private API used.
    private static int[] CompletedEnds(string value, string text)
    {
        var positions = new HashSet<int> { 0 };
        foreach (var expected in value)
        {
            var next = new HashSet<int>();
            foreach (var at in positions)
            {
                if (at < text.Length && text[at] == expected) next.Add(at + 1);
                if (at + 2 < text.Length && text[at] == '%'
                    && byte.TryParse(text.AsSpan(at + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var decoded)
                    && decoded == expected) next.Add(at + 3);
            }
            positions = next;
        }
        return positions.Order().ToArray();
    }

    private void DeadPartialCase(bool genuine, bool reverse)
    {
        const int prefixLength = Budget - 8;
        var prefix = "FAKE" + new string('a', prefixLength - 4);
        if (genuine) prefix = prefix[..100] + '3' + prefix[101..];
        const string suffix = "%2525";
        const string suffixText = "%2525%32X";
        Assert.Equal(new[] { 5 }, CompletedEnds(suffix, suffixText));
        Assert.Equal(new[] { 4, 8 }, CompletedEnds(suffix[..^1], suffixText));
        Assert.Equal('5', suffix[^1]);
        Assert.Equal('X', suffixText[8]); // The longer partial CANNOT consume the final '5'.
        Assert.DoesNotContain('3', suffixText[..5]);
        var value = prefix + suffix;
        var concrete = EscapeAt(prefix, 0) + suffix;
        var spelling = concrete + "%32X"; // Extra text is outside EVERY completed candidate.
        // Decode only the unambiguous prefix. The suffix's completed reading treats '%' literally;
        // Uri.UnescapeDataString would choose a DIFFERENT reading there and is not the oracle.
        Assert.Equal(prefix, Uri.UnescapeDataString(concrete[..^suffix.Length]));
        Assert.Equal(value, prefix + suffixText[..5]);
        Assert.Equal(genuine, concrete.Contains('3'));
        Assert.Equal(prefixLength + 5, value.Length);
        Assert.Equal(Budget, prefixLength + 8); // First final-step visit completes the shorter path.
        Assert.True(prefixLength + 9 > Budget); // Next visit trips budget BEFORE discarding dead X.
        var body = Url("3", spelling);
        var start = body.IndexOf(concrete, StringComparison.Ordinal);
        var hostAt = body.LastIndexOf("@3.invalid", StringComparison.Ordinal) + 1;
        Assert.True(start >= 0);
        Assert.Equal(start + concrete.Length + "%32X@".Length, hostAt);
        Assert.True(hostAt > start + concrete.Length, "Retained host must be beyond every completed extent.");
        var label = $"dead-partial/{genuine}/{reverse}";
        NoInventory(label, body, "https://3.invalid/r");
        Assert.Equal(Expected($"https://{(genuine ? "[redacted]" : "3")}.invalid/r"), Run(label, body, value, reverse));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeadPartialReading_MustNotInventMembershipOutsideCompletedSpans(bool reverse) => DeadPartialCase(false, reverse);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeadPartialReading_GenuineEqualCharacterStillCovers(bool reverse) => DeadPartialCase(true, reverse);
}
