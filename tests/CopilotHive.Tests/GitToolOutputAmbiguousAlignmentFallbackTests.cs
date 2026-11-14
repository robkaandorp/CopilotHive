using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Pins the FAIL-CLOSED fallback of the URL-rewrite alignment. When the redactor cannot decide which
/// of several equal characters the frozen <see cref="GitUrlRedactor"/> deleted (a region that spells
/// out every alphanumeric, a probe/scan budget overrun, a region no sentinel exists for), it must
/// fail TOWARD covering a known value, never toward dropping one of its bytes. The coverage is driven
/// by the known-value span: an ordinary retained host is NOT redacted merely because it is ambiguous.
/// Every family keeps an ordinary inventory line (so an inventory failure cannot hide) and a
/// no-inventory host-preservation control. Synthetic values only.
/// </summary>
public sealed class GitToolOutputAmbiguousAlignmentFallbackTests
{
    private const string Other = "FAKE_OTHER_KEY";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static string Wrap(string body) => "file.txt\n" + body + "\nend-marker";

    private static string WithOrdinary(string body) => Wrap("ordinary " + Other + "\n" + body);

    private static string ExpectedLines(string body) => Wrap("ordinary [redacted]\n" + body);

    private static string[] Inventory(bool reverse, params string[] values)
    {
        var all = values.Append(Other).ToArray();
        if (reverse)
            Array.Reverse(all);
        return all;
    }

    private static string Body(string user, string host, string path) => $"https://{user}:FAKE_PASSWORD@{host}/{path}";

    /// <summary>The known value spans the password AND the host: the whole retained host must go.</summary>
    private static void AssertHostExtentIsCovered(string user, string host, string path, bool reverse) =>
        Assert.Equal(
            ExpectedLines($"https://[redacted]/{path}"),
            GitToolOutputRedactor.Redact(WithOrdinary(Body(user, host, path)), Inventory(reverse, $"FAKE_PASSWORD@{host}")));

    /// <summary>The known value covers only the deleted password: the retained host is untouched.</summary>
    private static void AssertRetainedHostIsPreserved(string user, string host, string path, bool reverse) =>
        Assert.Equal(
            ExpectedLines($"https://{host}/{path}"),
            GitToolOutputRedactor.Redact(WithOrdinary(Body(user, host, path)), Inventory(reverse, "FAKE_PASSWORD")));

    /// <summary>No inventory at all: the frozen grammar alone removes userinfo and keeps host and path.</summary>
    private static void AssertNoInventoryKeepsHostAndPath(string user, string host, string path) =>
        Assert.Equal($"https://{host}/{path}", GitToolOutputRedactor.Redact(Body(user, host, path)));

    // ── The two reviewer-pinned failing shapes, as my own rows ───────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullAlphanumericRegion_KnownHostExtent_LeaksNoCharacter(bool reverse) =>
        AssertHostExtentIsCovered("hunter", "host.invalid", Alphabet, reverse);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiveThousandAmbiguousUserCharacters_KnownHostExtent_LeaksNoCharacter(bool reverse) =>
        AssertHostExtentIsCovered(new string('h', 5000), "host.invalid", "r", reverse);

