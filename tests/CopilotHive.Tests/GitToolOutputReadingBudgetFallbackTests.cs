using System.Text;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Pins what the percent-reading enumeration returns when its step budget (4,000,000) is EXHAUSTED.
/// A cost limit must never become an invented answer: the fallback is bounded by what genuine readings
/// have established (the concrete matched span end, or the furthest genuine partial reading), never by
/// the longest span any spelling could conceivably have. A character absent from every genuine reading
/// therefore cannot cover a retained host, while a genuine equal character still fails closed. Every
/// family keeps an ordinary inventory line and a no-inventory host-preservation control, and both
/// inventory orders. Synthetic values only.
/// </summary>
public sealed class GitToolOutputReadingBudgetFallbackTests
{
    private const string Other = "FAKE_OTHER_KEY";

    // MaxReadingSteps is 4,000,000 and the enumeration spends ONE step per value character per live
    // position, so a value longer than that exhausts it. Fixtures assert their own length.
    private const int ReadingBudget = 4_000_000;

    private static string Wrap(string body) => "file.txt\n" + body + "\nend-marker";

    private static string[] Order(bool reverse, string value)
    {
        var all = new[] { value, Other };
        if (reverse)
            Array.Reverse(all);
        return all;
    }

    private static string Run(string body, bool reverse, string value) =>
        GitToolOutputRedactor.Redact(Wrap("ordinary " + Other + "\n" + body), Order(reverse, value));

    private static string Expected(string body) => Wrap("ordinary [redacted]\n" + body);

    private static string Url(string spelling, string host = "hhh") => $"https://{host}:{spelling}@{host}.invalid/r";

    private static string EscapeFirst(string value) => "%" + ((int)value[0]).ToString("X2") + value[1..];

    /// <summary>Asserts the budget really is exhausted, so a row cannot pass without exercising the fallback.</summary>
    private static void AssertExhausts(string value) =>
        Assert.True(value.Length > ReadingBudget, $"Fixture must exceed the reading-step budget; length {value.Length}.");

    private static void AssertPreserved(string value, string spelling, bool reverse, string host = "hhh")
    {
        var body = Url(spelling, host);
        // No-inventory control: the frozen grammar alone keeps the host.
        Assert.Equal($"https://{host}.invalid/r", GitToolOutputRedactor.Redact(body));
        Assert.Equal(Expected($"https://{host}.invalid/r"), Run(body, reverse, value));
    }

    // ── The reviewer-pinned preserve vector, as my own rows ───────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedBudget_SingleReadingWithNoHostCharacter_PreservesTheHost(bool reverse)
    {
        var value = "F" + new string('A', ReadingBudget + 12);
        AssertExhausts(value);
        var spelling = EscapeFirst(value);
        Assert.DoesNotContain('h', spelling);

        AssertPreserved(value, spelling, reverse);
    }

    // ── Mirror: a genuine equal character at the same size still covers (fail-closed) ───────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedBudget_SpellingWithAGenuineHostCharacter_StillCovers(bool reverse)
    {
        var value = "F" + new string('A', ReadingBudget + 11) + "h";
        AssertExhausts(value);
        var spelling = EscapeFirst(value);
        Assert.Contains('h', spelling);

        Assert.Equal(Expected("https://[redacted].invalid/r"), Run(Url(spelling), reverse, value));
        Assert.Equal("https://hhh.invalid/r", GitToolOutputRedactor.Redact(Url(spelling)));
    }

    // ── Budget exhaustion with MIXED literal / escaped content, the end at various positions ────

    [Theory]
    [InlineData("escape-first", false)]
    [InlineData("escape-first", true)]
    [InlineData("escape-middle", false)]
    [InlineData("escape-middle", true)]
    [InlineData("escape-last-but-one", false)]
    [InlineData("escape-last-but-one", true)]
    [InlineData("escape-many", false)]
    [InlineData("escape-many", true)]
    public void ExhaustedBudget_MixedLiteralAndEscapes_PreservesTheHost(string shape, bool reverse)
    {
        var value = "F" + new string('A', ReadingBudget + 20);
        AssertExhausts(value);
        var spelling = shape switch
        {
            "escape-first" => EscapeFirst(value),
            "escape-middle" => value[..2_000_000] + "%41" + value[2_000_001..],
            "escape-last-but-one" => value[..^2] + "%41" + value[^1],
            "escape-many" => string.Concat(value.Select((c, i) => i % 400_000 == 0 ? $"%{(int)c:X2}" : c.ToString())),
            _ => throw new InvalidOperationException("Unknown shape"),
        };
        Assert.DoesNotContain('h', spelling);
        Assert.Equal(value, Uri.UnescapeDataString(spelling));

        AssertPreserved(value, spelling, reverse);
    }

