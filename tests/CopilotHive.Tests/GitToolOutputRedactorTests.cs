using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Focused unit tests for <see cref="GitToolOutputRedactor"/> — the ONE credential-redaction
/// transform the Composer's git tools apply to anything that becomes model-visible output.
/// Every vector is pure text in, pure text out: no live credentials, no network, no timing. The
/// Tester phase owns the adversarial/coverage battery; these pin the transform's contract.
/// </summary>
[Collection(GitOutputInventoryCollection.Name)]
public sealed class GitToolOutputRedactorTests
{
    // ── Null / empty / unchanged ─────────────────────────────────────────────

    [Fact]
    public void Redact_Null_ReturnsEmpty() => Assert.Equal(string.Empty, GitToolOutputRedactor.Redact(null));

    [Fact]
    public void Redact_Empty_ReturnsEmpty() =>
        Assert.Equal(string.Empty, GitToolOutputRedactor.Redact(string.Empty));

    [Fact]
    public void Redact_OrdinaryGitOutput_IsUnchanged()
    {
        const string output =
            "commit 3f2a1b\n"
            + "Author: Test <test@test.com>\n"
            + "Date:   Mon Jan 1 00:00:00 2024 +0000\n"
            + "\n"
            + "    Initial commit\n"
            + "\n"
            + " README.md | 3 +++\n";

        Assert.Equal(output, GitToolOutputRedactor.Redact(output));
    }

    // ── Composed HTTP/HTTPS URL grammar ──────────────────────────────────────

