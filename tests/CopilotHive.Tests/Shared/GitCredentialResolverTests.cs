using CopilotHive.Shared;

// NOTE: this file lives under tests/CopilotHive.Tests/Shared/ (it covers a CopilotHive.Shared
// type) but deliberately declares the ROOT test namespace, following the LogSanitizerTests
// convention — declaring `CopilotHive.Tests.Shared` would break the existing `Shared.Grpc.*`
// relative-name resolutions in sibling test files.
namespace CopilotHive.Tests;

/// <summary>
/// Removal-proof tests for <see cref="GitCredentialResolver.Resolve"/>. Every vector asserts the
/// EXACT returned value where it matters — most importantly the untrimmed guarantee: the selected
/// candidate is returned UNCHANGED, with any surrounding whitespace intact. Weakening the helper
/// (trimming, reordering, or dropping any candidate) fails the corresponding test.
/// </summary>
public sealed class GitCredentialResolverTests
{
    // ── First candidate wins; later candidates ignored ────────────────────────

    [Fact]
    public void Resolve_FirstCandidatePresent_ReturnsIt_IgnoresLaterCandidates()
    {
        var resolved = GitCredentialResolver.Resolve("first-token", "second-token", "third-token");

        Assert.Equal("first-token", resolved);
    }

    [Fact]
    public void Resolve_SingleCandidate_ReturnsItUnchanged()
    {
        Assert.Equal("only-token", GitCredentialResolver.Resolve("only-token"));
    }

    // ── Null/whitespace candidates fall through to the next ───────────────────

    [Fact]
    public void Resolve_NullFirstCandidate_FallsThroughToNext()
    {
        Assert.Equal("second-token", GitCredentialResolver.Resolve(null, "second-token", "third-token"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Resolve_WhitespaceFirstCandidate_FallsThroughToNext(string whitespace)
    {
        // Whitespace-is-absent applies at EACH step: a whitespace candidate NEVER wins,
        // even though it is technically non-null.
        Assert.Equal("fallback-token", GitCredentialResolver.Resolve(whitespace, "fallback-token"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Resolve_MixedAbsentCandidates_FallThroughToFirstPresent(string whitespace)
    {
        Assert.Equal(
            "present-token",
            GitCredentialResolver.Resolve(null, whitespace, "present-token", "ignored"));
    }

    // ── Null when all candidates are null/whitespace ──────────────────────────

    [Fact]
    public void Resolve_AllCandidatesNull_ReturnsNull()
    {
        Assert.Null(GitCredentialResolver.Resolve(null, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Resolve_AllCandidatesWhitespace_ReturnsNull(string whitespace)
    {
        Assert.Null(GitCredentialResolver.Resolve(whitespace, whitespace));
    }

    [Fact]
    public void Resolve_EmptyArgumentList_ReturnsNull()
    {
        Assert.Null(GitCredentialResolver.Resolve());
    }

    // ── The selected candidate is returned UNCHANGED (never trimmed) ──────────

    /// <summary>
    /// The resolver guarantees the RAW selection: a candidate carrying surrounding whitespace
    /// still passes <c>!string.IsNullOrWhiteSpace</c> and is returned WITH its padding — the
    /// exact value that would expose any trimming regression.
    /// </summary>
    [Fact]
    public void Resolve_PaddedCandidate_ReturnedUnchanged_NotTrimmed()
    {
        Assert.Equal("  padded-token  ", GitCredentialResolver.Resolve("  padded-token  "));
    }

    [Fact]
    public void Resolve_PaddedCandidateWithInteriorWhitespace_ReturnedUnchanged()
    {
        Assert.Equal(
            "\ttoken-with\tinternal spacing \n",
            GitCredentialResolver.Resolve("\ttoken-with\tinternal spacing \n"));
    }

    [Fact]
    public void Resolve_PaddedFirstCandidateWins_AndIsReturnedUnchanged()
    {
        // The first candidate wins even when padded; the later clean candidate is ignored.
        Assert.Equal("  padded-winner  ", GitCredentialResolver.Resolve("  padded-winner  ", "clean-token"));
    }

    [Fact]
    public void Resolve_PaddedCandidateAfterAbsentOnes_ReturnedUnchanged()
    {
        Assert.Equal(
            "  padded-fallback  ",
            GitCredentialResolver.Resolve(null, "  ", "  padded-fallback  "));
    }
}