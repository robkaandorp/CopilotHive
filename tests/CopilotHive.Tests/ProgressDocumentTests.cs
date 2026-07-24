using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Tests that verify the living progress document is created in the knowledge graph when a goal is
/// dispatched, linked to the goal, and appended to with Brain plans, worker narratives, and summaries.
/// </summary>
[Collection("HiveIntegration")]
public sealed class ProgressDocumentTests
{
    [Fact]
    public async Task DispatchNextGoal_CreatesScratchProgressDocument_WithProgressTopic()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out _, out _);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Equal(DocumentType.Scratch, doc!.Type);
        Assert.Equal("progress", doc.Topic);
    }

    [Fact]
    public async Task DispatchNextGoal_LinksProgressDocumentToGoal()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out _, out _);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        Assert.Contains($"progress-{goal.Id}", goal.Documents);
    }

    [Fact]
    public async Task DispatchNextGoal_AppendsBrainPlan_WithPhaseNames()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out _, out _);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("### Brain Plan", doc!.Content);
        Assert.Contains("Coding", doc.Content);
    }

    [Fact]
    public async Task DispatchNextGoal_WithNullKnowledgeGraph_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, knowledgeGraph: null, out _, out _);

        // Should complete without throwing
        await InvokeDispatchNextGoalAsync(dispatcher, ct);
    }

    [Fact]
    public async Task PhaseCompletion_AppendsWorkerNarratives_ToProgressDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _);

        // Dispatch to create the pipeline and progress document
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        // The dispatch registered a task for the first (Coding) phase.
        var taskId = pipeline!.ActiveTaskId;
        Assert.NotNull(taskId);

        // Add a narrative for the active task
        pipeline.AddNarrativeEntry("worker-1", taskId!, "I explored the codebase and implemented the change.");

        // Complete the coding phase (with file changes to avoid no-op detection)
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId!,
            Status = TaskOutcome.Completed,
            Output = "Made changes",
            GitStatus = new GitChangeSummary { FilesChanged = 2 },
            Metrics = new TaskMetrics { Verdict = "PASS" },
        }, ct);

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("I explored the codebase and implemented the change.", doc!.Content);
        Assert.Contains("(narrative)", doc.Content);
    }

    [Fact]
    public void BuildPlanSection_ContainsPhasesReasonAndInstructions()
    {
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Review],
            Reason = "Straightforward change",
            PhaseInstructions = { ["coding"] = "Focus on the core logic" },
        };

        var section = PipelineProgressFormatting.BuildPlanSection(3, plan);

        Assert.Contains("## Iteration 3", section);
        Assert.Contains("### Brain Plan", section);
        Assert.Contains("Coding → Review", section);
        Assert.Contains("Straightforward change", section);
        Assert.Contains("Focus on the core logic", section);
    }

    // ── Brain summary appended after iteration ───────────────────────────────

    [Fact]
    public async Task NewIteration_AppendsBrainSummary_ToProgressDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _, maxRetries: 3);

        // Dispatch to create the pipeline and progress document
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        // Advance to the Review phase and submit a REQUEST_CHANGES verdict to trigger a new iteration.
        // Walk the pipeline through Coding → Testing → DocWriting → Review manually.
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS", filesChanged: 2);   // Coding
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // Testing
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // DocWriting

        // Review phase with REQUEST_CHANGES triggers a new iteration (review retry budget allows it)
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "REQUEST_CHANGES");

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        // The new-iteration path appends a Brain Summary for the completed iteration.
        Assert.Contains("### Brain Summary (Iteration 1)", doc!.Content);
        // And a new Brain Plan for iteration 2.
        Assert.Contains("## Iteration 2", doc.Content);
        Assert.Contains("### Brain Plan", doc.Content);
    }

    [Fact]
    public async Task GoalCompletion_AppendsFinalBrainSummary_ToProgressDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };

        // Use a fake repo manager so the real Merging → PerformMergeAsync completion path succeeds
        // without touching git. This is the REAL normal completion path (not the DriveNextPhaseAsync
        // TransitionEffect.Completed case).
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _,
            repoManager: new ProgressFakeBrainRepoManager());

        // Dispatch to create the pipeline and progress document
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        // A branch must be set for PerformMergeAsync to proceed past its guard.
        pipeline!.CoderBranch = "copilothive/feat-x";

        // Verify document exists before completion
        Assert.NotNull(graph.GetDocument($"progress-{goal.Id}"));

        // Drive the default plan (Coding → Testing → DocWriting → Review → Merging) to completion.
        // The Review → Merging transition dispatches the Merging phase, which runs PerformMergeAsync
        // and completes the goal — appending the final Brain summary along the way.
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS", filesChanged: 2);  // Coding
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS");                    // Testing
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS");                    // DocWriting
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS");                    // Review → Merging → complete

        // PerformMergeAsync should have appended the final Brain summary (from SummarizeAndMergeAsync).
        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("### Brain Summary (Final)", doc!.Content);
        Assert.Contains($"Goal '{goal.Id}' completed.", doc.Content);
    }

    // ── Merge-conflict retry ─────────────────────────────────────────────────

    [Fact]
    public async Task MergeConflictRetry_AppendsBrainSummaryAndNewPlan_ToProgressDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };

        // Repo manager whose merge always fails → triggers HandleMergeFailureAsync (rebase retry).
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _,
            maxRetries: 3, repoManager: new FailingMergeBrainRepoManager());

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);
        pipeline!.CoderBranch = "copilothive/feat-x";

        // Drive to the Merging phase: Coding → Testing → DocWriting → Review → (Merging fails → retry)
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS", filesChanged: 2);  // Coding
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS");                    // Testing
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS");                    // DocWriting
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS");                    // Review → Merging (fails → retry)

        // The merge-conflict retry path should have appended a Brain summary for the failed iteration
        // and a new Brain plan for the next iteration.
        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("### Brain Summary (Iteration 1)", doc!.Content);
        Assert.Contains("Merge conflict encountered. Retrying with rebase.", doc.Content);
        Assert.Contains("## Iteration 2", doc.Content);
        Assert.Contains("### Brain Plan", doc.Content);
    }

    // ── Restart persistence ──────────────────────────────────────────────────

    [Fact]
    public async Task ProgressDocument_PersistsAcrossKnowledgeGraphInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"progress-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var configRepo = new FakeConfigRepoManager("https://example.com/config.git", tempDir);

            // First KnowledgeGraph instance: create and commit a progress document.
            var graph = new KnowledgeGraph(configRepo);
            await graph.CreateDocumentAsync(
                id: "progress-test-goal",
                title: "Progress: test",
                type: DocumentType.Scratch,
                content: "# Progress: test\n",
                topic: "progress",
                ct: ct);
            await graph.CommitToConfigRepoAsync(tempDir, "Create progress document", ct);

            // The document should have been written to disk at knowledge/progress/test-goal.md
            // (the "progress-" topic prefix is stripped from the id to form the leaf slug).
            var expectedPath = Path.Combine(tempDir, "knowledge", "progress", "test-goal.md");
            Assert.True(File.Exists(expectedPath), $"Expected progress document at {expectedPath}");

            // Second (post-"restart") KnowledgeGraph instance: reload from disk.
            var graph2 = new KnowledgeGraph(configRepo);
            await graph2.ReloadFromConfigRepoAsync(tempDir, ct);

            var doc = graph2.GetDocument("progress-test-goal");
            Assert.NotNull(doc);
            Assert.Contains("# Progress: test", doc!.Content);
            Assert.Equal(DocumentType.Scratch, doc.Type);
            Assert.Equal("progress", doc.Topic);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Empty narratives handled ─────────────────────────────────────────────

    [Fact]
    public async Task PhaseCompletion_WithNoNarratives_DoesNotAppendNarrativeSection()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _);

        // Dispatch to create the pipeline and progress document
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        var docBefore = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(docBefore);
        var contentBefore = docBefore!.Content;

        // Complete the coding phase WITHOUT adding any narratives
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS", filesChanged: 2);

        var docAfter = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(docAfter);
        // No narrative section should have been appended — the content is unchanged
        // except the pipeline advanced.  Actually the content should be identical since
        // AppendPhaseNarrativesAsync returns early when there are no matching narratives.
        Assert.DoesNotContain("(narrative)", docAfter!.Content);
    }

    // ── Multiple narratives ordering ─────────────────────────────────────────

    [Fact]
    public async Task PhaseCompletion_MultipleNarratives_AppendsInTimestampOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _);

        // Dispatch to create the pipeline and progress document
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        var taskId = pipeline!.ActiveTaskId;
        Assert.NotNull(taskId);

        // Add multiple narratives with the SAME task ID. AddNarrativeEntry uses DateTime.UtcNow,
        // so they may share the same timestamp. Instead, we add them in a specific order and verify
        // both appear in the document. The AppendPhaseNarrativesAsync orders by Timestamp.
        pipeline.AddNarrativeEntry("worker-early", taskId!, "First narrative about initial exploration.");
        pipeline.AddNarrativeEntry("worker-late", taskId!, "Second narrative about finalizing the implementation.");

        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS", filesChanged: 2);

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("First narrative about initial exploration.", doc!.Content);
        Assert.Contains("Second narrative about finalizing the implementation.", doc.Content);

        // Verify ordering: first narrative appears before second narrative in the content
        var firstIdx = doc.Content.IndexOf("First narrative about initial exploration.", StringComparison.Ordinal);
        var secondIdx = doc.Content.IndexOf("Second narrative about finalizing the implementation.", StringComparison.Ordinal);
        Assert.True(firstIdx >= 0 && secondIdx >= 0, "Both narratives should be present");
        Assert.True(firstIdx < secondIdx, "First narrative should appear before second narrative");
    }

    // ── Idempotent dispatch ──────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_WhenProgressDocumentAlreadyExists_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };

        // Pre-create the progress document so the dispatch's CreateDocumentAsync would throw
        // (document ID already exists). The catch block should swallow the exception.
        var docId = $"progress-{goal.Id}";
        await graph.CreateDocumentAsync(
            id: docId,
            title: "Pre-existing progress",
            type: DocumentType.Scratch,
            content: "# Pre-existing\n",
            topic: "progress",
            ct: ct);

        var dispatcher = CreateDispatcher(goal, graph, out _, out _);

        // Should complete without throwing despite the pre-existing document
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // The original document content should still be there (the catch swallowed the error)
        var doc = graph.GetDocument(docId);
        Assert.NotNull(doc);
        Assert.Contains("Pre-existing", doc!.Content);
    }

    // ── Config repo commit ───────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_WithConfigRepoManager_CommitsProgressDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };

        var configRepo = new FakeConfigRepoManager(
            "https://example.com/config.git",
            Path.Combine(Path.GetTempPath(), $"test-configrepo-{Guid.NewGuid():N}"));

        // The KnowledgeGraph must be constructed WITH the config repo so that
        // CommitToConfigRepoAsync calls CommitFileAsync internally.
        var graph = new KnowledgeGraph(configRepo);

        var dispatcher = CreateDispatcher(goal, graph, out _, out _, configRepo: configRepo);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // The progress document creation should have triggered a commit via CommitToConfigRepoAsync.
        Assert.NotEmpty(configRepo.Commits);
        Assert.Contains(configRepo.Commits, c => c.Message.Contains("progress", StringComparison.OrdinalIgnoreCase));
    }

    // ── Full lifecycle ────────────────────────────────────────────────────────

    [Fact]
    public async Task FullLifecycle_DispatchAndPhaseCompletion_DocumentHasPlanAndNarrative()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _);

        // Step 1: Dispatch — creates the progress document and appends the initial Brain plan
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("### Brain Plan", doc!.Content);
        var contentAfterDispatch = doc.Content;

        // Step 2: Complete the Coding phase with a narrative
        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        var taskId = pipeline!.ActiveTaskId;
        Assert.NotNull(taskId);
        pipeline.AddNarrativeEntry("worker-1", taskId!, "I implemented the core feature and added unit tests.");

        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS", filesChanged: 3);

        // Step 3: Verify the document now contains both the plan AND the narrative
        doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("### Brain Plan", doc!.Content);
        Assert.Contains("I implemented the core feature and added unit tests.", doc.Content);
        Assert.Contains("(narrative)", doc.Content);
        // The content should have grown
        Assert.True(doc.Content.Length > contentAfterDispatch.Length,
            "Document content should have grown after appending the narrative");
    }

    // ── Indexed per-phase instructions ──────────────────────────────────────

    [Fact]
    public void BuildPlanSection_MultiRoundPlan_IncludesIndexedPhaseInstructions()
    {
        // A multi-round plan with occurrence-based instruction keys.
        // BuildPlanSection uses the per-phase occurrence count (how many times a specific phase has
        // appeared so far) as the index passed to GetPhaseInstruction, matching the documented
        // semantics of GetPhaseInstruction / GetCurrentPhaseOccurrence. For
        // [Coding, Testing, Coding, Testing] the lookup keys are:
        //   Coding at occurrence 1 → "coding-1"
        //   Testing at occurrence 1 → "testing-1"
        //   Coding at occurrence 2 → "coding-2"
        //   Testing at occurrence 2 → "testing-2"
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing],
            Reason = "Multi-round plan",
            PhaseInstructions =
            {
                ["coding-1"] = "First coding round: implement core logic",
                ["coding-2"] = "Second coding round: refactor and optimize",
                ["testing-1"] = "First test round: unit tests",
                ["testing-2"] = "Second test round: integration tests",
            },
        };

        var section = PipelineProgressFormatting.BuildPlanSection(1, plan);

        Assert.Contains("## Iteration 1", section);
        Assert.Contains("### Brain Plan", section);
        Assert.Contains("Multi-round plan", section);

        // All four instructions should be present with occurrence-based indexed keys
        Assert.Contains("First coding round: implement core logic", section);
        Assert.Contains("Second coding round: refactor and optimize", section);
        Assert.Contains("First test round: unit tests", section);
        Assert.Contains("Second test round: integration tests", section);
    }

    [Fact]
    public void BuildPlanSection_OccurrenceBasedKeys_FoundByOccurrenceBasedLookup()
    {
        // BuildPlanSection uses occurrence-based indexing: each phase's index counts how many times
        // that specific phase has appeared so far. For [Coding, Testing, Coding, Testing] with keys
        // coding-1, coding-2, testing-1, testing-2, all four instructions are found.
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing],
            Reason = null,
            PhaseInstructions =
            {
                ["coding-1"] = "First coding",
                ["coding-2"] = "Second coding (occurrence-based key)",
                ["testing-1"] = "First testing",
                ["testing-2"] = "Second testing (occurrence-based key)",
            },
        };

        var section = PipelineProgressFormatting.BuildPlanSection(1, plan);

        // First coding (occurrence 1 → "coding-1") is found
        Assert.Contains("First coding", section);
        // Second coding (occurrence 2 → "coding-2") is now found with occurrence-based lookup
        Assert.Contains("Second coding (occurrence-based key)", section);
        // Both testing occurrences are found too
        Assert.Contains("First testing", section);
        Assert.Contains("Second testing (occurrence-based key)", section);
    }

    [Fact]
    public void BuildPlanSection_FallsBackToBareKey_WhenIndexedKeyAbsent()
    {
        // When only the bare "coding" key exists (no indexed keys), the 1-based lookup
        // falls back to the bare key for all occurrences.
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing],
            Reason = null,
            PhaseInstructions =
            {
                ["coding"] = "Bare coding instruction",
            },
        };

        var section = PipelineProgressFormatting.BuildPlanSection(1, plan);

        Assert.Contains("Bare coding instruction", section);
    }

    [Fact]
    public void BuildPlanSection_ThreeOccurrencePlan_IncludesAllIndexedInstructions()
    {
        // A plan with three Coding occurrences to verify the occurrence counter correctly
        // increments to 3 for the third Coding occurrence (not just 2).
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Review, GoalPhase.Coding],
            Reason = "Triple coding plan",
            PhaseInstructions =
            {
                ["coding-1"] = "First coding",
                ["coding-2"] = "Second coding",
                ["coding-3"] = "Third coding",
                ["testing-1"] = "First testing",
                ["review-1"] = "First review",
            },
        };

        var section = PipelineProgressFormatting.BuildPlanSection(1, plan);

        Assert.Contains("## Iteration 1", section);
        Assert.Contains("### Brain Plan", section);

        // All five instruction strings should appear, proving the occurrence counter
        // correctly tracks the third Coding occurrence as 3 (key "coding-3").
        Assert.Contains("First coding", section);
        Assert.Contains("Second coding", section);
        Assert.Contains("Third coding", section);
        Assert.Contains("First testing", section);
        Assert.Contains("First review", section);
    }

    [Fact]
    public void BuildPlanSection_MixedIndexedAndBareKeys_IncludesAllInstructions()
    {
        // A plan mixing indexed Coding keys (coding-1, coding-2) with a bare Testing key.
        // The bare "testing" key should be used as a fallback for the Testing occurrence
        // since no indexed "testing-1" key exists.
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding],
            Reason = null,
            PhaseInstructions =
            {
                ["coding-1"] = "First coding round",
                ["coding-2"] = "Second coding round",
                ["testing"] = "Testing all",
            },
        };

        var section = PipelineProgressFormatting.BuildPlanSection(1, plan);

        // Both indexed Coding instructions should be found via occurrence-based lookup
        Assert.Contains("First coding round", section);
        Assert.Contains("Second coding round", section);
        // The bare "testing" key should be found via fallback (no "testing-1" indexed key)
        Assert.Contains("Testing all", section);
    }

    // ── Custom Brain summary from SummarizeAndMergeAsync ──────────────────────

    [Fact]
    public async Task GoalCompletion_UsesBrainSummaryFromSummarizeAndMergeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };

        // Use a custom brain that returns a specific summary string from SummarizeAndMergeAsync.
        const string customSummary = "Goal achieved all acceptance criteria. Key decision: used pattern X for the core logic.";
        var brain = new ProgressFakeBrainWithCustomSummary(customSummary);

        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _,
            repoManager: new ProgressFakeBrainRepoManager(), brain: brain);

        // Dispatch to create the pipeline and progress document
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);
        pipeline!.CoderBranch = "copilothive/feat-x";

        // Drive to completion: Coding → Testing → DocWriting → Review → Merging
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS", filesChanged: 2);  // Coding
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS");                    // Testing
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS");                    // DocWriting
        await CompleteActivePhaseAsync(dispatcher, pipeline, ct, "PASS");                    // Review → Merging → complete

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("### Brain Summary (Final)", doc!.Content);
        // The ACTUAL Brain summary text should appear, not a hardcoded fallback
        Assert.Contains(customSummary, doc.Content);
    }

    // ── Formatting: title extraction ─────────────────────────────────────────

    [Fact]
    public async Task DispatchNextGoal_WithMarkdownHeadingDescription_TitleStripsHeadingPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal
        {
            Id = $"goal-progress-{Guid.NewGuid():N}",
            Description = "## Goal\nUpdate all NuGet package references to the latest versions.",
            RepositoryNames = ["test-repo"],
        };
        var dispatcher = CreateDispatcher(goal, graph, out _, out _);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        // The title must NOT contain the raw "## Goal" markdown heading.
        Assert.DoesNotContain("## Goal", doc!.Content);
        // The document header line (# ...) must use the goal identifier as the title.
        Assert.Contains($"# Progress: {goal.Id}", doc.Content);
    }

    [Fact]
    public async Task DispatchNextGoal_WithMultilineGoalDescription_TitleUsesGoalIdNotDescription()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal
        {
            Id = $"goal-progress-{Guid.NewGuid():N}",
            Description = "## Goal\n\nSimplify the progress document title to use the goal ID directly instead of trying to extract a title from the goal description.",
            RepositoryNames = ["test-repo"],
        };
        var dispatcher = CreateDispatcher(goal, graph, out _, out _);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);

        // The title must be exactly "Progress: {goal.Id}" — not derived from the description.
        var expectedTitle = $"Progress: {goal.Id}";
        Assert.DoesNotContain("## Goal", doc!.Content);
        Assert.DoesNotContain("Simplify the progress document title", doc.Content[..Math.Min(200, doc.Content.Length)]);

        // The document content must start with the "# Progress: {goal.Id}" header.
        Assert.StartsWith($"# {expectedTitle}\n", doc.Content);
    }

    // ── Progress document appended to improver prompt ────────────────────────

    [Fact]
    public async Task ImproverPrompt_WhenProgressDocumentExists_IncludesProgressSectionAndContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        const string progressBody = "### coder (narrative)\n\nI explored the codebase and implemented the change.";

        // Use a plan that includes Improve so the improver phase is reached in one iteration.
        var capturingBrain = new ProgressFakeBrainCapturingPrompts(planWithImprove: true);
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _, brain: capturingBrain);

        // Dispatch creates the progress document and starts at Coding.
        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // Populate the progress document with qualitative content.
        var docId = $"progress-{goal.Id}";
        var doc = graph.GetDocument(docId);
        Assert.NotNull(doc);
        await graph.UpdateDocumentAsync(docId, content: doc!.Content.TrimEnd() + "\n\n" + progressBody, ct: ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        // Drive through the standard plan until we reach Improve.
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS", filesChanged: 2);   // Coding
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // Testing
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // DocWriting
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // Review → Improve

        Assert.Equal(GoalPhase.Improve, pipeline!.Phase);

        // The improver prompt was captured by the fake brain when DispatchImproverCoreAsync called ResolvePrompt.
        var improverPrompt = capturingBrain.LastImproverPrompt;
        Assert.NotNull(improverPrompt);
        Assert.Contains("## Iteration Progress Document", improverPrompt);
        Assert.Contains(progressBody, improverPrompt);
    }

    [Fact]
    public async Task ImproverPrompt_WhenNoProgressDocument_DoesNotIncludeProgressSection()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };

        var capturingBrain = new ProgressFakeBrainCapturingPrompts(planWithImprove: true);
        // No knowledge graph => no progress document.
        var dispatcher = CreateDispatcher(goal, knowledgeGraph: null, out var pipelineManager, out _, brain: capturingBrain);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS", filesChanged: 2);   // Coding
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                       // Testing
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                       // DocWriting
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                       // Review → Improve

        var improverPrompt = capturingBrain.LastImproverPrompt;
        Assert.NotNull(improverPrompt);
        Assert.DoesNotContain("## Iteration Progress Document", improverPrompt);
    }

    [Fact]
    public async Task ImproverPrompt_WhenProgressDocumentEmpty_DoesNotIncludeProgressSection()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };

        var capturingBrain = new ProgressFakeBrainCapturingPrompts(planWithImprove: true);
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _, brain: capturingBrain);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        // The progress document exists but only has the auto-created header. Trim it to whitespace
        // so the improver logic's guard treats it as empty and does not append it.
        var docId = $"progress-{goal.Id}";
        await graph.UpdateDocumentAsync(docId, content: "   \n\t  ", ct: ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS", filesChanged: 2);   // Coding
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // Testing
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // DocWriting
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // Review → Improve

        var improverPrompt = capturingBrain.LastImproverPrompt;
        Assert.NotNull(improverPrompt);
        Assert.DoesNotContain("## Iteration Progress Document", improverPrompt);
    }

    [Fact]
    public async Task ImproverPrompt_ProgressDocumentSectionAppearsAfterTelemetrySection()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        const string progressBody = "### coder (narrative)\n\nI explored the codebase and implemented the change.";

        var capturingBrain = new ProgressFakeBrainCapturingPrompts(planWithImprove: true);
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _, brain: capturingBrain);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var docId = $"progress-{goal.Id}";
        var doc = graph.GetDocument(docId);
        Assert.NotNull(doc);
        await graph.UpdateDocumentAsync(docId, content: doc!.Content.TrimEnd() + "\n\n" + progressBody, ct: ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        // Write a telemetry file so that ## Telemetry is populated.
        var previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
        var ownsStateDir = string.IsNullOrEmpty(previousStateDir);
        var stateDir = ownsStateDir
            ? Path.Combine(Path.GetTempPath(), $"copilothive-test-{Guid.NewGuid():N}")
            : previousStateDir!;
        Environment.SetEnvironmentVariable("STATE_DIR", stateDir);
        try
        {
            Directory.CreateDirectory(stateDir);
            var telemetryPath = Path.Combine(stateDir, "traces-coder.jsonl");
            await File.WriteAllTextAsync(telemetryPath,
                "{\"input_tokens\":100,\"output_tokens\":50,\"cache_read_tokens\":0,\"cache_write_tokens\":0,\"duration_ms\":1234,\"cost\":0.01,\"api_call_id\":\"call-1\"}\n", ct);

            await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS", filesChanged: 2);   // Coding
            await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // Testing
            await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // DocWriting
            await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");                     // Review → Improve

            var improverPrompt = capturingBrain.LastImproverPrompt;
            Assert.NotNull(improverPrompt);

            var telemetryIndex = improverPrompt!.IndexOf("## Telemetry", StringComparison.Ordinal);
            var progressIndex = improverPrompt.IndexOf("## Iteration Progress Document", StringComparison.Ordinal);

            Assert.True(telemetryIndex >= 0, "Telemetry section should be present");
            Assert.True(progressIndex > telemetryIndex, "Progress document section should appear after telemetry section");
        }
        finally
        {
            Environment.SetEnvironmentVariable("STATE_DIR", previousStateDir);
            if (ownsStateDir && Directory.Exists(stateDir))
            {
                try { Directory.Delete(stateDir, recursive: true); } catch { }
            }
        }
    }

    // ── Formatting: Brain Plan blank line ────────────────────────────────────

    [Fact]
    public void BuildPlanSection_HasBlankLineAfterBrainPlanHeading()
    {
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing],
            Reason = "A simple plan",
        };

        var section = PipelineProgressFormatting.BuildPlanSection(1, plan);

        // There must be a blank line after "### Brain Plan" — content contains
        // "### Brain Plan\n\n" (heading followed by blank line).
        Assert.Contains("### Brain Plan\n\n", section);
    }

    // ── Formatting: worker narrative blank line ─────────────────────────────

    [Fact]
    public async Task PhaseCompletion_NarrativeHasBlankLineAfterHeading()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        var taskId = pipeline!.ActiveTaskId;
        Assert.NotNull(taskId);

        pipeline.AddNarrativeEntry("worker-1", taskId!, "I explored the codebase and implemented the change.");

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId!,
            Status = TaskOutcome.Completed,
            Output = "Made changes",
            GitStatus = new GitChangeSummary { FilesChanged = 2 },
            Metrics = new TaskMetrics { Verdict = "PASS" },
        }, ct);

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        // The narrative heading must be followed by a blank line:
        // "### coder (narrative)\n\n"
        Assert.Contains("### coder (narrative)\n\n", doc!.Content);
    }

    // ── Formatting: Brain Summary blank line ────────────────────────────────

    [Fact]
    public async Task NewIteration_BrainSummaryHasBlankLineAfterHeading()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        var goal = new Goal { Id = $"goal-progress-{Guid.NewGuid():N}", Description = "Implement feature X", RepositoryNames = ["test-repo"] };
        var dispatcher = CreateDispatcher(goal, graph, out var pipelineManager, out _, maxRetries: 3);

        await InvokeDispatchNextGoalAsync(dispatcher, ct);

        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        // Drive through Coding → Testing → DocWriting → Review with REQUEST_CHANGES
        // to trigger a new iteration, which appends a Brain Summary section.
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS", filesChanged: 2);
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "PASS");
        await CompleteActivePhaseAsync(dispatcher, pipeline!, ct, "REQUEST_CHANGES");

        var doc = graph.GetDocument($"progress-{goal.Id}");
        Assert.NotNull(doc);
        // The Brain Summary heading must be followed by a blank line.
        Assert.Contains("### Brain Summary (Iteration 1)\n\n", doc!.Content);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GoalDispatcher CreateDispatcher(
        Goal goal,
        KnowledgeGraph? knowledgeGraph,
        out GoalPipelineManager pipelineManager,
        out IGoalStore goalStore,
        ConfigRepoManager? configRepo = null,
        int maxRetries = 1,
        ILogger<GoalDispatcher>? logger = null,
        IBrainRepoManager? repoManager = null,
        IDistributedBrain? brain = null)
    {
        var goalSource = new ProgressFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        pipelineManager = new GoalPipelineManager();

        var store = new FakeGoalStore();
        store.CreateGoalAsync(goal).GetAwaiter().GetResult();
        goalStore = store;

        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/test-repo", DefaultBranch = "main" },
            ],
            Orchestrator = new OrchestratorConfig { MaxRetriesPerTask = maxRetries },
        };

        return new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger ?? NullLogger<GoalDispatcher>.Instance,
            repoManager ?? new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain ?? new ProgressFakeBrain(),
            config: config,
            knowledgeGraph: knowledgeGraph,
            configRepo: configRepo,
            goalStore: store,
            startupDelay: TimeSpan.Zero);
    }

    private static Task InvokeDispatchNextGoalAsync(GoalDispatcher dispatcher, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "DispatchNextGoalAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(dispatcher, [ct])!;
    }

    /// <summary>
    /// Completes the pipeline's currently active phase by calling HandleTaskCompletionAsync
    /// with the given verdict. For the Coding phase, a non-zero <paramref name="filesChanged"/>
    /// is required to avoid no-op detection.
    /// </summary>
    private static async Task CompleteActivePhaseAsync(
        GoalDispatcher dispatcher,
        GoalPipeline pipeline,
        CancellationToken ct,
        string verdict = "PASS",
        int filesChanged = 1)
    {
        // Each phase dispatch registers a new task ID. Read the current active task.
        var taskId = pipeline.ActiveTaskId;
        Assert.NotNull(taskId);

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId!,
            Status = TaskOutcome.Completed,
            Output = "Phase output",
            GitStatus = new GitChangeSummary { FilesChanged = filesChanged },
            Metrics = new TaskMetrics { Verdict = verdict },
        }, ct);
    }
}

