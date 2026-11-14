using System.Text;
using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Pins that an inventory value which COINCIDES with a structural recognition marker (the header
/// name "Authorization", the assignment name "GITHUB_TOKEN", the "ssh://u:" prefix) must not blind
/// the structural layer to the credential that follows it. Every row keeps an ordinary
/// inventory-bearing line next to the structural one so an inventory failure cannot be hidden by
/// structural redaction. All values are synthetic stand-ins, not live credentials.
/// </summary>
public sealed class GitToolOutputMarkerShapedInventoryTests
{
    private const string Private = "FAKE_PRIVATE_VALUE";

    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));

    // ── The four reviewer vectors, exact canonical outputs ───────────────────────────────────

    [Fact]
    public void AuthorizationHeaderName_AsInventory_StillRedactsTheBearerCredential() =>
        Assert.Equal(
            "[redacted]: Bearer [redacted]",
            GitToolOutputRedactor.Redact($"Authorization: Bearer {Private}", ["Authorization"]));

    [Fact]
    public void ProxyAuthorizationHeaderName_AsInventory_StillRedactsTheBasicCredential() =>
        Assert.Equal(
            "[redacted]: Basic [redacted]",
            GitToolOutputRedactor.Redact($"Proxy-Authorization: Basic {Private}==", ["Proxy-Authorization"]));

    [Fact]
    public void GithubTokenName_AsInventory_StillRedactsTheAssignedValue() =>
        Assert.Equal(
            "[redacted]=[redacted]",
            GitToolOutputRedactor.Redact($"GITHUB_TOKEN={Private}", ["GITHUB_TOKEN"]));

    [Fact]
    public void SshUserPrefix_AsInventory_KeepsTwoPlaceholdersForTouchingSpans() =>
        // The inventory span [0,8) only TOUCHES the structural password span, so two placeholders.
        Assert.Equal(
            "[redacted][redacted]@h.invalid/r",
            GitToolOutputRedactor.Redact($"ssh://u:{Private}@h.invalid/r", ["ssh://u:"]));

    // ── No-inventory positive controls (the structural layer alone) ──────────────────────────

    [Theory]
    [InlineData("Authorization: Bearer FAKE_PRIVATE_VALUE", "Authorization: Bearer [redacted]")]
    [InlineData("Proxy-Authorization: Basic FAKE_PRIVATE_VALUE==", "Proxy-Authorization: Basic [redacted]")]
    [InlineData("GITHUB_TOKEN=FAKE_PRIVATE_VALUE", "GITHUB_TOKEN=[redacted]")]
    [InlineData("ssh://u:FAKE_PRIVATE_VALUE@h.invalid/r", "ssh://u:[redacted]@h.invalid/r")]
    public void WithoutInventory_StructuralLayerAloneRedactsTheCredential(string input, string expected) =>
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));

    // ── Marker-shaped variants I probed beyond the reported four ─────────────────────────────

    [Theory]
    [InlineData("authorization", "authorization: Bearer FAKE_PRIVATE_VALUE", "[redacted]: Bearer [redacted]")]
    [InlineData("AUTHORIZATION", "AUTHORIZATION: Basic FAKE_PRIVATE_VALUE==", "[redacted]: Basic [redacted]")]
    [InlineData("proxy-authorization", "proxy-authorization: Bearer FAKE_PRIVATE_VALUE", "[redacted]: Bearer [redacted]")]
    public void CaseVariantsOfTheHeaderName_AsInventory_StillRedactTheCredential(string marker, string input, string expected) =>
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input, [marker]));

    [Theory]
    // Every row is a structural assignment NAME of at least 8 characters, so it is both an eligible
    // inventory value and a recognition marker. (GH_TOKEN is exactly at the 8-character bound.)
    [InlineData("GH_TOKEN")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GITHUB_CONFIG_REPO_TOKEN")]
    [InlineData("GITHUB_ORCHESTRATOR_TOKEN")]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("client_secret")]
    [InlineData("password")]
    [InlineData("api_token")]
    public void StructuralAssignmentNames_AsInventory_StillRedactTheAssignedValue(string name)
    {
        var actual = GitToolOutputRedactor.Redact($"{name}={Private}", [name]);

        Assert.DoesNotContain(Private, actual);
        Assert.EndsWith("=[redacted]", actual);
    }

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GITHUB_CONFIG_REPO_TOKEN")]
    [InlineData("GITHUB_ORCHESTRATOR_TOKEN")]
    public void AssignmentNameAsInventory_WithColonOperator_StillRedactsTheValue(string name)
    {
        var actual = GitToolOutputRedactor.Redact($"{name}: {Private}", [name]);

        Assert.DoesNotContain(Private, actual);
        Assert.Equal("[redacted]: [redacted]", actual);
    }

    [Fact]
    public void ScpUserPasswordPrefix_AsInventory_StillRedactsTheScpPassword()
    {
        // The inventory value is the "user:" marker (9 characters, so eligible); the password after
        // it must still go, as a SECOND placeholder because the two spans merely touch.
        var actual = GitToolOutputRedactor.Redact($"deployer:{Private}@h.invalid:org/r", ["deployer:"]);

        Assert.DoesNotContain(Private, actual);
        Assert.Equal("[redacted][redacted]@h.invalid:org/r", actual);
    }

    [Fact]
    public void FoldedHeader_WithTheNameAsInventory_StillRedactsEveryContinuationSegment()
    {
        var input = $"file.txt\nAuthorization: Bearer {Private}\n\tFAKE_SECOND_SEGMENT\nend-marker";
        var actual = GitToolOutputRedactor.Redact(input, ["Authorization"]);

        Assert.DoesNotContain(Private, actual);
        Assert.DoesNotContain("FAKE_SECOND_SEGMENT", actual);
        Assert.Contains("file.txt", actual);
        Assert.Contains("end-marker", actual);
        Assert.Equal(input.Count(c => c == '\n'), actual.Count(c => c == '\n'));
    }

    // ── Percent / base64 forms of a marker-shaped value must not blind anything (controls) ────

    [Fact]
    public void PercentEscapedMarker_AsInventory_DoesNotBlindTheStructuralLayer()
    {
        var input = $"{Percent("Authorization")}: x\nAuthorization: Bearer {Private}";
        var actual = GitToolOutputRedactor.Redact(input, ["Authorization"]);

        Assert.DoesNotContain(Private, actual);
        Assert.DoesNotContain(Percent("Authorization"), actual);
    }

    [Fact]
    public void Base64OfTheMarker_AsInventory_DoesNotBlindTheStructuralLayer()
    {
        var input = $"{B64("GITHUB_TOKEN")} GITHUB_TOKEN={Private}";
        var actual = GitToolOutputRedactor.Redact(input, ["GITHUB_TOKEN"]);

        Assert.DoesNotContain(Private, actual);
        Assert.DoesNotContain(B64("GITHUB_TOKEN"), actual);
        Assert.Equal("[redacted] [redacted]=[redacted]", actual);
    }

    // ── An ordinary inventory-bearing line beside the structural one ─────────────────────────

    [Fact]
    public void OrdinaryInventoryLine_BesideAMarkerShapedRow_IsStillRedactedByTheInventory()
    {
        const string ordinary = "FAKE_ORDINARY_INVENTORY";
        var input = $"Authorization: Bearer {Private}\nnote {ordinary} end\nGITHUB_TOKEN={Private}";
        var actual = GitToolOutputRedactor.Redact(input, ["Authorization", "GITHUB_TOKEN", ordinary]);

        Assert.Equal("[redacted]: Bearer [redacted]\nnote [redacted] end\n[redacted]=[redacted]", actual);
    }

    [Fact]
    public void InventoryValueInsideAStructuralValue_IsAbsorbedIntoOnePlaceholder()
    {
        // The inventory value sits INSIDE a value the structural layer already claims: the spans
        // overlap and must merge into a single placeholder rather than splicing the text twice.
        var actual = GitToolOutputRedactor.Redact("GITHUB_TOKEN=FAKE_PREFIX_FAKE_INNER_KEY_SUFFIX", ["FAKE_INNER_KEY"]);

        Assert.Equal("GITHUB_TOKEN=[redacted]", actual);
    }

    // ── Invariants the single-pass design must keep ───────────────────────────────────────────

    [Fact]
    public void MarkerShapedRows_PreserveTheLineStructure()
    {
        var input = $"a\r\nAuthorization: Bearer {Private}\r\nGITHUB_TOKEN={Private}\nssh://u:{Private}@h.invalid/r\nz";
        var actual = GitToolOutputRedactor.Redact(input, ["Authorization", "GITHUB_TOKEN", "ssh://u:"]);

        Assert.Equal(input.Where(c => c is '\r' or '\n'), actual.Where(c => c is '\r' or '\n'));
        Assert.DoesNotContain(Private, actual);
    }

    [Theory]
    [InlineData("git@h.invalid:org/r")]
    [InlineData("ssh://git@h.invalid/r")]
    public void PasswordlessUsernames_StayPreservedWhenTheInventoryHoldsAMarker(string input) =>
        Assert.Equal(input, GitToolOutputRedactor.Redact(input, ["GITHUB_TOKEN", "Authorization"]));

    [Fact]
    public void ShortMarkerShapedValues_AreStillNotExactReplaced()
    {
        // "password" (8) is eligible; "token" (5) is below the bound and must NOT be exact-replaced,
        // although the structural layer still redacts its assigned value.
        Assert.Equal("token=[redacted]", GitToolOutputRedactor.Redact($"token={Private}", ["token"]));
        Assert.Equal("see token here", GitToolOutputRedactor.Redact("see token here", ["token"]));
    }
}
