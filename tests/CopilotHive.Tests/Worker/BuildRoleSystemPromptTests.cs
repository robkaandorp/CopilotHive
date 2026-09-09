using System.Globalization;
using CopilotHive.Worker;
using CopilotHive.Workers;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for the <c>BuildRoleSystemPrompt</c> method on <see cref="SharpCoderRunner"/>.
/// Verifies that each role gets the correct hardcoded prompt, the infrastructure rules preamble
/// is always present, and AGENTS.md content is appended correctly under the heuristics separator.
/// </summary>
public sealed class BuildRoleSystemPromptTests
{
    // ── Shared infrastructure rules ───────────────────────────────────────────

    /// <summary>
    /// Every role's prompt must contain the infrastructure rules preamble that forbids
    /// <c>git push</c>, <c>git checkout</c>, <c>git branch</c>, and <c>git switch</c>.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.DocWriter)]
    [InlineData(WorkerRole.Improver)]
    public void BuildRoleSystemPrompt_AllRoles_ContainInfrastructureRules(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("INFRASTRUCTURE RULES", prompt);
        Assert.Contains("NEVER run `git push`", prompt);
        Assert.Contains("NEVER run `git checkout`", prompt);
        Assert.Contains("request_clarification", prompt);
    }

    /// <summary>
    /// The shared preamble must instruct workers to call <c>report_narrative</c>.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Coder_ContainsReportNarrativeInstruction()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, null);

