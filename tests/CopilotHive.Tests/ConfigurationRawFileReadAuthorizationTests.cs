using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// REMOVAL-PROOF SOURCE-CONTRACT tests for the CONFIGURATION PAGE's raw configuration-file read.
/// <para>
/// Before this fix the page read the entire <c>hive-config.yaml</c> — and every
/// <c>agents/*.agents.md</c> — straight off disk in <c>LoadFilesAsync</c> and rendered them through
/// its file tabs to ANY authenticated role. That was an identity-free bypass of the
/// <see cref="CopilotHive.Security.HiveCapability.GlobalAdmin"/> requirement every other
/// configuration projection enforces, because the raw files ARE a full config projection.
/// </para>
/// <para>
/// The read now goes through the identity-aware <see cref="IConfigFacade.GetConfigFilesAsync"/>,
/// which re-resolves the caller's current role/enabled state and requires GlobalAdmin BEFORE the file
/// system is touched. These tests pin that the direct disk read is GONE, that the facade call carries
/// the circuit identity, and that a refusal is handled without crashing and without content. The
/// behavioural half (denial, empty-repo success, admin/open-mode success) is covered by
/// <see cref="ReleaseConfigAuthorizationReviewRegressionTests"/>.
/// </para>
/// </summary>
public sealed class ConfigurationRawFileReadAuthorizationTests
{
    /// <summary>Reads the actual <c>Configuration.razor</c> source by walking up to the repo root.</summary>
    private static string ReadConfigurationSource()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot is not null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
            repoRoot = Directory.GetParent(repoRoot)?.FullName;

        Assert.NotNull(repoRoot);
        var path = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", "Configuration.razor");
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Extracts the source text of a method by signature, so an assertion targets the method body
    /// rather than a doc comment elsewhere in the file (brace-matched, so a nested block cannot end
    /// the extraction early).
    /// </summary>
    private static string ExtractMethodSource(string source, string methodSignature)
    {
        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{methodSignature}' not found in Configuration.razor");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"Opening brace for '{methodSignature}' not found");

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(braceStart, i - braceStart + 1);
            }
        }

        Assert.Fail($"Could not find the matching closing brace for '{methodSignature}'.");
        return string.Empty; // unreachable
    }

    /// <summary>
    /// THE RAW FILE READ IS THE AUTHORIZED FACADE OPERATION: <c>LoadFilesAsync</c> calls
    /// <c>IConfigFacade.GetConfigFilesAsync</c>, passing the circuit identity acquired through the
    /// page's single helper. Reverting to a direct <c>File.ReadAllTextAsync</c> /
    /// <c>Directory.GetFiles</c> read fails this test.
    /// </summary>
    [Fact]
    public void LoadFilesAsync_ReadsThroughTheAuthorizedFacade_WithTheCircuitIdentity()
    {
        var source = ReadConfigurationSource();
        var method = ExtractMethodSource(source, "private async Task LoadFilesAsync()");

        // The identity-aware facade call, with the identity.
        Assert.Contains("ConfigFacade.GetConfigFilesAsync(", method, StringComparison.Ordinal);
        Assert.Contains("await ActingUserAsync()", method, StringComparison.Ordinal);

        // NO direct file-system read of the configuration files survives.
        Assert.DoesNotContain("File.ReadAllTextAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllText", method, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.GetFiles", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigRepo.LocalPath", method, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllTextAsync(configPath", method, StringComparison.Ordinal);
    }

    /// <summary>
    /// A REFUSAL IS HANDLED, NOT THROWN, AND YIELDS NO CONTENT: the failure branch clears the loaded
    /// file map and reports through the page's EXISTING sanitized error surface, so a denied caller
    /// neither crashes the renderer nor retains any file text.
    /// </summary>
    [Fact]
    public void LoadFilesAsync_FailureBranch_ClearsContentAndUsesTheExistingErrorSurface()
    {
        var source = ReadConfigurationSource();
        var method = ExtractMethodSource(source, "private async Task LoadFilesAsync()");

        // The result is checked before use.
        Assert.Contains("if (!filesResult.Success || filesResult.Value is null)", method, StringComparison.Ordinal);

        // The failure branch clears the map FIRST, so no stale content can survive a later demotion...
        Assert.Contains("_files.Clear();", method, StringComparison.Ordinal);

        // ...and reports through the page's pre-existing error field (never a new denial-state surface).
        Assert.Contains("_error = filesResult.Error;", method, StringComparison.Ordinal);

        // Success is driven ONLY by the facade's own returned files.
        Assert.Contains("foreach (var file in filesResult.Value)", method, StringComparison.Ordinal);
        Assert.Contains("_files[file.Name] = file.Content;", method, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PAGE HOLDS NO OTHER RAW READ OF THE CONFIGURATION FILES: no other method in the page reads
    /// <c>hive-config.yaml</c> or an <c>agents</c> directory off disk, so the gated operation is the
    /// ONLY path by which that content can reach the page. The informational config-repo path display
    /// in the markup (a directory NAME, not file content) is not a read and is left alone.
    /// </summary>
    [Fact]
    public void ThePage_HasNoOtherRawReadOfTheConfigurationFiles()
    {
        var source = ReadConfigurationSource();

        // The content-bearing read verbs must not appear anywhere in the page's code.
        Assert.DoesNotContain("ReadAllTextAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllText(", source, StringComparison.Ordinal);

        // Nor may the agents directory be enumerated.
        Assert.DoesNotContain("*.agents.md", source, StringComparison.Ordinal);

        // The ONLY facade operation used to obtain the file bodies is the gated one.
        Assert.Contains("GetConfigFilesAsync(", source, StringComparison.Ordinal);
    }
}
