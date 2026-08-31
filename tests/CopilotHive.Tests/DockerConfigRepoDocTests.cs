namespace CopilotHive.Tests;

/// <summary>
/// Doc-content tests pinning the required documentation for the config-repo worker access:
/// <c>docker/.env.example</c> and the worker section of <c>docker/README.md</c>.
/// <para>
/// <b>Removal-proof strategy: scoped extraction.</b> Whole-file substring checks are vacuous —
/// an unrelated mention elsewhere in the README satisfies them. Each of the five required points
/// is therefore asserted inside the PARAGRAPH that carries it, within the worker config-repo
/// SECTION (located by its heading and sliced to the next heading). Deleting a point removes its
/// anchor paragraph, so the extraction fails the test; weakening a point removes one of the
/// required substrings from that paragraph, so the assertion fails.
/// </para>
/// <para>
/// The <c>.env.example</c> assertions additionally pin ADJACENCY (the verbatim comment sits on
/// the line immediately before the <c>GH_TOKEN=</c> line) and reject any
/// <c>GITHUB_CONFIG_REPO_TOKEN</c> ASSIGNMENT line — the variable is set only on the git child
/// process environment and must never appear as an operator setting.
/// </para>
/// </summary>
public sealed class DockerConfigRepoDocTests
{
    /// <summary>The heading that opens the worker config-repo access section.</summary>
    private const string WorkerConfigRepoHeading = "## Worker Config Repo Access";

