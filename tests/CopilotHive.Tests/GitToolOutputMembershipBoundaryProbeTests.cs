using System.Diagnostics;
using System.Text;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>Iteration-15 actual-character-membership probes. All values are generated synthetic stand-ins.</summary>
public sealed class GitToolOutputMembershipBoundaryProbeTests(ITestOutputHelper output)
{
    private const int Budget = 1_000_000;
    private const string Other = "FAKE_OTHER_KEY";
    private static string Wrap(string body) => "file.txt\nordinary " + Other + "\n" + body + "\nend-marker";
    private static string Expected(string body) => "file.txt\nordinary [redacted]\n" + body + "\nend-marker";
    private static string Url(string password, string host = "hhh.invalid", string path = "r") => $"https://{host.Split('.')[0]}:{password}@{host}/{path}";
    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));

    private string Run(string label, string body, bool reverse, params string[] values)
    {
        var inventory = values.Append(Other).ToArray();
        if (reverse) Array.Reverse(inventory);
        var input = Wrap(body);
        var timer = Stopwatch.StartNew();
        var actual = GitToolOutputRedactor.Redact(input, inventory);
        timer.Stop();
        // Observe, never assert a wall-clock deadline: correctness must not depend on host load.
        output.WriteLine($"MEMBERSHIP_RUNTIME {label}: {timer.Elapsed.TotalMilliseconds:F3} ms; inputChars={input.Length}");
        return actual;
    }

    private void NoInventoryControl(string body, string expected)
    {
        var timer = Stopwatch.StartNew();
        var actual = GitToolOutputRedactor.Redact(body);
        timer.Stop();
        output.WriteLine($"MEMBERSHIP_RUNTIME no-inventory-control: {timer.Elapsed.TotalMilliseconds:F3} ms; inputChars={body.Length}");
        Assert.Equal(expected, actual);
    }

    // First character is unique: avoid quadratic repeated-prefix fixture work unrelated to membership.
    private static string Huge(int length, bool containsHost) => "F" + new string('A', length - (containsHost ? 2 : 1)) + (containsHost ? "h" : "");

    public static TheoryData<int, bool, bool> BoundaryCases
    {
        get
        {
            var rows = new TheoryData<int, bool, bool>();
            foreach (var length in new[] { Budget - 1, Budget, Budget + 1 })
            foreach (var contains in new[] { false, true })
            foreach (var reverse in new[] { false, true }) rows.Add(length, contains, reverse);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void RawMembership_JustBelowAtAndAboveBudget_IsExactInBothDirections(int length, bool contains, bool reverse)
    {
        var value = Huge(length, contains);
        Assert.Equal(length, value.Length);
        Assert.Equal(contains, value.Contains('h'));
        var body = Url(value);
        // Actual equality permits fail-closed placement coverage; absence forbids any widening.
        var expectedHost = contains ? "[redacted].invalid" : "hhh.invalid";
        Assert.Equal(Expected($"https://{expectedHost}/r"), Run($"raw/{length}/{contains}/{reverse}", body, reverse, value));
        NoInventoryControl(body, "https://hhh.invalid/r");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugeAbsentAlphabet_PreservesEveryHostAndPathCharacter(bool reverse)
    {
        var value = Huge(Budget + 13, false);
        const string host = "hpq.xyz";
        const string path = "uv/w";
        foreach (var c in host + "/" + path) Assert.DoesNotContain(c, value);
        var body = Url(value, host, path);
        Assert.Equal(Expected($"https://{host}/{path}"), Run($"absent-alphabet/{reverse}", body, reverse, value));
        NoInventoryControl(body, $"https://{host}/{path}");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DisjointHugeValues_MembershipBelongsOnlyToItsOwnExtent(bool firstContains, bool reverse)
    {
        var first = Huge(Budget + 13, firstContains);
        var second = "Z" + new string('B', Budget + 12);
        Assert.Empty(first.ToHashSet().Intersect(second.ToHashSet()));
        Assert.Equal(firstContains, first.Contains('h'));
        Assert.DoesNotContain('h', second);
        var body = Url(first, path: "one") + " " + Url(second, path: "two");
        var firstHost = firstContains ? "[redacted].invalid" : "hhh.invalid";
        Assert.Equal(Expected($"https://{firstHost}/one https://hhh.invalid/two"), Run($"disjoint/{firstContains}/{reverse}", body, reverse, first, second));
        NoInventoryControl(body, "https://hhh.invalid/one https://hhh.invalid/two");
    }

    [Theory]
    [InlineData("near-start", false)]
    [InlineData("near-start", true)]
    [InlineData("middle", false)]
    [InlineData("middle", true)]
    [InlineData("last", false)]
    [InlineData("last", true)]
    public void EqualCharacterAtDifferentHugeValuePositions_PlacementStillFailsClosed(string position, bool reverse)
    {
        const int length = Budget + 13;
        var index = position switch
        {
            "near-start" => 1,
            "middle" => length / 2,
            "last" => length - 1,
            _ => throw new InvalidOperationException("Unknown position"),
        };
        var chars = Huge(length, false).ToCharArray();
        chars[index] = 'h';
        var value = new string(chars);
        var body = Url(value);
        Assert.Equal(Expected("https://[redacted].invalid/r"), Run($"placement/{position}/{reverse}", body, reverse, value));
        NoInventoryControl(body, "https://hhh.invalid/r");
    }

    [Theory]
    [InlineData("full-raw-h", false)]
    [InlineData("full-raw-h", true)]
    [InlineData("mixed-raw-h", false)]
    [InlineData("mixed-raw-h", true)]
    [InlineData("absent-hex-zero", false)]
    [InlineData("absent-hex-zero", true)]
    public void PercentMembership_ActualSpellingLacksHostCharacter_MustPreserveHost(string spelling, bool reverse)
    {
        string value;
        string encoded;
        char hostCharacter;
        switch (spelling)
        {
            case "full-raw-h":
                value = Huge(Budget + 13, true);
                encoded = Percent(value);
                hostCharacter = 'h';
                Assert.Contains('h', value);
                break;
            case "mixed-raw-h":
                value = Huge(Budget + 13, true);
                encoded = value[..^1] + "%68";
                hostCharacter = 'h';
                Assert.Contains('h', value);
                break;
            case "absent-hex-zero":
                value = Huge(Budget + 13, false); // Only 'F'/'A': escapes %46/%41 contain no '0'.
                encoded = Percent(value);
                hostCharacter = '0';
                Assert.DoesNotContain('0', value);
                break;
            default:
                throw new InvalidOperationException("Unknown spelling");
        }
        Assert.DoesNotContain(hostCharacter, encoded);
        var host = new string(hostCharacter, 3) + ".invalid";
        var body = Url(encoded, host);
        NoInventoryControl(body, $"https://{host}/r");
        // Membership is of THIS concrete matched spelling, not all hypothetical mixed spellings.
        Assert.Equal(Expected($"https://{host}/r"), Run($"percent-preserve/{spelling}/{reverse}", body, reverse, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PercentMembership_ActualSpellingContainsEqualCharacter_StillCovers(bool reverse)
    {
        var value = Huge(Budget + 13, true);
        var encoded = "F%41" + value[2..]; // Escape an 'A', but retain the actual trailing literal 'h'.
        Assert.Contains('h', encoded);
        var body = Url(encoded);
        Assert.Equal(Expected("https://[redacted].invalid/r"), Run($"percent-cover/{reverse}", body, reverse, value));
        NoInventoryControl(body, "https://hhh.invalid/r");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Base64Membership_UsesTheActualSpellingInBothDirections(bool spellingContains, bool reverse)
    {
        var value = spellingContains ? "F" + new string('H', Budget + 12) : Huge(Budget + 13, true);
        var encoded = B64(value);
        Assert.Equal(spellingContains, encoded.Contains('h'));
        Assert.Equal(!spellingContains, value.Contains('h'));
        var body = Url(encoded);
        var host = spellingContains ? "[redacted].invalid" : "hhh.invalid";
        Assert.Equal(Expected($"https://{host}/r"), Run($"base64/{spellingContains}/{reverse}", body, reverse, value));
        NoInventoryControl(body, "https://hhh.invalid/r");
    }
}
