using System.Text;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Pins that CHARACTER MEMBERSHIP ("does the known value contain character c?") is answered EXACTLY
/// while mapping a known value onto the URL-rewritten text, however large the value is. A scan budget
/// may only ever limit the genuinely undecidable question — WHERE an equal character lies — so a huge
/// value that cannot contain a host character must never widen over it, while a huge value that DOES
/// contain it must still cover it by placement fail-closing. Every family keeps an ordinary
/// inventory line and a no-inventory host-preservation control. Synthetic values only.
/// </summary>
public sealed class GitToolOutputMembershipExactnessTests
{
    private const string Other = "FAKE_OTHER_KEY";

    // MaxMapScan is 1,000,000 positions: lengths either side of it exercise the budget boundary.
    private const int ScanBudget = 1_000_000;

    private static string Wrap(string body) => "file.txt\n" + body + "\nend-marker";

    private static string Url(string user, string value, string host, string path) => $"https://{user}:{value}@{host}/{path}";

    private static string[] Order(bool reverse, params string[] values)
    {
        var all = values.Append(Other).ToArray();
        if (reverse)
            Array.Reverse(all);
        return all;
    }

    private static string Run(string body, bool reverse, params string[] values) =>
        GitToolOutputRedactor.Redact(Wrap("ordinary " + Other + "\n" + body), Order(reverse, values));

    private static string Expected(string body) => Wrap("ordinary [redacted]\n" + body);

    private static string Pad(string head, char fill, int totalLength) => head + new string(fill, totalLength - head.Length);

    // ── The reviewer-pinned vector, as my own row ──────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugeValueWithoutTheHostCharacter_PreservesTheRetainedHost(bool reverse)
    {
        var value = "FAKE_MAGIC" + new string('A', 1_000_001);
        Assert.DoesNotContain('h', value);

        Assert.Equal(
            Expected("https://hhh.invalid/r"),
            Run(Url("hhh", value, "hhh.invalid", "r"), reverse, value));
        Assert.Equal("https://hhh.invalid/r", GitToolOutputRedactor.Redact(Url("hhh", value, "hhh.invalid", "r")));
    }

