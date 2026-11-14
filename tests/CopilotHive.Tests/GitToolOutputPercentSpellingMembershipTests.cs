using System.Text;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Pins that a percent-form match's character membership is that of the CONCRETE matched spelling in
/// the original text, never of every spelling the value could hypothetically have (the raw value's
/// characters plus the whole hex alphabet). Only a value past the 1,000,000-position map-scan budget
/// reaches the membership decision — below it the positional scan decides exactly — so every row uses
/// a value of Budget + 13 characters. Each family keeps an ordinary inventory line and a no-inventory
/// host-preservation control. Synthetic values only.
/// </summary>
public sealed class GitToolOutputPercentSpellingMembershipTests
{
    private const string Other = "FAKE_OTHER_KEY";
    private const int Length = 1_000_000 + 13;

    private static string Wrap(string body) => "file.txt\n" + body + "\nend-marker";

    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));

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

    private static string Url(char hostCharacter, string spelling) =>
        $"https://{new string(hostCharacter, 3)}:{spelling}@{new string(hostCharacter, 3)}.invalid/r";

    private static void AssertHostPreserved(char hostCharacter, string spelling, string value, bool reverse)
    {
        Assert.DoesNotContain(hostCharacter, spelling);
        var body = Url(hostCharacter, spelling);
        var host = new string(hostCharacter, 3) + ".invalid";

        // No-inventory control: the frozen grammar alone keeps the host.
        Assert.Equal($"https://{host}/r", GitToolOutputRedactor.Redact(body));
        Assert.Equal(Expected($"https://{host}/r"), Run(body, reverse, value));
    }

    // ── The three reviewer-pinned shapes, as my own rows ─────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullyEscapedValueWhoseRawFormHasTheHostCharacter_PreservesTheHost(bool reverse)
    {
        // The raw value ends in 'h', but every byte is escaped ("%68"), so the spelling has no 'h'.
        var value = "F" + new string('A', Length - 2) + "h";
        Assert.Contains('h', value);
        AssertHostPreserved('h', Percent(value), value, reverse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedSpellingThatEscapesTheFinalHostCharacter_PreservesTheHost(bool reverse)
    {
        var value = "F" + new string('A', Length - 2) + "h";
        AssertHostPreserved('h', value[..^1] + "%68", value, reverse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HexDigitAbsentFromTheValueAndTheSpelling_PreservesTheHost(bool reverse)
    {
        var value = "F" + new string('A', Length - 1);
        Assert.DoesNotContain('0', value);
        AssertHostPreserved('0', Percent(value), value, reverse);
    }

    // ── The hex alphabet is not a licence: every letter and digit the escape form could use ────

    [Theory]
    [InlineData('b', false)]
    [InlineData('b', true)]
    [InlineData('d', false)]
    [InlineData('9', false)]
    [InlineData('9', true)]
    [InlineData('f', false)]
    [InlineData('e', true)]
    public void HostCharacterFromTheHexAlphabet_IsPreservedWhenTheActualSpellingLacksIt(char host, bool reverse)
    {
        // 'K' escapes to "%4B": an upper-case 'B' appears in the spelling, never a lower-case 'b'.
        // The superset (raw value + "0-9A-Fa-f") would license a lower-case host letter; the actual
        // spelling does not contain it.
        var value = "F" + new string('K', Length - 1);
        AssertHostPreserved(host, Percent(value), value, reverse);
    }

    // ── Mirror controls: a genuine equal character in the ACTUAL spelling still covers ────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedSpellingThatKeepsALiteralHostCharacter_StillCoversFailClosed(bool reverse)
    {
        // Escape an 'A', but keep the trailing literal 'h': the spelling DOES contain the host's
        // character, so WHERE it lies is undecidable at this size — the sanctioned fail-closed side.
        var value = "F" + new string('A', Length - 2) + "h";
        var spelling = "F%41" + value[2..];
        Assert.Contains('h', spelling);

        var actual = Run(Url('h', spelling), reverse, value);

        Assert.Equal(Expected("https://[redacted].invalid/r"), actual);
        Assert.Equal("https://hhh.invalid/r", GitToolOutputRedactor.Redact(Url('h', spelling)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpellingWithALiteralHexLetter_StillCoversThatLetterAsAHost(bool reverse)
    {
        // The spelling keeps literal lower-case 'b's, so a 'b' host is genuinely ambiguous.
        var value = "F" + new string('b', Length - 1);
        var spelling = "%46" + new string('b', Length - 1);
        Assert.Contains('b', spelling);

        Assert.Equal(Expected("https://[redacted].invalid/r"), Run(Url('b', spelling), reverse, value));
    }

    // ── Several candidate readings of a literal '%': membership is exact over the genuine ones ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueWithALiteralPercent_FullyEscaped_UsesOnlyTheCharactersItActuallySpells(bool reverse)
    {
        // The value holds a literal '%' (and an 'h'); the spelling escapes both ("%25" and "%68"), so
        // the spelling holds neither a literal 'h' nor any hex letter beyond the escapes' own digits.
        var value = "F%" + new string('A', Length - 3) + "h";
        Assert.Contains('%', value);
        AssertHostPreserved('h', Percent(value), value, reverse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueWithALiteralPercent_SpelledLiterally_StillCoversAnEqualHostCharacter(bool reverse)
    {
        // Here the '%' is spelled literally and the 'h' too, so the 'h' host is genuinely ambiguous.
        var value = "F%" + new string('A', Length - 3) + "h";
        var spelling = "F%" + new string('A', Length - 3) + "h";

        Assert.Equal(Expected("https://[redacted].invalid/r"), Run(Url('h', spelling), reverse, value));
    }

    [Fact]
    public void AmbiguousPercentReadings_AreAllConsidered_NeverDroppingTheKnownValue()
    {
        // "%25" is either one escaped '%' or a literal '%' followed by "25". A value ending in
        // "%25" matched against "...%2525" has two genuine readings; the known value must never
        // survive whichever the matcher takes, and the rest of the text must be intact.
        var value = "FAKE_KEY_%25";
        var actual = GitToolOutputRedactor.Redact("file.txt\nordinary " + Other + "\nsee FAKE_KEY_%2525 end\nend-marker",
            [value, Other]);

        Assert.DoesNotContain("FAKE_KEY", actual, StringComparison.Ordinal);
        Assert.StartsWith("file.txt\nordinary [redacted]\nsee ", actual, StringComparison.Ordinal);
        Assert.EndsWith(" end\nend-marker", actual, StringComparison.Ordinal);
    }

    // ── Controls and invariants ──────────────────────────────────────────────────────────────

    [Fact]
    public void SmallPercentSpelledValue_IsStillDecidedExactlyByThePositionalScan()
    {
        // Below the budget the positional scan decides; this row guards that the membership change
        // did not alter the ordinary-size behaviour.
        const string value = "FAKE_hASS";
        var spelling = Percent(value);
        Assert.DoesNotContain('h', spelling);

        Assert.Equal(
            Expected("https://hhh.invalid/r"),
            Run(Url('h', spelling), false, value));
    }

    [Fact]
    public void PercentRows_PreserveTheLineStructure()
    {
        var value = "F" + new string('A', Length - 2) + "h";
        var input = "a\r\n" + Url('h', Percent(value)) + "\nb\r\n" + Url('h', Percent(value)) + "\nz";
        var actual = GitToolOutputRedactor.Redact(input, [value]);

        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        Assert.DoesNotContain("%46%41", actual, StringComparison.Ordinal);
        Assert.Contains("https://hhh.invalid/r", actual, StringComparison.Ordinal);
    }
}
