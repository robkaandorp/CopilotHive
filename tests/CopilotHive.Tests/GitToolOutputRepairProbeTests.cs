using System.Text;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>Independent probes of the iteration-7 parser repairs; only synthetic non-secret sentinels.</summary>
public sealed class GitToolOutputRepairProbeTests
{
    private const string Known = "FAKE_KNOWN_KEY";
    private const string Unknown = "FAKE_NEW_VALUE";

    private static void AssertNoSentinels(string actual, params string[] sentinels)
    {
        Assert.StartsWith("file.txt\n", actual);
        Assert.EndsWith("\nend-marker", actual);
        foreach (var sentinel in sentinels)
            Assert.False(actual.Contains(sentinel, StringComparison.Ordinal), "Synthetic credential survived repair probe.");
    }

    [Theory]
    [InlineData("credential: password: token: FAKE_NEW_VALUE")]
    [InlineData("credential: password: token: secret: FAKE_NEW_VALUE")]
    [InlineData("credential = password: token = FAKE_NEW_VALUE")]
    [InlineData("credential: password = token: FAKE_NEW_VALUE")]
    [InlineData("credential=password=token=FAKE_NEW_VALUE")]
    [InlineData("credential: \"password: token: FAKE_NEW_VALUE\"")]
    [InlineData("credential = password: 'token = FAKE_NEW_VALUE'")]
    [InlineData("credential: password = token: \"FAKE_NEW_VALUE\"")]
    [InlineData("credential: password: token: 'FAKE_NEW_VALUE'")]
    public void NestedAssignments_DeeperMixedOperatorsAndQuotes_RedactTheActualValue(string text)
    {
        var actual = GitToolOutputRedactor.Redact("file.txt\n" + text + "\nend-marker");
        AssertNoSentinels(actual, Unknown);
        Assert.Contains("[redacted]", actual);
    }