    // ── (1) A ~1MB value that CONTAINS the host character: placement fail-closing still covers ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugeValueContainingTheHostCharacter_AndTheHost_StillCoversTheHostExtent(bool reverse)
    {
        // The known value spans the password AND the host, so the retained host is part of it and
        // the whole value must go as one placeholder. The 'h' in the value is exactly the genuinely
        // ambiguous character (the userinfo "hhh" also holds 'h').
        var password = "FAKE_MAGIC" + new string('A', 1_000_001);
        var value = password + "@hhh.invalid";

        Assert.Equal(
            Expected("https://[redacted]/r"),
            Run(Url("hhh", password, "hhh.invalid", "r"), reverse, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugePasswordThatContainsTheHostCharacter_NeverLeaksTheKnownValue(bool reverse)
    {
        // DOCUMENTED FAIL-CLOSED TRADE-OFF (same doctrine as the FAKE_hASS pin): the password holds a
        // lower-case 'h', so WHERE the host's 'h' lies is undecidable at this size. Pinned is the
        // safety direction only — the known value never survives and the rest of the host is intact.
        var password = "FAKE_MAGIC" + new string('A', 1_000_001) + "h";
        var actual = Run(Url("hhh", password, "hhh.invalid", "r"), reverse, password);

        Assert.DoesNotContain("FAKE_MAGIC", actual, StringComparison.Ordinal);
        Assert.Contains(".invalid/r", actual, StringComparison.Ordinal);
        Assert.StartsWith("file.txt\nordinary [redacted]\nhttps://", actual, StringComparison.Ordinal);
        Assert.EndsWith("\nend-marker", actual, StringComparison.Ordinal);
    }

    // ── (2) A huge value whose character set excludes SEVERAL host / path characters ───────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugeValueExcludingSeveralHostAndPathCharacters_PreservesAllOfThem(bool reverse)
    {
        // The value holds only 'A' and a few fixed characters. Host "hostname.example" and path
        // "xyz/abc" use characters it cannot contain, and the userinfo repeats them so every one of
        // those characters is ambiguous.
        var value = "FAKE_MAGIC" + new string('A', 1_000_001);
        foreach (var c in "hostname.example/xyzbcd")
            Assert.DoesNotContain(c, value);

        var user = "hostname.example";
        Assert.Equal(
            Expected("https://hostname.example/xyz/abc"),
            Run(Url(user, value, "hostname.example", "xyz/abc"), reverse, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugeValueExcludingTheHostCharacters_ButContainingAPathCharacter_StillPreservesTheHost(bool reverse)
    {
        // 'x' is in the value and in the path; the host characters are not in the value.
        var value = "FAKE_MAGIC" + new string('x', 1_000_001);
        Assert.DoesNotContain('h', value);

        Assert.Equal(
            Expected("https://hhh.invalid/xx"),
            Run(Url("hhh", value, "hhh.invalid", "xx"), reverse, value));
    }

    // ── (3) Membership exactness at lengths around the 1,000,000 scan-budget boundary ──────────

    [Theory]
    [InlineData(ScanBudget - 20_000, false)]
    [InlineData(ScanBudget - 11, false)]
    [InlineData(ScanBudget - 1, false)]
    [InlineData(ScanBudget, false)]
    [InlineData(ScanBudget + 1, false)]
    [InlineData(ScanBudget + 11, false)]
    [InlineData(ScanBudget + 20_000, false)]
    [InlineData(ScanBudget - 1, true)]
    [InlineData(ScanBudget, true)]
    [InlineData(ScanBudget + 1, true)]
    public void MembershipIsExactAtEveryLengthAroundTheScanBudget(int length, bool reverse)
    {
        var value = Pad("FAKE_MAGIC", 'A', length);
        Assert.Equal(length, value.Length);
        Assert.DoesNotContain('h', value);

        Assert.Equal(
            Expected("https://hhh.invalid/r"),
            Run(Url("hhh", value, "hhh.invalid", "r"), reverse, value));
    }

    [Theory]
    [InlineData(ScanBudget - 1)]
    [InlineData(ScanBudget)]
    [InlineData(ScanBudget + 1)]
    public void AValueThatDoesContainTheHostCharacter_IsCoveredOnBothSidesOfTheBudget(int length)
    {
        // The same sizes, but the value holds the host character, so it must be covered at every one.
        var password = Pad("FAKE_MAGIC", 'A', length - "@hhh.invalid".Length);
        var value = password + "@hhh.invalid";
        Assert.Equal(length, value.Length);

        Assert.Equal(
            Expected("https://[redacted]/r"),
            Run(Url("hhh", password, "hhh.invalid", "r"), false, value));
    }

    // ── (4) Several huge values with DISJOINT character sets ───────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeveralHugeValuesWithDisjointCharacterSets_EachDecidesMembershipForItsOwnExtent(bool reverse)
    {
        // Two huge known values in two URLs, with disjoint alphabets. Each URL's host uses a character
        // that only the OTHER value contains, so neither value's extent may cover its own host.
        var first = "FAKE_ONE_" + new string('A', 600_000);
        var second = "FAKE_TWO_" + new string('B', 600_000);
        Assert.DoesNotContain('h', first);
        Assert.DoesNotContain('h', second);

        var body = Url("hhh", first, "hhh.invalid", "one") + " " + Url("hhh", second, "hhh.invalid", "two");
        var actual = Run(body, reverse, first, second);

        Assert.Equal(Expected("https://hhh.invalid/one https://hhh.invalid/two"), actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoHugeValues_OneContainingTheHostCharacter_OnlyThatOneCoversItsHost(bool reverse)
    {
        var withoutH = "FAKE_ONE_" + new string('A', 600_000);
        var passwordWithH = "FAKE_TWO_" + new string('B', 600_000);
        var withH = passwordWithH + "@hhh.invalid";

        var body = Url("hhh", withoutH, "hhh.invalid", "one") + " " + Url("hhh", passwordWithH, "hhh.invalid", "two");
        var actual = Run(body, reverse, withoutH, withH);

        Assert.Equal(Expected("https://hhh.invalid/one https://[redacted]/two"), actual);
    }

    // ── Encoded variants carry THEIR OWN exact character set ──────────────────────────────────

    [Fact]
    public void Base64SpellingWithoutTheHostCharacter_PreservesTheHost_EvenWhenTheRawValueContainsIt()
    {
        // The RAW value holds a lower-case 'h' (as its last character) but its base64 SPELLING holds
        // none. The inventory matches the spelling that sits in the deleted userinfo, so membership
        // must come from the SPELLING: consulting the raw set would find an 'h', wrongly treat the
        // retained host's 'h' as possibly covered, and redact ordinary text.
        var value = "FAKE_MAGIC" + new string('A', 1_000_001) + "h";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        Assert.Contains('h', value);
        Assert.DoesNotContain('h', encoded);

        var actual = Run($"https://hhh:{encoded}@hhh.invalid/r", false, value);

        Assert.Equal(Expected("https://hhh.invalid/r"), actual);
    }

    [Fact]
    public void Base64SpellingContainingTheHostCharacter_IsCoveredFailClosed_NeverLeakingTheKnownValue()
    {
        // The mirror image: the raw value holds NO 'h' but its base64 spelling does, so the host's 'h'
        // may lie inside the known extent and placement is genuinely undecidable. This is the
        // sanctioned fail-closed direction; pinned is safety only (the known spelling never survives
        // and the rest of the host and path are intact), not that the host's first character is kept.
        var value = "FAKE_MAGIC" + new string('H', 1_000_001);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        Assert.DoesNotContain('h', value);
        Assert.Contains('h', encoded);

        var actual = Run($"https://hhh:{encoded}@hhh.invalid/r", false, value);

        Assert.DoesNotContain(encoded[..64], actual, StringComparison.Ordinal);
        Assert.Contains(".invalid/r", actual, StringComparison.Ordinal);
        Assert.StartsWith("file.txt\nordinary [redacted]\nhttps://", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void PercentEscapedValue_UsesTheEscapeCharacterSet()
    {
        // A percent-escaped reading may contain '%' and hex digits that the raw value does not; the
        // exact set for an escaped match must include them so a retained host can still be judged.
        const string value = "FAKE_MAGIC_KEY";
        var escaped = value.Replace("_", "%5F", StringComparison.Ordinal);
        var actual = Run($"https://u:{escaped}@host.invalid/r", false, value);

        Assert.DoesNotContain("MAGIC", actual, StringComparison.Ordinal);
        Assert.Contains("https://host.invalid/r", actual, StringComparison.Ordinal);
    }

    // ── No-inventory host-preservation controls ────────────────────────────────────────────────

    [Theory]
    [InlineData("hhh", "hhh.invalid", "r")]
    [InlineData("hostname.example", "hostname.example", "xyz/abc")]
    public void WithoutAnyInventory_TheFrozenGrammarAloneKeepsHostAndPath(string user, string host, string path)
    {
        var value = "FAKE_MAGIC" + new string('A', 200_000);
        Assert.Equal($"https://{host}/{path}", GitToolOutputRedactor.Redact(Url(user, value, host, path)));
    }

    [Fact]
    public void MembershipRows_PreserveTheLineStructure()
    {
        var value = "FAKE_MAGIC" + new string('A', 1_000_001);
        var input = "a\r\n" + Url("hhh", value, "hhh.invalid", "r") + "\nb\r\n" + Url("hhh", value, "hhh.invalid", "s") + "\nz";
        var actual = GitToolOutputRedactor.Redact(input, [value]);

        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        Assert.DoesNotContain("FAKE_MAGIC", actual, StringComparison.Ordinal);
        Assert.Contains("https://hhh.invalid/r", actual, StringComparison.Ordinal);
        Assert.Contains("https://hhh.invalid/s", actual, StringComparison.Ordinal);
    }
}
