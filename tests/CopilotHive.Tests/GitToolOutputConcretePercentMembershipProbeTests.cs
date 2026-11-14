using System.Diagnostics;
using System.Text;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>Iteration-16 concrete-spelling membership probes. Only generated synthetic stand-ins.</summary>
public sealed class GitToolOutputConcretePercentMembershipProbeTests(ITestOutputHelper output)
{
    private const int MapBoundaryLength = 1_000_013;
    private const int ReadingBudget = 4_000_000;
    private const string Other = "FAKE_OTHER_KEY";
    private static string Wrap(string body) => "file.txt\nordinary " + Other + "\n" + body + "\nend-marker";
    private static string Expected(string body) => "file.txt\nordinary [redacted]\n" + body + "\nend-marker";
    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));
    private static string Url(char host, string spelling) => $"https://{new string(host, 3)}:{spelling}@{new string(host, 3)}.invalid/r";

    private string Run(string label, string body, string value, bool reverse)
    {
        string[] inventory = reverse ? [Other, value] : [value, Other];
        var input = Wrap(body);
        var timer = Stopwatch.StartNew();
        var actual = GitToolOutputRedactor.Redact(input, inventory);
        timer.Stop();
        output.WriteLine($"CONCRETE_PERCENT_RUNTIME {label}: {timer.Elapsed.TotalMilliseconds:F3} ms; inputChars={input.Length}");
        return actual;
    }

    private void NoInventory(string label, string body, string expected)
    {
        var timer = Stopwatch.StartNew();
        var actual = GitToolOutputRedactor.Redact(body);
        timer.Stop();
        output.WriteLine($"CONCRETE_PERCENT_RUNTIME control/{label}: {timer.Elapsed.TotalMilliseconds:F3} ms; inputChars={body.Length}");
        Assert.Equal(expected, actual);
    }

    private static string EscapeAt(string value, int index) => value[..index] + $"%{(byte)value[index]:X2}" + value[(index + 1)..];

    private void CheckLarge(string label, string value, string spelling, char host, bool contains, bool reverse)
    {
        Assert.Equal(contains, spelling.Contains(host));
        var body = Url(host, spelling);
        NoInventory(label, body, $"https://{new string(host, 3)}.invalid/r");
        var safeHost = contains ? "[redacted]" : new string(host, 3);
        Assert.Equal(Expected($"https://{safeHost}.invalid/r"), Run(label, body, value, reverse));
    }

    [Theory]
    [InlineData("escape-start-h", false)]
    [InlineData("escape-start-h", true)]
    [InlineData("escape-middle-h", false)]
    [InlineData("escape-middle-h", true)]
    [InlineData("escape-end-d", false)]
    [InlineData("escape-end-d", true)]
    [InlineData("hex-one-absent", false)]
    [InlineData("hex-one-absent", true)]
    [InlineData("hex-zero-absent-mixed", false)]
    [InlineData("hex-zero-absent-mixed", true)]
    [InlineData("case-exact-upper-b", false)]
    [InlineData("case-exact-upper-b", true)]
    public void ConcretePercent_AbsentCharacterAtSpellingBoundaries_PreservesHost(string shape, bool reverse)
    {
        string value;
        string spelling;
        char host;
        if (shape.StartsWith("escape-", StringComparison.Ordinal))
        {
            var index = shape switch
            {
                "escape-start-h" => 0,
                "escape-middle-h" => MapBoundaryLength / 2,
                "escape-end-d" => MapBoundaryLength - 1,
                _ => throw new InvalidOperationException("Unknown escape position"),
            };
            host = shape == "escape-end-d" ? 'd' : 'h';
            var chars = ("F" + new string('A', MapBoundaryLength - 1)).ToCharArray();
            chars[index] = host;
            value = new string(chars);
            spelling = EscapeAt(value, index);
            Assert.Contains(host, value);
        }
        else
        {
            value = "F" + new string('K', MapBoundaryLength - 1);
            host = shape switch
            {
                "hex-one-absent" => '1',
                "hex-zero-absent-mixed" => '0',
                "case-exact-upper-b" => 'b',
                _ => throw new InvalidOperationException("Unknown absent alphabet"),
            };
            spelling = shape == "hex-one-absent" ? Percent(value)
                : EscapeAt(value, shape == "hex-zero-absent-mixed" ? 0 : MapBoundaryLength / 2);
            Assert.DoesNotContain(host, value);
        }
        CheckLarge(shape + "/" + reverse, value, spelling, host, false, reverse);
    }

    [Theory]
    [InlineData("literal-h-start", false)]
    [InlineData("literal-h-start", true)]
    [InlineData("literal-h-middle", false)]
    [InlineData("literal-h-middle", true)]
    [InlineData("escaped-digit-one", false)]
    [InlineData("escaped-digit-one", true)]
    public void ConcretePercent_GenuineCharacterInActualSpelling_StillCovers(string shape, bool reverse)
    {
        var chars = ("F" + new string(shape == "escaped-digit-one" ? 'K' : 'A', MapBoundaryLength - 1)).ToCharArray();
        var position = shape == "literal-h-start" ? 1 : MapBoundaryLength / 2;
        chars[position] = shape == "escaped-digit-one" ? 'A' : 'h';
        var value = new string(chars);
        var spelling = EscapeAt(value, shape == "escaped-digit-one" ? position : MapBoundaryLength / 3);
        var host = shape == "escaped-digit-one" ? '1' : 'h';
        if (host == '1') Assert.DoesNotContain(host, value);
        CheckLarge(shape + "/" + reverse, value, spelling, host, true, reverse);
    }

    [Theory]
    [InlineData('0', false)]
    [InlineData('0', true)]
    [InlineData('2', false)]
    [InlineData('2', true)]
    [InlineData('5', false)]
    [InlineData('5', true)]
    public void ConcretePercent_MultipleGenuineReadings_OnlyTheirActualCharactersCanCover(char host, bool reverse)
    {
        var value = "F" + new string('A', MapBoundaryLength - 2) + "%";
        var spelling = value + "25";
        // Two genuine readings: the raw/literal-% prefix, and the longer escaped-% spelling.
        Assert.Equal(value, spelling[..^2]);
        Assert.Equal(value, Uri.UnescapeDataString(spelling));
        Assert.DoesNotContain('0', spelling);
        Assert.Contains('2', spelling);
        Assert.Contains('5', spelling);
        CheckLarge("two-readings/" + host + "/" + reverse, value, spelling, host, host != '0', reverse);
    }

    [Theory]
    [InlineData("start", false)]
    [InlineData("start", true)]
    [InlineData("middle", false)]
    [InlineData("middle", true)]
    [InlineData("end", false)]
    [InlineData("end", true)]
    public void ConcretePercent_SmallSpans_PositionCanStillBeResolvedExactly(string position, bool reverse)
    {
        const string value = "FAKE_hASS_KEY";
        var at = position switch
        {
            "start" => 0,
            "middle" => value.IndexOf('h'),
            "end" => value.Length - 1,
            _ => throw new InvalidOperationException("Unknown position"),
        };
        var body = Url('h', EscapeAt(value, at));
        NoInventory("small/" + position, body, "https://hhh.invalid/r");
        Assert.Equal(Expected("https://hhh.invalid/r"), Run("small/" + position + "/" + reverse, body, value, reverse));
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public void ConcretePercent_LineStructureAndUrlAlignment_StayIntact(string newline, bool reverse)
    {
        var value = "F" + new string('A', MapBoundaryLength - 2) + "h";
        var spelling = EscapeAt(value, value.Length - 1);
        Assert.DoesNotContain('h', spelling);
        var body = Url('h', spelling) + newline + "retained-marker" + newline + Url('h', spelling);
        var safe = "https://hhh.invalid/r" + newline + "retained-marker" + newline + "https://hhh.invalid/r";
        NoInventory("lines", body, safe);
        var actual = Run("lines/" + newline.Length + "/" + reverse, body, value, reverse);
        Assert.Equal(Expected(safe), actual);
        Assert.Equal(Wrap(body).Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
    }

    private void ReadingBudgetCase(bool containsHost, bool reverse)
    {
        var value = "F" + new string('A', ReadingBudget + 11) + (containsHost ? "h" : "A");
        Assert.True(value.Length > ReadingBudget, "Fixture must exceed the reading-step budget.");
        Assert.DoesNotContain('%', value);
        var spelling = EscapeAt(value, 0); // Exactly ONE escaped F, all later characters literal.
        Assert.StartsWith("%46", spelling);
        Assert.Equal(value[1..], spelling[3..]);
        Assert.Equal(value, Uri.UnescapeDataString(spelling));
        Assert.Equal(value.Length + 2, spelling.Length);
        Assert.Equal(containsHost, spelling.Contains('h'));
        var body = Url('h', spelling);
        var matchStart = body.IndexOf(spelling, StringComparison.Ordinal);
        var hostStart = body.IndexOf("@hhh.invalid", StringComparison.Ordinal) + 1;
        Assert.Equal(matchStart + spelling.Length + 1, hostStart);
        // No '%' in the raw value, and no '%' after the initial %46 in the spelling: there is
        // exactly one genuine reading, whose end is BEFORE the retained host. Budget exhaustion
        // cannot license inventing an 'h' in the preserve row; the mirror really holds one.
        Assert.DoesNotContain('%', spelling[3..]);
        NoInventory("reading-budget", body, "https://hhh.invalid/r");
        var safeHost = containsHost ? "[redacted]" : "hhh";
        Assert.Equal(Expected($"https://{safeHost}.invalid/r"), Run("reading-budget/" + containsHost + "/" + reverse, body, value, reverse));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PercentSpellingBudget_ImpossibleCharacterMustNotCoverTheHost(bool reverse) => ReadingBudgetCase(false, reverse);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcretePercent_ReadingBudget_GenuineEqualCharacterStillCovers(bool reverse) => ReadingBudgetCase(true, reverse);
}
