using System.Text;
using CopilotHive.Services;

namespace CopilotHive.Tests;

// These collections do not overlap ANY other collection, including existing environment readers.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitOutputInventoryCollection
{
    public const string Name = "Git output inventory environment";
}

public sealed class GitToolOutputAdversarialTests
{
    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));

    [Theory]
    [InlineData("http://u:FAKE_PASSWORD@h.invalid/r", "http://h.invalid/r")]
    [InlineData("HTTPS://u:FAKE_PASSWORD@h.invalid/r", "HTTPS://h.invalid/r")]
    [InlineData("https://h.invalid/r?token=FAKE_PASSWORD&ref=topic", "https://h.invalid/r?ref=topic")]
    [InlineData("http://h.invalid/r?%74oKeN=FAKE_PASSWORD&x=1&token=FAKE_PASSWORD", "http://h.invalid/r?x=1")]
    // Inherited composition contract: HTTP userinfo is removed even without a password.
    [InlineData("https://user@h.invalid/r", "https://h.invalid/r")]
    [InlineData("ssh://git@h.invalid/r", "ssh://git@h.invalid/r")]
    [InlineData("git@h.invalid:org/r", "git@h.invalid:org/r")]
    [InlineData("ssh://git@h.invalid", "ssh://git@h.invalid")]
    public void UrlMatrix_ComposesExistingGrammarAndPreservesSshUsers(string input, string expected)
    {
        Assert.Equal("before " + expected + " after", GitToolOutputRedactor.Redact("before " + input + " after"));
    }

    [Theory]
    [InlineData("ssh://u:pw@h.invalid/r", "ssh://u:[redacted]@h.invalid/r")]
    [InlineData("SSH://u:pw@h.invalid:2222/r", "SSH://u:[redacted]@h.invalid:2222/r")]
    [InlineData("u:pw@h.invalid:org/r", "u:[redacted]@h.invalid:org/r")]
    public void ShortStructuralPassword_IsRedactedWithoutInventory(string input, string expected)
    {
        Assert.Equal("before " + expected + " after", GitToolOutputRedactor.Redact("before " + input + " after"));
    }

    [Theory]
    [InlineData("Basic", "\n")]
    [InlineData("Bearer", "\n")]
    [InlineData("bAsIc", "\r\n")]
    [InlineData("bEaReR", "\r\n")]
    public void FoldedHeaders_EverySegmentAndIndentationAreHandled(string scheme, string newline)
    {
        var input = $"file.txt{newline}Authorization: {scheme} FAKE_A=={newline}\tFAKE_B=={newline}  FAKE_C=={newline}next=line{newline}";
        var expected = $"file.txt{newline}Authorization: {scheme} [redacted]{newline}\t[redacted]{newline}  [redacted]{newline}next=line{newline}";
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));
    }

    [Theory]
    [InlineData("GH_TOKEN")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GITHUB_CONFIG_REPO_TOKEN")]
    [InlineData("GITHUB_ORCHESTRATOR_TOKEN")]
    [InlineData("credential")]
    [InlineData("password")]
    [InlineData("token")]
    public void Assignments_ShortValuesAndQuotesAreStructural(string name)
    {
        Assert.Equal($"file.txt {name} = '[redacted]' end", GitToolOutputRedactor.Redact($"file.txt {name} = 'pw' end"));
        Assert.Equal($"file.txt {name}: [redacted]\nnext", GitToolOutputRedactor.Redact($"file.txt {name}: pw\nnext"));
    }

    [Fact]
    public void FailClosedProse_IsDeliberatelyRedacted()
    {
        Assert.Equal("discuss password: [redacted] here", GitToolOutputRedactor.Redact("discuss password: syntax here"));
        Assert.Equal("Authorization: Bearer [redacted] required by server", GitToolOutputRedactor.Redact("Authorization: Bearer is required by server"));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void InventoryBoundary_RawPercentAndPaddedOrUnpaddedBase64(int length)
    {
        var value = "FAKE/a+bZQ"[..length];
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        string[] variants = [value, Percent(value), Percent(value).ToLowerInvariant(), Uri.EscapeDataString(value), encoded, encoded.TrimEnd('=')];
        foreach (var variant in variants)
        {
            var actual = GitToolOutputRedactor.Redact("file.txt | " + variant + " | unchanged", [value]);
            // No structural syntax: the 7-character row must not be exact-replaced.
            Assert.Equal("file.txt | " + (length < 8 ? variant : "[redacted]") + " | unchanged", actual);
        }
    }

    [Fact]
    public void InventoryPercent_IsCaseSensitiveInDecodedValueAndNonRecursive()
    {
        const string value = "FAKE/a+bZQ";
        var once = Percent(value);
        var twice = once.Replace("%", "%25", StringComparison.Ordinal);
        Assert.Equal("file [redacted] end", GitToolOutputRedactor.Redact("file " + once + " end", [value]));
        Assert.Equal("file " + twice + " end", GitToolOutputRedactor.Redact("file " + twice + " end", [value]));
        Assert.Equal("file " + value.ToLowerInvariant() + " end", GitToolOutputRedactor.Redact("file " + value.ToLowerInvariant() + " end", [value]));
    }

    [Fact]
    public void NonAsciiInventory_RawAndBase64Only_PercentLimitationIsExplicit()
    {
        const string value = "FAKE_café_界";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        Assert.Equal("file [redacted] [redacted] end", GitToolOutputRedactor.Redact($"file {value} {encoded} end", [value]));
        // Disclosed limitation: this matcher does not decode multibyte percent escapes.
        var percent = Percent(value);
        Assert.Equal("file " + percent + " end", GitToolOutputRedactor.Redact("file " + percent + " end", [value]));
    }

    [Fact]
    public void InventoryLiteralPercent_OneLevelEscapeMustStillMatch()
    {
        const string value = "FAKE%key";
        var actual = GitToolOutputRedactor.Redact("file FAKE%25key end", [value]);
        Assert.Contains("file ", actual);
        Assert.EndsWith(" end", actual);
        Assert.False(actual.Contains("FAKE%25key", StringComparison.Ordinal), "One-level encoded percent inventory value escaped redaction.");
    }

    [Theory]
    [InlineData("Basic", "\n")]
    [InlineData("Bearer", "\n")]
    [InlineData("Basic", "\r\n")]
    [InlineData("Bearer", "\r\n")]
    public void FoldedHeaders_TrailingHorizontalWhitespaceMustNotExposeContinuation(string scheme, string newline)
    {
        var input = $"file.txt{newline}Authorization: {scheme} FAKE_A== \t{newline}\tFAKE_B=={newline}next=line";
        var actual = GitToolOutputRedactor.Redact(input);
        Assert.Contains("file.txt", actual);
        Assert.Contains("next=line", actual);
        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        Assert.False(actual.Contains("FAKE_A", StringComparison.Ordinal), "First header segment escaped redaction.");
        Assert.False(actual.Contains("FAKE_B", StringComparison.Ordinal), "Whitespace before folding exposed continuation.");
    }

    [Theory]
    [InlineData("FAKE_FIRST")]
    [InlineData("FAKE_MIDDLE")]
    public void FoldedHeaders_InventoryReplacementMustNotPreventStructuralRedaction(string known)
    {
        const string input = "file.txt\nAuthorization: Bearer FAKE_FIRST\n\tFAKE_MIDDLE\n\tFAKE_LAST\nnext=line";
        var actual = GitToolOutputRedactor.Redact(input, [known]);
        Assert.Contains("file.txt", actual);
        Assert.Contains("next=line", actual);
        foreach (var value in new[] { "FAKE_FIRST", "FAKE_MIDDLE", "FAKE_LAST" })
            Assert.False(actual.Contains(value, StringComparison.Ordinal), "Inventory redaction exposed another structural header segment.");
    }

    [Fact]
    public void InventoryBase64_IsStandardAlphabetAndNotRecursivelyDecoded()
    {
        const string value = "FAKE_???";
        var padded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        Assert.Contains("/", padded);
        Assert.EndsWith("=", padded);
        var urlSafe = padded.Replace('/', '_');
        var twice = Convert.ToBase64String(Encoding.UTF8.GetBytes(padded));
        Assert.Equal("file [redacted] [redacted] end", GitToolOutputRedactor.Redact($"file {padded} {padded.TrimEnd('=')} end", [value]));
        Assert.Equal($"file {urlSafe} {twice} end", GitToolOutputRedactor.Redact($"file {urlSafe} {twice} end", [value]));
    }

    [Fact]
    public void Inventory_LongestFirstAndDuplicates_RemoveEntireContainingValue()
    {
        const string inner = "FAKE_INNER";
        const string outer = "FAKE_INNER_suffix";
        Assert.Equal("file [redacted] [redacted] end", GitToolOutputRedactor.Redact($"file {outer} {inner} end", [inner, outer, inner, null, ""]));
    }

    [Theory]
    [InlineData("password = token=FAKE_A", "password = [redacted]")]
    [InlineData("ssh://u:FAKE_A@a.invalid/r,ssh://v:FAKE_B@b.invalid/r", "ssh://u:[redacted]@a.invalid/r,ssh://v:[redacted]@b.invalid/r")]
    [InlineData("u:FAKE_A@a.invalid:r v:FAKE_B@b.invalid:r", "u:[redacted]@a.invalid:r v:[redacted]@b.invalid:r")]
    public void OverlapShapes_DoNotThrowAndPreserveOrdinaryContent(string input, string expected)
    {
        Assert.Equal("file.txt\n" + expected + "\nend", GitToolOutputRedactor.Redact("file.txt\n" + input + "\nend"));
    }

    [Fact]
    public void NestedCredentialAssignment_AllStructuralPasswordsMustBeRemoved()
    {
        var actual = GitToolOutputRedactor.Redact("file.txt\ncredential: password: FAKE_NESTED\nend");
        Assert.Contains("file.txt", actual);
        Assert.Contains("\nend", actual);
        Assert.False(actual.Contains("FAKE_NESTED", StringComparison.Ordinal), "Nested credential assignment escaped redaction.");
    }

    [Fact]
    public void NestedFoldedAuthorization_AllHeaderCredentialsMustBeRemoved()
    {
        var actual = GitToolOutputRedactor.Redact("file.txt\nAuthorization: Bearer FAKE_A\n\tAuthorization: Basic FAKE_B==\nnext=line");
        Assert.Contains("file.txt", actual);
        Assert.Contains("next=line", actual);
        Assert.False(actual.Contains("FAKE_A", StringComparison.Ordinal), "Outer header escaped redaction.");
        Assert.False(actual.Contains("FAKE_B", StringComparison.Ordinal), "Nested header escaped redaction.");
    }

    [Fact]
    public void DenseRepeats_AllCredentialsRemovedAndLineStructurePreserved()
    {
        const string row = "ssh://u:FAKE_A@h.invalid/r u:FAKE_B@h.invalid:r GH_TOKEN=FAKE_C\r\nAuthorization: Basic FAKE_D==\r\n\tFAKE_E==\n";
        var input = "file.txt\r\n" + string.Concat(Enumerable.Repeat(row, 100)) + "end";
        var actual = GitToolOutputRedactor.Redact(input);
        foreach (var value in new[] { "FAKE_A", "FAKE_B", "FAKE_C", "FAKE_D", "FAKE_E" })
            Assert.False(actual.Contains(value, StringComparison.Ordinal), "Dense credential escaped redaction.");
        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        Assert.StartsWith("file.txt\r\n", actual);
        Assert.EndsWith("end", actual);
    }
}
