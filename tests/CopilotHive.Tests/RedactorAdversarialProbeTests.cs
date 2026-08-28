using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// Adversarial probes for the credential-redaction goal, iteration 2.
/// <para>
/// Written by the tester AFTER the coder fixed the three review-identified defects
/// (overlapping occurrences, dangling delimiters, Brain remote-tag stderr). Each probe is a
/// NEW credential-leak vector of the two redactor defect classes that is NOT covered by the
/// coder's own tests. If any probe leaks, the goal round must FAIL.
/// </para>
/// </summary>
public sealed class RedactorAdversarialProbeTests
{
    private const string Token = "ghp_probe_secret";

    // ── Overlapping-occurrence class: nested http(s):// occurrences ───────────

    [Theory]
    [InlineData(
        // Three adjacent URLs separated only by accepted characters.
        "https://u1:s1@a.example/r,https://u2:s2@b.example/r,https://u3:s3@c.example/r",
        "https://a.example/r,https://b.example/r,https://c.example/r")]
    [InlineData(
        // The FIRST URL is clean; only the nested one carries a credential.
        "https://a.example/r,https://u2:s2@b.example/r",
        "https://a.example/r,https://b.example/r")]
    [InlineData(
        // Adjacent URLs separated by '?' (query boundary).
        "https://u1:s1@a.example/r?https://u2:s2@b.example/r",
        "https://a.example/r?https://b.example/r")]
    [InlineData(
        // Adjacent URLs separated by '#' (fragment boundary).
        "https://u1:s1@a.example/r#https://u2:s2@b.example/r",
        "https://a.example/r#https://b.example/r")]
    [InlineData(
        // Adjacent URLs separated by ';' (sub-delim boundary).
        "https://u1:s1@a.example/r;https://u2:s2@b.example/r",
        "https://a.example/r;https://b.example/r")]
    [InlineData(
        // Nested URL inside a query VALUE (redirect shape), with a further query.
        "https://u1:s1@a.example/r?next=https://u2:s2@b.example/r&ref=main",
        "https://a.example/r?next=https://b.example/r&ref=main")]
    [InlineData(
        // Nested URL inside a fragment, with a query on the outer URL too.
        "https://u1:s1@a.example/r?q=1#https://u2:s2@b.example/r",
        "https://a.example/r?q=1#https://b.example/r")]
    [InlineData(
        // Nested occurrence inside a userinfo-bearing URL's PATH.
        "https://u1:s1@a.example/https://u2:s2@b.example/r",
        "https://a.example/https://b.example/r")]
    public void Probe_OverlappingOccurrences_AllNestedCredentialsAreRedacted(
        string input, string expected)
    {
        var actual = GitUrlRedactor.Redact(input);

        Assert.Equal(expected, actual);
        // No userinfo fragment of ANY of the URLs survives.
        Assert.DoesNotContain("s1@", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("s2@", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("s3@", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("u2:", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_ThreeAdjacentTokenQueryUrls_AllAreRedacted()
    {
        var actual = GitUrlRedactor.Redact(
            "https://a.example/r?token=" + Token
            + ",https://b.example/r?token=" + Token
            + ",https://c.example/r?token=" + Token);

        Assert.DoesNotContain(Token, actual, StringComparison.Ordinal);
        Assert.Equal(
            "https://a.example/r,https://b.example/r,https://c.example/r", actual);
    }

    [Fact]
    public void Probe_MixedUserInfoAndTokenQueryAdjacentUrls_AllAreRedacted()
    {
        var actual = GitUrlRedactor.Redact(
            $"https://u1:s1@a.example/r?ref=main,https://b.example/r?token={Token}&x=1");

        Assert.DoesNotContain("s1@", actual, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, actual, StringComparison.Ordinal);
        Assert.Equal(
            "https://a.example/r?ref=main,https://b.example/r?x=1", actual);
    }

    [Fact]
    public void Probe_NestedUrlAfterHashInsideRedactedUrl_FragmentTextSurvives()
    {
        // The nested URL's text (after the '#') must survive verbatim — the fragment of a
        // TRUNCATED candidate is retained, not dropped.
        var actual = GitUrlRedactor.Redact(
            $"https://u1:s1@a.example/r#see-https://u2:s2@b.example/r");

        Assert.DoesNotContain("s1@", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("s2@", actual, StringComparison.Ordinal);
        Assert.Equal("https://a.example/r#see-https://b.example/r", actual);
    }

    // ── Dangling-delimiter class: token removal around separators ─────────────

    [Theory]
    [InlineData(
        // The exact iteration-1 review vector.
        "https://github.com/o/r?&token=" + Token + "&",
        "https://github.com/o/r")]
    [InlineData(
        // Repeated leading separators.
        "https://github.com/o/r?&&&&token=" + Token,
        "https://github.com/o/r")]
    [InlineData(
        // Token-only query with separators on both sides, then a trailing punctuation that
        // stage 2 strips from the CANDIDATE — the stripped '.' survives as ordinary surrounding
        // text (round-1 semantics), but no '?' or '&' does.
        "https://github.com/o/r?&&token=" + Token + "&&.",
        "https://github.com/o/r.")]
    [InlineData(
        // Adjacent token parameters with doubled separators between and after.
        "https://github.com/o/r?token=" + Token + "&&&token=" + Token + "&&",
        "https://github.com/o/r")]
    [InlineData(
        // Token parameter adjacent to a fragment: the fragment is dropped on redaction, and
        // the dangling '&' before it must not survive either.
        "https://github.com/o/r?ref=main&token=" + Token + "&#frag",
        "https://github.com/o/r?ref=main")]
    [InlineData(
        // Sole token parameter directly against the '?' with nothing after.
        "https://github.com/o/r?token=" + Token,
        "https://github.com/o/r")]
    public void Probe_DanglingDelimiters_NoSeparatorNoiseRemains(
        string input, string expected)
    {
        var actual = GitUrlRedactor.Redact(input);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(Token, actual, StringComparison.Ordinal);
        // No dangling delimiter noise anywhere in the rewritten URL.
        Assert.DoesNotContain("?&", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("&&", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("&?", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("#&", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("?#", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("?=", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_TokenOnlyQueryAdjacentToAnotherUrl_NoLeakEitherWay()
    {
        // A dangling-delimiter URL embedded in an overlapping-occurrence text: both defect
        // classes combined.
        var actual = GitUrlRedactor.Redact(
            "https://u1:s1@a.example/r?&token=" + Token
            + "&,https://b.example/r?&&token=" + Token + "&&");

        Assert.DoesNotContain("s1@", actual, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, actual, StringComparison.Ordinal);
        // Both defect classes combined: both URLs fully redacted AND every dangling separator
        // gone — the rewritten queries collapse to no query at all.
        Assert.Equal("https://a.example/r,https://b.example/r", actual);
    }
}