        Assert.Contains("report_narrative", prompt);
        Assert.Contains("2-5 sentences", prompt);
    }

    /// <summary>
    /// The shared preamble must instruct workers to call <c>raise_issue</c> for
    /// out-of-scope code quality problems, bugs, suggestions, concerns, or workflow issues.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.DocWriter)]
    [InlineData(WorkerRole.Improver)]
    public void BuildRoleSystemPrompt_AllRoles_ContainRaiseIssueGuidance(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("raise_issue", prompt);
        Assert.Contains("code quality problems, bugs, suggestions, concerns, or workflow issues", prompt);
        Assert.Contains("Do not fix them yourself unless they directly block the goal", prompt);
    }

    /// <summary>
    /// The <c>report_narrative</c> guidance lives in the shared preamble, so every role
    /// must receive the instruction regardless of which role-specific prompt is generated.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.DocWriter)]
    [InlineData(WorkerRole.Improver)]
    public void BuildRoleSystemPrompt_AllRoles_ContainNarrativeGuidance(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("report_narrative", prompt);
        Assert.Contains("2-5 sentences", prompt);
    }

    // ── Role identity ─────────────────────────────────────────────────────────

    /// <summary>
    /// The Coder prompt must contain the role identity and <c>report_code_changes</c> tool reference.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Coder_ContainsRoleIdentityAndToolContract()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, null);

        Assert.Contains("# Coder", prompt);
        Assert.Contains("report_code_changes", prompt);
    }

    /// <summary>
    /// The Tester prompt must contain the role identity and <c>report_test_results</c> tool reference.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Tester_ContainsRoleIdentityAndToolContract()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Tester, null);

        Assert.Contains("# Tester", prompt);
        Assert.Contains("report_test_results", prompt);
        Assert.Contains("Acceptance Criteria Verification", prompt);
    }

    /// <summary>
    /// The Reviewer prompt must contain the role identity, <c>report_review_verdict</c> tool
    /// reference, and the CRITICAL/MAJOR/MINOR severity legend.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Reviewer_ContainsRoleIdentityAndToolContract()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Reviewer, null);

        Assert.Contains("# Reviewer", prompt);
        Assert.Contains("report_review_verdict", prompt);
        Assert.Contains("CRITICAL", prompt);
        Assert.Contains("MAJOR", prompt);
        Assert.Contains("MINOR", prompt);
        Assert.Contains("Acceptance Criteria Verification", prompt);
    }

    /// <summary>
    /// The DocWriter prompt must contain the role identity and <c>report_doc_changes</c> tool reference.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_DocWriter_ContainsRoleIdentityAndToolContract()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.DocWriter, null);

        Assert.Contains("# Doc Writer", prompt);
        Assert.Contains("report_doc_changes", prompt);
    }

    /// <summary>
    /// The Improver prompt must contain the role identity, the character limit,
    /// and the constraint against removing safety rules.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Improver_ContainsRoleIdentityAndConstraints()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Improver, null);

        Assert.Contains("# Improver", prompt);
        Assert.Contains(WorkerConstants.AgentsMdMaxCharacters.ToString(CultureInfo.InvariantCulture), prompt);
        Assert.Contains("safety constraints", prompt);
        Assert.Contains("test requirements or output format compliance.", prompt);
        Assert.DoesNotContain("tool call contracts", prompt);
        Assert.Contains("enable_file_writes=true", prompt);
        Assert.Contains("sub-agents", prompt);
        Assert.Contains("Do not ask the orchestrator to apply file", prompt);
        Assert.Contains("file reading and editing only", prompt);
    }

    /// <summary>
    /// The complete Improver prompt must deliver the append-new/compress-old contract together
    /// with the existing scope, delegation, anti-duplication, and safety constraints. Supplying
    /// learned guidance also proves the role policy remains composed before that appendix.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Improver_DeliversAppendNewTopFirstPolicyAndPreservedConstraints()
    {
        const string LearnedRule = "Always preserve this learned rule.";
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Improver, LearnedRule);
        var normalizedPrompt = string.Join(' ',
            prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var configuredLimit = WorkerConstants.AgentsMdMaxCharacters.ToString(CultureInfo.InvariantCulture);

        Assert.Contains($"MUST NOT exceed {configuredLimit} characters", normalizedPrompt, StringComparison.Ordinal);
        Assert.Contains("UTF-16 code units", prompt, StringComparison.Ordinal);

        Assert.Contains("Read the existing files and check their current sizes", prompt, StringComparison.Ordinal);
        Assert.Contains("call the `get_file_sizes` tool before making any edit", prompt, StringComparison.Ordinal);
        Assert.Contains("call it again", prompt, StringComparison.Ordinal);
        Assert.Contains("after you re-read a file you changed", prompt, StringComparison.Ordinal);
        Assert.Contains("genuinely new", prompt, StringComparison.Ordinal);
        Assert.Contains("broadly applicable", prompt, StringComparison.Ordinal);
        Assert.Contains("not already covered by an existing rule", prompt, StringComparison.Ordinal);
        Assert.Contains("concise, readable Markdown", prompt, StringComparison.Ordinal);

        Assert.Contains("Add new lessons at the END of the file", prompt, StringComparison.Ordinal);
        Assert.Contains("Never interleave", prompt, StringComparison.Ordinal);
        Assert.Contains("never prepend them", prompt, StringComparison.Ordinal);
        Assert.Contains("unchanged", prompt, StringComparison.Ordinal);
        Assert.Contains("byte-for-byte", prompt, StringComparison.Ordinal);
        Assert.Contains("including during any later attempt", prompt, StringComparison.Ordinal);

        Assert.Contains("work from the TOP", prompt, StringComparison.Ordinal);
        Assert.Contains("existing/older material downward", prompt, StringComparison.Ordinal);
        Assert.Contains("first consolidate and compress the older", prompt, StringComparison.Ordinal);
        Assert.Contains("remove the oldest material", prompt, StringComparison.Ordinal);
        Assert.Contains("redundant or obsolete", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not meet the cap by truncating a whole file", prompt, StringComparison.Ordinal);
        Assert.Contains("modifying, removing or reordering the new lessons", prompt, StringComparison.Ordinal);
        Assert.Contains("weakening protected", prompt, StringComparison.Ordinal);
        Assert.Contains("cryptic abbreviations", prompt, StringComparison.Ordinal);
        Assert.Contains("symbol-heavy shorthand", prompt, StringComparison.Ordinal);
        Assert.Contains("ordinary-language bullets", prompt, StringComparison.Ordinal);
        Assert.Contains("useful headings", normalizedPrompt, StringComparison.Ordinal);
        Assert.Contains("After editing, re-read each changed file", prompt, StringComparison.Ordinal);
        Assert.Contains("new lessons are intact", prompt, StringComparison.Ordinal);
        Assert.Contains("older guidance is still readable", prompt, StringComparison.Ordinal);

        Assert.Contains("cannot run shell commands", prompt.Replace("**", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Only edit `*.agents.md` files", prompt, StringComparison.Ordinal);
        Assert.Contains("do not create new files, rename files, or touch anything", prompt, StringComparison.Ordinal);
        Assert.Contains("enable_file_writes=true", prompt, StringComparison.Ordinal);
        Assert.Contains("do not duplicate it", prompt, StringComparison.Ordinal);
        Assert.Contains("must NOT be added", prompt, StringComparison.Ordinal);
        Assert.Contains("specific file, class, method, past", prompt, StringComparison.Ordinal);
        Assert.Contains("incident advice", prompt, StringComparison.Ordinal);
        Assert.Contains("Never remove or weaken safety constraints", prompt, StringComparison.Ordinal);
        Assert.Contains("git workflow", prompt, StringComparison.Ordinal);
        Assert.Contains("test requirements or output format compliance", prompt, StringComparison.Ordinal);

        var policyIndex = prompt.IndexOf("## Guidance update policy", StringComparison.Ordinal);
        var heuristicsIndex = prompt.IndexOf("# Learned Heuristics", StringComparison.Ordinal);
        var learnedRuleIndex = prompt.IndexOf(LearnedRule, StringComparison.Ordinal);
        Assert.True(policyIndex >= 0, "The Improver policy heading must be present.");
        Assert.True(heuristicsIndex > policyIndex, "Learned Heuristics must follow the hardcoded Improver policy.");
        Assert.True(learnedRuleIndex > heuristicsIndex, "Supplied learned guidance must follow its heading.");
        Assert.EndsWith(LearnedRule, prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Improver prompt must forbid changelog-style entries and instruct the improver
    /// to extract actionable guidance rules instead.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Improver_ContainsAntiChangelogGuidance()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Improver, null);

        Assert.Contains("Do NOT add \"Iteration History\" or changelog-style entries", prompt);
        Assert.Contains("guidance rules and quality standards, not logs of past iterations", prompt);
        Assert.Contains("Extract actionable lessons from the", prompt);
        Assert.Contains("\"Always check X before Y\"", prompt);
        Assert.Contains("\"When doing Z, prefer approach", prompt);
        Assert.Contains("do not duplicate it", prompt);
        Assert.Contains("broadly-applicable software-development patterns", prompt);
        Assert.Contains("\"the TCS-block in ClassB\"", prompt);
        Assert.Contains("do not rewrite them into one-off", prompt);
        Assert.Contains("incident advice.", prompt);
    }

    // ── Learned heuristics appendix ───────────────────────────────────────────

    /// <summary>
    /// When <c>agentsMdContent</c> is non-empty, the returned prompt must contain
    /// the <c># Learned Heuristics</c> separator followed by the supplied content.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_WithAgentsMd_AppendsHeuristicsSeparatorAndContent()
    {
        const string AgentsMd = "Always write unit tests first.";

        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, AgentsMd);

        Assert.Contains("\n\n# Learned Heuristics\n\n", prompt);
        Assert.Contains(AgentsMd, prompt);
    }

    /// <summary>
    /// The heuristics section must appear AFTER the hardcoded prompt content,
    /// not before it.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_WithAgentsMd_HeuristicsAppearsAfterHardcodedContent()
    {
        const string AgentsMd = "Some learned rule.";

        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Tester, AgentsMd);

        var heuristicsIndex = prompt.IndexOf("# Learned Heuristics", StringComparison.Ordinal);
        var infraIndex = prompt.IndexOf("INFRASTRUCTURE RULES", StringComparison.Ordinal);

        Assert.True(infraIndex >= 0, "INFRASTRUCTURE RULES must be present");
        Assert.True(heuristicsIndex > infraIndex,
            "# Learned Heuristics must appear after the hardcoded infrastructure rules");
    }

    // ── Null / empty AGENTS.md handling ──────────────────────────────────────

    /// <summary>
    /// When <c>agentsMdContent</c> is <c>null</c>, no heuristics separator must
    /// appear and the prompt must still be non-empty and valid.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_NullAgentsMd_NoHeuristicsSeparator()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, null);

        Assert.DoesNotContain("# Learned Heuristics", prompt);
        Assert.False(string.IsNullOrWhiteSpace(prompt));
    }

    /// <summary>
    /// When <c>agentsMdContent</c> is an empty string, no heuristics separator
    /// must appear and the prompt must still be non-empty and valid.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_EmptyAgentsMd_NoHeuristicsSeparator()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Reviewer, string.Empty);

        Assert.DoesNotContain("# Learned Heuristics", prompt);
        Assert.False(string.IsNullOrWhiteSpace(prompt));
    }

    /// <summary>
    /// When <c>agentsMdContent</c> is whitespace-only, it is treated as empty
    /// and no heuristics separator must appear.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_WhitespaceAgentsMd_NoHeuristicsSeparator()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.DocWriter, "   \n\t  ");

        Assert.DoesNotContain("# Learned Heuristics", prompt);
    }

    // ── Coder targeted-subset / no-coverage contract ─────────────────────────

    /// <summary>
    /// The Coder prompt must instruct the coder to run a TARGETED subset of tests for
    /// the change instead of the full suite. Removal-proof: fails if the targeted-subset
    /// instruction sentence is removed from the prompt.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Coder_ContainsTargetedSubsetInstruction()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, null);

        Assert.Contains("Run a TARGETED subset of tests for your change", prompt);
        Assert.Contains("the test skill's targeted-subset section", prompt);
    }

    /// <summary>
    /// The Coder prompt must forbid attaching a coverage collector — coverage is opt-in.
    /// Removal-proof: fails if the no-coverage instruction sentence is removed.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Coder_ContainsNoCoverageCollectorInstruction()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, null);

        Assert.Contains("Do NOT attach a coverage collector", prompt);
        Assert.Contains("coverage is opt-in and not needed for your report", prompt);
    }

    /// <summary>
    /// The Coder prompt must NOT contain an affirmative, coder-directed full-suite
    /// instruction. The new targeted-subset sentence legitimately mentions the full suite
    /// twice in negation ("NOT the full suite", "the tester phase runs the authoritative
    /// full suite"), so the absence assertion only targets affirmatively directed
    /// full-suite phrasings.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Coder_DoesNotInstructFullSuiteRun()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, null);

        Assert.DoesNotContain("Run the full suite", prompt);
        Assert.DoesNotContain("run the full suite", prompt);
        Assert.DoesNotContain("run all tests", prompt);
        Assert.DoesNotContain("run all tests with coverage", prompt);
        Assert.DoesNotContain("run the FULL suite", prompt);
    }

    /// <summary>
    /// The Tester prompt must NOT receive the Coder-only targeted-subset or no-coverage
    /// instructions, and must retain its existing authoritative full-suite testing role.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Tester_DoesNotContainCoderTargetedSubsetInstructions()
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Tester, null);

        Assert.DoesNotContain("Run a TARGETED subset of tests for your change", prompt);
        Assert.DoesNotContain("Do NOT attach a coverage collector", prompt);
        Assert.DoesNotContain("targeted run is a pre-commit self-check", prompt);

        // The Tester remains the authoritative full-suite phase.
        Assert.Contains("comprehensive testing of the codebase", prompt);
        Assert.Contains("verify that the system actually works as a whole", prompt);
    }

    // ── Unknown role guard ────────────────────────────────────────────────────

    /// <summary>
    /// An unhandled <see cref="WorkerRole"/> value must throw <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_UnknownRole_ThrowsInvalidOperationException()
    {
        var unknownRole = (WorkerRole)999;

        Assert.Throws<InvalidOperationException>(
            () => SharpCoderRunner.BuildRoleSystemPrompt(unknownRole, null));
    }

    // ── Validation-run guidance (Coder and Tester) ─────────────────────────────

    /// <summary>
    /// Both Coder and Tester prompts must instruct the agent to select a 15-minute
    /// foreground command budget (<c>timeout_ms=900000</c> passed to
    /// <c>execute_bash_command</c>) before known multi-minute commands, and must make
    /// clear it is an optional positive-millisecond tool argument rather than a dotnet
    /// flag, AgentOptions property, sub-agent timeout, or test assertion timeout.
    /// Removal-proof: fails if the budget-selection guidance is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_ContainTimeoutBudgetSelection(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("timeout_ms=900000", prompt, StringComparison.Ordinal);
        Assert.Contains("15 minutes", prompt, StringComparison.Ordinal);
        Assert.Contains("execute_bash_command", prompt, StringComparison.Ordinal);
        // It is a tool-call argument, not a dotnet flag, AgentOptions property,
        // sub-agent session timeout, or test assertion timeout.
        Assert.Contains("not a dotnet CLI flag", prompt, StringComparison.Ordinal);
        Assert.Contains("not an AgentOptions property", prompt, StringComparison.Ordinal);
        Assert.Contains("not a sub-agent", prompt, StringComparison.Ordinal);
        Assert.Contains("not a test assertion timeout", prompt, StringComparison.Ordinal);
        Assert.Contains("positive milliseconds", prompt, StringComparison.Ordinal);
        // The budget applies before known long commands: full builds and full validation runs.
        Assert.Contains("full build", prompt, StringComparison.Ordinal);
        Assert.Contains("full test-suite validation run", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must explain that each shell call starts fresh,
    /// so there must be no cross-call shell-variable or job-state assumptions.
    /// Removal-proof: fails if the fresh-shell explanation is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_ContainFreshShellWarning(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("starts a FRESH shell", prompt, StringComparison.Ordinal);
        Assert.Contains("no cross-call shell", prompt, StringComparison.Ordinal);
        Assert.Contains("background-job state", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must require the absolute log path to be known
    /// BEFORE the long call — allocated in an earlier short call or chosen as a recorded
    /// unique literal path — because the SDK can discard the long call's stdout on timeout.
    /// Removal-proof: fails if the known-log-path requirement is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_RequireKnownLogPathBeforeLongCall(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("BEFORE launching the long call", prompt, StringComparison.Ordinal);
        Assert.Contains("recorded unique literal path", prompt, StringComparison.Ordinal);
        Assert.Contains("earlier", prompt, StringComparison.Ordinal);
        Assert.Contains("discard", prompt, StringComparison.Ordinal);
        Assert.Contains("timeout", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must require first-attempt, per-attempt unique log
    /// capture, an explicit completion marker written only after the validation process
    /// finishes, storage outside tracked source, and evidence preservation before teardown.
    /// Removal-proof: fails if the evidence-capture guidance is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_RequireFirstAttemptEvidenceCapture(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("FIRST attempt", prompt, StringComparison.Ordinal);
        Assert.Contains("unique per attempt", prompt, StringComparison.Ordinal);
        Assert.Contains("keep previous attempts separate", prompt, StringComparison.Ordinal);
        Assert.Contains("completion marker", prompt, StringComparison.Ordinal);
        Assert.Contains("ONLY after the validation process has finished", prompt, StringComparison.Ordinal);
        Assert.Contains("/tmp", prompt, StringComparison.Ordinal);
        Assert.Contains("before container teardown", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must state that logs surviving a command timeout do
    /// not imply crash or container durability.
    /// Removal-proof: fails if that caveat is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_StateLogsSurvivingTimeoutAreNotDurabilityProof(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("Logs surviving a command", prompt, StringComparison.Ordinal);
        Assert.Contains("timeout do NOT imply process survival, crash survival, or container durability", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must require preserving the original validation exit
    /// status (including nonzero outcomes): capture it immediately, append the marker, show
    /// a bounded tail, and exit with the original status.
    /// Removal-proof: fails if the exit-status preservation guidance is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_RequireExitStatusPreservation(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("preserve the original validation exit status", prompt, StringComparison.Ordinal);
        Assert.Contains("nonzero", prompt, StringComparison.Ordinal);
        Assert.Contains("exit with the ORIGINAL status", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must forbid unguarded tee pipelines, <c>&amp;&amp;</c> chains that
    /// drop failure evidence, and treating a tail/echo success as proof the validation
    /// passed. A tail must be described as only a display preview.
    /// Removal-proof: fails if the pipeline/tail guardrails are removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_ForbidEvidenceDroppingPipelines(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("tee", prompt, StringComparison.Ordinal);
        Assert.Contains("&&", prompt, StringComparison.Ordinal);
        Assert.Contains("drop failure evidence", prompt, StringComparison.Ordinal);
        Assert.Contains("echo success", prompt, StringComparison.Ordinal);
        Assert.Contains("only a display preview", prompt, StringComparison.Ordinal);
        Assert.Contains("COMPLETE log", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must allow --no-build reuse only when the existing
    /// build matches the tested revision and configuration — no stale-build shortcuts.
    /// Removal-proof: fails if the no-stale-build rule is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_RestrictNoBuildReuse(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("--no-build", prompt, StringComparison.Ordinal);
        Assert.Contains("revision", prompt, StringComparison.Ordinal);
        Assert.Contains("configuration", prompt, StringComparison.Ordinal);
        Assert.Contains("no stale-build shortcuts", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must require sequential execution in one workspace
    /// with no backgrounded or parallel duplicate validation.
    /// Removal-proof: fails if the sequential-execution rule is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_RequireSequentialValidation(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("SEQUENTIALLY", prompt, StringComparison.Ordinal);
        Assert.Contains("one workspace", prompt, StringComparison.Ordinal);
        Assert.Contains("background validation", prompt, StringComparison.Ordinal);
        Assert.Contains("parallel duplicate validation", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must require treating timeouts, cancellations, or a
    /// missing completion marker as incomplete validation, checking for surviving processes
    /// before rerunning, and never claiming a timeout guarantees process-tree cleanup.
    /// Removal-proof: fails if the incomplete-validation rule is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_RequireIncompleteValidationHandling(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("INCOMPLETE", prompt, StringComparison.Ordinal);
        Assert.Contains("missing completion marker", prompt, StringComparison.Ordinal);
        Assert.Contains("process or its descendants", prompt, StringComparison.Ordinal);
        Assert.Contains("never claim that a timeout guarantees process-tree cleanup", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must require truthful reporting when completion cannot
    /// be established: report the interruption and evidence/log path, never PASS or invented
    /// failing-test counts, using FAIL with a "validation incomplete" explanation under the
    /// existing binary report contract, distinguished from observed assertion failures.
    /// Removal-proof: fails if the truthful-reporting rule is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_RequireTruthfulIncompleteReporting(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("report the interruption and the available evidence/log path", prompt, StringComparison.Ordinal);
        Assert.Contains("never PASS", prompt, StringComparison.Ordinal);
        Assert.Contains("invented failing-test counts", prompt, StringComparison.Ordinal);
        Assert.Contains("\"validation incomplete\"", prompt, StringComparison.Ordinal);
        Assert.Contains("distinguished from observed", prompt, StringComparison.Ordinal);
        Assert.Contains("assertion failures", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both Coder and Tester prompts must forbid combining multiple runs into a fictitious
    /// single-suite total.
    /// Removal-proof: fails if the no-combined-runs rule is removed.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Tester)]
    public void BuildRoleSystemPrompt_CoderAndTester_ForbidCombiningRunsIntoSingleSuiteTotal(WorkerRole role)
    {
        var prompt = SharpCoderRunner.BuildRoleSystemPrompt(role, null);

        Assert.Contains("fictitious single-suite total", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Tester prompt must receive full-suite validation-run guidance affirmatively,
    /// while the Coder prompt must NOT receive any direction to run the full suite —
    /// the Coder's targeted pre-commit self-check role must be preserved. The Coder's
    /// budget guidance legitimately names long commands generically, so the absence
    /// assertion targets affirmatively coder-directed full-suite phrasings only.
    /// Removal-proof: fails if the Tester full-suite guidance or the Coder role
    /// separation is removed.
    /// </summary>
    [Fact]
    public void BuildRoleSystemPrompt_Tester_ReceivesFullSuiteGuidance_CoderDoesNot()
    {
        var testerPrompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Tester, null);
        var coderPrompt = SharpCoderRunner.BuildRoleSystemPrompt(WorkerRole.Coder, null);

        // Tester: full-suite validation runs are part of its authoritative role.
        Assert.Contains("full test-suite validation run", testerPrompt, StringComparison.Ordinal);

        // Coder: still targeted-subset only; the guidance must not direct full-suite testing.
        Assert.DoesNotContain("Run the full suite", coderPrompt);
        Assert.DoesNotContain("run the full suite", coderPrompt);
        Assert.DoesNotContain("run all tests", coderPrompt);
        Assert.DoesNotContain("Run a TARGETED subset", testerPrompt, StringComparison.Ordinal);
        Assert.Contains("Run a TARGETED subset of tests for your change", coderPrompt, StringComparison.Ordinal);
    }
}
