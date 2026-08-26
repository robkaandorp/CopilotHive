using CopilotHive.Configuration;

namespace CopilotHive.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ConfigRepoUrlSanitizer"/> — the startup guard that turns the raw,
/// possibly credential-bearing <c>--config-repo=</c> operator argument into a value that is
/// safe to log, pass to <see cref="ConfigRepoManager"/>, and provision to workers.
/// <para>
/// Every rejection test asserts the raw input never appears in the exception message; every
/// acceptance test asserts the EXACT sanitized output string.
/// </para>
/// </summary>
public sealed class ConfigRepoUrlSanitizerTests
{
    // ── Rule 1: scp-style normalization happens FIRST ──────────────────────────

    [Fact]
    public void NormalizeScpStyle_UserAtHostColonPath_BecomesSshUrl()
    {
        Assert.Equal(
            "ssh://user@host/org/repo.git",
            ConfigRepoUrlSanitizer.NormalizeScpStyle("user@host:org/repo.git"));
    }

    [Fact]
    public void Sanitize_ScpStyleGitHubRemote_NormalizesToSshUrl()
    {
        Assert.Equal(
            "ssh://git@github.com/org/repo.git",
            ConfigRepoUrlSanitizer.Sanitize("git@github.com:org/repo.git"));
    }

    [Fact]
    public void Sanitize_ScpStyleWithLeadingSlashInPath_NormalizesWithoutDoubleSlash()
    {
        Assert.Equal(
            "ssh://git@github.com/org/repo.git",
            ConfigRepoUrlSanitizer.Sanitize("git@github.com:/org/repo.git"));
    }

    // ── Rule 2: scheme allow-list ──────────────────────────────────────────────