    [Theory]
    [InlineData("escape-first", false)]
    [InlineData("escape-first", true)]
    [InlineData("escape-middle", false)]
    [InlineData("escape-middle", true)]
    [InlineData("escape-last-but-one", false)]
    [InlineData("escape-last-but-one", true)]
    public void ExhaustedBudget_MixedSpellingThatKeepsAGenuineHost_StillCovers(string shape, bool reverse)
    {
        // The value ends in 'h'; whatever else is escaped, the spelling keeps that literal 'h'.
        var value = "F" + new string('A', ReadingBudget + 19) + "h";
        AssertExhausts(value);
        var spelling = shape switch
        {
            "escape-first" => EscapeFirst(value),
            "escape-middle" => value[..2_000_000] + "%41" + value[2_000_001..],
            "escape-last-but-one" => value[..^2] + "%41" + value[^1],
            _ => throw new InvalidOperationException("Unknown shape"),
        };
        Assert.Contains('h', spelling);

        Assert.Equal(Expected("https://[redacted].invalid/r"), Run(Url(spelling), reverse, value));
    }

    // ── The concrete end falls on / just past the budget boundary ─────────────────────────────

    [Theory]
    [InlineData(ReadingBudget - 5, false)]
    [InlineData(ReadingBudget, false)]
    [InlineData(ReadingBudget + 1, false)]
    [InlineData(ReadingBudget + 5, false)]
    [InlineData(ReadingBudget - 5, true)]
    [InlineData(ReadingBudget, true)]
    [InlineData(ReadingBudget + 1, true)]
    [InlineData(ReadingBudget + 5, true)]
    public void ValueLengthsAroundTheReadingBudget_AlwaysPreserveTheHost(int length, bool reverse)
    {
        var value = "F" + new string('A', length - 1);
        Assert.Equal(length, value.Length);
        AssertPreserved(value, EscapeFirst(value), reverse);
    }

