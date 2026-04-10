using System.Text;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Orchestration;

/// <summary>
/// Pure static helper that assembles every prompt string the Brain sends to the LLM.
/// All methods are deterministic functions of <see cref="GoalPipeline"/> — no instance state.
/// </summary>
public static class BrainPromptBuilder
{
    /// <summary>
    /// The default system prompt for the Brain LLM.
    /// Kept here so it can be referenced by <see cref="DistributedBrain"/> as a constant alias.
    /// </summary>
    internal const string DefaultSystemPrompt = """
        You are the CopilotHive Orchestrator Brain — a product owner and project manager.
        You have two jobs:
        1. Plan iteration phases using the report_iteration_plan tool
        2. Craft clear, specific prompts for workers when asked

        You have read-only access to the target repositories via file tools (read_file, glob, grep).
        Use these to examine project structure, configuration, and code when it helps you plan
        better iterations or craft more targeted worker prompts.

        RULES:
        - When planning iterations, always call report_iteration_plan
        - When crafting prompts, respond with ONLY the prompt text — no JSON, no markdown formatting
        - If you need clarification during planning or prompt crafting that cannot be determined from the codebase, call the escalate_to_composer tool with a question and reason
        - Never include git checkout/branch/switch/push commands in prompts — infrastructure handles branching
        - Never include framework-specific build/test commands — workers use build and test skills
        - When planning iterations, use the search_knowledge, read_document, and traverse_graph tools to look up architecture documents related to the goal's target components. This gives you deeper context about WHY code is structured the way it is, not just WHAT it looks like.
        - When a goal has related docs, use `read_document` to read their full content before crafting worker prompts.
        - Use `traverse_graph` to discover related documents by following links from documents you have found.

        WORKER PROMPT RULES:
        When crafting worker prompts, follow these rules per role:
        - Coders: Tell them to implement immediately, read files, use build/test skills, commit with git add -A && git commit. Never include git branch or push commands.
        - Testers: Tell them to build, run test skill, write integration tests, call report_test_results. Never tell them to create report files.
        - Reviewers: Do NOT include git diff commands — the worker's workspace context provides the correct diff. Tell them to review using their workspace diff commands, focus on +/- lines, call report_review_verdict. Files to change is guidance, Files NOT to change is strict. Test changes are always acceptable. Use the testing phase results to verify that all tests pass — do NOT reject because you cannot run tests yourself.
        - DocWriters: Do NOT include git diff commands. Tell them to use workspace context diff, update only requested docs, build to verify, call report_doc_changes.
        - Improvers: Tell them to analyze results and update *.agents.md files using file tools. No git commands.
        """;