/// <summary>Goal source for progress-document tests.</summary>
file sealed class ProgressFakeGoalSource : IGoalSource
{
    private readonly Goal _goal;

    public ProgressFakeGoalSource(Goal goal) => _goal = goal;

    public string Name => "progress-fake";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>Brain stub that returns the default plan and phase-labelled prompts.</summary>
file class ProgressFakeBrain : IDistributedBrain
{
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PlanResult.Success(IterationPlan.Default()));

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public void DeleteGoalSession(string goalId) { }

    public void RegisterExistingGoalSession(string goalId) { }

    public bool GoalSessionExists(string goalId) => false;

    public virtual Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>Brain stub that records the additionalContext passed to CraftPromptAsync for the Improve phase.</summary>
file sealed class ProgressFakeBrainCapturingPrompts(bool planWithImprove = false) : IDistributedBrain
{
    public string? LastImproverPrompt { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PlanResult.Success(IterationPlan.Default(includeImprove: planWithImprove)));

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        if (phase == GoalPhase.Improve)
            LastImproverPrompt = additionalContext;

        return Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));
    }

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public void DeleteGoalSession(string goalId) { }

    public void RegisterExistingGoalSession(string goalId) { }

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>Brain stub that returns a custom summary string from SummarizeAndMergeAsync.</summary>
file sealed class ProgressFakeBrainWithCustomSummary(string summary) : ProgressFakeBrain
{
    public override Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(summary);
}