    [Fact]
    public void BothShapes_NoInventory_KeepHostAndPath()
    {
        AssertNoInventoryKeepsHostAndPath("hunter", "host.invalid", Alphabet);
        AssertNoInventoryKeepsHostAndPath(new string('h', 5000), "host.invalid", "r");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothShapes_RetainedHostBesideAKnownPassword_IsPreserved(bool reverse)
    {
        AssertRetainedHostIsPreserved("hunter", "host.invalid", Alphabet, reverse);
        AssertRetainedHostIsPreserved(new string('h', 5000), "host.invalid", "r", reverse);
    }

    // ── Probe-count budget boundary (MaxProbes = 4096 ambiguous candidates: n user chars + 1 host char) ──

    [Theory]
    [InlineData(4094, false)]
    [InlineData(4095, false)] // 4096 candidates: probing still runs, exact
    [InlineData(4096, false)] // 4097 candidates: over the limit, fail-closed fallback
    [InlineData(4097, false)]
    [InlineData(4094, true)]
    [InlineData(4095, true)]
    [InlineData(4096, true)]
    [InlineData(4097, true)]
    public void ProbeBudgetBoundary_KnownHostExtent_IsCoveredOnBothSidesOfTheLimit(int userLength, bool reverse) =>
        AssertHostExtentIsCovered(new string('h', userLength), "host.invalid", "r", reverse);

    [Theory]
    [InlineData(4094)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    public void ProbeBudgetBoundary_RetainedHostAndNoInventory_AreUntouchedOnBothSides(int userLength)
    {
        var user = new string('h', userLength);
        AssertRetainedHostIsPreserved(user, "host.invalid", "r", reverse: false);
        AssertRetainedHostIsPreserved(user, "host.invalid", "r", reverse: true);
        AssertNoInventoryKeepsHostAndPath(user, "host.invalid", "r");
    }

    // ── Scan-position budget (MaxScan = 200 000): a userinfo that repeats the host, so EVERY host
    //    character is ambiguous across a very wide range ───────────────────────────────────────────

    private static string RepeatedHostUser(int repetitions) => string.Concat(Enumerable.Repeat("host.invalid", repetitions));

    [Theory]
    [InlineData(300, false)] // ~3 300 candidates, well inside both budgets: exact probing
    [InlineData(1000, false)] // candidate budget exceeded
    [InlineData(2000, false)] // scan budget exceeded (~288 000 positions)
    [InlineData(300, true)]
    [InlineData(1000, true)]
    [InlineData(2000, true)]
    public void RepeatedHostUserinfo_KnownHostExtent_IsCoveredWhicheverBudgetIsExceeded(int repetitions, bool reverse) =>
        AssertHostExtentIsCovered(RepeatedHostUser(repetitions), "host.invalid", "r", reverse);

    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    [InlineData(2000)]
    public void RepeatedHostUserinfo_RetainedHostAndNoInventory_AreUntouched(int repetitions)
    {
        var user = RepeatedHostUser(repetitions);
        AssertRetainedHostIsPreserved(user, "host.invalid", "r", reverse: false);
        AssertRetainedHostIsPreserved(user, "host.invalid", "r", reverse: true);
        AssertNoInventoryKeepsHostAndPath(user, "host.invalid", "r");
    }

    // ── All-alphanumeric paths at several lengths (a path that spells the alphabet leaves no single-
    //    character sentinel) ─────────────────────────────────────────────────────────────────────

    private static string AlphabetPath(int length) =>
        string.Concat(Enumerable.Repeat(Alphabet, (length / Alphabet.Length) + 1))[..length];

    [Theory]
    [InlineData(62, false)]
    [InlineData(63, false)]
    [InlineData(124, false)]
    [InlineData(500, false)]
    [InlineData(3000, false)]
    [InlineData(62, true)]
    [InlineData(63, true)]
    [InlineData(124, true)]
    [InlineData(500, true)]
    [InlineData(3000, true)]
    public void AllAlphanumericPaths_KnownHostExtent_IsCoveredAtEveryLength(int pathLength, bool reverse)
    {
        var path = AlphabetPath(pathLength);
        Assert.True(Alphabet.All(path.Contains), "Non-vacuous: the path must contain every alphanumeric.");
        AssertHostExtentIsCovered("hunter", "host.invalid", path, reverse);
    }

    [Theory]
    [InlineData(62)]
    [InlineData(500)]
    [InlineData(3000)]
    public void AllAlphanumericPaths_RetainedHostAndNoInventory_AreUntouched(int pathLength)
    {
        var path = AlphabetPath(pathLength);
        AssertRetainedHostIsPreserved("hunter", "host.invalid", path, reverse: false);
        AssertRetainedHostIsPreserved("hunter", "host.invalid", path, reverse: true);
        AssertNoInventoryKeepsHostAndPath("hunter", "host.invalid", path);
    }

    // ── Repeated characters other than 'h' (the host's first character equals the userinfo's) ──

    [Theory]
    [InlineData('z', false)]
    [InlineData('7', false)]
    [InlineData('Q', false)]
    [InlineData('z', true)]
    [InlineData('7', true)]
    [InlineData('Q', true)]
    public void RepeatedNonHCharacters_KnownHostExtent_IsCovered(char repeated, bool reverse)
    {
        var host = repeated + "ost.invalid";
        AssertHostExtentIsCovered(new string(repeated, 5000), host, "r", reverse);
    }

    [Theory]
    [InlineData('z')]
    [InlineData('7')]
    [InlineData('Q')]
    public void RepeatedNonHCharacters_RetainedHostAndNoInventory_AreUntouched(char repeated)
    {
        var host = repeated + "ost.invalid";
        var user = new string(repeated, 5000);
        AssertRetainedHostIsPreserved(user, host, "r", reverse: false);
        AssertRetainedHostIsPreserved(user, host, "r", reverse: true);
        AssertNoInventoryKeepsHostAndPath(user, host, "r");
    }

    // ── The retained-host vs known-value pair, resolved EXACTLY when a sentinel exists ─────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownPasswordContainingTheHostsFirstCharacter_PreservesTheRetainedHost_WhenProbingCanResolveIt(bool reverse)
    {
        // The known value lives only in the DELETED password, but it holds an 'h' like the retained
        // host. The path spells the whole alphabet, so no single-character sentinel exists; a
        // two-character sentinel still resolves the ambiguity exactly, so the host's 'h' is KEPT.
        // (With the sentinel source exhausted the fallback would cover that 'h' — see below.)
        Assert.Equal(
            ExpectedLines($"https://host.invalid/{Alphabet}"),
            GitToolOutputRedactor.Redact(
                WithOrdinary($"https://hunter:FAKE_hASS@host.invalid/{Alphabet}"),
                Inventory(reverse, "FAKE_hASS")));
    }

    // ── A region NO sentinel exists for (a de Bruijn path holds every two-character pair) ─────

    /// <summary>A linear string over <see cref="Alphabet"/> that contains every ordered pair of its characters.</summary>
    private static string DeBruijnPath()
    {
        const int order = 2;
        var k = Alphabet.Length;
        var a = new int[k * order];
        var sequence = new List<int>();

        void Generate(int t, int p)
        {
            if (t > order)
            {
                if (order % p == 0)
                {
                    for (var i = 1; i <= p; i++)
                        sequence.Add(a[i]);
                }

                return;
            }

            a[t] = a[t - p];
            Generate(t + 1, p);
            for (var j = a[t - p] + 1; j < k; j++)
            {
                a[t] = j;
                Generate(t + 1, t);
            }
        }

        Generate(1, 1);
        sequence.Add(sequence[0]); // close the cycle so the last pair is present in a linear string
        return new string(sequence.Select(i => Alphabet[i]).ToArray());
    }

    private static string ExhaustedSentinelPath()
    {
        var path = DeBruijnPath();
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < path.Length; i++)
            pairs.Add(path.Substring(i, 2));

        Assert.Equal(Alphabet.Length * Alphabet.Length, pairs.Count); // non-vacuity: no pair is left to use as a sentinel
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SentinelExhaustedRegion_KnownHostExtent_LeaksNoCharacter(bool reverse) =>
        AssertHostExtentIsCovered("hunter", "host.invalid", ExhaustedSentinelPath(), reverse);

    [Fact]
    public void SentinelExhaustedRegion_RetainedHostAndNoInventory_AreUntouched()
    {
        var path = ExhaustedSentinelPath();
        AssertRetainedHostIsPreserved("hunter", "host.invalid", path, reverse: false);
        AssertRetainedHostIsPreserved("hunter", "host.invalid", path, reverse: true);
        AssertNoInventoryKeepsHostAndPath("hunter", "host.invalid", path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SentinelExhaustedRegion_KnownPasswordWithTheHostsFirstCharacter_NeverLeaksTheKnownValue(bool reverse)
    {
        // DOCUMENTED FAIL-CLOSED TRADE-OFF. Nothing can decide whether the host's 'h' or the one in the
        // known password was kept, so the fallback may cover the host's first character too. What is
        // pinned is the safety direction: the known value never survives and the rest of the host
        // and path are never damaged — not that the host's first character is preserved.
        var path = ExhaustedSentinelPath();
        var actual = GitToolOutputRedactor.Redact(
            WithOrdinary($"https://hunter:FAKE_hASS@host.invalid/{path}"),
            Inventory(reverse, "FAKE_hASS"));

        Assert.DoesNotContain("FAKE_hASS", actual, StringComparison.Ordinal);
        Assert.Contains("ost.invalid/" + path, actual, StringComparison.Ordinal);
        Assert.StartsWith("file.txt\nordinary [redacted]\nhttps://", actual, StringComparison.Ordinal);
        Assert.EndsWith("\nend-marker", actual, StringComparison.Ordinal);
    }

    // ── Invariants the fail-closed mapping must keep ──────────────────────────────────────────

    [Fact]
    public void FallbackRows_PreserveTheLineStructure()
    {
        var input = "a\r\n" + Body(new string('h', 5000), "host.invalid", "r") + "\nb\r\n" + Body("hunter", "host.invalid", Alphabet) + "\nz";
        var actual = GitToolOutputRedactor.Redact(input, ["FAKE_PASSWORD@host.invalid"]);

        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        Assert.DoesNotContain("FAKE_PASSWORD", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoCredentialUrlsInOneText_AreEachCoveredIndependently()
    {
        // The ambiguity of one URL must not leak into, or be resolved by, its neighbour.
        var first = Body(new string('h', 5000), "host.invalid", "one");
        var second = Body("hunter", "host.invalid", "two");
        var actual = GitToolOutputRedactor.Redact($"{first} {second}", ["FAKE_PASSWORD@host.invalid"]);

        Assert.Equal("https://[redacted]/one https://[redacted]/two", actual);
    }
}
