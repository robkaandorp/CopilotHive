using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Pins that a known inventory value which CONTAINS an HTTP credential URL is removed whole. The
/// composed <see cref="GitUrlRedactor"/> deletes userinfo and token parameters; matching the
/// inventory only against that rewritten text used to leave the rest of a known value behind.
/// Also pins where the union boundary lies (overlap merges, touching stays distinct) and that a
/// URL whose credential is in no inventory value is unchanged. Synthetic values only.
/// </summary>
public sealed class GitToolOutputKnownUrlValueTests
{
    private const string Url = "https://u:FAKE_PASSWORD@h.invalid/rFAKE_PRIVATE_TAIL";
    private const string Other = "FAKE_OTHER_KEY";

    // ── A known value that contains the whole credentialed URL ───────────────────────────────

    [Fact]
    public void KnownValueContainingTheWholeCredentialUrl_IsRemovedWhole() =>
        Assert.Equal("[redacted]", GitToolOutputRedactor.Redact(Url, [Url]));

    [Fact]
    public void KnownUrlValue_InsideSurroundingText_LeavesNoTailAndKeepsTheContext() =>
        Assert.Equal(
            "file.txt\nfetching [redacted] now\nend-marker",
            GitToolOutputRedactor.Redact($"file.txt\nfetching {Url} now\nend-marker", [Url]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownUrlValue_BesideAnOrdinaryInventoryLine_BothAreRemoved(bool reverse)
    {
        string[] inventory = reverse ? [Other, Url] : [Url, Other];
        Assert.Equal(
            "file.txt\nordinary [redacted]\n[redacted]\nend-marker",
            GitToolOutputRedactor.Redact($"file.txt\nordinary {Other}\n{Url}\nend-marker", inventory));
    }

    [Fact]
    public void KnownValueContainingATokenQueryUrl_IsRemovedWhole()
    {
        const string value = "https://h.invalid/r?token=FAKE_QUERY_VALUE&ref=topic";
        Assert.Equal("[redacted]", GitToolOutputRedactor.Redact(value, [value]));
        Assert.Equal("see [redacted] end", GitToolOutputRedactor.Redact($"see {value} end", [value]));
    }

    [Fact]
    public void KnownValueContainingBothUserinfoAndATokenQuery_IsRemovedWhole()
    {
        const string value = "https://u:FAKE_PASSWORD@h.invalid/r?token=FAKE_QUERY_VALUE&ref=topic";
        Assert.Equal("[redacted]", GitToolOutputRedactor.Redact(value, [value]));
    }

    // ── Partial overlap: where the union boundary lies ───────────────────────────────────────

    [Fact]
    public void ValueOverlappingTheUrlOnlyPartially_RemovesItsWholeExtent()
    {
        // The value starts INSIDE the userinfo and runs to the end of the URL. The frozen URL
        // grammar deletes the whole userinfo ("u:FAKE_PASSWORD@") on its own, so no "u:" remains;
        // what is left of the value's extent is host + path, which must go as ONE placeholder.
        const string value = "FAKE_PASSWORD@h.invalid/rFAKE_PRIVATE_TAIL";
        Assert.Equal("https://[redacted]", GitToolOutputRedactor.Redact(Url, [value]));
    }

    [Fact]
    public void TwoGenuinelyOverlappingValues_UnionIntoOnePlaceholder() =>
        Assert.Equal(
            "[redacted]",
            GitToolOutputRedactor.Redact(Url, ["https://u:FAKE_PASSWORD@h.invalid/rFAKE", "rFAKE_PRIVATE_TAIL"]));

    [Fact]
    public void TwoTouchingButDistinctValues_KeepTwoPlaceholders() =>
        Assert.Equal(
            "[redacted][redacted]",
            GitToolOutputRedactor.Redact(Url, ["https://u:FAKE_PASSWORD@h.invalid", "/rFAKE_PRIVATE_TAIL"]));

    // ── The ambiguous-deletion shape: a deleted character equals the kept one beside it ──────

    [Fact]
    public void KnownUserinfoBeginningLikeTheHost_DoesNotRedactTheHostsFirstCharacter()
    {
        // Userinfo "hunter:..." and host "host.invalid" both start with 'h'. The rewrite prints the
        // same text whichever 'h' it deleted, so a naive diff would redact the HOST's 'h'.
        const string value = "hunter:FAKE_PASSWORD";
        Assert.Equal(
            "https://host.invalid/r",
            GitToolOutputRedactor.Redact("https://hunter:FAKE_PASSWORD@host.invalid/r", [value]));
    }

    [Fact]
    public void KnownValueRunningFromTheUserinfoIntoTheHost_RemovesTheHostsFirstCharacterToo()
    {
        // The mirror image of the row above: here the host's first character BELONGS to the known
        // value, and the deleted userinfo also starts with 'h'. Which 'h' the rewrite deleted is
        // ambiguous from the text alone; resolving it wrongly leaks one character of a known secret
        // ("https://h[redacted]/r") instead of removing the whole value.
        const string value = "FAKE_PASSWORD@host.invalid";
        Assert.Equal(
            "https://[redacted]/r",
            GitToolOutputRedactor.Redact("https://hunter:FAKE_PASSWORD@host.invalid/r", [value]));
    }

    [Fact]
    public void KnownUserinfoSharingManyCharactersWithTheHost_DoesNotTouchTheHost() =>
        Assert.Equal(
            "https://host.example.invalid/path",
            GitToolOutputRedactor.Redact(
                "https://host.example:FAKE_SECRET_VALUE@host.example.invalid/path",
                ["host.example:FAKE_SECRET_VALUE"]));

    // ── Controls: nothing in the inventory covers the credential ─────────────────────────────

    [Fact]
    public void UrlWithoutAnyInventory_KeepsTheInheritedUserinfoRemoval() =>
        Assert.Equal("https://h.invalid/rFAKE_PRIVATE_TAIL", GitToolOutputRedactor.Redact(Url));

    [Fact]
    public void UrlWhoseCredentialIsInNoInventoryValue_IsUnchangedByTheFix() =>
        Assert.Equal(
            "https://h.invalid/r ordinary [redacted]",
            GitToolOutputRedactor.Redact($"https://u:FAKE_PASSWORD@h.invalid/r ordinary {Other}", [Other]));

    [Theory]
    [InlineData("https://FAKE_KNOWN_KEY@h.invalid/r", "https://h.invalid/r")]
    [InlineData("https://u:FAKE_PASSWORD@h.invalid/r?ref=FAKE_KNOWN_KEY&x=1", "https://h.invalid/r?ref=[redacted]&x=1")]
    [InlineData("https://h.invalid/r?token=FAKE_KNOWN_KEYFAKE_TAIL&ref=topic", "https://h.invalid/r?ref=topic")]
    public void ShorterKnownValueInsideACredentialUrl_StillComposesAsBefore(string url, string expected) =>
        Assert.Equal(expected, GitToolOutputRedactor.Redact(url, ["FAKE_KNOWN_KEY"]));

    [Fact]
    public void KnownUrlValue_PreservesTheLineStructure()
    {
        var input = $"a\r\n{Url}\nb\r\n{Url}\nz";
        var actual = GitToolOutputRedactor.Redact(input, [Url]);

        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        Assert.DoesNotContain("FAKE_PRIVATE_TAIL", actual);
    }
}