    /// <summary>
    /// Builds the raw prompt text that will be sent to the Brain to craft a worker prompt.
    /// Extracted for testability — allows verifying prompt content without a connected agent.
    /// </summary>
    /// <param name="pipeline">The goal pipeline with phase outputs and iteration state.</param>
    /// <param name="phase">The current goal phase.</param>
    /// <param name="additionalContext">Optional additional context to include.</param>
    /// <returns>The assembled prompt text.</returns>
    internal static string BuildCraftPromptText(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null)
    {
        var roleName = phase.ToWorkerRole().ToRoleName();

        var phaseInstructions = "";
        // Use occurrence-indexed key lookup for multi-round plans (e.g. "coding-2" for second coding round)
        var occurrenceIndex = GetPhaseOccurrenceIndex(pipeline, phase);
        var instructions = pipeline.Plan?.GetPhaseInstruction(phase, occurrenceIndex);
        if (!string.IsNullOrEmpty(instructions))
            phaseInstructions = $"\nPhase instructions from the plan:\n{instructions}";

        // Check if docwriting preceded review in this iteration's plan, so the
        // reviewer knows that CHANGELOG/README changes are expected and in-scope.
        var docWritingPrecededReview = pipeline.Plan?.Phases is { } phases
            && phases.IndexOf(GoalPhase.DocWriting) is >= 0 and var dwIdx
            && phases.IndexOf(GoalPhase.Review) is >= 0 and var rvIdx
            && dwIdx < rvIdx;

        // When retrying after review/test rejection, include the specific feedback
        // so the coder/tester knows exactly what to fix.
        var previousFeedback = (phase is GoalPhase.Coding or GoalPhase.Testing && pipeline.Iteration > 1)
            ? BuildPreviousIterationContext(pipeline)
            : "";

        // For review phase, extract the tester output from the current iteration so the reviewer
        // can verify test results without needing to run tests themselves.
        string currentTestResults;
        var testerEntry = pipeline.PhaseLog
            .LastOrDefault(e => e.Iteration == pipeline.Iteration && e.Name == GoalPhase.Testing);
        if (phase == GoalPhase.Review
            && testerEntry?.WorkerOutput is { } testerOut
            && !string.IsNullOrWhiteSpace(testerOut))
        {
            const int maxTesterOutputChars = 2000;
            currentTestResults = testerOut.Length > maxTesterOutputChars
                ? testerOut[..maxTesterOutputChars] + "..."
                : testerOut;
        }
        else
        {
            currentTestResults = "";
        }

        // For review phase, also include coder output so the reviewer understands the rationale
        // behind code decisions. Cap at 2000 chars with ellipsis if truncated.
        string currentCoderOutput;
        var coderEntry = pipeline.PhaseLog
            .LastOrDefault(e => e.Iteration == pipeline.Iteration && e.Name == GoalPhase.Coding);
        if (phase == GoalPhase.Review
            && coderEntry?.WorkerOutput is { } coderOut
            && !string.IsNullOrWhiteSpace(coderOut))
        {
            const int maxCoderOutputChars = 2000;
            currentCoderOutput = coderOut.Length > maxCoderOutputChars
                ? coderOut[..maxCoderOutputChars] + "..."
                : coderOut;
        }
        else
        {
            currentCoderOutput = "";
        }

        // Include docwriting-preceded-review guidance inline when relevant
        var docWritingNote = (phase == GoalPhase.Review && docWritingPrecededReview)
            ? "\nNote: The docwriting phase already ran before this review. Changes to CHANGELOG.md, README.md, and XML doc comments are EXPECTED and should NOT be flagged as scope violations."
            : "";

        return $$"""
            Craft a prompt for the {{roleName}} worker.

            Goal: {{pipeline.GoalId}} (iteration {{pipeline.Iteration}}, phase {{phase}})
            Target repositories: {{string.Join(", ", pipeline.Goal.RepositoryNames)}}
            {{phaseInstructions}}
            {{(previousFeedback.Length > 0 ? $"\n{previousFeedback}" : "")}}
            {{(additionalContext is not null ? $"\n=== Additional context ===\n{additionalContext}\n=== End additional context ===" : "")}}
            {{(currentTestResults.Length > 0 ? $"\n=== Tester output (iteration {pipeline.Iteration}) ===\n{currentTestResults}\n=== End tester output ===" : "")}}
            {{(currentCoderOutput.Length > 0 ? $"\n=== Coder output (iteration {pipeline.Iteration}) ===\n{currentCoderOutput}\n=== End coder output ===" : "")}}
            {{docWritingNote}}

            The worker has access to project skills (e.g. build, test) that describe how to build and test this project.
            Tell the worker to use those skills instead of hardcoding framework-specific commands.

            Respond with ONLY the prompt text — no JSON, no markdown wrapping.
            If you need clarification that cannot be determined from the codebase, call escalate_to_composer instead.
            Use the get_goal tool if you need the full goal description.
            """;
    }