    // ── A multi-candidate case where enumeration partially completes ──────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleReadingsThatExhaustTheBudget_UseOnlyGenuineCandidateCharacters(bool reverse)
    {
        // "%25" is either one escaped '%' or a literal '%' then "25". A leading run of such ambiguous
        // percents keeps more than one reading alive, so the enumeration does real branching work
        // before the long tail of ordinary characters exhausts the step budget. Whatever is returned
        // must come from genuine candidates only: the spelling holds no 'h' anywhere.
        var value = "F" + string.Concat(Enumerable.Repeat("%", 50)) + new string('A', ReadingBudget);
        AssertExhausts(value);
        var spelling = "F" + string.Concat(Enumerable.Repeat("%25", 50)) + new string('A', ReadingBudget);
        Assert.DoesNotContain('h', spelling);

        AssertPreserved(value, spelling, reverse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleReadingsThatExhaustTheBudget_StillCoverAGenuineEqualCharacter(bool reverse)
    {
        var value = "F" + string.Concat(Enumerable.Repeat("%", 50)) + new string('A', ReadingBudget) + "h";
        AssertExhausts(value);
        var spelling = "F" + string.Concat(Enumerable.Repeat("%25", 50)) + new string('A', ReadingBudget) + "h";
        Assert.Contains('h', spelling);

        Assert.Equal(Expected("https://[redacted].invalid/r"), Run(Url(spelling), reverse, value));
    }

    // ── Controls ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallSpanBelowTheBudget_IsDecidedExactlyAsBefore(bool reverse)
    {
        const string value = "FAKE_hASS";
        AssertPreserved(value, Percent(value), reverse);

        // Below the budget PLACEMENT is decided exactly (probing finds a sentinel in so small a
        // region), so even a spelling that genuinely contains an 'h' leaves the retained host's 'h'
        // alone: coverage is the fallback for UNDECIDABLE placement, not for mere equality.
        const string withHost = "FAKE_hASS_h";
        var spelling = "FAKE_%68ASS_h";
        Assert.Contains('h', spelling);
        Assert.Equal(Expected("https://hhh.invalid/r"), Run(Url(spelling), reverse, withHost));
    }

    [Fact]
    public void ExhaustedBudgetRows_PreserveTheLineStructure()
    {
        var value = "F" + new string('A', ReadingBudget + 12);
        var spelling = EscapeFirst(value);
        var input = "a\r\n" + Url(spelling) + "\nb\r\n" + Url(spelling) + "\nz";
        var actual = GitToolOutputRedactor.Redact(input, [value]);

        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        Assert.DoesNotContain("%46AAAA", actual, StringComparison.Ordinal);
        Assert.Contains("https://hhh.invalid/r", actual, StringComparison.Ordinal);
    }

    // ── Completed readings versus DEAD partial readings at exhaustion ──────────────────────────
    //
    // The suffix "%2525" is read two ways against the text "%2525%32X": "%" "25" "25" (a literal '%',
    // then two literal pairs... ending after "%2525") and a longer partial that runs on into "%32" and
    // then dies at the 'X' because the value's last character '5' cannot match it. Only a reading that
    // consumed the WHOLE value is a candidate; the dead partial must contribute nothing.

    /// <summary>
    /// A value of <c>prefix + "%2525"</c> whose prefix consumes exactly <paramref name="stepsBeforeSuffix"/>
    /// enumeration steps, so the budget runs out at a chosen point inside the suffix.
    /// </summary>
    private static (string Value, string Spelling) DeadPartialFixture(int stepsBeforeSuffix, bool hostCharacterInPrefix)
    {
        var prefix = "FAKE" + new string('a', stepsBeforeSuffix - 4);
        if (hostCharacterInPrefix)
            prefix = prefix[..100] + '3' + prefix[101..];

        var value = prefix + "%2525";
        var concrete = EscapeFirst(prefix) + "%2525";
        return (value, concrete + "%32X");
    }

    /// <summary>
    /// The prefix lengths put the exhausted step at different distances from the last value character.
    /// The step accounting (one visit per live position per value character; the suffix branches to two
    /// live positions on its last step) gives, for a prefix of <c>ReadingBudget - n</c> steps:
    /// <c>n = 8, 7</c> exhaust on the LAST suffix step (the dead partial is in <c>next</c>);
    /// <c>n = 6</c> exhausts one step earlier; <c>n = 9</c> and <c>n = 20</c> do NOT exhaust at all and
    /// are below-budget controls (the dead branch is discarded normally). Only the first two reproduce
    /// the reviewer's leak, which is why the baseline revert kills exactly those four rows.
    /// </summary>
    public static TheoryData<int, bool> DeadPartialDepths()
    {
        var rows = new TheoryData<int, bool>();
        foreach (var steps in new[] { ReadingBudget - 8, ReadingBudget - 7, ReadingBudget - 6, ReadingBudget - 9, ReadingBudget - 20 })
        {
            rows.Add(steps, false);
            rows.Add(steps, true);
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(DeadPartialDepths))]
    public void DeadPartialReading_AtAnyExhaustionDepth_NeverCoversTheRetainedHost(int stepsBeforeSuffix, bool reverse)
    {
        var (value, spelling) = DeadPartialFixture(stepsBeforeSuffix, hostCharacterInPrefix: false);
        Assert.DoesNotContain('3', value);

        var body = Url(spelling, host: "3");
        Assert.Equal("https://3.invalid/r", GitToolOutputRedactor.Redact(body));
        Assert.Equal(Expected("https://3.invalid/r"), Run(body, reverse, value));
    }

    [Theory]
    [MemberData(nameof(DeadPartialDepths))]
    public void DeadPartialReading_WithAGenuineEqualCharacterInTheCompletedSpelling_StillCovers(int stepsBeforeSuffix, bool reverse)
    {
        // The completed spelling DOES contain the host's '3' (inside the prefix), so placement is
        // undecidable and the sanctioned fail-closed coverage applies — the dead branch changes nothing.
        var (value, spelling) = DeadPartialFixture(stepsBeforeSuffix, hostCharacterInPrefix: true);
        Assert.Contains('3', value);

        var body = Url(spelling, host: "3");
        Assert.Equal(Expected("https://[redacted].invalid/r"), Run(body, reverse, value));
    }

    // The value's suffix "%25" against the text "%2525" has TWO completed readings: "%" "2" "5" ending
    // at 3, and the escape "%25" consumed as one '%' then "25" ending at 5. A prefix of
    // ReadingBudget - 4 steps makes the budget run out on the last step with the first completed
    // reading already established and the second still unvisited (checked by simulating the step
    // accounting; it exhausts at suffix step 2 of 3).
    private static (string Value, string Spelling) TwoCompletedFixture(string trailingText)
    {
        var prefix = "FAKE" + new string('a', ReadingBudget - 4 - 4);
        var value = prefix + "%25";
        return (value, EscapeFirst(prefix) + "%2525" + trailingText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoCompletedReadingsAtExhaustion_UseOnlyTheirOwnCharacters(bool reverse)
    {
        // Text after both completed readings holds a '3' (the retained host's character). No completed
        // reading contains it, so it must not cover the host.
        var (value, spelling) = TwoCompletedFixture("%32X");
        Assert.True(value.Length > ReadingBudget - 8, "Fixture must sit at the exhaustion boundary.");
        Assert.DoesNotContain('3', value);

        var body = Url(spelling, host: "3");
        Assert.Equal("https://3.invalid/r", GitToolOutputRedactor.Redact(body));
        Assert.Equal(Expected("https://3.invalid/r"), Run(body, reverse, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoCompletedReadingsAtExhaustion_StillCoverAGenuineEqualCharacter(bool reverse)
    {
        // The completed spelling itself holds the '3', so placement is undecidable: fail-closed coverage.
        var prefix = "FAKE" + new string('a', ReadingBudget - 4 - 4);
        prefix = prefix[..100] + '3' + prefix[101..];
        var value = prefix + "%25";
        var spelling = EscapeFirst(prefix) + "%2525%32X";
        Assert.Contains('3', EscapeFirst(prefix));

        Assert.Equal(Expected("https://[redacted].invalid/r"), Run(Url(spelling, host: "3"), reverse, value));
    }

    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));
}
