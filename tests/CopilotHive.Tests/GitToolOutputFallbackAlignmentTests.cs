using System.Text;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>Independent iteration-14 fallback/boundary probes, with synthetic inventory values only.</summary>
public sealed class GitToolOutputFallbackAlignmentTests
{
    private const string Other = "FAKE_OTHER_KEY";
    private const string Password = "FAKE_PASSWORD";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private static string Wrap(string body) => "file.txt\nordinary " + Other + "\n" + body + "\nend-marker";
    private static string Expected(string body) => "file.txt\nordinary [redacted]\n" + body + "\nend-marker";
    private static string[] Order(bool reverse, string value) => reverse ? [Other, value] : [value, Other];
    private static string Url(string user, string host, string path, string password = Password) => $"https://{user}:{password}@{host}/{path}";
    private static string AlphaPath(int length) => string.Concat(Enumerable.Repeat(Alphabet, length / Alphabet.Length + 1))[..length];

    private static void AssertSafeControls(string user, string host, string path, bool reverse)
    {
        var body = Url(user, host, path);
        var safe = $"https://{host}/{path}";
        // No lower-case host letter (or the chosen numeric initial) occurs in this deleted password.
        Assert.Equal(Expected(safe), GitToolOutputRedactor.Redact(Wrap(body), Order(reverse, Password)));
        Assert.Equal(Expected(safe), GitToolOutputRedactor.Redact(Wrap(body), Order(reverse, "FAKE_UNUSED_KEY")));
        Assert.Equal(safe, GitToolOutputRedactor.Redact(body));
    }