    /// <summary>
    /// Builds a direct worker prompt for the Review phase when the Brain agent is not connected.
    /// Includes tester output and reviewer guidance so reviewers always get test results
    /// and the "verify that all tests pass" instruction, regardless of agent availability.
    /// </summary>
    /// <param name="pipeline">The goal pipeline with phase outputs and iteration state.</param>
    /// <param name="additionalContext">Optional additional context to include.</param>
    /// <returns>A reviewer-ready fallback prompt containing test results and guidance.</returns>
    internal static string BuildReviewFallbackPrompt(GoalPipeline pipeline, string? additionalContext = null)
    {
        var testerLogEntry = pipeline.PhaseLog
            .LastOrDefault(e => e.Iteration == pipeline.Iteration && e.Name == GoalPhase.Testing);
        var currentTestResults = (!string.IsNullOrWhiteSpace(testerLogEntry?.WorkerOutput))
            ? testerLogEntry.WorkerOutput
            : "";

        // Include coder output so the reviewer understands the rationale behind code decisions.
        // Cap at 2000 chars with ellipsis if truncated.
        string currentCoderOutput = "";
        var coderLogEntry = pipeline.PhaseLog
            .LastOrDefault(e => e.Iteration == pipeline.Iteration && e.Name == GoalPhase.Coding);
        if (!string.IsNullOrWhiteSpace(coderLogEntry?.WorkerOutput))
        {
            var coderOut = coderLogEntry.WorkerOutput;
            const int maxCoderOutputChars = 2000;
            currentCoderOutput = coderOut.Length > maxCoderOutputChars
                ? coderOut[..maxCoderOutputChars] + "..."
                : coderOut;
        }

        return $$"""
            Review the changes for: {{pipeline.Description}}

            Use the diff commands from your workspace context to review the branch changes.
            Focus only on the diff lines (+ and -), then call the report_review_verdict tool when done.

            - "Files to change" in the goal is GUIDANCE, not an exhaustive whitelist. Test files and test changes that cover the modified code are ALWAYS acceptable and expected.
            - "Files NOT to change" in the goal IS a strict prohibition — flag any changes to those files as MAJOR.
            - The goal description defines WHAT to do. New behavior described in the goal is IN SCOPE — do not reject changes just because the base branch doesn't have them yet.
            - Only flag issues that are clearly bugs, security problems, or genuine scope violations (touching unrelated code/features).
            - Use the testing phase results to verify that all tests pass — do NOT reject because you cannot run tests yourself.
            {{(additionalContext is not null ? $"\n=== Additional context ===\n{additionalContext}\n=== End additional context ===" : "")}}
            {{(currentTestResults.Length > 0 ? $"\n=== Tester output (iteration {pipeline.Iteration}) ===\n{currentTestResults}\n=== End tester output ===" : "")}}
            {{(currentCoderOutput.Length > 0 ? $"\n=== Coder output (iteration {pipeline.Iteration}) ===\n{currentCoderOutput}\n=== End coder output ===" : "")}}
            """;
    }

    /// <summary>
    /// Builds a summary of the previous iteration's reviewer/tester feedback
    /// from <see cref="GoalPipeline.PhaseLog"/>. Returns an empty string
    /// for the first iteration.
    /// </summary>
    internal static string BuildPreviousIterationContext(GoalPipeline pipeline)
    {
        if (pipeline.Iteration <= 1)
            return "";

        var prevIteration = pipeline.Iteration - 1;
        var prevEntries = pipeline.PhaseLog
            .Where(e => e.Iteration == prevIteration)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"=== Previous iteration ({prevIteration}) feedback ===");

        var hasAnyFeedback = false;

        // Include reviewer feedback — the most critical signal for replanning
        var reviewerEntry = prevEntries
            .LastOrDefault(e => e.Name == GoalPhase.Review);
        if (!string.IsNullOrWhiteSpace(reviewerEntry?.WorkerOutput))
        {
            hasAnyFeedback = true;
            sb.AppendLine($"=== Reviewer feedback (iteration {prevIteration}) ===");
            sb.AppendLine(Truncate(reviewerEntry.WorkerOutput, Constants.TruncationConversationSummary));
            sb.AppendLine("=== End reviewer feedback ===");
        }

        // Include tester feedback if tests failed
        var testerEntry = prevEntries
            .LastOrDefault(e => e.Name == GoalPhase.Testing);
        if (!string.IsNullOrWhiteSpace(testerEntry?.WorkerOutput))
        {
            hasAnyFeedback = true;
            sb.AppendLine($"=== Tester feedback (iteration {prevIteration}) ===");
            sb.AppendLine(Truncate(testerEntry.WorkerOutput, Constants.TruncationConversationSummary));
            sb.AppendLine("=== End tester feedback ===");
        }

