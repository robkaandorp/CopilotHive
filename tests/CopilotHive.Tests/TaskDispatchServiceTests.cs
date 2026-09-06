// ═══════════════════════════════════════════════════════════════════════════════════════════
//  AC5 — ADMISSION-ATOMIC-SWITCH REGISTER-CALLER AUDIT (durable record)
// ═══════════════════════════════════════════════════════════════════════════════════════════
//
//  WHY THIS LIVES IN A SOURCE FILE. This is the complete, exhaustive per-file disposition of
//  every test file that calls GoalPipelineManager.RegisterTask / TryRegisterTask, required by
//  acceptance criterion 5 of the admission-atomic-switch goal. It is recorded here because file
//  content cannot be truncated by transient tooling output; the same table is reproduced in the
//  commit body. Do not delete it without superseding the record elsewhere.
//
//  ── THE THREE BREAKING SEMANTIC CHANGES BEING AUDITED FOR ────────────────────────────────
//   (1) OVERWRITE → REFUSAL. RegisterTask used to assign the dictionary slot unconditionally;
//       it now uses TryAdd and silently declines a duplicate. A caller that registered the SAME
//       task id twice on the SAME manager would change behaviour.
//   (2) DURABLE → MEMORY-ONLY. Both registers no longer write the task_mappings row at all,
//       even with a store configured. A caller asserting mapping PERSISTENCE would change
//       behaviour (or, worse, become VACUOUS — asserting the removal of a row never written).
//   (3) BLANK/NULL INPUT NOW THROWS ArgumentException (taskId validated before goalId).
//
//  ── METHODOLOGY (applied per call site, not per file) ────────────────────────────────────
//   a. FIRST-ARGUMENT INSPECTION: the literal task-id expression at every call site.
//   b. SAME-MANAGER TASK-ID SHARING: whether any two calls share a task id on ONE manager
//      instance — the only way change (1) is observable. Distinct ids and fresh-per-test
//      managers are both sufficient to rule it out.
//   c. STORE-BACKEDNESS: new GoalPipelineManager(store) vs new GoalPipelineManager().
//      A storeless manager cannot observe change (2) at all.
//   d. PERSISTENCE-ASSERTION PROBE: grep for TaskMappings / LoadActivePipelines / LoadPipeline /
//      RestoreFromStore / RestorePipeline / SaveTaskMapping / task_mappings, then read each hit
//      to distinguish a real mapping-persistence assertion from an unrelated pipeline-row check
//      or a comment.
//   e. BLANK/NULL ARGUMENTS passed to either register.
//
//  SCOPE: the grep matches exactly 18 files (enumerated below and independently confirmed by
//  the reviewer). PipelineStoreEfCoreIntegrationTests.cs is an ADDITIONAL named audit target
//  with ZERO register calls — it is dispositioned separately as entry 19, not as a grep match.
//
//  ITERATION-4 RE-VERIFICATION: every entry below was re-checked against the current tree after
//  iteration 3. Iteration 3 changed the caller surface only in comment / strengthened-assertion
//  form and added NO new register callers, so all dispositions carry forward unchanged. The one
//  iteration-4 edit (the unfiltered store observer in Dispatch_ExistingActiveTask_
//  ClaimRefusedNoMutation) is a test-observation change and adds no register caller.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  RED-FIXED (6) — failed under the new semantics and were migrated
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  1. TaskDispatchServiceTests.cs — RED-FIXED. Two vectors preseeded a live pointer via
//     SetActiveTask, which the atomic claim now refuses; migrated to seed CoderBranch directly
//     so the pointer stays free for the claim. Register calls here are arrangement-only, on
//     per-test managers with attempt-stamped ids; no duplicate id on one manager.
//  2. WorkSlotDispatchWiringTests.cs — RED-FIXED. Duplicate/persistence vectors migrated onto
//     the admission path; the competitor-steal seeds became unregister-then-register because
//     TryAdd no longer overwrites (a bare re-register would now be declined). Extended with the
//     section (8b) persisted-pointer E3 coverage.
//  3. WorkSlotMappingOwnershipTests.cs (incl. WorkSlotAdmissionCommitTests, same file) —
//     RED-FIXED. Validation-order/ParamName vectors, TryAdd refusal, memory-only raw-store
//     probes, removal of obsolete store-path expectations; SeedPersistedMapping /
//     SeedPersistedMappingRaw helpers added for fixtures that still need a durable row.
//  4. PipelineStoreTests.cs — RED-FIXED. RegisterTask_SavesTaskMappingToStore became
//     RegisterTask_ClaimsInMemoryWithoutWritingTheStore; the durable half of the old coverage is
//     retained in the new SaveTaskMapping_PersistsMappingLoadedByLoadActivePipelines.
//  5. PipelineLifecycleIntegrationTests.cs — RED-FIXED. The durable-register assumption was
//     replaced by a memory claim PLUS explicit _store.SaveTaskMapping seeding for all three
//     goals (lines 77-79); the restart/restoration assertions are preserved intact.
//  6. PipelineStoreEfCoreIntegrationTests.cs — RED-FIXED (pointer-snapshot expectations; ZERO
//     register calls, so it is not one of the 18 grep matches). Its admission assertions pin the
//     PREP-3 immutable-snapshot behaviour this goal consumes unchanged: line ~702 asserts
//     pipelines WHERE goal_id='goal-commit' AND active_task_id='task-commit' after
//     SaveAdmissionWithPointer; line ~897 seeds a pipelines row directly. No register-durability
//     or overwrite assumption is present anywhere in the file. Verified green.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  SEMANTIC-FIXED (2 files, 3 tests) — still GREEN under the new semantics, but VACUOUS
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  These did not fail; they would have silently stopped proving anything, because they asserted
//  the REMOVAL of a durable row that the memory-only register no longer writes. Each now seeds
//  the row explicitly AND asserts it exists BEFORE the removal, restoring the proof.
//
//  7. GoalPipelineTests.cs — SEMANTIC-FIXED. 5 sites, all DISTINCT ids per manager:
//     L779 "task-100" (storeless), L801 "task-1" (storeless), L995 "task-1" (STORE-BACKED),
//     L1016 "task-1" (storeless), L1106 "task-unreg" (STORE-BACKED). "task-1" repeats only
//     ACROSS separate test methods, each constructing its own manager — no same-manager sharing,
//     so change (1) is not observable. The two store-backed tests
//     (UnregisterTask_RemovesFromMemoryAndStore, and the unregister-then-restore vector) asserted
//     row removal; both now seed via store.SaveTaskMapping (L999, L1109) and assert presence
//     first. No blank/null arguments.
//  8. GoalDispatcherTests.cs — SEMANTIC-FIXED. 18 sites; 16 use fresh `task-{Guid}` ids on
//     storeless managers. The ONLY same-manager pair is L1603/L1604, and it uses DISTINCT
//     literals ("task-old-phase" / "task-current-phase") — no duplicate refusal. L1653 is
//     store-backed but its LoadPipeline assertion checks pipeline PHASE, not mappings. L4210
//     (resume-stale, store-backed) asserted "the stale mapping is gone" from
//     snapshot.TaskMappings — vacuous without a row, so it now seeds via store.SaveTaskMapping
//     (L4213) first. No blank/null arguments.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  UNAFFECTED (10) — audited call-site by call-site, no change needed
// ─────────────────────────────────────────────────────────────────────────────────────────────
//   9. DashboardNotifierWiringTests.cs — UNAFFECTED. 2 sites (L201, L253) in two separate
//      fixture helpers, each `$"task-{Guid.NewGuid():N}"` on its own storeless
//      `new GoalPipelineManager()`. Fresh-ID caller: no same-manager sharing, so the old
//      overwrite semantics were never relied on. Persistence probe: no hits. No blank input.
//  10. GoalDispatcherCancelTests.cs — UNAFFECTED. 3 sites (L51, L195, L972), all GUID ids on
//      storeless managers (L46, L192, L967). The single probe hit (L1199) is a COMMENT about an
//      unrelated manager that never registers. Fresh-ID caller. No blank input.
//  11. HiveOrchestratorIssueToolTests.cs — UNAFFECTED. 1 site (L58) in helper
//      CreatePipelineForTask on a storeless manager (L28). Callers pass the literal "task-1", but
//      each test builds a fresh service/manager and registers ONCE — no same-manager sharing.
//      Persistence probe: no hits.
//  12. HiveOrchestratorNarrativeToolTests.cs — UNAFFECTED. 1 site (L53), storeless manager
//      (L25), one registration per fresh manager. Persistence probe: no hits.
//  13. HonestPlanningWindowTests.cs — UNAFFECTED. 3 sites: the harness registers a fresh
//      `task-{Guid}` (L1073) on a storeless manager (L1068); L811 "task-other-active" and L840
//      "task-current-phase" are DISTINCT literals registered once each, and neither collides
//      with the harness GUID id — no same-manager sharing. Persistence probe: no hits.
//  14. PipelineDriverTests.cs — UNAFFECTED. 1 site (L165), GUID id, storeless manager (L154).
//      The driver is wired with a STUBBED `dispatchToRole: (_,_,_,_) => Task.CompletedTask`
//      (L472, L846), so the register/admission path is unreachable from this suite.
//      Persistence probe: no hits.
//  15. PlanRejectContractTests.cs — UNAFFECTED. 2 sites (L884, L955), both GUID ids on STORELESS
//      managers (L873, L943). The two store-backed managers (L699, L752) never call RegisterTask,
//      and their probe hits (L724, L776 LoadPipeline) assert pipeline PHASE, not mappings.
//      Fresh-ID caller.
//  16. PremiumModelSelectionTests.cs — UNAFFECTED. 2 sites (L248, L326), GUID ids on two separate
//      storeless managers (L237, L315). Persistence probe: no hits.
//  17. Services/EventBusProducerTests.cs — UNAFFECTED. 1 site (L810) on a storeless manager, one
//      registration. Persistence probe: no hits.
//  18. StaleWorkerCleanupServiceIntegrationTests.cs — UNAFFECTED. 1 site (L158), storeless
//      manager (L142). Persistence probe: no hits.
//  19. StaleWorkerCleanupServiceTests.cs — UNAFFECTED. 4 sites (L468, L616 via ArrangeAdmission,
//      L804, L871), all storeless managers. L871 registers the attempt-stamped
//      "…-coder-001-01-002" while ArrangeAdmission registered "…-001-01-001" on the same manager
//      — DISTINCT ids, so no duplicate refusal. The two probe hits (L502, L543) are COMMENTS
//      describing an interceptor, not persistence assertions.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  COMPLETION-FIX REMOVAL / CONCURRENCY VERIFICATION (TaskCompletionService.cs)
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  The authorized scope exception from iteration 2 — a single ownership-checked
//  `pipeline.ClearActiveTaskIfCurrent(result.TaskId)` on the completion path — is NOT modified by
//  iterations 3 or 4 (`git diff 315d04f..HEAD -- src/` touches TaskDispatchService.cs only).
//
//  REMOVAL PROOF (real-chain mutant, re-run in iteration 4): deleting that single line and
//  re-running the real completion chain fails 10 tests — the 9 ProgressDocumentTests plus
//  Dispatch_SequentialPhases_SecondClaimSucceedsAfterPointerRelease (which drives the REAL
//  TaskCompletionService → PipelineDriver → DispatchToRole chain, so it exercises the production
//  release rather than modelling it). Restoring the line returns all to green.
//
//  CONCURRENCY: the release is ownership-checked under GoalPipeline._lock — it nulls the pointer
//  only while it still equals THIS task id, so a newer dispatch's claim survives as a no-op and
//  the clear cannot race an in-flight admission for a DIFFERENT task. Placement is AFTER the
//  AdmitCompletion switch, so every guard/early-exit path (no pipeline, terminal goal, planning
//  window, stale completion, abandoned slot, already-admitted slot) returns before it and leaves
//  the pointer untouched. It is strictly BEFORE the drive because DriveNextPhaseAsync dispatches
//  the next phase inline — a later placement would not unblock that dispatch's claim.
//  CONCURRENCY CLASS RE-RUN (iteration 4): WorkSlotAdmissionCommitTests,
//  WorkSlotMappingOwnershipTests, ProgressDocumentTests, TaskCompletion* and StaleWorkerCleanup*
//  → 190 passed, 0 failed.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for <see cref="TaskDispatchService"/> verifying that behaviors
/// extracted from <see cref="GoalDispatcher"/> are preserved.
/// </summary>
public sealed class TaskDispatchServiceTests
{
    // ── ResolveRepositories ───────────────────────────────────────────────