    public static TheoryData<int, bool> AlphabetCases
    {
        get
        {
            var data = new TheoryData<int, bool>();
            foreach (var length in new[] { 94, 191, 1024, 4093 })
            foreach (var reverse in new[] { false, true }) data.Add(length, reverse);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AlphabetCases))]
    public void AlphabetRegions_KnownHostExtentIsCovered(int length, bool reverse)
    {
        var path = AlphaPath(length);
        Assert.True(Alphabet.All(path.Contains), "Fixture must exhaust the single-character sentinel alphabet.");
        Assert.Equal(Expected($"https://[redacted]/{path}"), GitToolOutputRedactor.Redact(Wrap(Url("bender", "budget.invalid", path)), Order(reverse, Password + "@budget.invalid")));
    }

    [Theory]
    [MemberData(nameof(AlphabetCases))]
    public void AlphabetRegions_SafeRetainedHostAndNoInventoryArePreserved(int length, bool reverse) =>
        AssertSafeControls("bender", "budget.invalid", AlphaPath(length), reverse);

    [Theory]
    [MemberData(nameof(AlphabetCases))]
    public void AlphabetRegions_ResolvableEqualPasswordCharacter_KeepsTheOrdinaryHost(int length, bool reverse)
    {
        var path = AlphaPath(length);
        var body = Url("bender", "budget.invalid", path, "FAKE_bASS");
        // Unlike the no-sentinel trade-off, an absent PAIR is available here, so probing resolves
        // that this equal 'b' belongs to deleted userinfo and the retained host must stay exact.
        Assert.Equal(Expected("https://budget.invalid/" + path), GitToolOutputRedactor.Redact(Wrap(body), Order(reverse, "FAKE_bASS")));
        Assert.Equal("https://budget.invalid/" + path, GitToolOutputRedactor.Redact(body));
    }

    // Independent fixture accounting only, not a redaction oracle. Greedy subsequence bounds
    // identify the candidate count and scan work implied by a frozen GitUrlRedactor rewrite.
    private static (int Candidates, long Scan) AlignmentWork(string original)
    {
        var rewritten = GitUrlRedactor.Redact(original)!;
        var left = new int[rewritten.Length];
        var right = new int[rewritten.Length];
        var at = 0;
        for (var j = 0; j < rewritten.Length; j++)
        {
            while (original[at] != rewritten[j]) at++;
            left[j] = at++;
        }
        at = original.Length - 1;
        for (var j = rewritten.Length - 1; j >= 0; j--)
        {
            while (original[at] != rewritten[j]) at--;
            right[j] = at--;
        }
        var positions = new HashSet<int>();
        long scan = 0;
        for (var j = 0; j < rewritten.Length; j++)
        {
            if (left[j] == right[j]) continue;
            scan += right[j] - left[j] + 1;
            for (var p = left[j]; p <= right[j]; p++)
                if (original[p] == rewritten[j] && char.IsAsciiLetterOrDigit(original[p])) positions.Add(p);
        }
        return (positions.Count, scan);
    }

    public static TheoryData<int, bool> ProbeCases
    {
        get
        {
            var data = new TheoryData<int, bool>();
            foreach (var count in new[] { 4095, 4096, 4097 })
            foreach (var reverse in new[] { false, true }) data.Add(count, reverse);
            return data;
        }
    }

    private static string ProbeUser(int candidates) => new('8', candidates - 1);
    private static void CheckProbeFixture(int candidates)
    {
        var work = AlignmentWork(Wrap(Url(ProbeUser(candidates), "8node.invalid", "r")));
        Assert.Equal(candidates, work.Candidates);
        Assert.True(work.Scan < 200_000, "This fixture must isolate the probe-count budget.");
    }

    [Theory]
    [MemberData(nameof(ProbeCases))]
    public void ProbeCountBoundary_KnownHostIsCoveredBelowAtAndAbove4096(int candidates, bool reverse)
    {
        CheckProbeFixture(candidates);
        Assert.Equal(Expected("https://[redacted]/r"), GitToolOutputRedactor.Redact(Wrap(Url(ProbeUser(candidates), "8node.invalid", "r")), Order(reverse, Password + "@8node.invalid")));
    }

    [Theory]
    [MemberData(nameof(ProbeCases))]
    public void ProbeCountBoundary_SafeHostIsPreservedBelowAtAndAbove4096(int candidates, bool reverse)
    {
        CheckProbeFixture(candidates);
        AssertSafeControls(ProbeUser(candidates), "8node.invalid", "r", reverse);
    }

    public static TheoryData<int, bool> ScanCases
    {
        get
        {
            var data = new TheoryData<int, bool>();
            foreach (var work in new[] { 199_999, 200_000, 200_001 })
            foreach (var reverse in new[] { false, true }) data.Add(work, reverse);
            return data;
        }
    }

    private static string ScanUser(int target)
    {
        const string host = "jj.invalid";
        var overhead = AlignmentWork(Wrap(Url("jj", host, "r"))).Scan;
        var user = "j" + new string('A', checked(target - (int)overhead)) + "j";
        var work = AlignmentWork(Wrap(Url(user, host, "r")));
        Assert.Equal((long)target, work.Scan);
        Assert.Equal(4, work.Candidates); // Well below 4096: this isolates MaxScan, not MaxProbes.
        return user;
    }

    [Theory]
    [MemberData(nameof(ScanCases))]
    public void ScanPositionBoundary_KnownHostIsCoveredBelowAtAndAbove200k(int work, bool reverse)
    {
        var user = ScanUser(work);
        Assert.Equal(Expected("https://[redacted]/r"), GitToolOutputRedactor.Redact(Wrap(Url(user, "jj.invalid", "r")), Order(reverse, Password + "@jj.invalid")));
    }

    [Theory]
    [MemberData(nameof(ScanCases))]
    public void ScanPositionBoundary_SafeHostIsPreservedBelowAtAndAbove200k(int work, bool reverse) =>
        AssertSafeControls(ScanUser(work), "jj.invalid", "r", reverse);

    public static TheoryData<char, int, bool> HostBoundaryCases
    {
        get
        {
            var data = new TheoryData<char, int, bool>();
            foreach (var character in new[] { 'b', '8', 'T' })
            foreach (var extent in new[] { 1, 4, 8 })
            foreach (var reverse in new[] { false, true }) data.Add(character, extent, reverse);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HostBoundaryCases))]
    public void RepeatedCharacters_KnownExtentEndsAtDifferentHostPositions_NoByteEscapes(char character, int extent, bool reverse)
    {
        var host = character + "node.invalid";
        var value = Password + "@" + host[..extent];
        Assert.Equal(Expected("https://[redacted]" + host[extent..] + "/r"), GitToolOutputRedactor.Redact(Wrap(Url(new string(character, 5002), host, "r")), Order(reverse, value)));
    }

    [Theory]
    [InlineData('b', false)]
    [InlineData('b', true)]
    [InlineData('8', false)]
    [InlineData('8', true)]
    [InlineData('T', false)]
    [InlineData('T', true)]
    public void RepeatedCharacters_SafeHostHasNoCoveringKnownCharacter(char character, bool reverse) =>
        AssertSafeControls(new string(character, 5002), character + "node.invalid", "r", reverse);

    private static string ExhaustedPairsPath()
    {
        // Deliberately non-minimal: concatenating every pair guarantees no two-character sentinel.
        var path = string.Concat(Alphabet.SelectMany(a => Alphabet.Select(b => string.Concat(a, b))));
        var pairs = Enumerable.Range(0, path.Length - 1).Select(i => path.Substring(i, 2)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(62 * 62, pairs.Count);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedPairs_OnlyCouldBeInsideCharactersAreCovered_OrdinarySuffixSurvives(bool reverse)
    {
        var path = ExhaustedPairsPath();
        AssertSafeControls("hunter", "host.invalid", path, reverse);
        // Accepted conservative direction: a password-only known extent CAN cover an equal 'h'
        // under unresolved alignment, so that one host character is erased, but no ordinary suffix.
        var input = Url("hunter", "host.invalid", path, "FAKE_hASS");
        Assert.Equal(Expected("https://[redacted]ost.invalid/" + path), GitToolOutputRedactor.Redact(Wrap(input), Order(reverse, "FAKE_hASS")));
    }

    public static TheoryData<string, bool> EncodedCases
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var form in new[] { "raw", "percent-upper", "percent-lower", "percent-mixed", "base64-padded", "base64-unpadded" })
            foreach (var reverse in new[] { false, true }) data.Add(form, reverse);
            return data;
        }
    }

    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));
    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    [Theory]
    [MemberData(nameof(EncodedCases))]
    public void EncodedUrlBearingValue_ExhaustedPairsRegion_HasWholeSingleLayerMatches(string form, bool reverse)
    {
        var value = Url("hunter", "host.invalid", ExhaustedPairsPath());
        while (value.Length % 3 == 0) value += "x";
        Assert.EndsWith("=", B64(value));
        var text = form switch
        {
            "raw" => value,
            "percent-upper" => Percent(value),
            "percent-lower" => Percent(value).ToLowerInvariant(),
            "percent-mixed" => Uri.EscapeDataString(value),
            "base64-padded" => B64(value),
            "base64-unpadded" => B64(value).TrimEnd('='),
            _ => throw new InvalidOperationException("Unknown encoding"),
        };
        Assert.Equal(Expected("[redacted]"), GitToolOutputRedactor.Redact(Wrap(text), Order(reverse, value)));
        var composed = B64(Percent(value));
        Assert.Equal(Expected(composed), GitToolOutputRedactor.Redact(Wrap(composed), Order(reverse, value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappingBudget_NoEqualKnownCharacter_MustPreserveTheRetainedHost(bool reverse)
    {
        // The large known value begins uniquely; no quadratic repeated-prefix fixture is needed.
        // None of its bytes equals lower-case 'h'. Exhaustion alone is not evidence that a known
        // extent could cover the retained host: the entire known value is deleted userinfo.
        var value = "FAKE_MAGIC" + new string('A', 1_000_001);
        Assert.DoesNotContain("h", value, StringComparison.Ordinal);
        var input = Url("hhh", "hhh.invalid", "r", value);
        Assert.Equal(Expected("https://hhh.invalid/r"), GitToolOutputRedactor.Redact(Wrap(input), Order(reverse, value)));
        Assert.Equal("https://hhh.invalid/r", GitToolOutputRedactor.Redact(input));
    }
}