        // Include coder output — all coding round outputs from the previous iteration
        var coderEntries = prevEntries
            .Where(e => e.Name == GoalPhase.Coding && !string.IsNullOrWhiteSpace(e.WorkerOutput))
            .ToList();
        for (var i = 0; i < coderEntries.Count; i++)
        {
            hasAnyFeedback = true;
            sb.AppendLine($"=== Coder output round {i + 1} (iteration {prevIteration}) ===");
            sb.AppendLine(Truncate(coderEntries[i].WorkerOutput!, Constants.TruncationMedium));
            sb.AppendLine($"=== End coder output round {i + 1} ===");
        }

        if (!hasAnyFeedback)
        {
            sb.AppendLine("  (No phase outputs recorded for the previous iteration)");
        }

        sb.AppendLine("=== End previous iteration feedback ===");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the planning prompt for <see cref="DistributedBrain.PlanIterationAsync"/>.
    /// Assembles retry context, previous iteration feedback, and conversation summary.
    /// </summary>
    /// <param name="pipeline">The goal pipeline containing iteration state and context.</param>
    /// <param name="additionalContext">Optional extra context prepended to the planning prompt.</param>
    /// <returns>The fully assembled planning prompt string.</returns>
    internal static string BuildPlanningPrompt(GoalPipeline pipeline, string? additionalContext = null)
    {
        var previousIterationContext = BuildPreviousIterationContext(pipeline);

        var retryContext = pipeline.Iteration > 1
            ? $"""
              Retry context:
              - This is iteration {pipeline.Iteration} (previous attempts: {pipeline.Iteration - 1})
              - Review retries: {pipeline.ReviewRetries}
              - Test retries: {pipeline.TestRetries}
              This is a retry — use the feedback above to plan which phases need re-running.
              """
            : "This is the first iteration — no previous feedback.";

        var conversationSummary = pipeline.Conversation.Count > 0
            ? $"Conversation history ({pipeline.Conversation.Count} messages): " +
              Truncate(string.Join(" | ", pipeline.Conversation.Select(e => $"[{e.Role}] {e.Content}")), Constants.TruncationConversationSummary)
            : "";

        return $$"""
            {{(additionalContext is not null ? $"=== Additional context ===\n{additionalContext}\n=== End additional context ===\n\n" : "")}}Plan the workflow for iteration {{pipeline.Iteration}} of goal: {{pipeline.Description}}

            Target repositories: {{string.Join(", ", pipeline.Goal.RepositoryNames)}}
            (You can browse the code under these folder names in your working directory)

            {{retryContext}}
            {{previousIterationContext}}
            {{conversationSummary}}

            Decide the ordered phases for this iteration. Consider:
            - Is this a documentation-only change? (coder edits, then docwriter — may skip testing)
            - Is this a retry after failure? (what phases need re-running)
            IMPORTANT: Only include the docwriting phase when the goal explicitly requests
            documentation updates (e.g. "update README", "add changelog entry", "update docs").
            Skip docwriting for purely internal changes (refactors, bug fixes, test additions)
            unless the goal description specifically calls for it.
            Include the improve phase to let the improver refine agents.md guidance based on
            how the iteration went — especially when steps needed retries or produced issues.

            Available phases: coding, testing, docwriting, review, improve, merging

            For large or complex coding tasks, you may plan multiple coding+testing rounds before review:
              ["coding", "testing", "coding", "testing", "review", "improve", "merging"]

            Use multiple coding rounds when:
            - The change involves a large file (>500 lines) that risks LLM response timeouts
            - The work naturally splits into sequential steps (e.g. "revert X, then implement Y")
            - A previous iteration timed out during coding

            Each coding round must be immediately followed by testing. Provide separate phase_instructions
            for each round using keys "coding-1", "coding-2", etc. and "testing-1", "testing-2", etc.
            Single-round plans may use bare keys "coding", "testing" as before.

            Call the `report_iteration_plan` tool with:
            - phases: ordered list of phase names
            - phase_instructions: JSON object with per-phase instructions.
              Single-round: {"coding": "...", "review": "..."}
              Multi-round:  {"coding-1": "step 1: revert...", "coding-2": "step 2: restructure...", "review": "..."}
            - reason: why this plan
            - model_tiers: (optional) JSON object to escalate specific phases to premium tier
              (e.g. {"coding": "premium"}). Valid phases: coding, testing, docwriting, review, improve.
              Only use premium when previous iterations failed and you believe the task requires
              stronger reasoning. Omitted phases use the default tier.

            If the goal description is ambiguous or you need domain knowledge to plan properly,
            call the `escalate_to_composer` tool instead with a question and reason.
            """;
    }

