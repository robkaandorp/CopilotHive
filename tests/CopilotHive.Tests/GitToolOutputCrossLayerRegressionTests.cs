using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Cross-layer regressions for <see cref="GitToolOutputRedactor"/>: the inventory layer runs first
/// and leaves a <c>[redacted]</c> placeholder in the text, and the structural layer must not be
/// blinded by it; likewise a longer encoded form of an inventory value must win over a shorter raw
/// prefix of it. All values are synthetic stand-ins, not live credentials.
/// </summary>
public sealed class GitToolOutputCrossLayerRegressionTests
{
    // ── Inventory placeholder must not blind SSH / scp structural password redaction ─────────

    [Fact]
    public void SshInventoryUsername_DoesNotBlindThePasswordRedaction()
    {
        // The inventory value is the USER part; the structural password after it must still go.
        Assert.Equal(
            "ssh://[redacted]:[redacted]@h.invalid/r",
            GitToolOutputRedactor.Redact("ssh://FAKE_KNOWN_KEY:FAKE_PASSWORD@h.invalid/r", ["FAKE_KNOWN_KEY"]));
    }

    [Fact]
    public void SshInventoryPasswordPrefix_RemovesTheWholePasswordAsOnePlaceholder()
    {
        Assert.Equal(
            "ssh://u:[redacted]@h.invalid/r",
            GitToolOutputRedactor.Redact("ssh://u:FAKE_KNOWN_KEYFAKE_TAIL@h.invalid/r", ["FAKE_KNOWN_KEY"]));
    }

    [Fact]
    public void ScpInventoryPasswordPrefix_RemovesTheWholePasswordAsOnePlaceholder()
    {
        Assert.Equal(
            "u:[redacted]@h.invalid:r",
            GitToolOutputRedactor.Redact("u:FAKE_KNOWN_KEYFAKE_TAIL@h.invalid:r", ["FAKE_KNOWN_KEY"]));
    }

    [Fact]
    public void ScpInventoryUsername_DoesNotBlindThePasswordRedaction()
    {
        Assert.Equal(
            "[redacted]:[redacted]@h.invalid:r",
            GitToolOutputRedactor.Redact("FAKE_KNOWN_KEY:FAKE_PASSWORD@h.invalid:r", ["FAKE_KNOWN_KEY"]));
    }

    [Fact]
    public void SshInventoryInsideTheHost_DoesNotBlindThePasswordRedaction()
    {
        Assert.Equal(
            "ssh://u:[redacted]@[redacted]/r",
            GitToolOutputRedactor.Redact("ssh://u:FAKE_PASSWORD@FAKE_KNOWN_KEY/r", ["FAKE_KNOWN_KEY"]));
    }

    [Theory]
    [InlineData("ssh://FAKE_KNOWN_KEY:FAKE_PASSWORD@h.invalid/r", "ssh://FAKE_KNOWN_KEY:[redacted]@h.invalid/r")]
    [InlineData("u:FAKE_KNOWN_KEYFAKE_TAIL@h.invalid:r", "u:[redacted]@h.invalid:r")]
    [InlineData("ssh://git@h.invalid/r", "ssh://git@h.invalid/r")]
    [InlineData("git@h.invalid:org/r", "git@h.invalid:org/r")]
    public void SshAndScp_WithoutInventory_AreUnchangedByTheCrossLayerFix(string input, string expected)
    {
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));
    }

    [Theory]
    [InlineData("ssh://git@h.invalid/r")]
    [InlineData("git@h.invalid:org/r")]
    public void PasswordlessUsernames_StayPreservedEvenWithAnUnrelatedInventory(string input)
    {
        Assert.Equal(input, GitToolOutputRedactor.Redact(input, ["FAKE_KNOWN_KEY"]));
    }

    [Fact]
    public void SshCrossLayer_PreservesLineStructure()
    {
        const string input = "a\nssh://FAKE_KNOWN_KEY:FAKE_PASSWORD@h.invalid/r\nb\nu:FAKE_KNOWN_KEYFAKE_TAIL@h.invalid:r\nc";
        var actual = GitToolOutputRedactor.Redact(input, ["FAKE_KNOWN_KEY"]);

        Assert.Equal(input.Count(c => c == '\n'), actual.Count(c => c == '\n'));
        Assert.DoesNotContain("FAKE_PASSWORD", actual);
        Assert.DoesNotContain("FAKE_TAIL", actual);
    }

    // ── A whole encoded form must win over a shorter raw prefix ──────────────────────────────

    [Fact]
    public void Base64SelfPrefix_Padded_IsRemovedWhole()
    {
        // "Vm0wd2Qy" is itself valid base64; its padded base64 form starts with the raw value.
        Assert.Equal("[redacted]", GitToolOutputRedactor.Redact("Vm0wd2QyUXk=", ["Vm0wd2Qy"]));
    }

    [Fact]
    public void Base64SelfPrefix_Unpadded_IsRemovedWhole()
    {
        Assert.Equal("[redacted]", GitToolOutputRedactor.Redact("Vm0wd2QyUXk", ["Vm0wd2Qy"]));
    }

    [Fact]
    public void Base64SelfPrefix_InsideSurroundingText_LeavesNoEncodedTail()
    {
        Assert.Equal(
            "file.txt\n[redacted]\nend-marker",
            GitToolOutputRedactor.Redact("file.txt\nVm0wd2QyUXk=\nend-marker", ["Vm0wd2Qy"]));
    }

    [Theory]
    [InlineData("Vm0wd2Qy")]
    [InlineData("RkFLRV9LRVk=")]
    public void BaseControls_RawAndOrdinaryBase64_StillRedact(string input)
    {
        Assert.Equal("[redacted]", GitToolOutputRedactor.Redact(input, [input == "Vm0wd2Qy" ? input : "FAKE_KEY"]));
    }

    [Fact]
    public void Base64Bounds_UrlSafeAlphabetAndShortValues_AreStillNotMatched()
    {
        // A value of 7 characters is below the inventory bound: no exact replacement, no base64.
        Assert.Equal("Vm0wd2Q", GitToolOutputRedactor.Redact("Vm0wd2Q", ["Vm0wd2Q"]));

        // Standard alphabet only: the URL-safe spelling of a '/'-bearing encoding is not decoded.
        const string value = "FAKE_???";
        var padded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        var urlSafe = padded.Replace('/', '_');
        Assert.Equal(urlSafe, GitToolOutputRedactor.Redact(urlSafe, [value]));
    }
}