    private static string RepoRoot()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);
        return repoRoot;
    }

    private static string ReadRepoFile(params string[] relativePath)
    {
        var path = Path.Combine([RepoRoot(), .. relativePath]);
        Assert.True(File.Exists(path), $"File not found at {path}");
        return File.ReadAllText(path);
    }

    private static string[] ReadRepoFileLines(params string[] relativePath)
    {
        var path = Path.Combine([RepoRoot(), .. relativePath]);
        Assert.True(File.Exists(path), $"File not found at {path}");
        return File.ReadAllLines(path);
    }

    // ── Scoped extraction helpers ─────────────────────────────────────────────

    /// <summary>
    /// Returns the worker config-repo section: everything from its heading up to (excluding) the
    /// next <c>##</c>-level heading. Assertions scoped here cannot be satisfied by text that
    /// lives anywhere else in the README.
    /// </summary>
    private static string ExtractWorkerConfigRepoSection(string readme)
    {
        var start = readme.IndexOf(WorkerConfigRepoHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Heading '{WorkerConfigRepoHeading}' not found in docker/README.md");

        var bodyStart = start + WorkerConfigRepoHeading.Length;
        var next = readme.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return next < 0 ? readme[bodyStart..] : readme[bodyStart..next];
    }

    /// <summary>
    /// Returns the single paragraph (blank-line-delimited block) of the worker config-repo
    /// section that contains <paramref name="anchor"/>. Fails when no paragraph carries the
    /// anchor — which is exactly what happens when the point is deleted — and when more than
    /// one does, so an assertion can never be satisfied by a different point's paragraph.
    /// </summary>
    private static string ExtractPointParagraph(string readme, string anchor)
    {
        var section = ExtractWorkerConfigRepoSection(readme);
        var paragraphs = section
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        var matches = paragraphs
            .Where(p => p.Contains(anchor, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            matches.Length == 1,
            $"Expected exactly ONE paragraph in '{WorkerConfigRepoHeading}' containing the anchor " +
            $"'{anchor}', but found {matches.Length}.");

        return matches[0];
    }

    private static void AssertAllPresent(string scope, string pointName, params string[] required)
    {
        foreach (var fragment in required)
        {
            Assert.True(
                scope.Contains(fragment, StringComparison.Ordinal),
                $"The '{pointName}' point is missing the required content '{fragment}'. Scoped text was:\n{scope}");
        }
    }

    // ── docker/.env.example ───────────────────────────────────────────────────

    /// <summary>
    /// The verbatim comment — exact text, including both em-dash characters — must sit on the
    /// line IMMEDIATELY BEFORE the <c>GH_TOKEN=</c> line, so it is read as the token line's
    /// annotation rather than floating anywhere in the file.
    /// </summary>
    [Fact]
    public void EnvExample_VerbatimConfigRepoTokenComment_SitsImmediatelyAboveGhTokenLine()
    {
        const string verbatimComment =
            "# GITHUB_CONFIG_REPO_TOKEN is NOT an operator setting — CopilotHive sets it only " +
            "on the git child process environment (child-process-only). Do not set it here.";

        var lines = ReadRepoFileLines("docker", ".env.example");

        var ghTokenIndex = Array.FindIndex(
            lines, l => l.TrimStart().StartsWith("GH_TOKEN=", StringComparison.Ordinal));
        Assert.True(ghTokenIndex >= 0, "No GH_TOKEN= assignment line found in docker/.env.example");
        Assert.True(ghTokenIndex >= 1, "The GH_TOKEN= line has no preceding line to carry the comment.");

        Assert.Equal(verbatimComment, lines[ghTokenIndex - 1].Trim());
    }

    /// <summary>
    /// <c>GITHUB_CONFIG_REPO_TOKEN</c> must never appear as an ASSIGNMENT (an operator setting):
    /// CopilotHive sets it only on the git child process environment. Comment lines mentioning
    /// it are fine — the check targets assignment lines only.
    /// </summary>
    [Fact]
    public void EnvExample_ContainsNoConfigRepoTokenAssignment()
    {
        var lines = ReadRepoFileLines("docker", ".env.example");

        var assignments = lines
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .Where(l => l.StartsWith("GITHUB_CONFIG_REPO_TOKEN=", StringComparison.Ordinal)
                     || l.StartsWith("export GITHUB_CONFIG_REPO_TOKEN=", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            assignments.Length == 0,
            $"docker/.env.example must not assign GITHUB_CONFIG_REPO_TOKEN, but found: {string.Join(" | ", assignments)}");
    }

    // ── docker/README.md — the five required points, each scoped ───────────────

    /// <summary>
    /// Point 1 — the per-assignment preparation: the entrypoint no longer clones; WorkerService
    /// probes the config repo and clones it if absent PER TASK ASSIGNMENT, using the
    /// owned-container staging directory and an atomic move, before the assignment's TaskExecutor
    /// runs.
    /// </summary>
    [Fact]
    public void Readme_CoversPerAssignmentPreparation()
    {
        var readme = ReadRepoFile("docker", "README.md");
        var paragraph = ExtractPointParagraph(readme, "per task assignment");

        AssertAllPresent(
            paragraph,
            "per-assignment preparation",
            "no longer clones",      // the entrypoint's clone is gone
            "WorkerService",         // the owner of the preparation
            "probes",                // the probe
            "clones it if absent",   // the clone-if-absent semantics
            "per task assignment",   // the required terminology
            "staging",               // the owned-container staging
            "atomic move",           // the atomic move
            "before",                // ordering relative to the TaskExecutor
            "TaskExecutor");
    }

    /// <summary>
    /// Point 2 — the child-process-only askpass mechanism: the worker sets
    /// <c>GITHUB_CONFIG_REPO_TOKEN</c> only on the git child process environment; the token-free
    /// helper reads it at invocation and answers git's prompts (<c>$1</c> username →
    /// <c>x-access-token</c>, password → the environment variable's value).
    /// </summary>
    [Fact]
    public void Readme_CoversChildProcessOnlyAskpassMechanism()
    {
        var readme = ReadRepoFile("docker", "README.md");
        var paragraph = ExtractPointParagraph(readme, "askpass");

        AssertAllPresent(
            paragraph,
            "askpass mechanism",
            "GITHUB_CONFIG_REPO_TOKEN",
            "child process environment",   // the scope of the variable
            "child-process-only",          // named explicitly
            "token-free",                  // the helper holds no token itself
            "at invocation",               // it reads the variable when git invokes it
            "$1 = \"Username\"",           // the username prompt mapping…
            "x-access-token",              // …and its answer
            "$1 = \"Password\"");          // the password prompt mapping
    }

    /// <summary>
    /// Point 3 — the hooks trust decision: the config repository and its local hooks are TRUSTED;
    /// credential-scoped git child processes may execute repository hooks.
    /// </summary>
    [Fact]
    public void Readme_CoversHooksTrustDecision()
    {
        var readme = ReadRepoFile("docker", "README.md");
        var paragraph = ExtractPointParagraph(readme, "hooks");

        AssertAllPresent(
            paragraph,
            "hooks trust decision",
            "TRUSTED",
            "hooks",
            "config repository",
            "may execute");
    }

    /// <summary>
    /// Point 4 — the <c>url.*.insteadOf</c> rewriting caveat: the seam verifies the TEXTUAL
    /// origin; the operator's own git configuration is trusted.
    /// </summary>
    [Fact]
    public void Readme_CoversInsteadOfCaveat()
    {
        var readme = ReadRepoFile("docker", "README.md");
        var paragraph = ExtractPointParagraph(readme, "insteadOf");

        AssertAllPresent(
            paragraph,
            "url.*.insteadOf caveat",
            "insteadOf",
            "TEXTUAL",
            "trusted");
    }

    /// <summary>
    /// Point 5 — the task-repo compatibility note: the five-variable environment scrub means a
    /// task repository relying on the worker's own <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> loses that
    /// authentication; task-repo credentials come via the per-task provisioned URL.
    /// </summary>
    [Fact]
    public void Readme_CoversTaskRepoCompatibilityNote()
    {
        var readme = ReadRepoFile("docker", "README.md");
        var paragraph = ExtractPointParagraph(readme, "scrub");

        AssertAllPresent(
            paragraph,
            "task-repo compatibility note",
            "GH_TOKEN",
            "GITHUB_TOKEN",
            "GIT_ASKPASS",
            "GITHUB_CONFIG_REPO_TOKEN",
            "GIT_TERMINAL_PROMPT",          // the five scrubbed variables
            "loses",                        // the consequence for the task repo
            "per-task provisioned URL");    // where task-repo credentials come from instead
    }
}