    [Theory]
    [InlineData("git://github.com/org/repo.git")]
    [InlineData("ftp://github.com/org/repo.git")]
    [InlineData("http://github.com/org/repo.git")]
    public void Sanitize_UnsupportedScheme_IsRejected(string input)
    {
        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));
        Assert.DoesNotContain(input, ex.Message);
    }

    // ── Rule 3: HTTPS userinfo is always a rejection ───────────────────────────

    [Fact]
    public void Sanitize_HttpsWithBareToken_IsRejectedAndMessageIsRedacted()
    {
        const string token = "ghp_super_secret_operator_token";
        var input = $"https://{token}@github.com/org/repo.git";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));

        Assert.DoesNotContain(input, ex.Message);
        Assert.DoesNotContain(token, ex.Message);
    }

    [Fact]
    public void Sanitize_HttpsWithUserAndPassword_IsRejectedAndMessageIsRedacted()
    {
        const string password = "p4ssw0rd_secret";
        var input = $"https://user:{password}@github.com/org/repo.git";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));

        Assert.DoesNotContain(input, ex.Message);
        Assert.DoesNotContain(password, ex.Message);
    }

    [Fact]
    public void Sanitize_BareHttpsGitHubUrl_PassesUnchanged()
    {
        Assert.Equal(
            "https://github.com/org/repo.git",
            ConfigRepoUrlSanitizer.Sanitize("https://github.com/org/repo.git"));
    }

    // ── Rule 4: SSH username must be exactly "git"; password is stripped ───────

    [Fact]
    public void Sanitize_SshWithGitUser_PassesUnchanged()
    {
        Assert.Equal(
            "ssh://git@github.com/org/repo.git",
            ConfigRepoUrlSanitizer.Sanitize("ssh://git@github.com/org/repo.git"));
    }

    [Fact]
    public void Sanitize_SshWithNonGitUser_IsRejected()
    {
        const string input = "ssh://wrong@github.com/org/repo.git";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));
        Assert.DoesNotContain(input, ex.Message);
    }

    [Fact]
    public void Sanitize_SshWithoutUser_IsRejected()
    {
        const string input = "ssh://github.com/org/repo.git";

        Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_SshWithPassword_StripsPasswordAndKeepsGitUser()
    {
        const string password = "sekret_ssh_password";

        var result = ConfigRepoUrlSanitizer.Sanitize($"ssh://git:{password}@github.com/org/repo.git");

        Assert.Equal("ssh://git@github.com/org/repo.git", result);
        Assert.DoesNotContain(password, result);
    }

    // ── Rule 5: host must be exactly github.com ────────────────────────────────

    [Theory]
    [InlineData("https://evil.com/org/repo.git")]
    [InlineData("https://github.com.evil.com/org/repo.git")]
    [InlineData("https://notgithub.com/org/repo.git")]
    [InlineData("ssh://git@evil.com/org/repo.git")]
    public void Sanitize_NonGitHubHost_IsRejected(string input)
    {
        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));
        Assert.DoesNotContain(input, ex.Message);
    }

    [Fact]
    public void Sanitize_GitHubHostWithDifferentCasing_IsAccepted()
    {
        Assert.Equal(
            "https://github.com/org/repo.git",
            ConfigRepoUrlSanitizer.Sanitize("https://GitHub.COM/org/repo.git"));
    }

    // ── Rule 6: no query / fragment on network URLs ────────────────────────────

    [Fact]
    public void Sanitize_HttpsWithQuery_IsRejected()
    {
        const string input = "https://github.com/org/repo?access_token=x";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));
        Assert.DoesNotContain("access_token", ex.Message);
    }

    [Fact]
    public void Sanitize_HttpsWithFragment_IsRejected()
    {
        const string input = "https://github.com/org/repo#frag";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));
        Assert.DoesNotContain(input, ex.Message);
    }

    // ── Rule 7: local paths ────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_AbsoluteLocalPath_IsCanonicalized()
    {
        var raw = Path.Combine(Path.GetTempPath(), "config-repo", "..", "config-repo");

        Assert.Equal(Path.GetFullPath(raw), ConfigRepoUrlSanitizer.Sanitize(raw));
    }

    [Theory]
    [InlineData("./my-cfg-repo")]
    [InlineData("my-cfg-repo")]
    [InlineData("../my-cfg-repo")]
    public void Sanitize_RelativeLocalPath_IsRejected(string input)
    {
        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));
        Assert.DoesNotContain(input, ex.Message);
    }

    [Fact]
    public void Sanitize_FileUriWithEmptyAuthority_IsCanonicalized()
    {
        var result = ConfigRepoUrlSanitizer.Sanitize("file:///tmp/config-repo");

        Assert.Equal(Path.GetFullPath("/tmp/config-repo"), result);
    }

    [Fact]
    public void Sanitize_FileUriWithHostAuthority_IsRejected()
    {
        const string input = "file://host/path";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.Sanitize(input));
        Assert.DoesNotContain(input, ex.Message);
    }

    [Fact]
    public void Sanitize_BareFileUri_IsRejected()
    {
        Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.Sanitize("file://"));
    }

    // ── Fix 1 regressions: syntactically present but EMPTY userinfo ────────────

    [Fact]
    public void Sanitize_HttpsWithEmptyUserInfo_IsRejected()
    {
        const string input = "https://@github.com/org/repo.git";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.Sanitize(input));

        Assert.DoesNotContain(input, ex.Message);
        Assert.DoesNotContain(input, ex.ToString());
    }

    [Fact]
    public void Sanitize_SshWithEmptyUserInfo_IsRejected()
    {
        const string input = "ssh://@github.com/org/repo.git";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.Sanitize(input));

        Assert.DoesNotContain(input, ex.Message);
        Assert.DoesNotContain(input, ex.ToString());
    }

    [Fact]
    public void Sanitize_SshWithPasswordButEmptyUserName_IsRejected()
    {
        const string password = "empty_user_password_secret";
        var input = $"ssh://:{password}@github.com/org/repo.git";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.Sanitize(input));

        Assert.DoesNotContain(input, ex.Message);
        Assert.DoesNotContain(password, ex.Message);
        Assert.DoesNotContain(password, ex.ToString());
    }

    [Fact]
    public void Sanitize_HostLookalikeInUserInfo_IsRejected()
    {
        // "github.com" appears only as userinfo; the real host is evil.com.
        const string input = "https://github.com@evil.com/org/repo.git";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.Sanitize(input));

        Assert.DoesNotContain(input, ex.Message);
    }

    // ── Fix 2 regressions: rootless file: URIs must never be canonicalized ─────

    [Theory]
    [InlineData("file:relative/path")]
    [InlineData("file:./relative")]
    [InlineData("file:relative")]
    public void Sanitize_RootlessFileUri_IsRejected(string input)
    {
        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.Sanitize(input));

        Assert.DoesNotContain(input, ex.Message);
        Assert.DoesNotContain(input, ex.ToString());
    }

    [Fact]
    public void Sanitize_RootlessFileUri_IsNotResolvedAgainstCurrentDirectory()
    {
        // A rootless file: URI must NOT fall through to the absolute-local-path branch and
        // become a process-relative absolute path. Proven by the absence of any successful
        // return: the call throws instead of yielding CurrentDirectory/relative.
        var ex = Record.Exception(() => ConfigRepoUrlSanitizer.Sanitize("file:relative/path"));

        Assert.IsType<ConfigRepoUrlSanitizer.RejectedException>(ex);
        Assert.DoesNotContain(Directory.GetCurrentDirectory(), ex.Message);
    }

    [Theory]
    [InlineData("file:///")]
    [InlineData("file:///..")]
    public void Sanitize_FileUriWithEmptyPath_IsRejected(string input)
    {
        Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_FileUriWhoseLocalPathIsNotRooted_IsRejectedInsteadOfResolvedAgainstCwd()
    {
        // "file:///c|/p" is an absolute URI whose LocalPath decodes to "c:\p". On Unix that is
        // NOT a rooted path, so without the rootedness guard Path.GetFullPath would resolve it
        // against the process working directory and ACCEPT it — the rootless-file bypass.
        // On Windows the very same LocalPath IS a rooted drive path, so it is legitimately
        // accepted there; either way the sanitizer must never return a non-rooted result.
        const string input = "file:///c|/p";

        if (OperatingSystem.IsWindows())
        {
            var accepted = ConfigRepoUrlSanitizer.Sanitize(input);
            Assert.NotNull(accepted);
            Assert.True(Path.IsPathRooted(accepted));
            return;
        }

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.Sanitize(input));

        Assert.DoesNotContain(input, ex.ToString());
        Assert.DoesNotContain(Directory.GetCurrentDirectory(), ex.ToString());
    }

    [Theory]
    [InlineData("file:///c|/p")]
    [InlineData("file:relative/path")]
    [InlineData("file:///")]
    [InlineData("file:///abs/path")]
    public void Sanitize_FileUri_NeverReturnsAPathResolvedAgainstTheWorkingDirectory(string input)
    {
        // Invariant across every file: form — the sanitizer either rejects, or returns a ROOTED
        // path that is not the working directory with a relative remainder appended.
        var result = Record.Exception(() => ConfigRepoUrlSanitizer.Sanitize(input)) is null
            ? ConfigRepoUrlSanitizer.Sanitize(input)
            : null;

        if (result is null)
            return;

        Assert.True(Path.IsPathRooted(result));
        Assert.DoesNotContain(Directory.GetCurrentDirectory(), result);
    }

    // ── Fix 3 regressions: canonicalization failures stay redacted ─────────────

    [Fact]
    public void Sanitize_AbsolutePathThatFailsCanonicalization_ThrowsRedactedWithoutInnerException()
    {
        // A rooted path containing a NUL character passes Path.IsPathRooted but makes
        // Path.GetFullPath throw (ArgumentException "Null character in path") on every OS.
        const string marker = "canonicalization_failure_marker";
        var input = $"{Path.DirectorySeparatorChar}tmp{Path.DirectorySeparatorChar}{marker}\0suffix";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.Sanitize(input));

        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(marker, ex.Message);
        Assert.DoesNotContain(marker, ex.ToString());
        Assert.DoesNotContain(input, ex.ToString());
    }

    [Fact]
    public void SanitizeArgs_CanonicalizationFailure_ThrowsRedactedWithoutInnerException()
    {
        const string marker = "args_canonicalization_marker";
        var value = $"{Path.DirectorySeparatorChar}tmp{Path.DirectorySeparatorChar}{marker}\0suffix";

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.SanitizeArgs([$"--config-repo={value}"]));

        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(marker, ex.ToString());
    }

    [Fact]
    public void Sanitize_EveryRejection_ThrowsTheSanitizersRedactedExceptionType()
    {
        // The Program startup catch is narrowed to RejectedException; every rejection class
        // must therefore surface as that exact type, or startup would crash with a framework
        // exception whose message embeds the raw value.
        string[] rejected =
        [
            "https://tok@github.com/org/repo.git",
            "https://@github.com/org/repo.git",
            "ssh://@github.com/org/repo.git",
            "ssh://:pw@github.com/org/repo.git",
            "ssh://wrong@github.com/org/repo.git",
            "ssh://github.com/org/repo.git",
            "https://evil.com/org/repo.git",
            "https://github.com/org/repo?access_token=x",
            "https://github.com/org/repo#frag",
            "git://github.com/org/repo.git",
            "ftp://github.com/org/repo.git",
            "file://host/path",
            "file://",
            "file:///",
            "file:relative/path",
            "relative/path",
            $"{Path.DirectorySeparatorChar}tmp{Path.DirectorySeparatorChar}nul\0char",
        ];

        foreach (var input in rejected)
        {
            var ex = Record.Exception(() => ConfigRepoUrlSanitizer.Sanitize(input));
            Assert.IsType<ConfigRepoUrlSanitizer.RejectedException>(ex);
            Assert.DoesNotContain(input, ex.ToString());
        }
    }

    // ── ABSENT handling ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_AbsentValue_ReturnsNull(string? input)
    {
        Assert.Null(ConfigRepoUrlSanitizer.Sanitize(input));
    }

    // ── Argument scanning (Program-level, no WebApplication boot) ──────────────

    [Fact]
    public void SanitizeArgs_SingleValidArg_ReplacesArgWithSanitizedValue()
    {
        var (value, args) = ConfigRepoUrlSanitizer.SanitizeArgs(
            ["--port=9001", "--config-repo=https://github.com/org/repo.git"]);

        Assert.Equal("https://github.com/org/repo.git", value);
        Assert.Equal(["--port=9001", "--config-repo=https://github.com/org/repo.git"], args);
    }

    [Fact]
    public void SanitizeArgs_CredentialBearingArg_RejectsAndNeverEchoesRawValue()
    {
        const string token = "ghp_never_log_me";
        string[] raw = ["--port=9001", $"--config-repo=https://{token}@github.com/org/repo.git"];

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.SanitizeArgs(raw));

        Assert.DoesNotContain(token, ex.Message);
    }

    [Fact]
    public void SanitizeArgs_DuplicateConfigRepoArgs_AreRejected()
    {
        string[] raw =
        [
            "--config-repo=https://github.com/org/repo.git",
            "--config-repo=https://github.com/org/other.git",
        ];

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() => ConfigRepoUrlSanitizer.SanitizeArgs(raw));
        Assert.DoesNotContain("other.git", ex.Message);
    }

    [Fact]
    public void SanitizeArgs_EmptyValue_IsTreatedAsAbsentAndArgIsRemoved()
    {
        var (value, args) = ConfigRepoUrlSanitizer.SanitizeArgs(["--config-repo=", "--port=9001"]);

        Assert.Null(value);
        Assert.Equal(["--port=9001"], args);
        Assert.DoesNotContain(args, a => a.StartsWith("--config-repo=", StringComparison.Ordinal));
    }

    [Fact]
    public void SanitizeArgs_BareConfigRepoArg_IsRejectedAsUnrecognizedForm()
    {
        Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() =>
            ConfigRepoUrlSanitizer.SanitizeArgs(["--config-repo", "https://github.com/org/repo.git"]));
    }

    [Fact]
    public void SanitizeArgs_DifferentCasing_IsRejectedAsUnrecognizedForm()
    {
        Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(() =>
            ConfigRepoUrlSanitizer.SanitizeArgs(["--Config-Repo=https://github.com/org/repo.git"]));
    }

    [Fact]
    public void SanitizeArgs_NoConfigRepoArg_ReturnsNullAndLeavesArgsUntouched()
    {
        string[] raw = ["--port=9001", "--config-repo-path=./config-repo"];

        var (value, args) = ConfigRepoUrlSanitizer.SanitizeArgs(raw);

        Assert.Null(value);
        Assert.Equal(raw, args);
    }

    [Fact]
    public void SanitizeArgs_ConfigRepoPathArg_IsNotMistakenForConfigRepoArg()
    {
        var (value, args) = ConfigRepoUrlSanitizer.SanitizeArgs(
            ["--config-repo-path=./config-repo", "--config-repo=https://github.com/org/repo.git"]);

        Assert.Equal("https://github.com/org/repo.git", value);
        Assert.Contains("--config-repo-path=./config-repo", args);
    }

    [Fact]
    public void SanitizeArgs_SshWithPassword_PutsOnlyTheStrippedValueIntoArgs()
    {
        const string password = "ssh_password_secret";

        var (value, args) = ConfigRepoUrlSanitizer.SanitizeArgs(
            [$"--config-repo=ssh://git:{password}@github.com/org/repo.git"]);

        Assert.Equal("ssh://git@github.com/org/repo.git", value);
        Assert.Equal(["--config-repo=ssh://git@github.com/org/repo.git"], args);
        Assert.DoesNotContain(args, a => a.Contains(password, StringComparison.Ordinal));
    }

    // ── Startup ingestion regressions (SanitizeArgs is the Program seam) ───────

    [Fact]
    public void SanitizeArgs_CredentialBearingInput_NeverLeaksRawValueInMessageOrToString()
    {
        const string token = "ghp_startup_ingestion_token";
        string[] raw = ["--port=9001", $"--config-repo=https://{token}@github.com/org/repo.git"];

        var ex = Assert.Throws<ConfigRepoUrlSanitizer.RejectedException>(
            () => ConfigRepoUrlSanitizer.SanitizeArgs(raw));

        Assert.DoesNotContain(token, ex.Message);
        Assert.DoesNotContain(token, ex.ToString());
        Assert.DoesNotContain(raw[1], ex.ToString());
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void SanitizeArgs_ReturnsFreshArray_LeavingTheCallersArrayUnmodified()
    {
        // The contract is a FRESH array: the caller's array is never mutated in place, and the
        // returned array is a different instance carrying only the sanitized value.
        string[] raw = ["--port=9001", "--config-repo=ssh://git:pw@github.com/org/repo.git"];
        var original = (string[])raw.Clone();

        var (value, sanitizedArgs) = ConfigRepoUrlSanitizer.SanitizeArgs(raw);

        Assert.Equal(original, raw);
        Assert.NotSame(raw, sanitizedArgs);
        Assert.Equal("ssh://git@github.com/org/repo.git", value);
        Assert.DoesNotContain(sanitizedArgs, a => a.Contains("pw@", StringComparison.Ordinal));
    }

    [Fact]
    public void SanitizeArgs_SanitizedArgs_ContainAtMostOneConfigRepoArgHoldingOnlyTheSanitizedValue()
    {
        var (value, args) = ConfigRepoUrlSanitizer.SanitizeArgs(
            ["--config-repo=ssh://git:leaked_password@github.com/org/repo.git", "--port=9001"]);

        var configArgs = args.Where(a => a.StartsWith("--config-repo=", StringComparison.Ordinal)).ToArray();
        var only = Assert.Single(configArgs);
        Assert.Equal($"--config-repo={value}", only);
        Assert.DoesNotContain("leaked_password", string.Join(' ', args));
    }
}
