using System.Diagnostics;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>Iteration-18 completed-versus-dead reading probes. Generated synthetic values only.</summary>
public sealed class GitToolOutputCompletedReadingProbeTests(ITestOutputHelper output)
{
    private const int Budget = 4_000_000;
    private const string Other = "FAKE_OTHER_KEY";
    private sealed record Snapshot(int ValueIndex, int[] Current, int[] Pending);
    private sealed record Enumeration(int[] Completed, Snapshot? Exhausted, int DeadTransitions);

    // Small independent fixture oracle: enumerate entire suffix readings. A transition that dies
    // contributes no endpoint. The optional visit limit models exactly where exhaustion occurs;
    // it does not call production internals or use elapsed time as a correctness oracle.
    private static Enumeration Readings(string value, string spelling, int allowedVisits = int.MaxValue)
    {
        var current = new[] { 0 };
        var visits = 0;
        var dead = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var next = new HashSet<int>();
            foreach (var at in current)
            {
                if (++visits > allowedVisits)
                    return new Enumeration([], new Snapshot(index, current, next.Order().ToArray()), dead);
                var reached = false;
                if (at < spelling.Length && spelling[at] == value[index])
                {
                    next.Add(at + 1);
                    reached = true;
                }
                var escape = $"%{(byte)value[index]:X2}";
                if (at + 3 <= spelling.Length && spelling.AsSpan(at, 3).Equals(escape, StringComparison.OrdinalIgnoreCase))
                {
                    next.Add(at + 3);
                    reached = true;
                }
                if (!reached) dead++;
            }
            current = next.Order().ToArray();
        }
        return new Enumeration(current, null, dead);
    }

    private static (string Suffix, string Text, int End, int VisitsBeforeFinal) DeadShape(string shape) => shape switch
    {
        "short" => ("%2525", "%2525%32X", 5, 7),
        "longer-completion" => ("%252525", "%252525%32X", 7, 11),
        "sequential-branches" => ("%2525%2525", "%2525%2525%32X", 10, 15),
        _ => throw new InvalidOperationException("Unknown dead-reading shape"),
    };

    private static string Prefix(int length, bool genuine)
    {
        var prefix = "FAKE" + new string('a', length - 4);
        return genuine ? prefix[..100] + '3' + prefix[101..] : prefix;
    }

    private static string EscapeFirst(string prefix) => "%46" + prefix[1..];
    private static string Url(string spelling) => $"https://3:{spelling}@3.invalid/r";

    private string Run(string label, string body, string value, bool reverse, string noInventory, string expected)
    {
        var timer = Stopwatch.StartNew();
        var control = GitToolOutputRedactor.Redact(body);
        timer.Stop();
        output.WriteLine($"COMPLETED_READING_RUNTIME control/{label}: {timer.Elapsed.TotalMilliseconds:F3} ms; inputChars={body.Length}");
        Assert.Equal(noInventory, control);
        var input = "file.txt\nordinary " + Other + "\n" + body + "\nend-marker";
        string[] inventory = reverse ? [Other, value] : [value, Other];
        timer.Restart();
        var actual = GitToolOutputRedactor.Redact(input, inventory);
        timer.Stop();
        output.WriteLine($"COMPLETED_READING_RUNTIME {label}: {timer.Elapsed.TotalMilliseconds:F3} ms; inputChars={input.Length}; valueChars={value.Length}");
        Assert.Equal("file.txt\nordinary [redacted]\n" + expected + "\nend-marker", actual);
        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        return actual;
    }

    public static TheoryData<string, bool, bool, bool> DeadCases
    {
        get
        {
            var rows = new TheoryData<string, bool, bool, bool>();
            foreach (var shape in new[] { "short", "longer-completion", "sequential-branches" })
            foreach (var firstCompletionProcessed in new[] { false, true })
            foreach (var genuine in new[] { false, true })
            foreach (var reverse in new[] { false, true })
                rows.Add(shape, firstCompletionProcessed, genuine, reverse);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(DeadCases))]
    public void DeadReadingBoundary_OnlyCompletedCharactersDetermineCoverage(string shape, bool firstCompletionProcessed, bool genuine, bool reverse)
    {
        var (suffix, text, end, beforeFinal) = DeadShape(shape);
        var full = Readings(suffix, text);
        Assert.Equal(new[] { end }, full.Completed);
        Assert.True(full.DeadTransitions >= (shape == "sequential-branches" ? 2 : 1));
        var allowed = beforeFinal + (firstCompletionProcessed ? 1 : 0);
        var limited = Readings(suffix, text, allowed);
        var stopped = Assert.IsType<Snapshot>(limited.Exhausted);
        Assert.Equal(suffix.Length - 1, stopped.ValueIndex);
        Assert.Equal(firstCompletionProcessed ? new[] { end } : [], stopped.Pending);
        Assert.Contains(stopped.Current, p => p > end);
        Assert.DoesNotContain('3', text[..end]);
        Assert.Contains('3', text[..stopped.Current.Max()]); // Precisely the old fallback's false membership.
        var prefixLength = Budget - allowed;
        Assert.Equal(Budget, prefixLength + allowed);
        var prefix = Prefix(prefixLength, genuine);
        var value = prefix + suffix;
        Assert.Equal(genuine, (EscapeFirst(prefix) + text[..end]).Contains('3'));
        Run($"dead/{shape}/{firstCompletionProcessed}/{genuine}/{reverse}", Url(EscapeFirst(prefix) + text), value, reverse,
            "https://3.invalid/r", $"https://{(genuine ? "[redacted]" : "3")}.invalid/r");
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    [InlineData(3, false, false)]
    [InlineData(3, false, true)]
    [InlineData(3, true, false)]
    [InlineData(3, true, true)]
    public void MultipleCompletedReadings_AreControlsWithGenuineCharacterMirrors(int pairs, bool genuine, bool reverse)
    {
        var suffix = "%" + string.Concat(Enumerable.Repeat("25", pairs));
        var text = suffix + "25%32X";
        var full = Readings(suffix, text);
        Assert.Equal(new[] { suffix.Length, suffix.Length + 2 }, full.Completed);
        var allowed = 2 * suffix.Length - 2; // The first final-step completion has been processed.
        var stopped = Assert.IsType<Snapshot>(Readings(suffix, text, allowed).Exhausted);
        Assert.Equal(suffix.Length - 1, stopped.ValueIndex);
        Assert.Equal(new[] { suffix.Length }, stopped.Pending);
        Assert.True(stopped.Current.Max() < full.Completed.Max());
        Assert.DoesNotContain('3', text[..full.Completed.Max()]);
        // Unlike the negative dead-branch family, this is a multi-completion CONTROL: the old
        // partial-inclusive fallback agrees with the new one. The genuine mirror pins coverage.
        var prefix = Prefix(Budget - allowed, genuine);
        Run($"completed-control/{pairs}/{genuine}/{reverse}", Url(EscapeFirst(prefix) + text), prefix + suffix, reverse,
            "https://3.invalid/r", $"https://{(genuine ? "[redacted]" : "3")}.invalid/r");
    }

    [Theory]
    [InlineData("\n", false, false)]
    [InlineData("\n", false, true)]
    [InlineData("\n", true, false)]
    [InlineData("\n", true, true)]
    [InlineData("\r\n", false, false)]
    [InlineData("\r\n", false, true)]
    [InlineData("\r\n", true, false)]
    [InlineData("\r\n", true, true)]
    public void DeadPartialLines_ExactStructureAndMembershipSurvive(string newline, bool genuine, bool reverse)
    {
        var (suffix, text, end, beforeFinal) = DeadShape("short");
        Assert.Equal(new[] { end }, Readings(suffix, text).Completed);
        var prefix = Prefix(Budget - beforeFinal - 1, genuine);
        var body = Url(EscapeFirst(prefix) + text) + newline + "retained-marker" + newline + "https://ordinary.invalid/other";
        var noInventory = "https://3.invalid/r" + newline + "retained-marker" + newline + "https://ordinary.invalid/other";
        var expected = $"https://{(genuine ? "[redacted]" : "3")}.invalid/r" + newline + "retained-marker" + newline + "https://ordinary.invalid/other";
        Run($"lines/{newline.Length}/{genuine}/{reverse}", body, prefix + suffix, reverse, noInventory, expected);
    }
}