/// <summary>Brain repo manager stub whose merge always succeeds without touching git.</summary>
file sealed class ProgressFakeBrainRepoManager : IBrainRepoManager
{
    public string WorkDirectory => "/fake/work";

    public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.FromResult($"/fake/work/{repoName}");

    public Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default) =>
        Task.FromResult("fake-sha");

    public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default) =>
        Task.FromResult(BranchDeleteResult.Success);

    public string GetClonePath(string repoName) => $"/fake/work/{repoName}";

    public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default) =>
        Task.FromResult(new List<string>());
}

/// <summary>Brain repo manager stub whose merge always throws, forcing a merge-conflict retry.</summary>
file sealed class FailingMergeBrainRepoManager : IBrainRepoManager
{
    public string WorkDirectory => "/fake/work";

    public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.FromResult($"/fake/work/{repoName}");

    public Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default) =>
        throw new InvalidOperationException("Simulated merge conflict");

    public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default) =>
        Task.FromResult(BranchDeleteResult.Success);

    public string GetClonePath(string repoName) => $"/fake/work/{repoName}";

    public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default) =>
        Task.FromResult(new List<string>());
}

/// <summary>In-memory goal store for progress-document tests.</summary>
file sealed class FakeGoalStore : IGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new();

    public string Name => "FakeGoalStore";

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.FromResult(goal);
    }

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.Remove(goalId));

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(Array.Empty<(string, PersistedClarification)>());
}