    [Theory]
    [InlineData("FAKE_KNOWN_KEY credential: password: token: FAKE_NEW_VALUE")]
    [InlineData("FAKE_KNOWN_KEYcredential: password: token: FAKE_NEW_VALUE")]
    [InlineData("credential: FAKE_KNOWN_KEY password: token: FAKE_NEW_VALUE")]
    [InlineData("credential: 'FAKE_KNOWN_KEY' password = token: FAKE_NEW_VALUE")]
    [InlineData("credential: password: FAKE_KNOWN_KEY token: FAKE_NEW_VALUE")]
    public void NestedAssignments_AfterInventoryRegion_StillFindLaterKeywords(string text)
    {
        var actual = GitToolOutputRedactor.Redact("file.txt\n" + text + "\nend-marker", [Known]);
        AssertNoSentinels(actual, Known, Unknown);
        Assert.Contains("[redacted]", actual);
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Bearer")]
    [InlineData("bAsIc")]
    public void NestedHeaders_ManySegmentsMixedIndentationAndInventory_RemainRedacted(string innerScheme)
    {
        var input = "file.txt\nAuthorization: Bearer FAKE_ONE \t  \n"
            + "\tFAKE_KNOWN_KEY\t  \n"
            + $"  Authorization : {innerScheme} FAKE_TWO== \t\t \n"
            + " \tFAKE_THREE==  \t \n"
            + "\t\tFAKE_FOUR==\nend-marker";
        var actual = GitToolOutputRedactor.Redact(input, [Known]);
        AssertNoSentinels(actual, "FAKE_ONE", Known, "FAKE_TWO", "FAKE_THREE", "FAKE_FOUR");
        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        Assert.Contains($"  Authorization : {innerScheme} [redacted] \t\t \n", actual);
        Assert.Contains("\t[redacted]\t  \n", actual);
        Assert.Contains(" \t[redacted]  \t \n", actual);
    }

    [Fact]
    public void NestedProxyHeader_ContinuationMustNotConsumeTheHeaderName()
    {
        const string input = "file.txt\nAuthorization: Bearer FAKE_ONE\n\tProxy-Authorization: Basic FAKE_TWO==\n  FAKE_THREE==\nend-marker";
        var actual = GitToolOutputRedactor.Redact(input);
        // Proxy-Authorization is a documented supported header in the reused parser, not a new scheme.
        AssertNoSentinels(actual, "FAKE_ONE", "FAKE_TWO", "FAKE_THREE");
        Assert.Contains("Proxy-Authorization: Basic [redacted]", actual);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void HeaderInventory_EverySegmentPositionAndAdjacentTokenText_AreHandled(int position, bool adjacent)
    {
        string[] values = ["FAKE_ONE", "FAKE_TWO", "FAKE_THREE", "FAKE_FOUR"];
        values[position] = adjacent ? "FAKE_PREFIX" + Known + "FAKE_SUFFIX" : Known;
        var input = "file.txt\nAuthorization: Basic " + values[0]
            + "\t  \n\t" + values[1] + "  \t\n  " + values[2] + "\t \n \t" + values[3] + "\nend-marker";
        var actual = GitToolOutputRedactor.Redact(input, [Known]);
        AssertNoSentinels(actual, Known, "FAKE_ONE", "FAKE_TWO", "FAKE_THREE", "FAKE_FOUR", "FAKE_PREFIX", "FAKE_SUFFIX");
        Assert.Equal("file.txt\nAuthorization: Basic [redacted]\t  \n\t[redacted]  \t\n  [redacted]\t \n \t[redacted]\nend-marker", actual);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t\t")]
    [InlineData(" \t \t  ")]
    public void TrailingWhitespace_AfterEveryNestedHeaderAndSegment_IsPreserved(string whitespace)
    {
        var input = $"file.txt\nAuthorization: Bearer FAKE_ONE{whitespace}\n"
            + $"  Authorization: Basic FAKE_TWO=={whitespace}\n"
            + $"\tFAKE_THREE=={whitespace}\n \tFAKE_FOUR==\nend-marker";
        var actual = GitToolOutputRedactor.Redact(input);
        AssertNoSentinels(actual, "FAKE_ONE", "FAKE_TWO", "FAKE_THREE", "FAKE_FOUR");
        Assert.Equal($"file.txt\nAuthorization: Bearer [redacted]{whitespace}\n"
            + $"  Authorization: Basic [redacted]{whitespace}\n"
            + $"\t[redacted]{whitespace}\n \t[redacted]\nend-marker", actual);
    }

    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));

    [Theory]
    [InlineData("%FAKE_key")]
    [InlineData("FAKE%key")]
    [InlineData("FAKE_key%")]
    [InlineData("FAKE%25key")]
    [InlineData("FAKE%key%")]
    [InlineData("FAKE%+/=xy")]
    [InlineData("FAKE%+/=xyz")]
    public void PercentInventory_PositionMixedEscapesAndBase64Forms_RedactCompletely(string value)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        // Fully escaped, URI-escaped, and a mixture of literal/escaped '%' occurrences.
        var mixed = string.Concat(value.Select((c, i) => c == '%' && i % 2 == 0 ? "%25" : c.ToString()));
        string[] variants = [value, Percent(value), Percent(value).ToLowerInvariant(), Uri.EscapeDataString(value), mixed, b64, b64.TrimEnd('=')];
        foreach (var variant in variants)
        {
            var actual = GitToolOutputRedactor.Redact("file.txt\n" + variant + "\nend-marker", [value]);
            // Exact output also catches partial replacement leaving encoded credential suffixes.
            Assert.Equal("file.txt\n[redacted]\nend-marker", actual);
        }
        var twice = Percent(value).Replace("%", "%25", StringComparison.Ordinal);
        Assert.Equal("file.txt\n" + twice + "\nend-marker", GitToolOutputRedactor.Redact("file.txt\n" + twice + "\nend-marker", [value]));
    }

    [Fact]
    public void PercentInventory_MultipleAmbiguousPercents_MixedLiteralAndEscapedForms()
    {
        const string value = "FAKE%a%b";
        foreach (var encoded in new[] { "FAKE%25a%b", "FAKE%a%25b", "FAKE%25a%25b", "%46AKE%25a%25b" })
            Assert.Equal("file.txt\n[redacted]\nend-marker", GitToolOutputRedactor.Redact("file.txt\n" + encoded + "\nend-marker", [value]));
    }
}