    [Theory]
    [InlineData(
        "fatal: unable to access 'https://x-access-token:gho_secret@github.com/o/r.git/': 403",
        "fatal: unable to access 'https://github.com/o/r.git/': 403")]
    [InlineData("https://github.com/o/r?token=gho_secret&ref=main", "https://github.com/o/r?ref=main")]
    public void Redact_HttpUrlCredentials_AreRemoved(string input, string expected) =>
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));

    // ── SSH / scp credential syntax ──────────────────────────────────────────

    [Theory]
    [InlineData("ssh://deploy:hunter2@example.com:22/repo.git", "ssh://deploy:[redacted]@example.com:22/repo.git")]
    [InlineData("ssh://deploy:hunter2@example.com/repo.git", "ssh://deploy:[redacted]@example.com/repo.git")]
    [InlineData("deploy:hunter2@example.com:org/repo.git", "deploy:[redacted]@example.com:org/repo.git")]
    public void Redact_SshPasswordUserInfo_IsRemoved(string input, string expected) =>
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input));

    [Theory]
    [InlineData("git clone git@github.com:acme/widgets.git")]
    [InlineData("ssh://git@github.com/acme/widgets.git")]
    [InlineData("ssh://github.com/acme/widgets.git")]
    [InlineData("deploy@example.com:org/repo.git")]
    public void Redact_PasswordlessUserInfo_IsPreserved(string input) =>
        Assert.Equal(input, GitToolOutputRedactor.Redact(input));

    // ── Folded authorization headers ─────────────────────────────────────────

    [Fact]
    public void Redact_FoldedBearerHeader_RedactsEverySegmentAndKeepsLineStructure()
    {
        const string input =
            "extraheader=Authorization: Bearer gho_aaa\n"
            + "\tZ2hsbG9uZ2VyYmFzZTY0c2Vjb25k\n"
            + "\tUGFydE9mVGhlSGVhZGVy\n"
            + "next=line\n";

        var redacted = GitToolOutputRedactor.Redact(input);

        Assert.DoesNotContain("gho_aaa", redacted);
        Assert.DoesNotContain("Z2hsbG9uZ2VyYmFzZTY0c2Vjb25k", redacted);
        Assert.DoesNotContain("UGFydE9mVGhlSGVhZGVy", redacted);
        Assert.Equal(input.Count(c => c == '\n'), redacted.Count(c => c == '\n'));
        Assert.Contains("next=line", redacted);
    }

    [Fact]
    public void Redact_BasicHeader_RedactsTheCredential()
    {
        var redacted = GitToolOutputRedactor.Redact("Authorization: Basic dXNlcjpwYXNzd29yZA==");

        Assert.DoesNotContain("dXNlcjpwYXNzd29yZA", redacted);
        Assert.Contains("Authorization: Basic [redacted]", redacted);
    }

    [Fact]
    public void Redact_AuthorizationHeaderValue_IsRedactedToTheEndOfTheTokenRun()
    {
        // Fail-closed: an authorization header value is a credential by construction, so the
        // token run is redacted even when it is not obviously a secret. Surrounding text outside
        // the value survives.
        var redacted = GitToolOutputRedactor.Redact("Authorization: Bearer abc123 [see logs]");

        Assert.Equal("Authorization: Bearer [redacted] [see logs]", redacted);
    }

    [Fact]
    public void Redact_BearerWordWithoutAHeader_IsUnchanged()
    {
        const string input = "the Bearer scheme is unsupported here";

        Assert.Equal(input, GitToolOutputRedactor.Redact(input));
    }

    // ── Token / credential assignments ───────────────────────────────────────

    [Theory]
    [InlineData("GH_TOKEN=gho_env_secret_value")]
    [InlineData("GITHUB_TOKEN=gho_env_secret_value")]
    [InlineData("GITHUB_CONFIG_REPO_TOKEN=gho_env_secret_value")]
    [InlineData("GITHUB_ORCHESTRATOR_TOKEN=gho_env_secret_value")]
    public void Redact_EnvironmentTokenAssignment_RedactsTheValue(string input)
    {
        var redacted = GitToolOutputRedactor.Redact(input);

        Assert.DoesNotContain("gho_env_secret_value", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Theory]
    [InlineData("token = ghp_gitconfigsecret")]
    [InlineData("  credential: ghp_gitconfigsecret")]
    [InlineData("password=ghp_gitconfigsecret")]
    [InlineData("api_key = ghp_gitconfigsecret")]
    public void Redact_GitConfigStyleAssignment_RedactsTheValue(string input)
    {
        var redacted = GitToolOutputRedactor.Redact(input);

        Assert.DoesNotContain("ghp_gitconfigsecret", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void Redact_QuotedAssignment_RedactsInsideTheQuotes()
    {
        var redacted = GitToolOutputRedactor.Redact("password = \"ghp_quoted_secret\"");

        Assert.DoesNotContain("ghp_quoted_secret", redacted);
        Assert.Contains("password = \"[redacted]\"", redacted);
    }

    [Theory]
    [InlineData("the token is expired")]
    [InlineData("tokens available: none")]
    [InlineData("mypassword=not-a-name-boundary")]
    public void Redact_ProseOrNonBoundaryName_IsUnchanged(string input) =>
        Assert.Equal(input, GitToolOutputRedactor.Redact(input));

    // ── Known-secret inventory ───────────────────────────────────────────────

    [Fact]
    public void Redact_InventoryValue_IsReplacedRawPercentAndBase64()
    {
        const string secret = "gho_inventory_secret_value";
        var raw = $"leak {secret}";
        var percent = $"leak {Uri.EscapeDataString(secret)}";
        var base64 = $"leak {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret))}";
        var unpadded = base64.TrimEnd('=').Replace("leak ", "leak ", StringComparison.Ordinal);

        string[] inventory = [secret];

        Assert.DoesNotContain(secret, GitToolOutputRedactor.Redact(raw, inventory));
        Assert.DoesNotContain(Uri.EscapeDataString(secret), GitToolOutputRedactor.Redact(percent, inventory));
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret))
            .TrimEnd('='), GitToolOutputRedactor.Redact(unpadded, inventory));
    }

    [Fact]
    public void Redact_OneLevelPercentEncodedSecret_IsReplacedWithoutRecursiveDecoding()
    {
        // The password is percent-encoded exactly once inside the URL.
        const string secret = "gho_pct/secret+value";
        var encoded = Uri.EscapeDataString(secret);

        var redacted = GitToolOutputRedactor.Redact($"leak {encoded}", [secret]);

        Assert.Contains("[redacted]", redacted);
        Assert.DoesNotContain(encoded, redacted);
        Assert.DoesNotContain(secret, redacted);
    }

    [Fact]
    public void Redact_ShortInventoryValue_IsNotExactReplaced()
    {
        // A 7-character "secret" is below the bound, so no literal replacement happens...
        var redacted = GitToolOutputRedactor.Redact("value abcdefg here", ["abcdefg"]);

        Assert.Equal("value abcdefg here", redacted);
    }

    [Fact]
    public void Redact_ShortInventoryValueAtTheBound_IsReplaced()
    {
        var redacted = GitToolOutputRedactor.Redact("value abcdefgh here", ["abcdefgh"]);

        Assert.DoesNotContain("abcdefgh", redacted);
    }

    [Fact]
    public void Redact_ShortStructuralSecret_IsStillRedacted()
    {
        // Below the inventory bound, but a structural pattern still removes it.
        var redacted = GitToolOutputRedactor.Redact("https://user:pw@example.com/r.git");

        Assert.Equal("https://example.com/r.git", redacted);
    }

    // ── Line structure ───────────────────────────────────────────────────────

    /// <summary>
    /// The transform NEVER adds or removes a line break, which is what lets a caller truncate the
    /// redacted text and report its own line count: the count is identical whether the caller
    /// redacts before or after splitting.
    /// </summary>
    [Theory]
    [InlineData("a\nhttps://user:pw@example.com/r.git\nb\n")]
    [InlineData("Authorization: Bearer abc\n\tdef\n\tghi\nnext\n")]
    [InlineData("GH_TOKEN=gho_line_structure_secret\nline2\nline3")]
    [InlineData("deploy:pw@example.com:org/repo.git\nline2")]
    [InlineData("no credentials at all\nline2\n")]
    public void Redact_PreservesTheLineCount(string input)
    {
        var redacted = GitToolOutputRedactor.Redact(input);

        Assert.Equal(input.Count(c => c == '\n'), redacted.Count(c => c == '\n'));
    }

    // ── Inventory source ─────────────────────────────────────────────────────

    [Fact]
    public void ReadEnvironmentSecrets_ReturnsOneEntryPerInventoryName()
    {
        var values = GitToolOutputRedactor.ReadEnvironmentSecrets();

        Assert.Equal(GitToolOutputRedactor.InventoryEnvironmentVariableNames.Count, values.Count);
    }

    [Fact]
    public void ReadEnvironmentSecrets_ReadsTheCurrentValue()
    {
        const string variable = "GITHUB_CONFIG_REPO_TOKEN";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "gho_fresh_read_probe");
            var values = GitToolOutputRedactor.ReadEnvironmentSecrets();

            var index = GitToolOutputRedactor.InventoryEnvironmentVariableNames
                .ToList()
                .IndexOf(variable);
            Assert.Equal("gho_fresh_read_probe", values[index]);

            // ...and a LATER change is observed too: nothing is cached.
            Environment.SetEnvironmentVariable(variable, "gho_second_probe_value");
            values = GitToolOutputRedactor.ReadEnvironmentSecrets();
            Assert.Equal("gho_second_probe_value", values[index]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }
}
