using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Text-level tests for <see cref="BrainPromptBuilder.BuildPlanningPrompt"/> ensuring the planning
/// prompt documents the block-based plan grammar (R1-R7) and the phase-name rejection rules.
/// </summary>
public sealed class BrainPlanningPromptTests
{
    private static string BuildPrompt()
    {
        var pipeline = new GoalPipeline(new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["repo"],
        });

        return BrainPromptBuilder.BuildPlanningPrompt(pipeline);
    }

    private static string BuildRetryPrompt()
    {
        var pipeline = new GoalPipeline(new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["repo"],
        });

        // Iteration is computed from IterationBudget.Used + 1. Consume one use so Iteration == 2.
        pipeline.IterationBudget.TryConsume();

        return BrainPromptBuilder.BuildPlanningPrompt(pipeline);
    }

    /// <summary>
    /// Builds the planning prompt with every run of whitespace collapsed to a single space, so
    /// assertions can match a full guidance sentence that the raw string literal wraps across
    /// several indented source lines.
    /// </summary>
    private static string BuildPromptCollapsed() =>
        System.Text.RegularExpressions.Regex.Replace(BuildPrompt(), @"\s+", " ");

    [Fact]
    public void BuildPlanningPrompt_ContainsR1OccupancyRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R1 (Occupancy)", prompt);
        Assert.Contains("at least one Coding or DocWriting", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR2ContentBlockRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R2 (Testing after each content block)", prompt);
        Assert.Contains("content block", prompt);
        Assert.Contains("maximal contiguous run", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR3ReviewRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R3 (Review)", prompt);
        Assert.Contains("exactly one Review", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR4ImproveRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R4 (Improve)", prompt);
        Assert.Contains("at most one Improve", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_FirstIterationDoesNotRecommendImprove()
    {
        var prompt = BuildPrompt();

        // The old unconditional recommendation is gone.
        Assert.DoesNotContain("Include the improve phase to let the improver refine", prompt);

        // The conditional guidance is present for clean first iterations.
        Assert.Contains("Include the improve phase ONLY when this iteration had previous issues", prompt);
        Assert.Contains("For a clean first iteration with no prior failures, do NOT", prompt);
        Assert.Contains("a clean plan should omit it", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_RetryIterationRecommendsImprove()
    {
        var prompt = BuildRetryPrompt();

        // The shared prompt body still contains the conditional Improve guidance,
        // which now applies because this is a retry iteration.
        Assert.Contains("Include the improve phase ONLY when this iteration had previous issues", prompt);
        Assert.Contains("second-or-later iteration with prior feedback", prompt);

        // The retry-context block fires for Iteration > 1.
        Assert.Contains("This is a retry — use the feedback above", prompt);
        Assert.Contains("iteration 2", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_R4ImproveRulePreserved()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R4 (Improve)", prompt);
        Assert.Contains("at most one Improve is allowed, positioned after the Review and before Merging", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_MultiRoundExampleDoesNotIncludeImprove()
    {
        var prompt = BuildPrompt();

        Assert.Contains("[\"coding\", \"testing\", \"coding\", \"testing\", \"review\", \"merging\"]", prompt);
        Assert.DoesNotContain("[\"coding\", \"testing\", \"coding\", \"testing\", \"review\", \"improve\", \"merging\"]", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ImproveRemainsInAvailablePhases()
    {
        var prompt = BuildPrompt();
        Assert.Contains("Available phases: coding, testing, docwriting, review, improve, merging", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR5MergingRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R5 (Merging)", prompt);
        Assert.Contains("exactly one Merging", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR6AllowedPhasesRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R6 (Allowed phases only)", prompt);
        Assert.Contains("only the six phase values", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR7OrderingDependencyRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R7 (Ordering-dependency)", prompt);
        Assert.Contains("Ordering-dependency", prompt);
        Assert.Contains("first Testing", prompt);
        Assert.Contains("first occurrences", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_R7DescribesSubmittedSequenceNotGoalNeeds()
    {
        var prompt = BuildPrompt();

        // R7 must describe the submitted plan's phase sequence, not the goal's requirements.
        Assert.Contains("when the submitted plan contains both Coding and DocWriting", prompt);
        Assert.Contains("exactly one of their first occurrences comes after the first Testing", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsPhaseNameRejectionRules()
    {
        var prompt = BuildPrompt();
        Assert.Contains("Phase-NAME rules", prompt);
        Assert.Contains("Unrecognized phase names:", prompt);
        Assert.Contains("Valid phases: coding, testing, docwriting, review, improve, merging.", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_PhaseNameRulesAreSeparateFromR6()
    {
        var prompt = BuildPrompt();

        var r6Index = prompt.IndexOf("R6 (Allowed phases only)");
        var phaseNameRulesIndex = prompt.IndexOf("Phase-NAME rules");

        Assert.NotEqual(-1, r6Index);
        Assert.NotEqual(-1, phaseNameRulesIndex);
        Assert.True(phaseNameRulesIndex > r6Index, "Phase-NAME rules should appear after R6, not be conflated with it.");

        // The unrecognized-name guidance paragraph itself must not contain the R6 label.
        var paragraphEnd = prompt.IndexOf("\n\n", phaseNameRulesIndex);
        var phaseNameParagraph = prompt[phaseNameRulesIndex..paragraphEnd];
        Assert.DoesNotContain("R6", phaseNameParagraph);
    }

    [Fact]
    public void BuildPlanningPrompt_StatesNoAutoFixBehavior()
    {
        var prompt = BuildPrompt();
        Assert.Contains("does NOT auto-fix", prompt);
        Assert.Contains("bounded attempts", prompt);
        Assert.Contains("no default-plan fallback", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_DoesNotContainStaleWording()
    {
        var prompt = BuildPrompt();
        Assert.DoesNotContain("may skip testing", prompt);
        Assert.DoesNotContain("auto-insert", prompt);
        Assert.DoesNotContain("auto-adjust", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_DocsOnlyChangeClarifiesTestingIsRequired()
    {
        var prompt = BuildPrompt();
        Assert.DoesNotContain("may skip testing", prompt);
        Assert.Contains("DocWriting → Testing → Review → Merging", prompt);
        Assert.Contains("Testing is always required after each content block per R2", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_DocsOnlyLineRemovesOldCoderThenDocwriterPhrasing()
    {
        var prompt = BuildPrompt();

        // The iteration-2 fix replaced "coder edits, then docwriter — may skip testing"
        // with "docwriter edits — a docs-only plan is DocWriting → Testing → Review → Merging".
        // Assert the old stale phrasing is gone and the new one is present.
        Assert.DoesNotContain("coder edits, then docwriter", prompt);
        Assert.Contains("docwriter edits", prompt);
        Assert.Contains("docs-only plan is DocWriting → Testing → Review → Merging", prompt);
    }

    // ── Additional gap-coverage tests ─────────────────────────────────────

    [Fact]
    public void BuildPlanningPrompt_ContainsExactRejectionMessageFormat()
    {
        var prompt = BuildPrompt();

        // The full rejection message template must appear as a single contiguous string
        // so the Brain can quote it verbatim when an unrecognized name is rejected.
        Assert.Contains(
            "Unrecognized phase names: <names>. Valid phases: coding, testing, docwriting, review, improve, merging.",
            prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_RejectsLifecycleAndNumericNames()
    {
        var prompt = BuildPrompt();

        // The prompt must name the lifecycle states and bare numeric tokens that are
        // rejected as unrecognized, so the Brain does not confuse them with valid phases.
        Assert.Contains("Planning", prompt);
        Assert.Contains("Done", prompt);
        Assert.Contains("Failed", prompt);
        Assert.Contains("numeric token", prompt);
        Assert.Contains("\"1\"", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_StatesNoReorderInsertOrFill()
    {
        var prompt = BuildPrompt();

        // The goal requires the precise "no-auto-fix/no-reorder/no-insert/no-fill"
        // wording, not a vague "does not alter".
        Assert.Contains("reorder", prompt);
        Assert.Contains("insert", prompt);
        Assert.Contains("fill structural phases", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_StatesResubmitViaReportIterationPlan()
    {
        var prompt = BuildPrompt();

        Assert.Contains("report_iteration_plan", prompt);
        Assert.Contains("resubmit", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_R7DoesNotOverstateAsNeverInterleave()
    {
        var prompt = BuildPrompt();

        // R7 must NOT be stated as a blanket "never interleave" rule. The prompt
        // must explicitly negate this overstatement.
        Assert.Contains("NOT a blanket", prompt);
        Assert.Contains("\"never interleave\"", prompt);
        Assert.Contains("prohibition", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_StaleAutoFixPhrasingsAbsent()
    {
        var prompt = BuildPrompt();

        // Goal requires "no-auto-fix/no-reorder/no-insert" — the vague "does not alter"
        // phrasing must not be used as a stand-in.
        Assert.DoesNotContain("does not alter", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auto-correct", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auto-reorder", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlanningPrompt_R7PrefersSingleBlockBeforeTesting()
    {
        var prompt = BuildPrompt();

        // The preferred fix must describe consolidating both content phases into a
        // single block before the Testing.
        Assert.Contains("single block", prompt);
        Assert.Contains("before that Testing", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_AcceptedPlanRunsInSubmittedOrder()
    {
        var prompt = BuildPrompt();

        // The prompt must state that an accepted (recognized) phase sequence runs in
        // the exact order submitted.
        Assert.Contains("runs in the exact order you submit it", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_OccurrenceSuffixesNormalizedNotFixing()
    {
        var prompt = BuildPrompt();

        // Suffix normalization and name-mapping are input parsing, not plan fixing.
        Assert.Contains("coding-2", prompt);
        Assert.Contains("normalized to the base name", prompt);
        Assert.Contains("input parsing", prompt);
    }

    // ── model_tiers guidance: Merging is a plan phase but NOT a tier key ─────

    /// <summary>
    /// Regression guard for the model_tiers guidance. This is the ONLY test that fails if the
    /// "Merging is a plan phase but NOT a tier key" sentence is deleted from the prompt, so
    /// reverting that guidance can no longer leave the suite green.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_ModelTiersSectionStatesMergingIsNotTierable()
    {
        var prompt = BuildPromptCollapsed();

        // The exact guidance sentence, matched whole so deleting any part of it fails the test.
        Assert.Contains(
            "Merging is a plan phase but NOT a tier key: `merging` must NEVER appear in model_tiers.",
            prompt);

        // The tierable key list is stated as the ONLY keys model_tiers accepts.
        Assert.Contains(
            "Tierable keys — the ONLY keys allowed here — are: coding, testing, docwriting, review, improve.",
            prompt);
        Assert.Contains(
            "only coding/testing/docwriting/review/improve may appear in model_tiers.",
            prompt);
    }

    /// <summary>
    /// The model_tiers guidance and R5 must COEXIST: excluding Merging from model_tiers must not
    /// be read as demoting Merging from its required final-plan-phase status.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_MergingExcludedFromTiersButStillRequiredFinalPhase()
    {
        var prompt = BuildPromptCollapsed();

        // R5 still declares Merging a required, final plan phase.
        Assert.Contains(
            "R5 (Merging): exactly one Merging is required, and it must be the final phase of the plan.",
            prompt);

        // Merging is still an available plan phase name.
        Assert.Contains("Available phases: coding, testing, docwriting, review, improve, merging", prompt);

        // …and the model_tiers bullet itself tells the Brain to KEEP Merging in `phases`
        // while removing it from model_tiers — proving the two statements coexist.
        Assert.Contains(
            "Keep Merging in `phases` (R5 still requires it as the final phase);",
            prompt);

        // The tierable-key list must never include merging.
        Assert.DoesNotContain(
            "Tierable keys — the ONLY keys allowed here — are: coding, testing, docwriting, review, improve, merging",
            prompt);
    }

    /// <summary>
    /// The Merging-is-not-a-tier-key guidance must live in the model_tiers bullet (not somewhere
    /// unrelated), and must come after the phase-name rules that govern the `phases` array.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_MergingTierGuidanceLivesInModelTiersBullet()
    {
        var prompt = BuildPromptCollapsed();

        var modelTiersIndex = prompt.IndexOf("- model_tiers: (optional)", StringComparison.Ordinal);
        var guidanceIndex = prompt.IndexOf(
            "Merging is a plan phase but NOT a tier key", StringComparison.Ordinal);
        var premiumIndex = prompt.IndexOf(
            "Only use premium when previous iterations failed", StringComparison.Ordinal);

        Assert.NotEqual(-1, modelTiersIndex);
        Assert.NotEqual(-1, guidanceIndex);
        Assert.NotEqual(-1, premiumIndex);

        // The guidance sits inside the model_tiers bullet body.
        Assert.True(
            guidanceIndex > modelTiersIndex && guidanceIndex < premiumIndex,
            "The Merging-is-not-a-tier-key guidance must appear inside the model_tiers bullet.");

        // Phase-NAME rules (which govern `phases`) explicitly distinguish themselves from
        // the model_tiers KEY rules.
        Assert.Contains("Phase-NAME rules (these govern the `phases` array", prompt);
        Assert.Contains("`model_tiers` KEY rules", prompt);
    }

    /// <summary>
    /// Regression guard for the "default tier" planning bug: the prompt must enumerate the ONLY
    /// valid model_tiers VALUES ("standard" and "premium") inside the bullet itself, otherwise
    /// the planning LLM emits "default" and the plan is rejected.
    /// </summary>
    [Fact]
    public void PlanningPrompt_StatesTierValues_StandardAndPremiumOnly()
    {
        var prompt = BuildPromptCollapsed();

        var modelTiersIndex = prompt.IndexOf("- model_tiers: (optional)", StringComparison.Ordinal);
        var premiumUsageIndex = prompt.IndexOf(
            "Only use premium when previous iterations failed", StringComparison.Ordinal);

        Assert.NotEqual(-1, modelTiersIndex);
        Assert.NotEqual(-1, premiumUsageIndex);

        // The exact values enumeration must appear INSIDE the model_tiers bullet body.
        var valuesSentence =
            "The ONLY valid VALUES are \"standard\" and \"premium\" (case-insensitive)";
        var valuesIndex = prompt.IndexOf(valuesSentence, StringComparison.Ordinal);

        Assert.NotEqual(-1, valuesIndex);
        Assert.True(
            valuesIndex > modelTiersIndex && valuesIndex < premiumUsageIndex,
            "The tier-values enumeration must appear inside the model_tiers bullet.");
    }

    /// <summary>
    /// The "default" trap must be called out explicitly: there is NO "default" value, and the
    /// correct way to get the default tier is to omit the phase from model_tiers entirely.
    /// </summary>
    [Fact]
    public void PlanningPrompt_WarnsAgainstDefaultTierValue()
    {
        var prompt = BuildPromptCollapsed();

        var modelTiersIndex = prompt.IndexOf("- model_tiers: (optional)", StringComparison.Ordinal);
        var premiumUsageIndex = prompt.IndexOf(
            "Only use premium when previous iterations failed", StringComparison.Ordinal);

        Assert.NotEqual(-1, modelTiersIndex);
        Assert.NotEqual(-1, premiumUsageIndex);

        var noDefaultIndex = prompt.IndexOf(
            "there is NO \"default\" value", StringComparison.Ordinal);
        var omitIndex = prompt.IndexOf(
            "OMIT the phase from model_tiers entirely", StringComparison.Ordinal);

        // Both statements must exist AND sit inside the model_tiers bullet body.
        Assert.NotEqual(-1, noDefaultIndex);
        Assert.NotEqual(-1, omitIndex);
        Assert.True(
            noDefaultIndex > modelTiersIndex && noDefaultIndex < premiumUsageIndex,
            "The no-default warning must appear inside the model_tiers bullet.");
        Assert.True(
            omitIndex > modelTiersIndex && omitIndex < premiumUsageIndex,
            "The omit-the-phase instruction must appear inside the model_tiers bullet.");
    }

    /// <summary>
    /// The bullet's final line must say "standard tier" (not "default tier"), and the OLD priming
    /// sentence — "Omitted phases use the default tier" — must be gone. That sentence is what
    /// primed the planning LLM to emit "default" as a tier value. (The trap-callout sentence
    /// elsewhere in the bullet deliberately mentions "the default tier" only to instruct the LLM
    /// to OMIT the phase; that is corrective text, not priming text.)
    /// </summary>
    [Fact]
    public void PlanningPrompt_UsesStandardNotDefaultWording()
    {
        var prompt = BuildPromptCollapsed();

        var modelTiersIndex = prompt.IndexOf("- model_tiers: (optional)", StringComparison.Ordinal);
        var bulletEndIndex = prompt.IndexOf(
            "call the `escalate_to_composer` tool", StringComparison.Ordinal);

        Assert.NotEqual(-1, modelTiersIndex);
        Assert.NotEqual(-1, bulletEndIndex);

        var bullet = prompt[modelTiersIndex..bulletEndIndex];

        Assert.Contains("Omitted phases use the standard tier", bullet);
        Assert.DoesNotContain("Omitted phases use the default tier", bullet);
    }

    // ── Clarification-history section ─────────────────────────────────────

    /// <summary>A marker that appears only in the tail of a long answer.</summary>
    private const string TailMarker = "TAIL-MARKER-BEYOND-2000-CHARS";

    /// <summary>
    /// Builds a pipeline whose conversation holds a single long "planning" entry (a recursive
    /// planning wrapper, excluded from the recorded-context projection), so clarification tests
    /// can prove that the clarification section's completeness is independent of the
    /// conversation content.
    /// </summary>
    private static GoalPipeline PipelineWithLongConversation()
    {
        var pipeline = new GoalPipeline(new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["repo"],
        });

        // One long recursive-planning entry: excluded from the recorded-context section,
        // proving clarification completeness does not depend on conversation content.
        pipeline.Conversation.Add(new ConversationEntry(
            "user", new string('x', 5000), 1, "planning"));

        return pipeline;
    }

    private static ClarificationEntry Clarification(
        DateTime timestamp, int iteration, string phase, string workerRole,
        string question, string answer, string answeredBy, int occurrence = 1) =>
        new(timestamp, "test-goal", iteration, phase, workerRole, question, answer, answeredBy)
        {
            Occurrence = occurrence,
        };

    /// <summary>
    /// A long multiline Q&amp;A whose answer carries a distinctive marker near its tail must
    /// appear COMPLETE in the clarification-history section — the section is never truncated.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_LongAnswerBeyondConversationCap_PreservedWithoutTruncation()
    {
        var pipeline = PipelineWithLongConversation();

        var longAnswer = new string('y', 2500) + $"\n{TailMarker}\n" + new string('z', 300);
        var records = new List<ClarificationEntry>
        {
            Clarification(
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, "Coding", "coder",
                "Should the API be versioned?", longAnswer, "human"),
        };

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline, null, records);

        var sectionIndex = prompt.IndexOf(
            "=== Clarification history (complete, untruncated) ===", StringComparison.Ordinal);
        var sectionEndIndex = prompt.IndexOf("=== End clarification history ===", StringComparison.Ordinal);

        Assert.NotEqual(-1, sectionIndex);
        Assert.NotEqual(-1, sectionEndIndex);
        Assert.True(sectionIndex < sectionEndIndex);

        // The COMPLETE answer — including the tail marker — is present in the section.
        var section = prompt[sectionIndex..(sectionEndIndex + "=== End clarification history ===".Length)];
        Assert.Contains(TailMarker, section);
        Assert.Contains(longAnswer, section);
    }

    /// <summary>Every answerer category (brain, composer, human) renders with its own label.</summary>
    [Theory]
    [InlineData("brain")]
    [InlineData("composer")]
    [InlineData("human")]
    public void BuildPlanningPrompt_AllAnswererCategories_Labeled(string answeredBy)
    {
        var pipeline = PipelineWithLongConversation();
        var records = new List<ClarificationEntry>
        {
            Clarification(
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, "Coding", "coder",
                "Which library?", "Use the standard one.", answeredBy),
        };

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline, null, records);

        Assert.Contains($"answered by: {answeredBy}", prompt);
        Assert.Contains("A: Use the standard one.", prompt);
    }

    /// <summary>
    /// A timeout outcome is visibly NOT a decision: the label and the answer line both say so.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_TimeoutOutcome_MarkedAsNotADecision()
    {
        var pipeline = PipelineWithLongConversation();
        var records = new List<ClarificationEntry>
        {
            Clarification(
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, "Coding", "coder",
                "Should we change the schema?", "No one answered in time.", "timeout"),
        };

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline, null, records);

        Assert.Contains(
            "TIMEOUT OUTCOME (not a decision", prompt);
        Assert.Contains(
            "Outcome (timeout — NOT an answer): No one answered in time.", prompt);
        // The timeout record must NOT be presented as an answered record.
        Assert.DoesNotContain("answered by: timeout", prompt);
        Assert.DoesNotContain("\nA: No one answered in time.", prompt);
    }

    /// <summary>Each record carries the full label: iteration, phase, occurrence, role, timestamp, AnsweredBy.</summary>
    [Fact]
    public void BuildPlanningPrompt_RecordLabels_CarryAllMetadata()
    {
        var pipeline = PipelineWithLongConversation();
        var records = new List<ClarificationEntry>
        {
            Clarification(
                new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc), 1, "Testing", "tester",
                "Run coverage?", "Yes.", "human", occurrence: 3),
        };

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline, null, records);

        Assert.Contains(
            "iteration 1, phase Testing, occurrence 3, worker role tester, 2024-03-04 05:06:07Z — answered by: human",
            prompt);
        Assert.Contains("Q: Run coverage?", prompt);
        Assert.Contains("A: Yes.", prompt);
    }

    /// <summary>With no clarification records, NO clarification section is emitted at all.</summary>
    [Fact]
    public void BuildPlanningPrompt_EmptyHistory_NoClarificationSection()
    {
        var prompt = BuildPrompt();

        Assert.DoesNotContain("Clarification history", prompt);
        Assert.DoesNotContain("End clarification history", prompt);
        Assert.DoesNotContain("Clarification answer", prompt);
        // No misleading placeholder.
        Assert.DoesNotContain("(no clarifications", prompt);
    }

    // ── Recorded conversation context section ────────────────────────────

    private const string RecordedContextStart = "=== Recorded conversation context";
    private const string RecordedContextEnd = "=== End recorded conversation context ===";

    private static GoalPipeline EmptyPipeline(int iteration = 1)
    {
        var pipeline = new GoalPipeline(new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["repo"],
        });
        for (var i = 1; i < iteration; i++)
            pipeline.IterationBudget.TryConsume();
        return pipeline;
    }

    /// <summary>Extracts the recorded-context section (start marker through end marker inclusive).</summary>
    private static string ExtractRecordedContextSection(string prompt)
    {
        var start = prompt.IndexOf(RecordedContextStart, StringComparison.Ordinal);
        var end = prompt.IndexOf(RecordedContextEnd, StringComparison.Ordinal);
        Assert.NotEqual(-1, start);
        Assert.NotEqual(-1, end);
        Assert.True(start < end);
        return prompt[start..(end + RecordedContextEnd.Length)];
    }

    /// <summary>
    /// A long multiline standalone entry well beyond 2,000 characters — with a unique trailing
    /// marker and a literal "..." inside its content — appears COMPLETE in the recorded-context
    /// section, proving no prefix cut and no ellipsis truncation is applied to entry content.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_RecordedContext_LongEntryPreservedVerbatim_NoCut()
    {
        var pipeline = EmptyPipeline();
        var longContent = new string('a', 1000) + "\nline two\nline three\n" + new string('b', 1000)
            + "\n... (literal ellipsis in source content)\n" + new string('c', 500)
            + $"\n{TailMarker}\n" + new string('d', 300);
        pipeline.Conversation.Add(new ConversationEntry(
            "user", longContent, 1, "error"));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline);
        var section = ExtractRecordedContextSection(prompt);

        // The complete content appears verbatim — head, middle, literal ellipsis, and tail.
        Assert.Contains(longContent, section);
        Assert.Contains(TailMarker, section);
        // No truncation ellipsis was appended to the entry content.
        Assert.DoesNotContain(new string('d', 300) + "...", section);
    }

    /// <summary>
    /// Late standalone notes placed after long old prompts still appear in the
    /// recorded-context section — proving there is no newest-only window.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_RecordedContext_LateNotesAfterLongOldPrompts_Included()
    {
        var pipeline = EmptyPipeline(iteration: 2);

        // Long old standalone prompts (iteration 1) followed by a late note (iteration 2).
        pipeline.Conversation.Add(new ConversationEntry(
            "user", new string('p', 3000), 1, "error"));
        pipeline.Conversation.Add(new ConversationEntry(
            "assistant", new string('q', 3000), 1, "worker-output"));
        pipeline.Conversation.Add(new ConversationEntry(
            "user", "LATE_NOTE_UNIQUE_9f8e7d", 2, "error"));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline);
        var section = ExtractRecordedContextSection(prompt);

        // Both the long old entries AND the late note appear — no newest-only window.
        Assert.Contains(new string('p', 3000), section);
        Assert.Contains(new string('q', 3000), section);
        Assert.Contains("LATE_NOTE_UNIQUE_9f8e7d", section);
        Assert.Equal(3, CountEntries(section));
    }

    /// <summary>Counts "[n] role:" entry headers in a recorded-context section.</summary>
    private static int CountEntries(string section) =>
        System.Text.RegularExpressions.Regex.Matches(section, @"\[\d+\] role: ").Count;

    /// <summary>
    /// Both roles (user request and assistant response) of BOTH excluded purpose tags are
    /// omitted, including case-insensitive variants ("Planning", "CRAFT-PROMPT").
    /// </summary>
    [Theory]
    [InlineData("planning", "user")]
    [InlineData("planning", "assistant")]
    [InlineData("Planning", "user")]
    [InlineData("PLANNING", "assistant")]
    [InlineData("craft-prompt", "user")]
    [InlineData("craft-prompt", "assistant")]
    [InlineData("Craft-Prompt", "user")]
    [InlineData("CRAFT-PROMPT", "assistant")]
    public void BuildPlanningPrompt_RecordedContext_ExcludesBothRolesOfBothTags_CaseInsensitive(
        string purpose, string role)
    {
        var pipeline = EmptyPipeline();
        pipeline.Conversation.Add(new ConversationEntry(
            role, $"EXCLUDED-{purpose}-{role}-CONTENT", 1, purpose));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline);

        // Only the excluded entry — the section is omitted entirely.
        Assert.DoesNotContain(RecordedContextStart, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(RecordedContextEnd, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain($"EXCLUDED-{purpose}-{role}-CONTENT", prompt);
    }

    /// <summary>
    /// Entries with a null or unrecognized Purpose and a null iteration are RETAINED, labeled
    /// "unclassified/legacy" and "unknown" respectively, with accurate role attribution.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_RecordedContext_NullPurposeAndNullIteration_RetainedWithLabels()
    {
        var pipeline = EmptyPipeline();
        pipeline.Conversation.Add(new ConversationEntry(
            "user", "LEGACY_NULL_PURPOSE_CONTENT", null, null));
        pipeline.Conversation.Add(new ConversationEntry(
            "assistant", "UNKNOWN_PURPOSE_CONTENT", null, "some-unknown-tag"));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline);
        var section = ExtractRecordedContextSection(prompt);

        Assert.Contains("LEGACY_NULL_PURPOSE_CONTENT", section);
        Assert.Contains("UNKNOWN_PURPOSE_CONTENT", section);
        Assert.Contains("[1] role: user | purpose: unclassified/legacy | iteration: unknown", section);
        Assert.Contains("[2] role: assistant | purpose: unclassified/legacy | iteration: unknown", section);
        Assert.Equal(2, CountEntries(section));
        // The section is framed as recorded context, not new instructions.
        Assert.Contains("RECORDED context", section);
        Assert.Contains("NOT new instructions", section);
    }

    /// <summary>
    /// Known-future exclusion still applies to legacy (unrecognized-Purpose) entries: a legacy
    /// entry with a known iteration greater than the current iteration is excluded, while its
    /// null-iteration sibling passes the filter.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_RecordedContext_KnownFutureExcluded_ForLegacyEntries()
    {
        var pipeline = EmptyPipeline(iteration: 2);

        pipeline.Conversation.Add(new ConversationEntry(
            "user", "LEGACY_KEEP_UNKNOWN_ITERATION", null, null));
        pipeline.Conversation.Add(new ConversationEntry(
            "user", "LEGACY_FUTURE_EXCLUDED_3", 3, "some-unknown-tag"));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline);
        var section = ExtractRecordedContextSection(prompt);

        Assert.Contains("LEGACY_KEEP_UNKNOWN_ITERATION", section);
        // Known-future entry is excluded even though its purpose is unrecognized.
        Assert.DoesNotContain("LEGACY_FUTURE_EXCLUDED_3", prompt);
        Assert.Equal(1, CountEntries(section));
    }

    /// <summary>
    /// Original order is preserved and repeated (duplicate) entries are preserved — no
    /// deduplication is applied.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_RecordedContext_OrderPreserved_DuplicatesKept()
    {
        var pipeline = EmptyPipeline();
        pipeline.Conversation.Add(new ConversationEntry(
            "user", "FIRST_ENTRY_ALPHA", 1, "worker-output"));
        pipeline.Conversation.Add(new ConversationEntry(
            "assistant", "DUPLICATE_ENTRY_BETA", 1, "worker-output"));
        pipeline.Conversation.Add(new ConversationEntry(
            "user", "MIDDLE_ENTRY_GAMMA", 1, "worker-output"));
        pipeline.Conversation.Add(new ConversationEntry(
            "assistant", "DUPLICATE_ENTRY_BETA", 1, "worker-output"));
        pipeline.Conversation.Add(new ConversationEntry(
            "user", "LAST_ENTRY_DELTA", 1, "worker-output"));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline);
        var section = ExtractRecordedContextSection(prompt);

        Assert.Equal(5, CountEntries(section));

        var first = section.IndexOf("FIRST_ENTRY_ALPHA", StringComparison.Ordinal);
        var dup1 = section.IndexOf("DUPLICATE_ENTRY_BETA", StringComparison.Ordinal);
        var middle = section.IndexOf("MIDDLE_ENTRY_GAMMA", StringComparison.Ordinal);
        var dup2 = section.IndexOf("DUPLICATE_ENTRY_BETA", middle + 1, StringComparison.Ordinal);
        var last = section.IndexOf("LAST_ENTRY_DELTA", StringComparison.Ordinal);

        Assert.True(first >= 0 && dup1 > first && middle > dup1 && dup2 > middle && last > dup2,
            "Entries must appear in original list order, with both duplicates kept.");
    }

    /// <summary>
    /// The section header carries an ACCURATE selected count, and when every entry is filtered
    /// out the section is omitted entirely.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_RecordedContext_AccurateCount_AndOmittedWhenAllFiltered()
    {
        // All-filtered case: only recursive planning/craft entries → section omitted.
        var allFiltered = EmptyPipeline();
        allFiltered.Conversation.Add(new ConversationEntry(
            "user", "PLAN_REQUEST", 1, "planning"));
        allFiltered.Conversation.Add(new ConversationEntry(
            "assistant", "PLAN_RESPONSE", 1, "planning"));
        allFiltered.Conversation.Add(new ConversationEntry(
            "user", "CRAFT_REQUEST", 1, "craft-prompt"));

        var emptyPrompt = BrainPromptBuilder.BuildPlanningPrompt(allFiltered);
        Assert.DoesNotContain(RecordedContextStart, emptyPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(RecordedContextEnd, emptyPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_RESPONSE", emptyPrompt);

        // Partially filtered case → accurate count in the header.
        var partial = EmptyPipeline(iteration: 2);
        partial.Conversation.Add(new ConversationEntry(
            "user", "KEEP_ONE", 1, "worker-output"));
        partial.Conversation.Add(new ConversationEntry(
            "assistant", "DROP_FUTURE", 3, "worker-output"));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(partial);
        var section = ExtractRecordedContextSection(prompt);
        Assert.Contains("=== Recorded conversation context (1 of 2 entries selected) ===", section);
        Assert.Contains("KEEP_ONE", section);
        Assert.DoesNotContain("DROP_FUTURE", prompt);
        Assert.Equal(1, CountEntries(section));
    }

    /// <summary>
    /// An empty conversation produces no recorded-context section at all.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_EmptyConversation_NoRecordedContextSection()
    {
        var prompt = BrainPromptBuilder.BuildPlanningPrompt(EmptyPipeline());

        Assert.DoesNotContain(RecordedContextStart, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(RecordedContextEnd, prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A complete worker summary carrying distinguishing operational metadata (git/tests/
    /// verdict) is RETAINED even when a PhaseLog report for the same role is embedded
    /// elsewhere in the conversation — no dedup by role is applied.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_RecordedContext_WorkerSummaryKeptAlongsidePhaseLogReport()
    {
        var pipeline = EmptyPipeline();

        // A worker summary with distinguishing operational metadata.
        var summaryContent =
            "Worker summary: coder finished round 1. " +
            "GIT_SHA=abc1234; TESTS_PASSED=636; VERDICT=PASS; " +
            "DISTINGUISHING_SUMMARY_METADATA_XYZ";
        pipeline.Conversation.Add(new ConversationEntry(
            "assistant", summaryContent, 1, "worker-output"));

        // A separate PhaseLog report for the same role, embedded in another entry elsewhere.
        pipeline.Conversation.Add(new ConversationEntry(
            "assistant", "PhaseLog report (CODER): round 1 report body", 1, "error"));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline);
        var section = ExtractRecordedContextSection(prompt);

        // BOTH entries are retained — the summary is not discarded for sharing a role with
        // the PhaseLog report.
        Assert.Contains("DISTINGUISHING_SUMMARY_METADATA_XYZ", section);
        Assert.Contains("GIT_SHA=abc1234", section);
        Assert.Contains("TESTS_PASSED=636", section);
        Assert.Contains("VERDICT=PASS", section);
        Assert.Contains("PhaseLog report (CODER): round 1 report body", section);
        Assert.Equal(2, CountEntries(section));
    }

    /// <summary>
    /// Two-planning-pass regression: the actual first-built planning prompt is replayed into
    /// the conversation with the production planning tag (user request) alongside a planning
    /// response with the production tag, so the second build must exclude the prior planning
    /// wrapper pair from the recorded-context section while the new standalone entry still
    /// appears. The second prompt must also carry a NONEMPTY clarification section (complete
    /// question and answer text) and the complete previous-iteration reviewer/tester/coder
    /// report sections — each section complete AND separate from the others. The source
    /// conversation is not mutated by prompt building.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_TwoPlanningPasses_ExcludesPriorPlanningWrappers()
    {
        const string clarificationSectionStart =
            "=== Clarification history (complete, untruncated) ===";
        const string clarificationSectionEnd = "=== End clarification history ===";
        const string prevFeedbackSectionStart = "=== Previous iteration (1) feedback ===";
        const string prevFeedbackSectionEnd = "=== End previous iteration feedback ===";

        var pipeline = EmptyPipeline(iteration: 2);

        // Previous-iteration (1) phase outputs: reviewer, tester, and coder reports.
        var reviewerReport =
            "REVIEWER_REPORT_ITER1_BODY\nverdict: REJECT\n" +
            "REVIEWER_TAIL_EVIDENCE_ITER1_a1b2c3";
        var testerReport =
            "TESTER_REPORT_ITER1_BODY\nfailed: 3 tests\n" +
            "TESTER_TAIL_EVIDENCE_ITER1_d4e5f6";
        var coderReport =
            "CODER_REPORT_ITER1_BODY\ncommitted: services refactor\n" +
            "CODER_TAIL_EVIDENCE_ITER1_7c8d9e";
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Review, Iteration = 1, Occurrence = 1,
            WorkerOutput = reviewerReport, Result = PhaseOutcome.Fail,
            StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Iteration = 1, Occurrence = 1,
            WorkerOutput = testerReport, Result = PhaseOutcome.Fail,
            StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Iteration = 1, Occurrence = 1,
            WorkerOutput = coderReport, Result = PhaseOutcome.Pass,
            StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });

        // First planning pass: a standalone note plus a clarification record.
        pipeline.Conversation.Add(new ConversationEntry(
            "user", "ITER1_STANDALONE_NOTE", 1, "error"));
        var clarificationRecord = new ClarificationEntry(
            new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), "test-goal", 1,
            "Review", "reviewer", "SHOULD_WE_RETRY_QUESTION_MARKER",
            "YES_RETRY_WITH_FIXES_ANSWER_MARKER", "composer")
        { Occurrence = 1 };

        var firstPrompt = BrainPromptBuilder.BuildPlanningPrompt(
            pipeline, null, [clarificationRecord]);
        var firstSection = ExtractRecordedContextSection(firstPrompt);
        Assert.Contains("ITER1_STANDALONE_NOTE", firstSection);

        // Pass 2: replay the ACTUAL first planning prompt into the conversation with the
        // production planning tag (as DistributedBrain.PlanIterationAsync does), add a
        // planning response with the production tag, and a new standalone entry.
        var conversationCountBefore = pipeline.Conversation.Count;
        pipeline.Conversation.Add(new ConversationEntry(
            "user", firstPrompt, 2, "planning"));
        pipeline.Conversation.Add(new ConversationEntry(
            "assistant", "FIRST_PLANNING_RESPONSE_TEXT_ITER2", 2, "planning"));
        pipeline.Conversation.Add(new ConversationEntry(
            "user", "NEW_STANDALONE_NOTE_AFTER_PASS", 2, "error"));

        var secondPrompt = BrainPromptBuilder.BuildPlanningPrompt(
            pipeline, null, [clarificationRecord]);
        var secondSection = ExtractRecordedContextSection(secondPrompt);

        // Only the selected standalone entries appear in the recorded-context section;
        // the prior planning wrapper pair (the replayed REAL first prompt and the planning
        // response) does NOT.
        Assert.Contains("ITER1_STANDALONE_NOTE", secondSection);
        Assert.Contains("NEW_STANDALONE_NOTE_AFTER_PASS", secondSection);
        Assert.Equal(2, CountEntries(secondSection));
        Assert.DoesNotContain(firstPrompt, secondPrompt);
        Assert.DoesNotContain("=== Recorded conversation context (4 of", secondPrompt);
        Assert.DoesNotContain("FIRST_PLANNING_RESPONSE_TEXT_ITER2", secondPrompt);

        // The clarification section is NONEMPTY in pass 2 and carries the COMPLETE record.
        var clarificationStart = secondPrompt.IndexOf(clarificationSectionStart, StringComparison.Ordinal);
        var clarificationEnd = secondPrompt.IndexOf(clarificationSectionEnd, StringComparison.Ordinal);
        Assert.NotEqual(-1, clarificationStart);
        Assert.NotEqual(-1, clarificationEnd);
        Assert.True(clarificationStart < clarificationEnd);
        var clarificationSection = secondPrompt[clarificationStart..(clarificationEnd + clarificationSectionEnd.Length)];
        Assert.Contains("SHOULD_WE_RETRY_QUESTION_MARKER", clarificationSection);
        Assert.Contains("YES_RETRY_WITH_FIXES_ANSWER_MARKER", clarificationSection);
        Assert.Contains("answered by: composer", clarificationSection);
        Assert.Contains(
            "iteration 1, phase Review, occurrence 1, worker role reviewer, 2024-05-06 07:08:09Z — answered by: composer",
            clarificationSection);

        // The previous-iteration report sections carry the COMPLETE reviewer/tester/coder
        // report contents, not just a generic marker.
        var prevFeedbackStart = secondPrompt.IndexOf(prevFeedbackSectionStart, StringComparison.Ordinal);
        var prevFeedbackEnd = secondPrompt.IndexOf(prevFeedbackSectionEnd, StringComparison.Ordinal);
        Assert.NotEqual(-1, prevFeedbackStart);
        Assert.NotEqual(-1, prevFeedbackEnd);
        Assert.True(prevFeedbackStart < prevFeedbackEnd);
        var prevFeedbackSection = secondPrompt[prevFeedbackStart..(prevFeedbackEnd + prevFeedbackSectionEnd.Length)];
        Assert.Contains(reviewerReport.ReplaceLineEndings("\n"), prevFeedbackSection);
        Assert.Contains(testerReport.ReplaceLineEndings("\n"), prevFeedbackSection);
        Assert.Contains(coderReport.ReplaceLineEndings("\n"), prevFeedbackSection);
        Assert.Contains("=== Reviewer feedback (iteration 1) ===", prevFeedbackSection);
        Assert.Contains("=== Tester feedback (iteration 1) ===", prevFeedbackSection);
        Assert.Contains("=== Coder output round 1 (iteration 1) ===", prevFeedbackSection);

        // The three sections are complete AND separate from each other: each framing marker
        // pair appears exactly once, in the fixed order previous-feedback → clarification →
        // recorded-context, and no section bleeds into another.
        var recordedStart = secondPrompt.IndexOf(RecordedContextStart, StringComparison.Ordinal);
        var recordedEnd = secondPrompt.IndexOf(RecordedContextEnd, StringComparison.Ordinal);
        Assert.NotEqual(-1, recordedStart);
        Assert.NotEqual(-1, recordedEnd);
        Assert.True(recordedStart < recordedEnd);
        Assert.True(prevFeedbackStart < clarificationStart);
        Assert.True(clarificationEnd < recordedStart);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            secondPrompt, System.Text.RegularExpressions.Regex.Escape(RecordedContextEnd)));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            secondPrompt, System.Text.RegularExpressions.Regex.Escape(clarificationSectionEnd)));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            secondPrompt, System.Text.RegularExpressions.Regex.Escape(prevFeedbackSectionEnd)));

        // The replayed first prompt's own content must not leak into pass 2's recorded
        // context as selected entries — the recorded-context section contains only the two
        // standalone entries.
        Assert.Contains("(2 of 4 entries selected)", secondSection);

        // Prompt building did not mutate the source conversation.
        Assert.Equal(conversationCountBefore + 3, pipeline.Conversation.Count);
        Assert.Equal(4, pipeline.Conversation.Count);
    }

    /// <summary>
    /// A "plan-adjustment" entry (the tag written by DistributedBrain.InjectSystemNoteAsync)
    /// is RETAINED as a standalone note and rendered with its OWN purpose attribution —
    /// not the unclassified/legacy label.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_RecordedContext_PlanAdjustmentRetained_WithAccurateLabel()
    {
        var pipeline = EmptyPipeline();
        pipeline.Conversation.Add(new ConversationEntry(
            "system", "PLAN_ADJUSTMENT_NOTE_CONTENT_MARKER", 1, "plan-adjustment"));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline);
        var section = ExtractRecordedContextSection(prompt);

        Assert.Contains("PLAN_ADJUSTMENT_NOTE_CONTENT_MARKER", section);
        Assert.Contains(
            "[1] role: system | purpose: plan-adjustment | iteration: 1", section);
        Assert.DoesNotContain(
            "purpose: unclassified/legacy", section, StringComparison.Ordinal);
        Assert.Equal(1, CountEntries(section));
    }

    /// <summary>
    /// An empty-string Purpose is a DISTINCT vector from null and unrecognized: it is retained
    /// and labeled unclassified/legacy (accurate attribution, not guessed).
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_RecordedContext_EmptyPurpose_Retained_LabeledLegacy()
    {
        var pipeline = EmptyPipeline();
        pipeline.Conversation.Add(new ConversationEntry(
            "user", "EMPTY_PURPOSE_CONTENT_MARKER", 1, ""));

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline);
        var section = ExtractRecordedContextSection(prompt);

        Assert.Contains("EMPTY_PURPOSE_CONTENT_MARKER", section);
        Assert.Contains(
            "[1] role: user | purpose: unclassified/legacy | iteration: 1", section);
        Assert.Equal(1, CountEntries(section));
    }
}