    /// <summary>
    /// Builds the commit message generation prompt for <see cref="DistributedBrain.GenerateCommitMessageAsync"/>.
    /// </summary>
    /// <param name="pipeline">The goal pipeline.</param>
    /// <returns>The fully assembled commit message prompt string.</returns>
    internal static string BuildCommitMessagePrompt(GoalPipeline pipeline)
    {
        return $$"""
            Generate a concise git commit message for a squash merge of goal: {{pipeline.Description}}

            Goal ID: {{pipeline.GoalId}}
            Target repositories: {{string.Join(", ", pipeline.Goal.RepositoryNames)}}

            Format:
            - First line: a short imperative subject (~72 characters, no "Goal:" prefix)
            - Blank line
            - 2–4 bullet points summarizing what was implemented

            Respond with ONLY the commit message text — no tool calls, no JSON, no markdown wrapping.
            """;
    }

    /// <summary>
    /// Builds the summarize-and-merge prompt for <see cref="DistributedBrain.SummarizeAndMergeAsync"/>.
    /// </summary>
    /// <param name="pipeline">The goal pipeline.</param>
    /// <returns>The fully assembled summarize prompt string.</returns>
    internal static string BuildSummarizePrompt(GoalPipeline pipeline)
    {
        return $"""
            This goal has been completed successfully and merged.
            
            Goal: {pipeline.GoalId}
            Description: {Truncate(pipeline.Description, 500)}
            Iterations: {pipeline.Iteration}
            
            Summarize what was accomplished in 2-4 sentences. Focus on:
            - What was changed (files, components, patterns)
            - Key decisions or trade-offs made
            - Any important context for future goals
            
            Respond with ONLY the summary text.
            """;
    }

    /// <summary>
    /// Returns the 1-based occurrence index of the given phase within the plan's phases list,
    /// counting up to and including the position of the current pipeline phase.
    /// Uses the state machine's remaining-phases count to compute the exact position,
    /// avoiding enum-value matching which would always stop at the first occurrence.
    /// </summary>
    private static int GetPhaseOccurrenceIndex(GoalPipeline pipeline, GoalPhase phase)
    {
        if (pipeline.Plan?.Phases is not { } phases)
            return 1;

        // Determine the current position in the plan using the state machine's remaining phases.
        // After a transition, RemainingPhases contains everything after the current phase, so:
        // currentPosition = totalPhases - 1 - remainingCount
        var remaining = pipeline.StateMachine.RemainingPhases;
        var currentPosition = phases.Count - 1 - remaining.Count;

        var count = 0;
        for (var i = 0; i <= currentPosition && i < phases.Count; i++)
        {
            if (phases[i] == phase)
                count++;
        }
        return count > 0 ? count : 1;
    }

    internal static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>Formats a context-usage log message for the Brain LLM call.</summary>
    internal static string FormatContextUsageMessage(long inputTokens, int contextWindow, string callerName)
    {
        var pct = contextWindow > 0 ? inputTokens * 100.0 / contextWindow : 0.0;
        return $"Brain context usage: {pct.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}% ({inputTokens}/{contextWindow} tokens) after {callerName}";
    }

    /// <summary>
    /// Builds the prompt for asking the Brain a worker question.
    /// </summary>
    internal static string BuildAskQuestionPrompt(
        string goalId, int iteration, string phase, string workerRole, string question)
    {
        return $"""
            A worker ({workerRole}) in goal {goalId} (iteration {iteration}, phase {phase}) has a question:

            {question}

            If you can answer this from the codebase and project context, respond with ONLY the answer text.
            If the question requires domain knowledge, business context, or a decision that cannot be
            determined from the codebase alone, call the escalate_to_composer tool with the question and reason.
            """;
    }
}
