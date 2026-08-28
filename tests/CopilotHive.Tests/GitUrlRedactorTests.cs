using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Deterministic unit tests for <see cref="GitUrlRedactor"/>. Every vector is pure text in,
/// pure text out — no network, no live credentials, no timing.
/// </summary>
public sealed class GitUrlRedactorTests
{
    // ── Null / empty ──────────────────────────────────────────────────────────

    [Fact]
    public void Redact_Null_ReturnsNull()
    {
        Assert.Null(GitUrlRedactor.Redact(null));
    }

    [Fact]
    public void Redact_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, GitUrlRedactor.Redact(string.Empty));
    }

    [Fact]
    public void Redact_TextWithoutUrl_ReturnsUnchanged()
    {
        const string text = "fatal: could not read Username for 'origin': terminal prompts disabled";
        Assert.Equal(text, GitUrlRedactor.Redact(text));
    }

    // ── Userinfo form ─────────────────────────────────────────────────────────

    [Theory]
    // Classic token-bearing clone URL.
    [InlineData(
        "https://x-access-token:ghp_abc@github.com/org/repo.git",
        "https://github.com/org/repo.git")]
    // Single-component userinfo (no password).
    [InlineData(
        "https://ghp_abc@github.com/org/repo.git",
        "https://github.com/org/repo.git")]
    // Empty but syntactically present userinfo.
    [InlineData("https://@github.com/org/repo.git", "https://github.com/org/repo.git")]
    // Plain http is in scope too.
    [InlineData("http://user:pw@example.com/o/r", "http://example.com/o/r")]
    // Port is preserved.
    [InlineData("https://user:pw@example.com:8443/o/r", "https://example.com:8443/o/r")]
    // A '@' inside the PATH is not userinfo: the authority ends at the first '/'.
    [InlineData("https://user:pw@example.com/o/r@v1", "https://example.com/o/r@v1")]
    // Userinfo removal is positional on the LAST raw '@' in the authority.
    [InlineData("https://a@b:c@github.com/o/r", "https://github.com/o/r")]
    public void Redact_UserInfoForm_RemovesAllUserInfo(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Fact]
    public void Redact_UserInfoInsideSentence_RedactsOnlyTheUrl()
    {
        Assert.Equal(
            "Failed to clone https://github.com/o/r.git: exit 128",
            GitUrlRedactor.Redact(
                "Failed to clone https://x-access-token:tok@github.com/o/r.git: exit 128"));
    }

    // ── Query-token form: position coverage ───────────────────────────────────

    [Theory]
    // Sole parameter — the dangling '?' is cleaned up.
    [InlineData("https://github.com/o/r?token=abc", "https://github.com/o/r")]
    // First of several.
    [InlineData("https://github.com/o/r?token=abc&ref=main", "https://github.com/o/r?ref=main")]
    // Middle.
    [InlineData(
        "https://github.com/o/r?a=1&token=abc&ref=main",
        "https://github.com/o/r?a=1&ref=main")]
    // Last — the dangling '&' is cleaned up.
    [InlineData("https://github.com/o/r?ref=main&token=abc", "https://github.com/o/r?ref=main")]
    public void Redact_QueryTokenForm_RemovesTokenParameter(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Theory]
    // Bare 'token' with no '=' is removed.
    [InlineData("https://github.com/o/r?token&ref=main", "https://github.com/o/r?ref=main")]
    [InlineData("https://github.com/o/r?token", "https://github.com/o/r")]
    // Empty 'token=' is removed.
    [InlineData("https://github.com/o/r?token=&ref=main", "https://github.com/o/r?ref=main")]
    [InlineData("https://github.com/o/r?token=", "https://github.com/o/r")]
    public void Redact_BareOrEmptyTokenParameter_IsRemoved(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Theory]
    // Duplicates: BOTH removed.
    [InlineData("https://github.com/o/r?token=a&token=b", "https://github.com/o/r")]
    // Mixed empty + valued duplicate, other parameter preserved.
    [InlineData("https://github.com/o/r?token=&token=b&ref=main", "https://github.com/o/r?ref=main")]
    // Bare + valued duplicate.
    [InlineData("https://github.com/o/r?token&token=b&ref=main", "https://github.com/o/r?ref=main")]
    // Every occurrence removed regardless of position.
    [InlineData(
        "https://github.com/o/r?token=a&ref=main&token=b&x=2",
        "https://github.com/o/r?ref=main&x=2")]
    public void Redact_DuplicateTokenParameters_AllRemoved(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Theory]
    // Parameter NAME comparison is case-insensitive.
    [InlineData("https://github.com/o/r?TOKEN=abc&ref=main", "https://github.com/o/r?ref=main")]
    [InlineData("https://github.com/o/r?ToKeN=abc", "https://github.com/o/r")]
    public void Redact_TokenParameterName_IsCaseInsensitive(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Theory]
    // Only the EXACT decoded name 'token' matches — no prefix/suffix matching.
    [InlineData("https://github.com/o/r?access_token=abc")]
    [InlineData("https://github.com/o/r?tokens=abc")]
    [InlineData("https://github.com/o/r?mytoken=abc")]
    // A value that merely mentions 'token' is not a name match.
    [InlineData("https://github.com/o/r?ref=token")]
    public void Redact_NonTokenParameterName_LeavesUrlUnchanged(string input)
    {
        Assert.Equal(input, GitUrlRedactor.Redact(input));
    }

    // ── Semicolon-separated parameters: OUT of scope ──────────────────────────

    [Theory]
    // Semicolon separators are not parameter separators — the URL is left as-is.
    [InlineData("https://github.com/o/r?ref=main;token=abc")]
    [InlineData("https://github.com/o/r;token=abc")]
    public void Redact_SemicolonSeparatedToken_IsNotMatched(string input)
    {
        Assert.Equal(input, GitUrlRedactor.Redact(input));
    }

    // ── Userinfo + query combined ─────────────────────────────────────────────

    [Fact]
    public void Redact_UserInfoAndTokenParameter_BothRemoved()
    {
        Assert.Equal(
            "https://github.com/o/r?ref=main",
            GitUrlRedactor.Redact("https://user:pw@github.com/o/r?token=abc&ref=main"));
    }

    // ── Multiple URLs in one text block ───────────────────────────────────────

    [Fact]
    public void Redact_MultipleUrls_AllRedacted()
    {
        Assert.Equal(
            "clone https://github.com/o/r.git then fetch https://gitlab.com/a/b.git done",
            GitUrlRedactor.Redact(
                "clone https://x-access-token:t1@github.com/o/r.git then fetch "
                + "https://x-access-token:t2@gitlab.com/a/b.git done"));
    }

    [Fact]
    public void Redact_MixedRedactedAndCleanUrls_OnlyCredentialBearingChanged()
    {
        Assert.Equal(
            "a https://github.com/o/clean.git b https://github.com/o/r.git c https://github.com/o/x?ref=main",
            GitUrlRedactor.Redact(
                "a https://github.com/o/clean.git b https://tok@github.com/o/r.git "
                + "c https://github.com/o/x?token=abc&ref=main"));
    }

    [Fact]
    public void Redact_MultilineText_RedactsUrlOnEveryLine()
    {
        Assert.Equal(
            "line1 https://github.com/o/r.git\nline2 https://github.com/o/s.git\n",
            GitUrlRedactor.Redact(
                "line1 https://a:b@github.com/o/r.git\nline2 https://c:d@github.com/o/s.git\n"));
    }

    // ── Scheme handling ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("HTTPS://x-access-token:tok@github.com/o/r", "HTTPS://github.com/o/r")]
    [InlineData("HtTpS://x-access-token:tok@github.com/o/r", "HtTpS://github.com/o/r")]
    [InlineData("HTTP://user:pw@example.com/o/r", "HTTP://example.com/o/r")]
    public void Redact_SchemeComparison_IsCaseInsensitiveAndSchemeTextPreserved(
        string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Theory]
    // Non-HTTP schemes are never matched.
    [InlineData("ssh://git:secret@github.com/o/r.git")]
    [InlineData("git://user:pw@github.com/o/r.git")]
    [InlineData("file:///home/user@host/repo")]
    [InlineData("git@github.com:org/repo.git")]
    // Relative / scheme-less paths are never matched.
    [InlineData("../o/r?token=abc")]
    [InlineData("/var/lib/repos/user:pw@host/o/r")]
    [InlineData("github.com/o/r?token=abc")]
    public void Redact_NonHttpSchemesAndRelativePaths_AreNotMatched(string input)
    {
        Assert.Equal(input, GitUrlRedactor.Redact(input));
    }

    // ── Host text preserved exactly ───────────────────────────────────────────

    [Theory]
    [InlineData("https://user:pw@GitHub.COM/o/r.git", "https://GitHub.COM/o/r.git")]
    [InlineData("https://user:pw@GITHUB.com:8443/o/r", "https://GITHUB.com:8443/o/r")]
    [InlineData("https://user:pw@Example.Internal./o/r", "https://Example.Internal./o/r")]
    [InlineData("https://user:pw@127.0.0.1:3000/o/r", "https://127.0.0.1:3000/o/r")]
    public void Redact_HostText_IsPreservedExactly(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Fact]
    public void Redact_PathCasing_IsPreservedExactly()
    {
        Assert.Equal(
            "https://github.com/Org/RePo.GIT?Ref=Main",
            GitUrlRedactor.Redact("https://User:PW@github.com/Org/RePo.GIT?token=ABC&Ref=Main"));
    }

    // ── Non-credential URLs unchanged (fragment included) ─────────────────────

    [Theory]
    [InlineData("https://github.com/o/r")]
    [InlineData("https://github.com/o/r.git")]
    [InlineData("https://github.com/o/r#fragment")]
    [InlineData("https://github.com/o/r?ref=main#fragment")]
    [InlineData("http://example.com/")]
    public void Redact_NonCredentialUrl_IsReturnedUnchanged(string input)
    {
        Assert.Equal(input, GitUrlRedactor.Redact(input));
    }

    // ── Fragment rule ─────────────────────────────────────────────────────────

    [Theory]
    // Fragment dropped when userinfo is removed…
    [InlineData("https://x-access-token:tok@github.com/o/r#frag", "https://github.com/o/r")]
    // …and when a token parameter is removed…
    [InlineData("https://github.com/o/r?token=abc#frag", "https://github.com/o/r")]
    // …with the remaining parameters still preserved.
    [InlineData("https://github.com/o/r?token=abc&ref=main#frag", "https://github.com/o/r?ref=main")]
    public void Redact_Fragment_DroppedOnlyWhenUrlIsRedacted(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Fact]
    public void Redact_FragmentOnUnredactedUrl_IsKept()
    {
        const string text = "See https://github.com/o/r#readme for details.";
        Assert.Equal(text, GitUrlRedactor.Redact(text));
    }

    [Fact]
    public void Redact_TokenInsideFragmentOnly_IsNotAQueryParameterAndUrlIsUnchanged()
    {
        const string text = "https://github.com/o/r#token=abc";
        Assert.Equal(text, GitUrlRedactor.Redact(text));
    }

    // ── Encoded matching ──────────────────────────────────────────────────────

    [Theory]
    // Parameter NAME is decoded for identification.
    [InlineData("https://github.com/o/r?%74oken=value&ref=main", "https://github.com/o/r?ref=main")]
    [InlineData("https://github.com/o/r?%74OKEN=value", "https://github.com/o/r")]
    [InlineData("https://github.com/o/r?toke%6E=value", "https://github.com/o/r")]
    // Lowercase hex digits decode identically ('%6f' → 'o').
    [InlineData("https://github.com/o/r?t%6fken=value", "https://github.com/o/r")]
    public void Redact_EncodedParameterName_IsDecodedForIdentification(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Fact]
    public void Redact_RetainedParameters_KeepTheirOriginalEncoding()
    {
        Assert.Equal(
            "https://github.com/o/r?%72ef=ma%69n&x=a%2Bb",
            GitUrlRedactor.Redact("https://github.com/o/r?token=abc&%72ef=ma%69n&x=a%2Bb"));
    }

    [Theory]
    // An encoded '=' inside a VALUE does not terminate the value: the split is on the RAW '='.
    [InlineData("https://github.com/o/r?token=a%3Db&ref=main", "https://github.com/o/r?ref=main")]
    [InlineData("https://github.com/o/r?ref=a%3Db&token=x", "https://github.com/o/r?ref=a%3Db")]
    public void Redact_EncodedEqualsInValue_DoesNotTerminateTheValue(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Theory]
    // Userinfo is removed unconditionally without decoding.
    [InlineData("https://x-access-token%3Atok@github.com/o/r", "https://github.com/o/r")]
    [InlineData("https://%78-access-token:tok@github.com/o/r", "https://github.com/o/r")]
    public void Redact_EncodedUserInfo_IsRemovedWithoutDecoding(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Theory]
    // Double encoding is NOT recursively decoded: '%2574oken' decodes once to '%74oken',
    // which is not the token name, so the parameter is retained literally.
    [InlineData("https://github.com/o/r?%2574oken=value")]
    // A literal '%2526' stays literal in a retained value.
    [InlineData("https://github.com/o/r?ref=a%2526b")]
    public void Redact_DoubleEncoding_IsNotRecursivelyDecoded(string input)
    {
        Assert.Equal(input, GitUrlRedactor.Redact(input));
    }

    [Fact]
    public void Redact_DoubleEncodedValueOnRedactedUrl_StaysLiteral()
    {
        Assert.Equal(
            "https://github.com/o/r?ref=a%2526b",
            GitUrlRedactor.Redact("https://user:pw@github.com/o/r?ref=a%2526b"));
    }

    // ── Malformed percent escapes (fail-safe) ─────────────────────────────────

    [Theory]
    // Recognition happens on the RAW text, so a malformed escape never blocks redaction;
    // malformed escapes in RETAINED components are preserved verbatim.
    [InlineData("https://x-access-token:tok@github.com/o/r%2z", "https://github.com/o/r%2z")]
    [InlineData("https://x-access-token:tok@github.com/o/r%", "https://github.com/o/r%")]
    [InlineData("https://user:pw@github.com/o/r?ref=%zz", "https://github.com/o/r?ref=%zz")]
    [InlineData("https://github.com/o/r?token=abc&ref=%2", "https://github.com/o/r?ref=%2")]
    // A malformed escape in a parameter NAME simply fails the token comparison.
    [InlineData("https://user:pw@github.com/o/r?%zzoken=abc", "https://github.com/o/r?%zzoken=abc")]
    public void Redact_MalformedPercentEscapes_StillRedactedAndPreservedVerbatim(
        string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    // ── Stage 2: trailing punctuation ─────────────────────────────────────────

    [Theory]
    [InlineData('.')]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData(':')]
    [InlineData('!')]
    [InlineData('?')]
    public void Redact_TrailingPunctuation_IsStrippedFromTheCandidate(char punctuation)
    {
        Assert.Equal(
            $"See https://github.com/o/r{punctuation}",
            GitUrlRedactor.Redact($"See https://x-access-token:tok@github.com/o/r{punctuation}"));
    }

    [Fact]
    public void Redact_TrailingColonBeforeExitCode_IsStrippedFromTheCandidate()
    {
        Assert.Equal(
            "Failed to clone https://github.com/o/r.git: exit 128",
            GitUrlRedactor.Redact(
                "Failed to clone https://x-access-token:tok@github.com/o/r.git: exit 128"));
    }

    [Fact]
    public void Redact_TrailingSentencePeriod_IsStrippedFromTheCandidate()
    {
        Assert.Equal(
            "See https://github.com/o/r.",
            GitUrlRedactor.Redact("See https://x-access-token:tok@github.com/o/r."));
    }

    [Fact]
    public void Redact_MultipleTrailingPunctuationCharacters_AllStripped()
    {
        Assert.Equal(
            "https://github.com/o/r?!.",
            GitUrlRedactor.Redact("https://tok@github.com/o/r?!."));
    }

    [Fact]
    public void Redact_InnerPunctuation_IsKeptInsideTheUrl()
    {
        // ':' and ',' are valid URL characters and only stripped when TRAILING.
        Assert.Equal(
            "https://github.com/o/r/a:b,c/d",
            GitUrlRedactor.Redact("https://tok@github.com/o/r/a:b,c/d"));
    }

    // ── Stage 1: terminators ──────────────────────────────────────────────────

    [Theory]
    [InlineData(
        "url=\"https://x-access-token:tok@github.com/o/r.git\"",
        "url=\"https://github.com/o/r.git\"")]
    [InlineData(
        "`https://x-access-token:tok@github.com/o/r.git`",
        "`https://github.com/o/r.git`")]
    [InlineData(
        "<https://x-access-token:tok@github.com/o/r.git>",
        "<https://github.com/o/r.git>")]
    [InlineData(
        "https://x-access-token:tok@github.com/o/r.git\tnext",
        "https://github.com/o/r.git\tnext")]
    [InlineData(
        "https://x-access-token:tok@github.com/o/r.git\nnext",
        "https://github.com/o/r.git\nnext")]
    public void Redact_DelimiterCharacters_TerminateStageOneScanning(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    // ── Stage 1: bracket balancing ────────────────────────────────────────────

    [Theory]
    // Balanced openers/closers INSIDE the URL stay part of the URL.
    [InlineData(
        "https://x-access-token:tok@github.com/o/r(main)/x",
        "https://github.com/o/r(main)/x")]
    [InlineData(
        "https://x-access-token:tok@github.com/o/r[main]/x",
        "https://github.com/o/r[main]/x")]
    [InlineData(
        "https://x-access-token:tok@github.com/o/r{main}/x",
        "https://github.com/o/r{main}/x")]
    [InlineData(
        "https://x-access-token:tok@github.com/o/r((a))/x",
        "https://github.com/o/r((a))/x")]
    public void Redact_BalancedBracketsInsideUrl_RemainPartOfTheUrl(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Theory]
    // An unbalanced closer terminates stage 1 — its opener is OUTSIDE the URL.
    [InlineData("(https://x-access-token:tok@github.com/o/r)", "(https://github.com/o/r)")]
    [InlineData("[https://x-access-token:tok@github.com/o/r]", "[https://github.com/o/r]")]
    [InlineData("{https://x-access-token:tok@github.com/o/r}", "{https://github.com/o/r}")]
    [InlineData(
        "(see https://x-access-token:tok@github.com/o/r.git) and more",
        "(see https://github.com/o/r.git) and more")]
    public void Redact_UnbalancedCloser_TerminatesStageOneScanning(string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    [Fact]
    public void Redact_UnbalancedCloserAfterBalancedPair_TerminatesAtTheExtraCloser()
    {
        Assert.Equal(
            "(https://github.com/o/r(main))",
            GitUrlRedactor.Redact("(https://x-access-token:tok@github.com/o/r(main))"));
    }

    [Fact]
    public void Redact_UnbalancedOpener_IsKeptInsideTheUrl()
    {
        // A never-closed opener is still an accepted URL character.
        Assert.Equal(
            "https://github.com/o/r(main",
            GitUrlRedactor.Redact("https://tok@github.com/o/r(main"));
    }

    [Fact]
    public void Redact_BracketBalancingIsPerBracketType()
    {
        // The '(' opened inside the URL does not balance the ']' closer.
        Assert.Equal(
            "[https://github.com/o/r(a)]",
            GitUrlRedactor.Redact("[https://tok@github.com/o/r(a)]"));
    }

    // ── Realistic message shapes ──────────────────────────────────────────────

    [Fact]
    public void Redact_GitCloneErrorMessage_HasCredentialRemoved()
    {
        const string input =
            "fatal: unable to access 'https://x-access-token:ghp_secret@github.com/org/repo.git/': "
            + "The requested URL returned error: 403";
        const string expected =
            "fatal: unable to access 'https://github.com/org/repo.git/': "
            + "The requested URL returned error: 403";

        var actual = GitUrlRedactor.Redact(input);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("ghp_secret", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RemoteUrlWithTokenQuery_HasTokenRemoved()
    {
        const string input = "remote configured as https://github.com/org/repo.git?token=ghp_secret&ref=main";

        var actual = GitUrlRedactor.Redact(input);

        Assert.Equal("remote configured as https://github.com/org/repo.git?ref=main", actual);
        Assert.DoesNotContain("ghp_secret", actual, StringComparison.Ordinal);
    }

    // ── Overlapping / nested http(s) occurrences ──────────────────────────────
    //
    // Stage 1 deliberately accepts `,` `;` `:` `/` `?` `#` `&` `=` and friends, so two adjacent
    // URLs separated only by accepted characters scan as ONE structural span. Scanning must
    // still START at every occurrence, otherwise the second URL's userinfo is never examined
    // and its credential survives verbatim.

    [Theory]
    // Comma-separated — the original reported leak vector.
    [InlineData(
        "https://u1:s1@a.example/r,https://u2:s2@b.example/r",
        "https://a.example/r,https://b.example/r")]
    // Semicolon-separated.
    [InlineData(
        "https://u1:s1@a.example/r;https://u2:s2@b.example/r",
        "https://a.example/r;https://b.example/r")]
    // Slash-separated (no separator character at all beyond the path delimiter).
    [InlineData(
        "https://u1:s1@a.example/r/https://u2:s2@b.example/r",
        "https://a.example/r/https://b.example/r")]
    // Colon-separated.
    [InlineData(
        "https://u1:s1@a.example/r:https://u2:s2@b.example/r",
        "https://a.example/r:https://b.example/r")]
    // Question-mark-separated: the second URL sits inside the first one's query.
    [InlineData(
        "https://u1:s1@a.example/r?https://u2:s2@b.example/r",
        "https://a.example/r?https://b.example/r")]
    // Hash-adjacent: the second URL sits inside the first one's fragment.
    [InlineData(
        "https://u1:s1@a.example/r#https://u2:s2@b.example/r",
        "https://a.example/r#https://b.example/r")]
    // Ampersand-separated.
    [InlineData(
        "https://u1:s1@a.example/r&https://u2:s2@b.example/r",
        "https://a.example/r&https://b.example/r")]
    // Equals-separated (a redirect-style parameter value).
    [InlineData(
        "https://u1:s1@a.example/r=https://u2:s2@b.example/r",
        "https://a.example/r=https://b.example/r")]
    // Plus-separated.
    [InlineData(
        "https://u1:s1@a.example/r+https://u2:s2@b.example/r",
        "https://a.example/r+https://b.example/r")]
    // At-separated.
    [InlineData(
        "https://u1:s1@a.example/r@https://u2:s2@b.example/r",
        "https://a.example/r@https://b.example/r")]
    public void Redact_AdjacentUrlsSeparatedByAcceptedCharacters_AllAreRedacted(
        string input, string expected)
    {
        var actual = GitUrlRedactor.Redact(input);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("s1@", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("s2@", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SECOND URL alone carrying a credential is the strictest form of the defect: the first
    /// URL is clean, so a scanner that consumed the whole span would emit no rewrite at all.
    /// </summary>
    [Fact]
    public void Redact_OnlyTheNestedUrlHasCredential_StillRedacted()
    {
        var actual = GitUrlRedactor.Redact(
            "https://a.example/r,https://u2:s2@b.example/r");

        Assert.Equal("https://a.example/r,https://b.example/r", actual);
    }

    /// <summary>
    /// A credential-bearing URL embedded as a redirect-style QUERY VALUE of a clean URL.
    /// </summary>
    [Fact]
    public void Redact_NestedUrlInsideQueryValue_IsRedacted()
    {
        var actual = GitUrlRedactor.Redact(
            "https://a.example/r?next=https://u2:s2@b.example/r");

        Assert.Equal("https://a.example/r?next=https://b.example/r", actual);
    }

    /// <summary>
    /// A credential-bearing URL embedded inside a clean URL's FRAGMENT.
    /// </summary>
    [Fact]
    public void Redact_NestedUrlInsideFragment_IsRedacted()
    {
        var actual = GitUrlRedactor.Redact(
            "https://a.example/r#see=https://u2:s2@b.example/r");

        Assert.Equal("https://a.example/r#see=https://b.example/r", actual);
    }

    /// <summary>
    /// The nested occurrence's scheme comparison is case-insensitive too.
    /// </summary>
    [Fact]
    public void Redact_NestedUrlWithUppercaseScheme_IsRedacted()
    {
        var actual = GitUrlRedactor.Redact(
            "https://a.example/r?a=1&b=HTTPS://u2:s2@b.example/r");

        Assert.Equal("https://a.example/r?a=1&b=HTTPS://b.example/r", actual);
    }

    /// <summary>
    /// Three chained URLs: EVERY occurrence is redacted, not just the first and last.
    /// </summary>
    [Fact]
    public void Redact_ThreeChainedUrls_AllAreRedacted()
    {
        var actual = GitUrlRedactor.Redact(
            "https://u1:s1@a.example/r,https://u2:s2@b.example/r,https://u3:s3@c.example/r");

        Assert.Equal(
            "https://a.example/r,https://b.example/r,https://c.example/r", actual);
        Assert.DoesNotContain("s1", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("s2", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("s3", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nested token-QUERY credentials are removed from every occurrence as well.
    /// </summary>
    [Fact]
    public void Redact_AdjacentUrlsWithTokenQueries_AllAreRedacted()
    {
        var actual = GitUrlRedactor.Redact(
            "https://a.example/r?token=s1,https://b.example/r?token=s2&ref=main");

        Assert.Equal("https://a.example/r,https://b.example/r?ref=main", actual);
        Assert.DoesNotContain("s1", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("s2", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cutting a candidate at a nested occurrence must not lose the surrounding text, and a
    /// pair of adjacent CLEAN urls is still returned completely unchanged.
    /// </summary>
    [Fact]
    public void Redact_AdjacentCleanUrls_AreReturnedUnchanged()
    {
        const string text = "see https://a.example/r,https://b.example/r#frag now";

        Assert.Equal(text, GitUrlRedactor.Redact(text));
    }

    /// <summary>
    /// A realistic multi-remote git error line where both remotes carry the same token.
    /// </summary>
    [Fact]
    public void Redact_MultiRemoteErrorLine_HasNoCredential()
    {
        const string Secret = "ghp_multi_remote_secret";
        var actual = GitUrlRedactor.Redact(
            $"fatal: could not read from 'https://x-access-token:{Secret}@github.com/o/a.git',"
            + $"'https://x-access-token:{Secret}@github.com/o/b.git': exit 128");

        Assert.DoesNotContain(Secret, actual, StringComparison.Ordinal);
        Assert.Contains("https://github.com/o/a.git", actual, StringComparison.Ordinal);
        Assert.Contains("https://github.com/o/b.git", actual, StringComparison.Ordinal);
    }

    // ── Dangling query-delimiter cleanup ──────────────────────────────────────
    //
    // Removing a token parameter can strand '&' separators. The rewritten query must never
    // begin with '&', contain '&&', end with '&', or leave a bare '?' with an empty query.

    [Theory]
    // Leading '?&' plus a trailing '&' around a SOLE token — the reported vector.
    [InlineData("https://github.com/o/r?&token=secret&", "https://github.com/o/r")]
    // Leading '?&' only.
    [InlineData("https://github.com/o/r?&token=secret", "https://github.com/o/r")]
    // Trailing '&' only.
    [InlineData("https://github.com/o/r?token=secret&", "https://github.com/o/r")]
    // Repeated separators on both sides.
    [InlineData("https://github.com/o/r?&&token=secret&&", "https://github.com/o/r")]
    // Nothing but separators left behind.
    [InlineData("https://github.com/o/r?&&&token=secret", "https://github.com/o/r")]
    // Duplicate sole tokens with a trailing separator.
    [InlineData("https://github.com/o/r?token=a&&token=b&", "https://github.com/o/r")]
    public void Redact_SoleTokenWithDanglingSeparators_LeavesNoQueryAtAll(
        string input, string expected)
    {
        var actual = GitUrlRedactor.Redact(input);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("?", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("&", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", actual, StringComparison.Ordinal);
    }

    [Theory]
    // Doubled separator left behind in the middle.
    [InlineData("https://github.com/o/r?token=a&&ref=main", "https://github.com/o/r?ref=main")]
    // Leading '?&' with a surviving parameter.
    [InlineData("https://github.com/o/r?&token=a&ref=main", "https://github.com/o/r?ref=main")]
    // Trailing '&' with a surviving parameter.
    [InlineData("https://github.com/o/r?ref=main&token=a&", "https://github.com/o/r?ref=main")]
    // Separators scattered on every side of a surviving parameter.
    [InlineData("https://github.com/o/r?&&token=a&&ref=main&&", "https://github.com/o/r?ref=main")]
    // Two surviving parameters keep their relative order and single separator.
    [InlineData("https://github.com/o/r?&a=1&token=x&&b=2&", "https://github.com/o/r?a=1&b=2")]
    public void Redact_TokenRemovalWithDanglingSeparators_ProducesACleanQuery(
        string input, string expected)
    {
        var actual = GitUrlRedactor.Redact(input);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("?&", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("&&", actual, StringComparison.Ordinal);
        Assert.False(actual.EndsWith('&'), "the query must not end with a dangling '&'");
        Assert.False(actual.EndsWith('?'), "the query must not be a bare '?'");
    }

    /// <summary>
    /// An EMPTY-valued parameter is substantive (it has a name) and must survive the cleanup —
    /// only pure delimiter noise is dropped.
    /// </summary>
    [Fact]
    public void Redact_EmptyValuedParameter_SurvivesTheDanglingCleanup()
    {
        Assert.Equal(
            "https://github.com/o/r?a=",
            GitUrlRedactor.Redact("https://github.com/o/r?a=&token=x"));
    }

    /// <summary>
    /// The cleanup runs ONLY when a token was actually removed. A userinfo-only redaction emits
    /// the query byte-for-byte, dangling separators included, so retained components keep their
    /// exact original form.
    /// </summary>
    [Theory]
    [InlineData("https://u:p@github.com/o/r?&a=1&", "https://github.com/o/r?&a=1&")]
    [InlineData("https://u:p@github.com/o/r?a=1&&b=2", "https://github.com/o/r?a=1&&b=2")]
    [InlineData("https://u:p@github.com/o/r?", "https://github.com/o/r?")]
    public void Redact_UserInfoOnlyRedaction_LeavesTheQueryByteForByte(
        string input, string expected)
    {
        Assert.Equal(expected, GitUrlRedactor.Redact(input));
    }

    /// <summary>
    /// A URL with dangling separators and NO credential is returned completely unchanged — the
    /// cleanup never rewrites an innocent URL.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/o/r?&a=1&")]
    [InlineData("https://github.com/o/r?&&")]
    [InlineData("https://github.com/o/r?")]
    public void Redact_DanglingSeparatorsWithoutCredential_AreLeftUntouched(string input)
    {
        Assert.Equal(input, GitUrlRedactor.Redact(input));
    }
}