    [Fact]
    public void ResolveRepositories_AllValidNames_ReturnsAllRepositories()
    {
        var service = CreateService(config: new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
                new RepositoryConfig { Name = "RepoB", Url = "https://github.com/org/repo-b" },
            ],
        });
        var goal = new Goal { Id = "goal-1", Description = "Test", RepositoryNames = ["RepoA", "RepoB"] };

        var result = service.ResolveRepositories(goal);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Name == "RepoA");
        Assert.Contains(result, r => r.Name == "RepoB");
    }

    [Fact]
    public void ResolveRepositories_UnknownName_ThrowsInvalidOperationExceptionWithGoalIdAndRepoName()
    {
        var service = CreateService(config: new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
            ],
        });
        var goal = new Goal { Id = "goal-42", Description = "Test", RepositoryNames = ["missing-repo"] };

        var ex = Assert.Throws<InvalidOperationException>(() => service.ResolveRepositories(goal));

        Assert.Contains("goal-42", ex.Message);
        Assert.Contains("missing-repo", ex.Message);
    }

    [Fact]
    public void ResolveRepositories_MixOfValidAndInvalid_FailsWithoutPartialResults()
    {
        var service = CreateService(config: new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "RepoA", Url = "https://github.com/org/repo-a" },
            ],
        });
        var goal = new Goal { Id = "goal-3", Description = "Test", RepositoryNames = ["RepoA", "bad-repo"] };

        Assert.Throws<InvalidOperationException>(() => service.ResolveRepositories(goal));
    }

    // ── DispatchToRole: premium model selection ───────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenPremiumTierAndPremiumModelConfigured_UsesPremiumModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            PremiumModel = "premium-coder-model",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("premium-coder-model", capturedTask!.Model);
    }

    [Fact]
    public async Task DispatchToRole_WhenPremiumTierButNoPremiumModel_FallsBackToStandardModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            // No PremiumModel configured
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model", capturedTask!.Model);
    }

    [Fact]
    public async Task DispatchToRole_WhenStandardTier_UsesStandardModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            PremiumModel = "premium-coder-model",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model", capturedTask!.Model);
    }

    // ── DispatchToRole: Slice 3b refusal (effective-model-first) ─────────

    /// <summary>
    /// A null/unconfigured role model REFUSES the dispatch at the TOP of DispatchToRole with the
    /// exact lowercase role-name message. No task is registered with the pipeline manager, no
    /// task is enqueued/delivered to a worker, and no worker/LLM session is created.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenEffectiveModelNull_RefusesWithoutAnyRegistrationOrDelivery()
    {
        var config = CreateConfig();
        // No Workers["coder"] entry → GetModelForRole returns null → effective model is null.

        var workerPool = new WorkerPool();
        var idleWorker = workerPool.RegisterWorker("worker-1", []);
        var gateway = new GrpcWorkerGateway(workerPool);

        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue,
            workerGateway: gateway);

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        Assert.Equal("role 'coder' has no configured model", ex.Message);

        // No task registration with the pipeline manager (SetActiveTask/RegisterTask never ran).
        Assert.Null(pipeline.ActiveTaskId);
        // No task enqueued.
        Assert.Null(capturedTask);
        Assert.Null(taskQueue.TryDequeueAny());
        // No worker delivery / session: the idle worker was never touched.
        Assert.False(idleWorker.IsBusy);
        Assert.Null(idleWorker.CurrentTaskId);
        Assert.Null(idleWorker.CurrentModel);
        Assert.Equal(WorkerRole.Unspecified, idleWorker.Role);
    }

    /// <summary>
    /// A premium phase with <c>premium_model</c> set and NO standard role model dispatches on the
    /// premium model — it is NOT refused (the effective model is the premium model).
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenPremiumTierPremiumModelSetButNoStandardModel_DispatchesOnPremiumModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            // No standard Model configured — only the premium model.
            PremiumModel = "premium-coder-model",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("premium-coder-model", capturedTask!.Model);
    }

    /// <summary>
    /// A premium phase with no <c>premium_model</c> falls back to the standard role model
    /// (preserved exemption — NOT refused). Pins the existing behavior.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenPremiumTierNoPremiumModel_UsesStandardRoleModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            // No PremiumModel configured.
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model", capturedTask!.Model);
    }

    // ── DispatchToRole: model names are always plain ──────────────────────

    /// <summary>
    /// An <c>available_models</c> reasoning effort is never appended to the dispatched model
    /// name, and it is no longer used as a fallback source for the transported effort.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenAvailableModelHasReasoningEffort_DispatchesPlainModelName()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "standard-coder-model", ReasoningEffort = "high" }
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model", capturedTask!.Model);
        Assert.Null(capturedTask.ReasoningEffort);
    }

    [Fact]
    public async Task DispatchToRole_WhenModelHasNoReasoningEffort_DoesNotAppendSuffix()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "standard-coder-model" }
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model", capturedTask!.Model);
    }

    // ── DispatchToRole: reasoning effort transport ────────────────────────

    /// <summary>
    /// A per-role <c>reasoning_effort</c> in WorkerConfig is transported as an enum on the
    /// WorkTask (authoritative for the worker); the model name stays plain.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenWorkerConfigReasoningEffortSet_TransportsEnum()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            ReasoningEffort = "high",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal(ReasoningEffort.High, capturedTask!.ReasoningEffort);
        Assert.Equal("standard-coder-model", capturedTask.Model);
    }

    /// <summary>
    /// When the Brain requested the premium tier and a premium model is actually configured,
    /// the premium reasoning effort must be selected instead of the standard one.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenPremiumTierAndPremiumReasoningEffort_TransportsPremiumEnum()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            ReasoningEffort = "low",
            PremiumModel = "premium-coder-model",
            PremiumReasoningEffort = "medium",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal(ReasoningEffort.Medium, capturedTask!.ReasoningEffort);
        Assert.Equal("premium-coder-model", capturedTask.Model);
    }

    /// <summary>
    /// Premium tier without a configured premium model falls back to the standard model, so it
    /// must also fall back to the standard reasoning effort — not the premium one.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenPremiumTierButNoPremiumModel_FallsBackToStandardReasoningEffort()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            ReasoningEffort = "low",
            // No PremiumModel configured — premium effort must be ignored
            PremiumReasoningEffort = "extra_high",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal(ReasoningEffort.Low, capturedTask!.ReasoningEffort);
        Assert.Equal("standard-coder-model", capturedTask.Model);
    }

    /// <summary>
    /// A whitespace-only premium model name is not a usable model. Both the model selection and
    /// the reasoning-effort selection must reject it, so the standard model AND the standard
    /// effort apply — a whitespace model name must never reach the worker.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenPremiumModelIsWhitespace_FallsBackToStandardModelAndStandardReasoning()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            ReasoningEffort = "low",
            PremiumModel = "   ",
            PremiumReasoningEffort = "extra_high",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        // The dispatched model must be the standard model — never the whitespace premium value.
        Assert.Equal("standard-coder-model", capturedTask!.Model);
        Assert.False(string.IsNullOrWhiteSpace(capturedTask.Model));
        // And the standard effort applies, not the premium one.
        Assert.Equal(ReasoningEffort.Low, capturedTask.ReasoningEffort);
    }

    /// <summary>
    /// Same guard with no efforts configured at all: a whitespace premium model must not become
    /// the dispatched model name.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenPremiumModelIsWhitespaceAndNoEfforts_DispatchesStandardModel()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            PremiumModel = "   ",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("standard-coder-model", capturedTask!.Model);
        Assert.Null(capturedTask.ReasoningEffort);
    }

    /// <summary>
    /// With no per-role effort configured, nothing is transported — the per-model
    /// <c>available_models</c> reasoning effort is no longer a fallback source.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenWorkerConfigEffortNull_TransportsNullAndDoesNotUseAvailableModels()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            ReasoningEffort = null,
        };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "standard-coder-model", ReasoningEffort = "high" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Null(capturedTask!.ReasoningEffort);
        Assert.Equal("standard-coder-model", capturedTask.Model);
    }

    /// <summary>
    /// A whitespace-only per-role effort is treated as unset; no fallback source exists.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenWorkerConfigEffortWhitespace_TransportsNull()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            ReasoningEffort = "   ",
        };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "standard-coder-model", ReasoningEffort = "high" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Null(capturedTask!.ReasoningEffort);
        Assert.Equal("standard-coder-model", capturedTask.Model);
    }

    /// <summary>
    /// A sloppy YAML value like <c>"  High "</c> is parsed into the canonical enum and the
    /// dispatched model name stays plain.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_CanonicalizesEffort_AndKeepsModelNamePlain()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            ReasoningEffort = "  High ",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal(ReasoningEffort.High, capturedTask!.ReasoningEffort);
        Assert.Equal("standard-coder-model", capturedTask.Model);
        Assert.DoesNotContain(":", capturedTask.Model, StringComparison.Ordinal);
    }

    /// <summary>
    /// A colon in the configured model name is part of the name (e.g. an Ollama tag) and is
    /// dispatched verbatim. The configured per-role effort is transported separately.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenModelNameContainsColon_DispatchedVerbatim()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "test-model:low",
            ReasoningEffort = "high",
        };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "test-model:low" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("test-model:low", capturedTask!.Model);
        Assert.Equal(ReasoningEffort.High, capturedTask.ReasoningEffort);
    }

    /// <summary>
    /// With no config loaded, no model can be resolved for the role — the Slice 3b refusal
    /// fires at the TOP of DispatchToRole, BEFORE repository resolution, so nothing is
    /// enqueued and the goal is not marked failed (the refusal propagates to the caller).
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenNoConfig_RefusesWithNoConfiguredModel()
    {
        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config: null, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken));

        Assert.Equal("role 'coder' has no configured model", ex.Message);
        Assert.Null(capturedTask);
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// When no model is resolved for the role, the Slice 3b refusal fires at the TOP of
    /// DispatchToRole — the dispatch is refused with the exact lowercase role-name message
    /// and no task is built or enqueued.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenNoModelForRole_RefusesWithExactMessage()
    {
        var config = CreateConfig();
        // No Workers["coder"] entry → GetModelForRole returns null.
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "some-other-model", ReasoningEffort = "high" }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken));

        Assert.Equal("role 'coder' has no configured model", ex.Message);
        Assert.Null(capturedTask);
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// A role with no WorkerConfig entry at all is refused (Slice 3b) — the effective model is
    /// null, so the dispatch throws with the exact lowercase role-name message.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenRoleMissingFromWorkersConfig_RefusesWithExactMessage()
    {
        var config = CreateConfig();
        // Deliberately no config.Workers["coder"] entry.

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken));

        Assert.Equal("role 'coder' has no configured model", ex.Message);
        Assert.Null(capturedTask);
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// With neither a per-role nor a per-model effort configured, nothing is transported and the
    /// model name is left untouched.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenNoEffortAnywhere_TransportsNullAndLeavesModelUnchanged()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "standard-coder-model" }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Null(capturedTask!.ReasoningEffort);
        Assert.Equal("standard-coder-model", capturedTask.Model);
    }

    /// <summary>
    /// Startup validates reasoning efforts, but dynamic config reloads deliberately do not
    /// re-validate. An invalid value must therefore degrade to "unset" and still dispatch,
    /// rather than throwing an unhandled <see cref="ArgumentException"/> and failing the goal.
    /// </summary>
    [Theory]
    [InlineData("turbo")]
    [InlineData("HIGHEST")]
    [InlineData("1")]
    public async Task DispatchToRole_WhenConfiguredEffortInvalid_DispatchesWithNullEffort(string effort)
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            ReasoningEffort = effort,
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        // The dispatch succeeded (no exception) and the invalid effort was treated as unset.
        Assert.NotNull(capturedTask);
        Assert.Null(capturedTask!.ReasoningEffort);
        Assert.Equal("standard-coder-model", capturedTask.Model);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);
    }

    /// <summary>
    /// The same leniency applies to an invalid premium reasoning effort on the premium tier.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenPremiumEffortInvalid_DispatchesWithNullEffort()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig
        {
            Model = "standard-coder-model",
            PremiumModel = "premium-coder-model",
            PremiumReasoningEffort = "turbo",
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Premium);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Null(capturedTask!.ReasoningEffort);
        Assert.Equal("premium-coder-model", capturedTask.Model);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);
    }

    // ── DispatchToRole: compaction model metadata ─────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenCompactionModelConfigured_PropagatesCompactionMetadata()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            CompactionModel = "gpt-mini",
            AvailableModels =
            [
                new ModelEntry { Name = "gpt-mini", ContextWindow = 8000 },
                new ModelEntry { Name = "standard-coder-model" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(capturedTask!.Metadata.ContainsKey("compaction_model"));
        Assert.Equal("gpt-mini", capturedTask.Metadata["compaction_model"]);
        Assert.True(capturedTask.Metadata.ContainsKey("compaction_max_tokens"));
        Assert.Equal("8000", capturedTask.Metadata["compaction_max_tokens"]);
    }

    [Fact]
    public async Task DispatchToRole_WhenCompactionModelHasReasoningEffort_UsesPlainModelNameWithoutSuffix()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            CompactionModel = "gpt-mini",
            AvailableModels =
            [
                new ModelEntry { Name = "gpt-mini", ContextWindow = 8000, ReasoningEffort = "low" },
                new ModelEntry { Name = "standard-coder-model" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        // Reasoning effort is no longer encoded as a model-name suffix — the plain name is sent.
        Assert.Equal("gpt-mini", capturedTask!.Metadata["compaction_model"]);
        Assert.DoesNotContain(":low", capturedTask.Metadata["compaction_model"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchToRole_WhenNoCompactionModel_DoesNotSetCompactionMetadata()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        // No Models config → no compaction model

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("compaction_model"));
    }

    // ── DispatchToRole: iteration SHA metadata ────────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenIterationStartShaSet_PropagatesShaMetadata()
    {
        var config = CreateConfig();
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Review, config, ModelTier.Default);

        pipeline.IterationStartSha = "abc123sha";

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Reviewer, "Review it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(capturedTask!.Metadata.ContainsKey("iteration_start_sha"));
        Assert.Equal("abc123sha", capturedTask.Metadata["iteration_start_sha"]);
    }

    [Fact]
    public async Task DispatchToRole_WhenIterationStartShaNull_DoesNotSetShaMetadata()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        // IterationStartSha is null by default
        Assert.Null(pipeline.IterationStartSha);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("iteration_start_sha"));
    }

    // ── DispatchToRole: tester report metadata for reviewer role ──────────

    [Fact]
    public async Task DispatchToRole_WhenReviewerRoleAndTesterOutputExists_PropagatesTesterReport()
    {
        var config = CreateConfig();
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Review, config, ModelTier.Default);

        // Add a testing phase log entry with worker output for the current iteration
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            WorkerOutput = "All 50 tests passed.",
        });

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Reviewer, "Review it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(capturedTask!.Metadata.ContainsKey("tester_report"));
        Assert.Equal("All 50 tests passed.", capturedTask.Metadata["tester_report"]);
    }

    [Fact]
    public async Task DispatchToRole_WhenReviewerRoleButNoTesterOutput_DoesNotSetTesterReport()
    {
        var config = CreateConfig();
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Review, config, ModelTier.Default);

        // No testing phase log entry

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Reviewer, "Review it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("tester_report"));
    }

    [Fact]
    public async Task DispatchToRole_WhenNonReviewerRole_DoesNotSetTesterReport()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        // Even with tester output in the log, coder should not get tester_report
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            WorkerOutput = "Tests passed.",
        });

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("tester_report"));
    }

    // ── DispatchToRole: improver branch downgrade ─────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenImproverRole_DowngradesBranchActionToUnspecified()
    {
        var config = CreateConfig();
        config.Workers["improver"] = new WorkerConfig { Model = "improver-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Improve, config, ModelTier.Default);

        // Seed ONLY the coder branch so the task has BranchInfo. Deliberately NOT via
        // SetActiveTask: the admission-atomic-switch made the dispatch's pointer claim
        // ownership-refusing (TrySetActiveTask), so leaving a live pointer here would make the
        // dispatch legitimately refuse. The branch is what this test is about; the pointer must
        // stay free for the dispatch to claim.
        pipeline.CoderBranch = "feature/test-branch";

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Improver, "Improve it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.NotNull(capturedTask!.BranchInfo);
        Assert.Equal(BranchAction.Unspecified, capturedTask.BranchInfo!.Action);
    }

    [Fact]
    public async Task DispatchToRole_WhenNonImproverRole_KeepsBranchAction()
    {
        var config = CreateConfig();
        config.Workers["tester"] = new WorkerConfig { Model = "tester-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Testing, config, ModelTier.Default);

        // Seed ONLY the coder branch so the task has BranchInfo (Checkout action). See the
        // improver test above: the pointer must stay free for the dispatch's atomic claim.
        pipeline.CoderBranch = "feature/test-branch";

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Tester, "Test it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.NotNull(capturedTask!.BranchInfo);
        // Tester reuses the existing branch → action should be Checkout, not Unspecified
        Assert.Equal(BranchAction.Checkout, capturedTask.BranchInfo!.Action);
    }

    // ── The atomic claim: refusal, escape cleanup and branch residue ──────

    /// <summary>
    /// THE OVERLAPPING-DISPATCH REFUSAL. A pipeline that already carries a LIVE active-task
    /// pointer refuses the claim: the newly captured slot is released, nothing else is mutated,
    /// and the exact refusal message is thrown.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: relaxing <c>TrySetActiveTask</c> back to the old unconditional
    /// <c>SetActiveTask</c> overwrite would let the dispatch steal the live pointer — this test
    /// then fails on both the throw and the preserved-pointer assertion.
    /// </remarks>
    [Fact]
    public async Task Dispatch_ExistingActiveTask_ClaimRefusedNoMutation()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        // A REAL STORE plus an UNFILTERED command observer, so "no store call" is an OBSERVED
        // fact rather than an inference. AdmissionCommandCounter records EVERY statement, not
        // just task_mappings ones: a filtered counter would stay empty for a pipeline-only
        // persistence call (e.g. a stray PersistState/pipelines UPDATE) inside the refusal
        // block, leaving the contract violated but the assertion green.
        using var storeFixture = new SqliteMappingFixture();
        var counter = new AdmissionCommandCounter();
        var pipelineManager = new GoalPipelineManager(storeFixture.CreateStore(counter));

        var taskQueue = new TaskQueue();
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        var service = CreateService(
            config: config, pipelineManager: pipelineManager, taskQueue: taskQueue, goal: goal);

        // THE LIVE POINTER, arranged deterministically: another attempt already owns it.
        pipeline.SetActiveTask("incumbent-task", "copilothive/incumbent-branch");
        var pointerBefore = pipeline.ActiveTaskId;
        var branchBefore = pipeline.CoderBranch;

        var enqueued = false;
        taskQueue.OnEnqueue = _ => enqueued = true;

        // Recording is ARMED AFTER the whole arrangement (pipeline creation, plan install and
        // the incumbent pointer), so setup-seeded writes cannot contaminate the count — only
        // the dispatch's own statements are observed.
        counter.Start();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        var refusedTaskId = $"{pipeline.GoalId}-coder-001-01-001";
        Assert.Equal(
            $"Task mapping registration failed for {refusedTaskId} (goal {pipeline.GoalId}) — " +
            "the pipeline already has an active task (an overlapping dispatch refused)",
            ex.Message);
        Assert.Null(ex.InnerException);

        // THE SLOT the refused dispatch captured is released…
        var slot = Assert.Single(pipeline.GetSlotsForTest(), s => s.Slot.TaskId == refusedTaskId);
        Assert.Equal(WorkSlotState.Abandoned, slot.State);

        // NO MAPPING was added — neither in memory…
        Assert.Null(pipelineManager.GetByTaskId(refusedTaskId));
        // …nor in the store (the durable row was never written).
        Assert.Null(storeFixture.ReadPersistedGoalId(refusedTaskId));

        // NO STORE CALL OF ANY KIND: the claim refusal precedes PersistAdmission, so not a
        // single statement — against task_mappings, pipelines or anything else — reached the
        // database. The observer is unfiltered precisely so a pipeline-only write cannot slip
        // past this assertion.
        Assert.Empty(counter.Commands);

        // …and NOTHING else moved: the incumbent pointer and branch are untouched, and nothing
        // was enqueued.
        Assert.Equal(pointerBefore, pipeline.ActiveTaskId);
        Assert.Equal(branchBefore, pipeline.CoderBranch);
        Assert.False(enqueued);
        Assert.Null(taskQueue.TryDequeueAny());
    }

    /// <summary>
    /// THE ESCAPED-VALIDATION FLOW. The pointer is cleared through the
    /// <c>AdmissionGateForTest</c> seam AFTER the claim but BEFORE the admission, so
    /// <c>PersistAdmission</c>'s own active-task equality check throws. The dispatch's catch
    /// releases everything it took and rethrows the ORIGINAL exception.
    /// </summary>
    /// <remarks>
    /// The gate deliberately throws NOTHING: it is invoked OUTSIDE the try, so a sentinel thrown
    /// from it would never reach the catch under test. The exception asserted here is the one
    /// PersistAdmission itself constructs, so its identity is pinned by TYPE, ParamName and
    /// MESSAGE rather than by reference (no observer seam, no unsealing, no hook sentinel).
    /// </remarks>
    [Fact]
    public async Task Dispatch_PointerClearedBeforeAdmission_EscapeCleanupAndOriginalRethrown()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        // A CAPTURING logger so the abandoned-registration emission is assertable — the escape
        // flow must produce the same slot-release record as the R1–R5 flow.
        var logger = new TestLogger<TaskDispatchService>();
        var service = CreateService(config, pipelineManager, taskQueue, logger);

        var expectedTaskId = $"{pipeline.GoalId}-coder-001-01-001";

        // AN INDEPENDENTLY OWNED MAPPING for the very task id this dispatch will use. The
        // rollback's unregister is OWNERSHIP-CHECKED, so it must leave this foreign claim intact.
        pipelineManager.CreatePipeline(new Goal
        {
            Id = "goal-foreign-owner",
            Description = "Foreign",
            RepositoryNames = ["test-repo"],
        });
        pipelineManager.RegisterTask(expectedTaskId, "goal-foreign-owner");

        // THE SEAM: clear the pointer between the claim and the admission. The gate itself never
        // throws — it only arranges the state that makes PersistAdmission's validation fail.
        var gateObservedPointer = (string?)null;
        service.AdmissionGateForTest = (p, taskId) =>
        {
            gateObservedPointer = p.ActiveTaskId;
            p.ClearActiveTaskIfCurrent(taskId);
        };

        var enqueued = false;
        taskQueue.OnEnqueue = _ => enqueued = true;

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE SETUP PROOF: the claim really had succeeded when the gate ran.
        Assert.Equal(expectedTaskId, gateObservedPointer);

        // THE ORIGINAL exception — PersistAdmission's own validation, rethrown BARE (a wrapper
        // would surface as InvalidOperationException with this as InnerException instead).
        // The FULL rendered message is compared, including the ParamName suffix ArgumentException
        // appends, so no part of the validation text can drift unnoticed.
        Assert.Equal("taskId", ex.ParamName);
        Assert.Equal(
            $"Task id '{expectedTaskId}' does not match the pipeline's active task id '' " +
            $"(goal={pipeline.GoalId}). (Parameter 'taskId')",
            ex.Message);

        // THE CLEANUP: the slot is released…
        var slot = Assert.Single(pipeline.GetSlotsForTest(), s => s.Slot.TaskId == expectedTaskId);
        Assert.Equal(WorkSlotState.Abandoned, slot.State);

        // …the ownership-checked unregister left the FOREIGN mapping untouched…
        Assert.Equal("goal-foreign-owner", pipelineManager.GetByTaskId(expectedTaskId)?.GoalId);

        // …the if-current clear was a no-op (the gate had already nulled the pointer)…
        Assert.Null(pipeline.ActiveTaskId);

        // …the ABANDONED-REGISTRATION record was emitted, verbatim at WARNING: the escape flow
        // logs the same slot-release line the R1–R5 flow does.
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message ==
                $"WorkSlotIntegrity: abandoned-registration goal={pipeline.GoalId} task={expectedTaskId} " +
                $"position=1:Coding:1 — the dispatch failed before delivery; the slot is released");

        // …and nothing was enqueued.
        Assert.False(enqueued);
        Assert.Null(taskQueue.TryDequeueAny());
    }

    /// <summary>
    /// THE ACCEPTED BRANCH RESIDUE. A refusal AFTER a successful claim runs the R1–R5 rollback,
    /// which deliberately does NOT restore <c>CoderBranch</c>: the branch is derived
    /// deterministically per goal, so the value the claim assigned is already the canonical one.
    /// </summary>
    [Fact]
    public async Task Dispatch_AdmissionConflict_BranchResidueCanonical()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var (service, pipeline, taskQueue, pipelineManager) = CreateServiceWithPipelineAndManager(
            GoalPhase.Coding, config, ModelTier.Default);

        var expectedTaskId = $"{pipeline.GoalId}-coder-001-01-001";
        Assert.Null(pipeline.CoderBranch);

        // A FOREIGN in-memory mapping for this task id makes PersistAdmission refuse with
        // MemoryConflict AFTER the claim has already assigned CoderBranch.
        pipelineManager.CreatePipeline(new Goal
        {
            Id = "goal-conflict-owner",
            Description = "Conflict",
            RepositoryNames = ["test-repo"],
        });
        pipelineManager.RegisterTask(expectedTaskId, "goal-conflict-owner");

        var enqueued = false;
        taskQueue.OnEnqueue = _ => enqueued = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // R5 — the occupied-mapping message with a NULL inner exception (MemoryConflict is not a
        // persistence failure, so nothing is carried).
        Assert.Equal(
            $"Task mapping registration failed for {expectedTaskId} (goal {pipeline.GoalId}) — " +
            "the mapping is occupied or the persistence failed",
            ex.Message);
        Assert.Null(ex.InnerException);

        // R1 — the slot is released; R3 — the pointer is cleared.
        var slot = Assert.Single(pipeline.GetSlotsForTest(), s => s.Slot.TaskId == expectedTaskId);
        Assert.Equal(WorkSlotState.Abandoned, slot.State);
        Assert.Null(pipeline.ActiveTaskId);

        // R2 — the ownership-checked unregister left the foreign mapping intact.
        Assert.Equal("goal-conflict-owner", pipelineManager.GetByTaskId(expectedTaskId)?.GoalId);
        Assert.False(enqueued);

        // THE RESIDUE, and why it is correct: the retained branch equals what a later same-goal
        // derivation produces, so it is canonical rather than stale.
        var laterDerivation = new BranchCoordinator().GetFeatureBranch(pipeline.GoalId);
        Assert.Equal(laterDerivation, pipeline.CoderBranch);
    }

    /// <summary>
    /// THE SEQUENTIAL PHASE HANDOFF. Two dispatches for the SAME pipeline, separated by the
    /// completion path's pointer release, both succeed — the second one ONLY because the release
    /// happened.
    /// </summary>
    /// <remarks>
    /// THE REGRESSION THIS LOCKS. The atomic claim replaced the old unconditional
    /// <c>SetActiveTask</c> overwrite, so the pointer is no longer released implicitly between
    /// phases. <c>TaskCompletionService</c> now releases it explicitly via the ownership-checked
    /// <c>ClearActiveTaskIfCurrent</c>. REMOVE THAT CLEAR AND THIS TEST FAILS: the second
    /// dispatch's claim finds the first task's pointer still live and refuses with the
    /// overlapping-dispatch message, which is exactly the bug that broke every multi-phase goal.
    /// <para>
    /// THE RELEASE IS NOT MODELLED HERE — it is EXERCISED. The completion runs through a REAL
    /// <see cref="TaskCompletionService"/>, whose driver re-enters the REAL
    /// <see cref="TaskDispatchService.DispatchToRole"/> for the next phase. So the second claim
    /// is performed by production code on a pointer released by production code; deleting the
    /// production clear makes this test go red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Dispatch_SequentialPhases_SecondClaimSucceedsAfterPointerRelease()
    {
        var config = CreateConfig();
        // Every worker phase in the default plan needs a model: the driver advances
        // Coding -> DocWriting and dispatches the next phase inline.
        foreach (var roleName in new[] { "coder", "docwriter", "tester", "reviewer" })
            config.Workers[roleName] = new WorkerConfig { Model = $"{roleName}-model" };

        var (service, pipeline, taskQueue, pipelineManager) = CreateServiceWithPipelineAndManager(
            GoalPhase.Coding, config, ModelTier.Default);

        var dispatched = new List<string>();
        taskQueue.OnEnqueue = t => dispatched.Add(t.TaskId);

        // PHASE 1 — the Coding dispatch claims the free pointer.
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);
        var firstTaskId = pipeline.ActiveTaskId;
        Assert.NotNull(firstTaskId);
        Assert.Equal([firstTaskId], dispatched);

        // THE COMPLETION — the REAL TaskCompletionService. Its driver advances the plan and
        // re-enters the REAL DispatchToRole for the next phase, so the release and the second
        // claim are both production code paths, not test-arranged state.
        var completionService = CreateCompletionService(pipelineManager, service, config, pipeline.Goal);
        await completionService.HandleTaskCompletionAsync(
            new TaskResult
            {
                TaskId = firstTaskId!,
                Status = TaskOutcome.Completed,
                Output = "done",
                GitStatus = new GitChangeSummary { FilesChanged = 2 },
                Metrics = new TaskMetrics { Verdict = "PASS" },
            },
            TestContext.Current.CancellationToken);

        // THE PROOF: the next phase really dispatched — a second, DIFFERENT task id reached the
        // queue and now owns the pointer. Under the un-released pointer the second claim would
        // have thrown the overlapping-dispatch refusal and nothing would have been enqueued.
        Assert.Equal(2, dispatched.Count);
        var secondTaskId = dispatched[1];
        Assert.NotEqual(firstTaskId, secondTaskId);
        Assert.Equal(secondTaskId, pipeline.ActiveTaskId);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);
    }

    /// <summary>
    /// A real <see cref="TaskCompletionService"/> whose driver re-enters
    /// <paramref name="dispatchService"/> — the production completion → drive → dispatch chain.
    /// </summary>
    private static TaskCompletionService CreateCompletionService(
        GoalPipelineManager pipelineManager, TaskDispatchService dispatchService, HiveConfigFile config, Goal goal)
    {
        var goalManager = new GoalManager();
        goalManager.AddSource(new DispatchTestGoalSource(goal));
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();
        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var pipelineDriver = new PipelineDriver(
            brain: new SequentialPhaseBrain(),
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (p, role, prompt, ct) => dispatchService.DispatchToRole(p, role, prompt, ct),
            resolvePrompt: (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default())),
            resolveRepositories: _ => [.. config.Repositories.Select(r => new TargetRepository
            {
                Name = r.Name,
                Url = r.Url,
                DefaultBranch = r.DefaultBranch,
            })],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return new TaskCompletionService(
            pipelineManager, new SequentialPhaseBrain(), pipelineDriver, lifecycleService,
            dashboardNotifier: null, NullLogger<TaskCompletionService>.Instance);
    }

    // ── DispatchToRole: task registration and queue enqueue ───────────────

    /// <summary>
    /// The dispatched task ID is ATTEMPT-STAMPED by the work-slot capture and reaches
    /// <see cref="TaskBuilder.Build"/> verbatim, and the task → goal mapping is claimed through
    /// the ADMISSION (<c>GoalPipelineManager.PersistAdmission</c>, entered after the atomic
    /// <c>TrySetActiveTask</c> claim) — a mapping the manager can look up, backed by a live
    /// pending slot.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_RegistersTaskAndEnqueuesToQueue()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue);

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        WorkTask? enqueuedTask = null;
        taskQueue.OnEnqueue = t => enqueuedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // Task was enqueued with the attempt-stamped ID the capture allocated.
        Assert.NotNull(enqueuedTask);
        Assert.Equal($"{goal.Id}-coder-001-01-001", enqueuedTask!.TaskId);

        // Task was registered in pipeline manager (taskId → goalId mapping)
        var lookupPipeline = pipelineManager.GetByTaskId(enqueuedTask.TaskId);
        Assert.NotNull(lookupPipeline);
        Assert.Equal(goal.Id, lookupPipeline!.GoalId);

        // Active task was set on the pipeline
        Assert.Equal(enqueuedTask.TaskId, pipeline.ActiveTaskId);
    }

    /// <summary>
    /// The dispatch consumes the OWNERSHIP-CHECKED registration: a mapping already owned by
    /// another goal is REFUSED (never stolen), the captured slot is released, and no task is
    /// enqueued. Under the legacy unconditional <c>RegisterTask</c> the mapping would silently
    /// be overwritten and the dispatch would proceed.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenMappingOwnedByAnotherGoal_RefusesAndReleasesSlot()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue);

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        var expectedTaskId = $"{goal.Id}-coder-001-01-001";
        var otherGoal = new Goal { Id = $"goal-other-{Guid.NewGuid():N}", Description = "Other" };
        pipelineManager.CreatePipeline(otherGoal);
        pipelineManager.RegisterTask(expectedTaskId, otherGoal.Id);

        WorkTask? enqueuedTask = null;
        taskQueue.OnEnqueue = t => enqueuedTask = t;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Task mapping registration failed for {expectedTaskId} (goal {goal.Id}) — the mapping is occupied or the persistence failed",
            ex.Message);
        Assert.Null(ex.InnerException);

        // The competing mapping is intact, nothing was delivered, and the pointer stays unset.
        Assert.Equal(otherGoal.Id, pipelineManager.GetByTaskId(expectedTaskId)!.GoalId);
        Assert.Null(enqueuedTask);
        Assert.Null(taskQueue.TryDequeueAny());
        Assert.Null(pipeline.ActiveTaskId);
    }

    // ── DispatchToRole: idle worker direct dispatch ───────────────────────

    [Fact]
    public async Task DispatchToRole_WhenIdleWorkerAvailable_DispatchesDirectlyToWorker()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var workerPool = new WorkerPool();
        var idleWorker = workerPool.RegisterWorker("worker-1", []);
        var gateway = new GrpcWorkerGateway(workerPool);

        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue,
            workerGateway: gateway);

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // The idle worker should now be busy with the dispatched task
        Assert.True(idleWorker.IsBusy);
        Assert.NotNull(idleWorker.CurrentTaskId);
        Assert.Equal("coder-model", idleWorker.CurrentModel);
        Assert.Equal(WorkerRole.Coder, idleWorker.Role);

        // The task should have been removed from the pending queue (activated)
        Assert.Null(taskQueue.TryDequeueAny());
    }

    [Fact]
    public async Task DispatchToRole_WhenNoIdleWorker_EnqueuesButDoesNotDispatch()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        // Empty worker pool → no idle worker
        var workerPool = new WorkerPool();
        var gateway = new GrpcWorkerGateway(workerPool);

        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue,
            workerGateway: gateway);

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // Task should remain in the queue (no worker to dispatch to)
        var queuedTask = taskQueue.TryDequeueAny();
        Assert.NotNull(queuedTask);
    }

    // ── DispatchToRole: repository failure → MarkGoalFailedAsync ─────────

    [Fact]
    public async Task DispatchToRole_WhenRepositoryResolutionFails_CallsMarkGoalFailedAsync()
    {
        // Config with no repositories — the goal references a repo that doesn't exist.
        // A coder model IS configured so the Slice 3b refusal does not fire and the
        // repository-resolution failure path is exercised.
        var config = new HiveConfigFile
        {
            Repositories = [], // empty — no repos defined
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "coder-model" },
            },
        };

        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();

        // The goal references "test-repo" which is not in the config → ResolveRepositories throws
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        // Pass the actual goal to the GoalManager so MarkGoalFailedAsync can update it
        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue,
            goal: goal);

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        // Pipeline should be marked as Failed
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
    }

    // ── DispatchToRole: sub-agent model catalog ──────────────────────────

    [Fact]
    public async Task DispatchToRole_PopulatesCatalogFromAvailableModels()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a", ContextWindow = 200_000 },
                new ModelEntry { Name = "model-b", ContextWindow = null },
                new ModelEntry { Name = "model-c", ContextWindow = 128_000 },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal(3, capturedTask!.SubAgentModels.Count);

        Assert.Equal("model-a", capturedTask.SubAgentModels[0].Id);
        Assert.Equal(200_000, capturedTask.SubAgentModels[0].ContextWindow);
        Assert.Contains("K context", capturedTask.SubAgentModels[0].Description);

        Assert.Equal("model-b", capturedTask.SubAgentModels[1].Id);
        Assert.Null(capturedTask.SubAgentModels[1].ContextWindow);
        Assert.Equal("Configured model", capturedTask.SubAgentModels[1].Description);

        Assert.Equal("model-c", capturedTask.SubAgentModels[2].Id);
        Assert.Equal(128_000, capturedTask.SubAgentModels[2].ContextWindow);
        Assert.Contains("K context", capturedTask.SubAgentModels[2].Description);
    }

    [Fact]
    public async Task DispatchToRole_DescriptionContainsContextInfo_WhenContextWindowKnown()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a", ContextWindow = 200_000 },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Single(capturedTask!.SubAgentModels);
        Assert.Equal("Configured model, 200K context", capturedTask.SubAgentModels[0].Description);
    }

    [Fact]
    public async Task DispatchToRole_DescriptionIsConfiguredModel_WhenContextWindowNull()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-unknown" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Single(capturedTask!.SubAgentModels);
        Assert.Equal("Configured model", capturedTask.SubAgentModels[0].Description);
    }

    [Fact]
    public async Task DispatchToRole_ConfiguredDescription_FlowsToCatalog()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a", ContextWindow = 200_000, Description = "Deep reasoning workhorse" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("Deep reasoning workhorse", Assert.Single(capturedTask!.SubAgentModels).Description);
    }

    [Fact]
    public async Task DispatchToRole_BlankDescription_FallsBackToAutoGenerated()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a", ContextWindow = 200_000, Description = "   " },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Equal("Configured model, 200K context", Assert.Single(capturedTask!.SubAgentModels).Description);
    }

    [Fact]
    public async Task DispatchToRole_CuratedSubAgentModels_TakePrecedence()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "model-a" }],
            SubAgentModels = [new ModelEntry { Name = "curated-b", Description = "Curated pick" }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        var entry = Assert.Single(capturedTask!.SubAgentModels);
        Assert.Equal("curated-b", entry.Id);
        Assert.Equal("Curated pick", entry.Description);
    }

    [Fact]
    public async Task DispatchToRole_WhenModelsConfigNull_CatalogIsEmpty()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        // config.Models left null

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Empty(capturedTask!.SubAgentModels);
    }

    [Fact]
    public async Task DispatchToRole_WhenAvailableModelsNull_CatalogIsEmpty()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = null,
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Empty(capturedTask!.SubAgentModels);
    }

    [Fact]
    public async Task DispatchToRole_WhenAvailableModelsEmpty_CatalogIsEmpty()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Empty(capturedTask!.SubAgentModels);
    }

    [Fact]
    public async Task DispatchToRole_FiltersBlankModelNames()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "valid-model", ContextWindow = 100_000 },
                new ModelEntry { Name = "  " },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Single(capturedTask!.SubAgentModels);
        Assert.Equal("valid-model", capturedTask.SubAgentModels[0].Id);
    }

    [Fact]
    public async Task DispatchToRole_NoReasoningSuffixAppliedToCatalogIds()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "my-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "my-model", ReasoningEffort = "high" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Single(capturedTask!.SubAgentModels);
        // Model names are always plain — neither the dispatched model nor the catalog ID
        // carries a reasoning suffix.
        Assert.Equal("my-model", capturedTask.Model);
        Assert.Equal("my-model", capturedTask.SubAgentModels[0].Id);
    }

    // ── DispatchToRole: null prompt defaults to description ───────────────

    [Fact]
    public async Task DispatchToRole_WhenPromptIsNull_UsesDefaultPromptWithDescription()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, null, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.Contains(pipeline.Description, capturedTask!.Prompt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static HiveConfigFile CreateConfig()
    {
        var config = new HiveConfigFile();
        if (!config.Repositories.Any(r => r.Name == "test-repo"))
        {
            config.Repositories.Add(new RepositoryConfig
            {
                Name = "test-repo",
                Url = "https://example.com/test-repo.git",
                DefaultBranch = "develop",
            });
        }
        return config;
    }

    /// <summary>
    /// Installs an iteration plan carrying <paramref name="tier"/> on every worker phase and
    /// ALIGNS the state machine with the pipeline's current phase.
    /// </summary>
    /// <remarks>
    /// The dispatch now captures its work-slot position before building the task, and the capture
    /// refuses when the pipeline's phase and the machine's phase disagree. A fixture that only
    /// called <c>StartIteration</c> would leave the machine parked on the plan's FIRST phase while
    /// the pipeline had already advanced — an incoherent state no production path produces. The
    /// plan includes <see cref="GoalPhase.Improve"/> so the improver vectors have a real position.
    /// </remarks>
    private static void SetPlan(GoalPipeline pipeline, ModelTier tier)
    {
        var plan = IterationPlan.Default(includeImprove: true);
        // Set the requested tier on all worker phases
        foreach (var phase in plan.Phases)
        {
            if (phase is GoalPhase.Planning or GoalPhase.Merging or GoalPhase.Done or GoalPhase.Failed)
                continue;
            plan.PhaseTiers[phase] = tier;
        }
        pipeline.SetPlan(plan);
        pipeline.StateMachine.StartIteration(plan.Phases);
        // Align the machine with the pipeline's own phase (a no-op when it is the plan's first).
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, pipeline.Phase);
    }

    /// <summary>
    /// Creates a <see cref="TaskDispatchService"/> with the given config and optional overrides.
    /// Dependencies are constructed the same way <see cref="GoalDispatcher"/> does.
    /// </summary>
    private static TaskDispatchService CreateService(
        HiveConfigFile? config = null,
        GoalPipelineManager? pipelineManager = null,
        TaskQueue? taskQueue = null,
        IWorkerGateway? workerGateway = null,
        Goal? goal = null,
        bool useNullConfig = false)
    {
        // useNullConfig models the "hive-config.yaml was never loaded" case, where the service
        // receives a genuinely null config rather than a defaulted one.
        config = useNullConfig ? null : (config ?? CreateConfig());
        pipelineManager ??= new GoalPipelineManager();
        taskQueue ??= new TaskQueue();
        workerGateway ??= new GrpcWorkerGateway(new WorkerPool());

        var goalManager = new GoalManager();
        // Register the actual goal (if provided) so UpdateGoalStatusAsync can find it;
        // otherwise use a throwaway setup goal to populate the internal map.
        goalManager.AddSource(new DispatchTestGoalSource(
            goal ?? new Goal { Id = "setup-goal", Description = "Setup" }));
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var logger = NullLogger<TaskDispatchService>.Instance;

        // GoalLifecycleService — constructed the same way as GoalDispatcher
        var lifecycleService = new GoalLifecycleService(
            goalManager, logger);

        // DispatcherMaintenance — constructed the same way as GoalDispatcher
        var redispatchQueue = new ConcurrentQueue<string>();
        var maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway,
            brain: null,
            agentsManager: null,
            configRepo: null,
            redispatchQueue,
            logger,
            config: config);

        var taskBuilder = new TaskBuilder(new BranchCoordinator());

        return new TaskDispatchService(
            taskQueue, workerGateway, taskBuilder, config,
            logger, pipelineManager, lifecycleService, maintenance);
    }

    /// <summary>
    /// THE SUPPORTING-HELPER OVERLOAD (β-PREP-2): identical to the default
    /// <see cref="CreateService(HiveConfigFile?, GoalPipelineManager?, TaskQueue?, IWorkerGateway?, Goal?, bool)"/>
    /// wiring, but installs a CALLER-SUPPLIED <see cref="ILogger{TaskDispatchService}"/> — the
    /// seam the guarded-logging vectors need (a throwing logger whose predicate targets one
    /// guarded emission). Added per the supporting-helper exception; no existing test's
    /// assertions are affected.
    /// </summary>
    private static TaskDispatchService CreateService(
        HiveConfigFile? config,
        GoalPipelineManager pipelineManager,
        TaskQueue taskQueue,
        ILogger<TaskDispatchService> customLogger)
    {
        config ??= CreateConfig();
        var workerGateway = new GrpcWorkerGateway(new WorkerPool());

        var goalManager = new GoalManager();
        goalManager.AddSource(new DispatchTestGoalSource(
            new Goal { Id = "setup-goal", Description = "Setup" }));
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var lifecycleService = new GoalLifecycleService(goalManager, customLogger);

        var redispatchQueue = new ConcurrentQueue<string>();
        var maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway,
            brain: null,
            agentsManager: null,
            configRepo: null,
            redispatchQueue,
            customLogger,
            config: config);

        var taskBuilder = new TaskBuilder(new BranchCoordinator());

        return new TaskDispatchService(
            taskQueue, workerGateway, taskBuilder, config,
            customLogger, pipelineManager, lifecycleService, maintenance);
    }

    /// <summary>
    /// Creates a <see cref="TaskDispatchService"/> and a <see cref="GoalPipeline"/> ready for
    /// DispatchToRole testing at the given phase with the given model tier.
    /// </summary>
    private static (TaskDispatchService service, GoalPipeline pipeline, TaskQueue taskQueue)
        CreateServiceWithPipeline(GoalPhase phase, HiveConfigFile? config, ModelTier tier)
    {
        var (service, pipeline, taskQueue, _) = CreateServiceWithPipelineAndManager(phase, config, tier);
        return (service, pipeline, taskQueue);
    }

    /// <summary>
    /// As <see cref="CreateServiceWithPipeline"/>, but also hands back the
    /// <see cref="GoalPipelineManager"/> so a test can arrange or inspect task → goal mappings
    /// (the admission surface).
    /// </summary>
    private static (TaskDispatchService service, GoalPipeline pipeline, TaskQueue taskQueue, GoalPipelineManager pipelineManager)
        CreateServiceWithPipelineAndManager(GoalPhase phase, HiveConfigFile? config, ModelTier tier)
    {
        var useNullConfig = config is null;
        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(phase);
        SetPlan(pipeline, tier);

        var service = CreateService(
            config: config,
            pipelineManager: pipelineManager,
            taskQueue: taskQueue,
            goal: goal,
            useNullConfig: useNullConfig);

        return (service, pipeline, taskQueue, pipelineManager);
    }

    [Fact]
    public async Task DispatchToRole_CuratedEntryWithMatchingAvailable_MergesContextWindow()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "ollama-cloud/glm-5.2", ContextWindow = 976000 },
            ],
            SubAgentModels =
            [
                new ModelEntry { Name = "ollama-cloud/glm-5.2" },
            ],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        var entry = Assert.Single(capturedTask!.SubAgentModels);
        Assert.Equal("ollama-cloud/glm-5.2", entry.Id);
        // ContextWindow inherited from the matching available_models entry
        Assert.Equal(976000, entry.ContextWindow);
        // Auto-description is generated from the merged ContextWindow
        Assert.Contains("976K context", entry.Description);
    }

    // ── SupportsVision boundary (null → false resolution) ────────────────────

    [Fact]
    public async Task DispatchToRole_SupportsVisionTrue_FlowsToDtoAsTrue()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "model-a", ContextWindow = 200_000, SupportsVision = true }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(Assert.Single(capturedTask!.SubAgentModels).SupportsVision);
    }

    [Fact]
    public async Task DispatchToRole_SupportsVisionFalse_FlowsToDtoAsFalse()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "model-a", ContextWindow = 200_000, SupportsVision = false }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(Assert.Single(capturedTask!.SubAgentModels).SupportsVision);
    }

    [Fact]
    public async Task DispatchToRole_SupportsVisionNull_ResolvesToFalseInDto()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "model-a", ContextWindow = 200_000, SupportsVision = null }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        // null merged ModelEntry.SupportsVision must resolve to false at the DTO boundary
        Assert.False(Assert.Single(capturedTask!.SubAgentModels).SupportsVision);
    }

    [Fact]
    public async Task DispatchToRole_CuratedSupportsVisionTrueOverridesAvailableFalse()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "m", ContextWindow = 200_000, SupportsVision = false }],
            SubAgentModels = [new ModelEntry { Name = "m", SupportsVision = true }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(Assert.Single(capturedTask!.SubAgentModels).SupportsVision);
    }

    [Fact]
    public async Task DispatchToRole_CuratedUnsetInheritsAvailableTrue()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "m", ContextWindow = 200_000, SupportsVision = true }],
            SubAgentModels = [new ModelEntry { Name = "m", SupportsVision = null }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(Assert.Single(capturedTask!.SubAgentModels).SupportsVision);
    }

    [Fact]
    public async Task DispatchToRole_CuratedUnsetAndAvailableUnset_ResolvesToFalseInDto()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        config.Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "m", ContextWindow = 200_000, SupportsVision = null }],
            SubAgentModels = [new ModelEntry { Name = "m", SupportsVision = null }],
        };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        // The merge preserves null, but the DTO boundary resolves it to false
        Assert.False(Assert.Single(capturedTask!.SubAgentModels).SupportsVision);
    }

    // ── β-PREP-2: the guarded admission-rollback logging helpers ─────────────

    /// <summary>
    /// THE FORCED REFUSAL VECTOR: the mapping is owned by another goal, so
    /// <see cref="GoalPipelineManager.PersistAdmission"/> refuses with MemoryConflict — the
    /// R1–R5 rollback path runs
    /// (slot abandoned, <c>abandoned-registration</c> logged, the ORIGINAL
    /// <see cref="InvalidOperationException"/> thrown). The predicate logger throws exactly at
    /// the abandoned-registration template, and the throw is SWALLOWED inside
    /// <c>TaskDispatchService.LogSafely</c>: the rollback COMPLETED (the slot abandoned,
    /// the pointer never claimed), the logger's exception appears NOWHERE, and what leaves the
    /// dispatch is the ORIGINAL refusal — the two-exception distinction.
    /// </summary>
    [Fact]
    public async Task Dispatch_AbandonedRegistrationLogThrows_RollbackCompletesAndOriginalRethrows()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        var expectedTaskId = $"{goal.Id}-coder-001-01-001";
        var otherGoal = new Goal { Id = $"goal-other-{Guid.NewGuid():N}", Description = "Other" };
        pipelineManager.CreatePipeline(otherGoal);
        pipelineManager.RegisterTask(expectedTaskId, otherGoal.Id);

        // THE SEAM: throws ONLY at the abandoned-registration template — the guarded helper under
        // test. Every other emission (none occurs on this vector's refusal path) must succeed.
        var logger = new DispatchPredicateThrowingLogger(
            m => m.Contains("abandoned-registration", StringComparison.Ordinal));
        var service = CreateService(
            config, pipelineManager, taskQueue, logger);

        WorkTask? enqueuedTask = null;
        taskQueue.OnEnqueue = t => enqueuedTask = t;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE TWO-EXCEPTION DISTINCTION: the ORIGINAL refusal (its exact message and null inner)
        // left the dispatch — the logger's sentinel never did.
        Assert.Equal(
            $"Task mapping registration failed for {expectedTaskId} (goal {goal.Id}) — the mapping is occupied or the persistence failed",
            thrown.Message);
        Assert.Null(thrown.InnerException);

        // THE GUARD ACTUALLY FIRED (the swallow is otherwise unprovable).
        Assert.True(logger.ThrewAtLeastOnce, "the abandoned-registration site's logger must actually have thrown");
        Assert.Contains(
            logger.SeenMessages,
            m => m.Contains("abandoned-registration", StringComparison.Ordinal));

        // THE ROLLBACK COMPLETED: the slot is abandoned and the pointer was never claimed.
        Assert.Equal(WorkSlotState.Abandoned, pipeline.GetSlotsForTest().Single().State);
        Assert.Null(pipeline.ActiveTaskId);
        // The competing mapping is intact and nothing was enqueued.
        Assert.Equal(otherGoal.Id, pipelineManager.GetByTaskId(expectedTaskId)!.GoalId);
        Assert.Null(enqueuedTask);
        Assert.Null(taskQueue.TryDequeueAny());
    }

    /// <summary>
    /// THE (true, false) VECTOR ON THE EXISTING BRANCH: the enqueue-throw vector PLUS an
    /// interceptor-forced store failure at the mapping-row delete. The REAL
    /// <c>(true, false)</c> unregister result drives the EXISTING
    /// <c>LogRollbackFailure("unregister-persist")</c> branch (NO new production branch), the
    /// predicate logger throws at the rollback-failure template — and the throw is SWALLOWED
    /// inside <c>TaskDispatchService.LogSafely</c>: the remaining rollback steps still run
    /// (the pointer cleared, the abandoned-registration line recorded) and the ORIGINAL enqueue
    /// sentinel is what leaves the dispatch.
    /// </summary>
    [Fact]
    public async Task Dispatch_RollbackFailureLogThrows_RollbackContinuesAndOriginalRethrows()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };

        var deleteSentinel = new InvalidOperationException("delete-sentinel");
        using var storeFixture = new SqliteMappingFixture();
        var pipelineManager = new GoalPipelineManager(
            storeFixture.CreateStore(new SentinelThrowingInterceptor(deleteSentinel, "DELETE")));

        var taskQueue = new TaskQueue();
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var pipeline = pipelineManager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        SetPlan(pipeline, ModelTier.Default);

        var enqueueSentinel = new InvalidOperationException("enqueue-sentinel");
        taskQueue.OnEnqueue = _ => throw enqueueSentinel;

        // THE SEAM: throws ONLY at the rollback-failure template — the guarded helper under test.
        // The dispatch-owned unregister-result DEBUG record (a different template) must SUCCEED.
        var logger = new DispatchPredicateThrowingLogger(
            m => m.Contains("rollback-failure", StringComparison.Ordinal));
        var service = CreateService(config, pipelineManager, taskQueue, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken));

        // THE TWO-EXCEPTION DISTINCTION: the ORIGINAL enqueue sentinel left the dispatch.
        Assert.Same(enqueueSentinel, thrown);

        // THE EXISTING BRANCH REALLY FIRED: the store's delete was forced to fail, so the
        // unregister returned (true, false) — the DEBUG record shows it…
        var expectedTaskId = $"{goal.Id}-coder-001-01-001";
        Assert.Contains(
            logger.SeenMessages,
            m => m == $"WorkSlotIntegrity: unregister goal={goal.Id} task={expectedTaskId} memoryRemoved=True persistenceRemoved=False");
        // …and the rollback-failure template was reached (and its logger threw).
        Assert.True(logger.ThrewAtLeastOnce, "the rollback-failure site's logger must actually have thrown");
        Assert.Contains(
            logger.SeenMessages,
            m => m.Contains("rollback-failure", StringComparison.Ordinal) &&
                 m.Contains("unregister-persist", StringComparison.Ordinal));

        // THE REMAINING ROLLBACK STEPS STILL RAN: the slot is abandoned and the pointer cleared.
        Assert.Equal(WorkSlotState.Abandoned, pipeline.GetSlotsForTest().Single().State);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Contains(
            logger.SeenMessages,
            m => m.Contains("abandoned-registration", StringComparison.Ordinal));

        // THE MEMORY SIDE OF THE ROLLBACK HELD; the persisted row is the honest residue.
        Assert.Null(pipelineManager.GetByTaskId(expectedTaskId));
        Assert.NotNull(storeFixture.ReadPersistedGoalId(expectedTaskId));
    }

    /// <summary>
    /// β-PREP-2 vector logger: throws ONLY when the formatted message matches the predicate —
    /// the targeted seam proving that ONE guarded emission swallows the logger's exception while
    /// the rollback continues. Used ONLY by this slice's two guarded-helper vectors.
    /// </summary>
    private sealed class DispatchPredicateThrowingLogger : ILogger<TaskDispatchService>
    {
        private readonly Func<string, bool> _shouldThrow;

        public DispatchPredicateThrowingLogger(Func<string, bool> shouldThrow) => _shouldThrow = shouldThrow;

        /// <summary>True once the throwing branch has actually been taken.</summary>
        public bool ThrewAtLeastOnce { get; private set; }

        /// <summary>Every message the logger was asked to emit, throwing ones included.</summary>
        public List<string> SeenMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            SeenMessages.Add(message);
            if (!_shouldThrow(message))
                return;

            ThrewAtLeastOnce = true;
            throw new InvalidOperationException("dispatch-logger-sentinel");
        }
    }

    /// <summary>
    /// THE SQLITE-INTERCEPTION FIXTURE (the supporting-helper exception): a per-instance
    /// shared-cache in-memory SQLite database anchored by a keeper connection, so a
    /// <see cref="PipelineStore"/>-backed <see cref="GoalPipelineManager"/> can be exercised here
    /// (the store-failure vector needs a REAL conditional row delete, forced to throw by an EF
    /// interceptor). No test uses Task.Delay; the throw is injected synchronously.
    /// </summary>
    private sealed class SqliteMappingFixture : IDisposable
    {
        private readonly string _connectionString =
            $"Data Source=file:memdb-dispatchsvctests-{Guid.NewGuid():N}?mode=memory&cache=shared";

        private readonly SqliteConnection _keeper;

        public SqliteMappingFixture()
        {
            _keeper = new SqliteConnection(_connectionString);
            _keeper.Open();
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }

        /// <summary>Creates a PipelineStore over its own connection to the shared database.</summary>
        public PipelineStore CreateStore(IInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(CreateConnection());
            if (interceptor is not null)
                builder.AddInterceptors(interceptor);

            return new PipelineStore(new CopilotHiveDbContext(builder.Options), NullLogger<PipelineStore>.Instance);
        }

        /// <summary>Reads the persisted goal id for a task RAW — no EF Core, no change tracker.</summary>
        public string? ReadPersistedGoalId(string taskId)
        {
            using var command = _keeper.CreateCommand();
            command.CommandText = "SELECT goal_id FROM task_mappings WHERE task_id = $taskId";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$taskId";
            parameter.Value = taskId;
            command.Parameters.Add(parameter);
            return command.ExecuteScalar() as string;
        }

        private SqliteConnection CreateConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private CopilotHiveDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(CreateConnection()).Options);

        public void Dispose() => _keeper.Dispose();
    }

    /// <summary>
    /// Minimal <see cref="IGoalSource"/> that returns a single pre-configured goal.
    /// </summary>
    private sealed class DispatchTestGoalSource : IGoalSource
    {
        private readonly Goal _goal;
        public DispatchTestGoalSource(Goal goal) => _goal = goal;
        public string Name => "dispatch-test-fake";
        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([_goal]);
        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
/// <summary>
/// Minimal Brain for the sequential-phase handoff test: it supplies the default plan and a
/// phase-labelled prompt so <c>PipelineDriver</c> advances Coding → Testing and re-dispatches.
/// </summary>
file sealed class SequentialPhaseBrain : IDistributedBrain
{
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PlanResult.Success(IterationPlan.Default()));

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>("message");

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}
