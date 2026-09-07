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
//  with ZERO register calls — it is dispositioned separately as entry 6, not as a grep match.
//
//  CATEGORY TOTALS (corrected in β′; they must add up): the 18 grep-match files are
//  RED-FIXED (5, entries 1-5) + SEMANTIC-FIXED (2, entries 7-8) + UNAFFECTED (11, entries 9-19)
//  = 18. Entry 6 is the additional NON-grep verification target, giving 19 dispositioned files.
//
//  ITERATION-4 RE-VERIFICATION: every entry below was re-checked against the current tree after
//  iteration 3. Iteration 3 changed the caller surface only in comment / strengthened-assertion
//  form and added NO new register callers, so all dispositions carry forward unchanged. The one
//  iteration-4 edit (the unfiltered store observer in Dispatch_ExistingActiveTask_
//  ClaimRefusedNoMutation) is a test-observation change and adds no register caller.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  RED-FIXED (5) — failed under the new semantics and were migrated
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
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  ADDITIONAL VERIFICATION TARGET (1) — NOT a grep match, NOT migrated by this goal
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  6. PipelineStoreEfCoreIntegrationTests.cs — UNCHANGED / VERIFIED READ-ONLY. (Reclassified in
//     β′: the original record labelled this RED-FIXED, but that was a bookkeeping error — its own
//     rationale, and the 4a85897 merge diff, show the file was NOT touched by the
//     admission-atomic-switch. Its PREP-3 immutable-snapshot coverage was CONSUMED UNCHANGED.)
//     ZERO register calls, so it is not one of the 18 grep matches. Its admission assertions pin
//     the pointer-snapshot behaviour this goal consumes: line ~702 asserts pipelines WHERE
//     goal_id='goal-commit' AND active_task_id='task-commit' after SaveAdmissionWithPointer;
//     line ~897 seeds a pipelines row directly. No register-durability or overwrite assumption is
//     present anywhere in the file. Verified green.
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
//     ⚠ COUNT CORRECTED IN β′ (C): "18 sites" is a MISCOUNT in this historical entry — the true
//     figure is 17 executable invocations, at 4a85897 AND at the current baseline alike. The
//     caller surface did not change; only the record was wrong. The per-site findings above
//     (the L1603/L1604 distinct pair, the L1653 phase-only store assertion, the seeded L4210
//     resume-stale vector) were re-verified and STAND. See β′ (C) for the line mapping.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
//  UNAFFECTED (11) — audited call-site by call-site, no change needed
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
//      ⚠ CORRECTED IN β′ (F): "all storeless managers" is FALSE — the
//      Reclaim_PersistenceRemovalDoesNotConfirm vector uses a STORE-BACKED manager, and it
//      carries the audit's one outstanding finding (tracking issue
//      cleanup-persistence-removal-fixture-passes-without-exercising-a-persisted-mapping).
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
//
// ═══════════════════════════════════════════════════════════════════════════════════════════
//  β′ ADDENDUM — INDEPENDENT RE-VERIFICATION OF THE ABOVE TABLE
//  ⚠ OUTSTANDING FINDING (one; see (F) below). Reported, NOT repaired — repair is a separate goal.
// ═══════════════════════════════════════════════════════════════════════════════════════════
//
//  (A) BASELINE. Inspected commit: aa23c674f59c2c293ca6d8f909e5d95a54480357
//      ("guard-transport-completion-pipeline-mutations"). Everything ABOVE this addendum is a
//      HISTORICAL record whose baseline is 4a85897 (admission-atomic-switch); line numbers and
//      per-file dispositions in the table are stated AS OF 4a85897 and are not silently rewritten
//      to the current tree — the deltas are recorded here instead.
//      ANCESTRY/CAPABILITY CHECK (not a "latest N commits" or exact-HEAD gate): 4a85897, 503abdb
//      (admission-snapshot-plumbing, the prerequisite) and aa23c67 are all ancestors of the
//      inspected commit (`git merge-base --is-ancestor` → true for each). The corresponding
//      CAPABILITIES are present in the baseline: transport's pre-admission pipeline write is gone
//      (HiveOrchestratorService's ClearActiveTask/WorkerOutput block replaced by the
//      mutation-ownership comment) and the admitted no-brain output copy exists in
//      TaskCompletionService. So the two preceding fixes are in the baseline by ancestry AND by
//      observable behaviour, regardless of where HEAD later moves.
//
//  (B) CALLER ENUMERATION AT THIS BASELINE (counted now, NOT asserted to equal the historical 18).
//      `grep -rn "RegisterTask\|TryRegisterTask" tests/` → 117 raw hits in 18 files. Separating
//      EXECUTABLE invocations from non-invocations:
//        • Executable `…RegisterTask(` / `…TryRegisterTask(` call sites, by file:
//          WorkSlotMappingOwnershipTests 30 (incl. the TryRegisterTask vectors), GoalDispatcher-
//          Tests 17, GoalPipelineTests 5, TaskDispatchServiceTests 4 (self), WorkSlotDispatch-
//          WiringTests 4, StaleWorkerCleanupServiceTests 4, HonestPlanningWindowTests 3,
//          GoalDispatcherCancelTests 3, PipelineLifecycleIntegrationTests 3, DashboardNotifier-
//          WiringTests 2, PlanRejectContractTests 2, PremiumModelSelectionTests 2,
//          PipelineStoreTests 2, PipelineDriverTests 1, HiveOrchestratorIssueToolTests 1,
//          HiveOrchestratorNarrativeToolTests 1, Services/EventBusProducerTests 1,
//          StaleWorkerCleanupServiceIntegrationTests 1.
//        • NON-invocations excluded: `<see cref="GoalPipelineManager.TryRegisterTask"/>` doc refs
//          and `<c>RegisterTask</c>` prose, `#region RegisterTask / GetByTaskId`, TEST-METHOD
//          DECLARATIONS whose names begin with RegisterTask_/TryRegisterTask_ (they are
//          declarations, and their bodies' calls are counted separately), and the quoted audit
//          text in THIS header. A multiline-formatting probe (`RegisterTask\s*\($`) found ZERO
//          split call sites — every invocation is single-line. Unrelated similarly named methods
//          (RegisterWorker, TryUnregisterTask, RegisterPipeline*, SaveTaskMapping) are excluded.
//        • HELPER-MEDIATED sites are single call sites reached from many tests and are counted
//          ONCE each, with their fan-out read: HiveOrchestratorIssueToolTests.CreatePipelineForTask
//          (L58; 6 callers, all literal "task-1" but each on a FRESH manager from CreateService),
//          Services/EventBusProducerTests.CreatePipelineForTool (L810; 1 caller),
//          HiveOrchestratorNarrativeToolTests (L53), HonestPlanningWindowTests.CreateHarness
//          (L1073), StaleWorkerCleanupServiceTests.ArrangeAdmission (L616; 6 callers, each with
//          its own manager), GoalDispatcherTests.SlotGuardFixture (L1748; ~20 callers, each
//          building its own manager and a GUID task id).
//      The file COUNT is unchanged at 18, but that is an observation, not an acceptance condition.
//
//  (C) DELTAS vs. THE 4a85897 TABLE.
//      • NO new register-caller FILE and NO removed one, and NO test file was ADDED between
//        4a85897 and this baseline: `git diff --name-status 4a85897..aa23c67` reports exactly
//        four paths, ALL status M (two src/, DashboardNotifierWorkerWiringTests.cs,
//        GoalDispatcherTests.cs) — no A and no D entries. `git diff 4a85897..HEAD -- tests/`
//        shows zero added/removed lines containing RegisterTask.
//      • GoalDispatcherTests.cs: 17 call sites, and 17 is ALSO the true count at 4a85897 — the
//        file's caller surface did NOT change. HISTORICAL BOOKKEEPING CORRECTION: entry 8 above
//        records "18 sites"; that entry MISCOUNTED. Direct inspection of both commits
//        (`git show <rev>:…/GoalDispatcherTests.cs | grep -E '(RegisterTask|TryRegisterTask)\('`)
//        yields exactly 17 executable invocations at 4a85897 AND at aa23c67. This is a correction
//        to the historical RECORD, not a caller being added or removed — no register call site
//        was introduced, deleted or migrated between the two commits.
//        LINE SHIFT ONLY: SIX sites moved, each by exactly +354 — GoalDispatcherTests.cs's OWN
//        insertions (`git diff --numstat 4a85897..aa23c67` → 354/0 for this file). The 674/0 in
//        the same commit belongs to DashboardNotifierWorkerWiringTests.cs and shifts nothing
//        here. Mapped by CONTAINING-METHOD IDENTITY resolved from the source via brace matching
//        at BOTH baselines (not by the nearest preceding `public async Task` header, which
//        misattributes helper-mediated sites), the historical → current pairs are:
//          L2101 → L2455  GoalDispatcherPushFailureLoggingTests.
//                         HandleTaskCompletionAsync_LogsWarning_WhenFilesChangedButNotPushed
//          L2153 → L2507  GoalDispatcherPushFailureLoggingTests.
//                         HandleTaskCompletionAsync_NoWarning_WhenPushedSuccessfully
//          L2203 → L2557  GoalDispatcherPushFailureLoggingTests.RunPushFailureAsync  [HELPER]
//          L2911 → L3265  GoalDispatcherIterationShaTests.CreateDispatcherInCodingPhase [HELPER]
//          L3265 → L3619  GoalDispatcherDocWritingPhaseTests.
//                         DocWritingPhase_AfterTestingSucceeds_CallsBrainCraftPromptWithDocWritingPhase
//          L4210 → L4564  GoalDispatcherResumeTests.ResumeGoalAsync_StaleTaskMapping_RemovedFromStore
//        (Two corrections to an earlier draft of this addendum, retracted here rather than
//        silently overwritten: it mapped historical L2911 to L3619 — wrong, L2911 lands on L3265
//        and the historical L3265 is what lands on L3619; and it labelled the two [HELPER] rows
//        with the nearest preceding [Fact] name — L2203/L2557 as …_NoWarning_WhenPushedSuccessfully
//        and L2911/L3265 as GetGoalsByRelease_ReturnsCorrectGoals. Both are wrong: those two
//        registrations are HELPER-MEDIATED, sitting inside RunPushFailureAsync and
//        CreateDispatcherInCodingPhase respectively, each reached from several [Fact] methods that
//        get a FRESH manager per call. That distinction is material to a caller audit, so the
//        labels now name the actual containing member. It also said "moved five"; six moved.)
//        The other ELEVEN sites (L118, L1202, L1287, L1329, L1380, L1504, L1556, L1603, L1604,
//        L1653, L1748) are unmoved — 6 + 11 = 17, the full count at both baselines.
//        FIVE of the six shifted sites use fresh `task-{Guid}` ids on storeless managers; the
//        SIXTH is the store-backed resume-stale vector (now L4564), whose id is the literal
//        "stale-task-1" and which still seeds durably via store.SaveTaskMapping (L4567) before
//        asserting the mapping is gone — verified NON-vacuous.
//      • EXISTING FILE EXTENDED since 4a85897: DashboardNotifierWorkerWiringTests.cs. (Corrected
//        in this round: an earlier draft of this addendum called it a NEW file added by aa23c67.
//        That was WRONG and is retracted — no file addition ever occurred. The file already
//        existed at 4a85897 with 1,124 lines; aa23c67 MODIFIED it (`--name-status` M), adding 674
//        lines and deleting none (`--numstat` → 674/0).) Its DISPOSITION is unchanged and still
//        holds for the extended file: NOT a register caller — it contains ZERO RegisterTask/
//        TryRegisterTask invocations, before or after the extension. Its RealTransportHarness
//        drives the REAL dispatch service, so every mapping it relies on is written by the
//        PRODUCTION admission path; its `PipelineManager.GetByTaskId(taskA)` retention assertion
//        (L1144) therefore reads an admission-written mapping, not a test-registered one, and is
//        unaffected by all three breaking changes. Its manager is storeless. No audit action
//        required.
//      • Iteration-4 dispositions 9–19 were re-read call site by call site at this baseline and
//        are CORROBORATED unchanged, with one correction in (F).
//
//  (D) RE-VERIFICATION METHOD APPLIED TO EVERY CALLER (helpers read, not just grepped):
//      task-ID expressions, same-manager reuse, store-backedness, blank/null inputs, and whether
//      any assertion depends on overwrite behaviour or on an IMPLICIT durable mapping write.
//      RESULTS: (1) OVERWRITE→REFUSAL is observable nowhere accidentally. The only same-manager
//      task-id pairs are GoalDispatcherTests L1603/L1604 ("task-old-phase" vs "task-current-phase",
//      DISTINCT), StaleWorkerCleanupServiceTests L871 vs ArrangeAdmission's L616 (attempt-stamped
//      …-01-002 vs …-01-001, DISTINCT), HonestPlanningWindowTests L811/L840 vs the harness GUID
//      (DISTINCT), and the INTENTIONAL duplicate vectors in WorkSlotMappingOwnershipTests
//      (RegisterTask_RefusesDuplicateAndIsMemoryOnly L812/L819 and
//      TryRegisterTask_InMemoryDuplicate_RefusesWithoutExecutingSql L521/L525) plus the
//      unregister-then-register steals in WorkSlotDispatchWiringTests (L766, L885) — all of which
//      PIN the new semantics deliberately rather than depending on the old ones.
//      (3) BLANK/NULL inputs occur ONLY in the intentional validation Theories
//      (WorkSlotMappingOwnershipTests L451/L479/L855/L879), which assert the ArgumentException
//      and the taskId-before-goalId ParamName order. No other caller passes blank/null.
//      (2) DURABLE→MEMORY-ONLY: every store-backed caller whose assertion needs a durable row was
//      re-read to confirm the row is SEEDED and its existence ESTABLISHED before the act —
//      GoalPipelineTests L999/L1109 (both seed via store.SaveTaskMapping and assert
//      NotEmpty/Contains first), GoalDispatcherTests L4567, PipelineLifecycleIntegrationTests
//      L77-79, PipelineStoreTests (the memory-only claim and the durable
//      SaveTaskMapping_PersistsMappingLoadedByLoadActivePipelines are now two separate tests),
//      WorkSlotMappingOwnershipTests (SeedPersistedMapping / SeedPersistedMappingRaw at every
//      durable fixture), WorkSlotDispatchWiringTests (SeedPersistedMapping at L523). Store-backed
//      managers whose probe hits are pipeline-row/PHASE checks rather than mapping checks are
//      confirmed benign: PlanRejectContractTests L699/L752 (LoadPipeline → Phase, and neither
//      manager registers), GoalDispatcherTests L1653 (LoadPipeline → Phase),
//      GoalDispatcherCancelTests L1199-region (store-backed manager that never registers).
//
//  (E) TARGETED RE-RUN AT THIS BASELINE: StaleWorkerCleanupServiceTests → 24 passed, 0 failed.
//      Read-only verification of PipelineStoreEfCoreIntegrationTests.cs is RETAINED as entry 6:
//      it is untouched by 4a85897 and by this audit, has zero register calls, and its
//      SaveAdmissionWithPointer pointer-snapshot assertions remain valid and unrelied-upon by the
//      register contract.
//
//  (F) OUTSTANDING FINDING — one accidental old-semantic dependency, REPORTED NOT REPAIRED.
//      TRACKING ISSUE (durable reference, raised in the review round):
//        cleanup-persistence-removal-fixture-passes-without-exercising-a-persisted-mapping
//      Repair is owned by that issue and is a SEPARATE goal; this audit's obligation is the
//      accurate report recorded here.
//      FILE/TEST: StaleWorkerCleanupServiceTests.cs →
//      Reclaim_PersistenceRemovalDoesNotConfirm_ExactWarningAndContinuation (~L508).
//      CORRECTION TO THE TABLE: entry 19 states StaleWorkerCleanupServiceTests uses "all storeless
//      managers". That is FALSE at both baselines — this vector's manager is STORE-BACKED
//      (CreateStoreForCleanup, a DELETE-throwing interceptor). The audit's store-backedness step
//      was mis-recorded for this file.
//      OFFENDING ASSUMPTION: the fixture registers ONLY through ArrangeAdmission's memory-only
//      RegisterTask, so NO task_mappings row is ever written; the persistenceRemoved=False
//      outcome it asserts is therefore produced by the interceptor's throw over an EMPTY table —
//      the same False would arise from a 0-row delete. The test's stated intent ("an interceptor
//      forces the DELETE … to throw") is not what the assertion discriminates.
//      EVIDENCE (mutation, run at this baseline): removing the interceptor from
//      CreateStoreForCleanup leaves the test GREEN (1 passed) — a clean mutant it fails to kill.
//      PROPOSED SMALLEST FIX (validated, then reverted; NOT applied here): hoist the store into a
//      local and seed the durable row after ArrangeAdmission —
//        var expStore = CreateStoreForCleanup(new InvalidOperationException("delete-sentinel"));
//        var manager  = new GoalPipelineManager(expStore, new TestLogger<GoalPipelineManager>());
//        …
//        expStore.SaveTaskMapping(taskId, "goal-d2-unconfirmed");
//      With this seed the interceptor-removed mutant FAILS (the warning is absent because the
//      delete now succeeds → persistenceRemoved=True) and the unmutated test stays GREEN.
//      SCOPE NOTE: this is a defect in an existing test's arrangement, not in production code, and
//      it is NOT weakened or fixed by this audit goal — see the tracking issue named above.
// ═══════════════════════════════════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Tests.Worker;
using CopilotHive.Worker;
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

    // ── DispatchToRole: compaction model raw-value spectrum ────────────────

    /// <summary>
    /// Verifies that an EMPTY-STRING compaction model is omitted from task metadata:
    /// the dispatch gating (<c>IsNullOrEmpty</c>) treats empty exactly like null. The
    /// value is installed via <see cref="HiveConfigFile.SetCompactionModel"/> so it flows
    /// through the synchronized accessor the production read now uses.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenCompactionModelEmptyString_DoesNotSetCompactionMetadata()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.SetCompactionModel(string.Empty);

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("compaction_model"));
        Assert.False(capturedTask.Metadata.ContainsKey("compaction_max_tokens"));
    }

    /// <summary>
    /// Verifies that a WHITESPACE-ONLY compaction model is treated as nonempty: it is
    /// propagated verbatim into <c>compaction_model</c> with no trimming, and (with no
    /// matching context-window entry) no <c>compaction_max_tokens</c> is emitted — the
    /// positive-only rule.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenCompactionModelWhitespace_TreatsAsNonempty()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };
        config.Models = new ModelsConfig
        {
            CompactionModel = "   ",
            AvailableModels =
            [
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
        Assert.Equal("   ", capturedTask.Metadata["compaction_model"]);
        Assert.False(capturedTask.Metadata.ContainsKey("compaction_max_tokens"));
    }

    /// <summary>
    /// Verifies that a configured compaction model set AFTER service construction — via the
    /// synchronized <see cref="HiveConfigFile.SetCompactionModel"/> writer — is observed by
    /// the dispatch path through <see cref="HiveConfigFile.GetCompactionModel"/> and flows
    /// into the task metadata of a subsequent dispatch.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenCompactionModelSetAfterConstruction_PropagatesConfiguredValue()
    {
        var config = CreateConfig();
        config.Workers["coder"] = new WorkerConfig { Model = "standard-coder-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Coding, config, ModelTier.Default);

        // Set AFTER the service was constructed with this config instance.
        config.SetCompactionModel("gpt-mini-late");

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Work on it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(capturedTask!.Metadata.ContainsKey("compaction_model"));
        Assert.Equal("gpt-mini-late", capturedTask.Metadata["compaction_model"]);
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

    // ── Tester-report handoff through the REAL completion chain ───────────

    /// <summary>
    /// THE FULL CHAIN under the fix: an admitted tester task under a valid plan completes
    /// through the REAL <see cref="TaskCompletionService"/> → <see cref="PipelineDriver"/>, the
    /// driver advances Testing → Review, and the reviewer <see cref="WorkTask"/> captured from
    /// the queue carries the ENTIRE tester report in its tester_report metadata. The expected
    /// report is never constructed independently of the chain — it is the same string fed in as
    /// the tester's Metrics.Summary, and both the completed Testing entry's WorkerOutput and the
    /// reviewer task's metadata are asserted to equal it EXACTLY (beyond the legacy 4,000-char
    /// truncation that previously clipped mutation evidence).
    /// </summary>
    [Fact]
    public async Task TestingCompletion_FullReport_SurvivesToReviewerTaskMetadata()
    {
        var config = CreateConfig();
        config.Workers["tester"] = new WorkerConfig { Model = "tester-model" };
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue, pipelineManager) = CreateServiceWithPipelineAndManager(
            GoalPhase.Testing, config, ModelTier.Default);

        var report = """
            ## Test Report — iteration 1
            Build: success. Tests: 636 passed, 0 failed. Coverage: 78%.

            """;
        while (report.Length < 4_100)
            report += "Filler analysis line with stable content for realistic report shape.\n";
        report += "MUTATION-KILL-EVIDENCE-BEYOND-4000: mutant truncation removed → suite red.\n";
        while (report.Length < 7_800)
            report += "Further section detail — metrics table rows and issue enumeration.\n";
        report += "TRAILING-EVIDENCE-AT-END: all 636 tests green.";

        // The tester task must be admitted so the completion path accepts the result.
        var testerTaskId = $"{pipeline.GoalId}-tester-001-01-001";
        pipeline.CoderBranch = "feature/test-branch"; // seed so the tester task has BranchInfo

        var dispatched = new List<WorkTask>();
        taskQueue.OnEnqueue = t => dispatched.Add(t);

        await service.DispatchToRole(pipeline, WorkerRole.Tester, "Test it", TestContext.Current.CancellationToken);
        var dispatchedTesterId = Assert.Single(dispatched).TaskId;
        Assert.Equal(testerTaskId, dispatchedTesterId);

        // The current Testing phase entry (created by the real dispatch path, or seeded here when
        // the fixture dispatched manually): DriveNextPhaseAsync writes the WorkerOutput onto the
        // CURRENT entry, so it must exist for iteration 1 before the completion runs.
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            StartedAt = DateTime.UtcNow,
        });

        // THE COMPLETION — the REAL TaskCompletionService, whose driver advances Testing → Review
        // and re-enters the REAL DispatchToRole for the reviewer phase.
        var completionService = CreateCompletionService(pipelineManager, service, config, pipeline.Goal);
        await completionService.HandleTaskCompletionAsync(
            new TaskResult
            {
                TaskId = dispatchedTesterId,
                Status = TaskOutcome.Completed,
                Output = "PASS",
                Metrics = new TaskMetrics { Verdict = "PASS", Summary = report },
            },
            TestContext.Current.CancellationToken);

        // Phase advanced to Review with the Testing entry carrying the FULL report.
        Assert.Equal(GoalPhase.Review, pipeline.Phase);
        var testingEntry = Assert.Single(pipeline.PhaseLog, e => e.Name == GoalPhase.Testing);
        Assert.Equal(report, testingEntry.WorkerOutput);

        // The reviewer task dispatched by the driver carries the same FULL report verbatim.
        var reviewerTask = dispatched.Single(t => t.TaskId.EndsWith("reviewer-001-01-001"));
        Assert.Equal(WorkerRole.Reviewer, reviewerTask.Role);
        Assert.True(reviewerTask.Metadata.ContainsKey("tester_report"));
        Assert.Equal(report, reviewerTask.Metadata["tester_report"]);
    }

    /// <summary>
    /// A FAILED Testing completion through the same real chain must still hand the FULL report
    /// to the reviewer — verdict must not gate the report's size.
    /// </summary>
    [Fact]
    public async Task TestingCompletion_FailVerdict_FullReportStillSurvivesToReviewerMetadata()
    {        var config = CreateConfig();
        config.Workers["tester"] = new WorkerConfig { Model = "tester-model" };
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue, pipelineManager) = CreateServiceWithPipelineAndManager(
            GoalPhase.Testing, config, ModelTier.Default);

        var report = new string('F', 6_500) + "TAIL-FAIL-EVIDENCE";

        pipeline.CoderBranch = "feature/test-branch";

        var dispatched = new List<WorkTask>();
        taskQueue.OnEnqueue = t => dispatched.Add(t);

        await service.DispatchToRole(pipeline, WorkerRole.Tester, "Test it", TestContext.Current.CancellationToken);
        var dispatchedTesterId = Assert.Single(dispatched).TaskId;

        // The current Testing phase entry (see the PASS test above): must exist for iteration 1.
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Fail,
            Iteration = pipeline.Iteration,
            StartedAt = DateTime.UtcNow,
        });

        var completionService = CreateCompletionService(pipelineManager, service, config, pipeline.Goal);
        await completionService.HandleTaskCompletionAsync(
            new TaskResult
            {
                TaskId = dispatchedTesterId,
                Status = TaskOutcome.Completed,
                Output = "FAIL",
                Metrics = new TaskMetrics { Verdict = "FAIL", Summary = report },
            },
            TestContext.Current.CancellationToken);

        // Under a FAIL verdict the state machine opens a re-plan (NewIteration) — the pipeline
        // phase may be Planning/Testing-again — but the completed Testing entry MUST still carry
        // the FULL report (verdict must not gate report preservation).
        var testingEntry = Assert.Single(pipeline.PhaseLog, e => e.Name == GoalPhase.Testing);
        Assert.Equal(report, testingEntry.WorkerOutput);

        var reviewerTask = dispatched.FirstOrDefault(t => t.TaskId.EndsWith("reviewer-001-01-001"));
        if (reviewerTask is not null)
            Assert.Equal(report, reviewerTask.Metadata["tester_report"]);
    }

    // ── Tester-report SELECTION: latest current-iteration non-null wins ────

    /// <summary>
    /// THE SELECTION REGRESSION (AC4). The dispatch picks the LATEST Testing entry of the
    /// CURRENT iteration that has a non-null <see cref="PhaseResult.WorkerOutput"/>. This test
    /// populates every competing shape at once — an OLDER-iteration report, an EARLIER
    /// current-iteration report, the LATEST current-iteration report, and a later NULL entry —
    /// and asserts the dispatched metadata equals the latest current non-null report EXACTLY.
    /// </summary>
    /// <remarks>
    /// EACH DECOY KILLS A DISTINCT MUTANT:
    /// <list type="bullet">
    ///   <item><description>drop the <c>e.Iteration == pipeline.Iteration</c> filter → an older
    ///     iteration's report could be selected (it is the FIRST entry, so a First-based
    ///     selection also lands on it);</description></item>
    ///   <item><description>swap <c>LastOrDefault</c> for <c>FirstOrDefault</c> → the EARLIER
    ///     current-iteration report wins;</description></item>
    ///   <item><description>drop the <c>WorkerOutput is not null</c> predicate → the trailing
    ///     null entry becomes the selection and the key is dropped (a silent
    ///     clearing).</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task DispatchToRole_WhenMultipleTestingOutputs_SelectsLatestCurrentIterationNonNullReport()
    {
        var config = CreateConfig();
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Review, config, ModelTier.Default);

        // Move to iteration 2 so "older iteration" entries are genuinely addressable.
        Assert.True(pipeline.IterationBudget.TryConsume());
        var currentIteration = pipeline.Iteration;
        var olderIteration = currentIteration - 1;
        Assert.Equal(2, currentIteration);

        const string OlderReport = "OLDER-ITERATION-REPORT: iteration 1 tester output.";
        const string EarlierCurrentReport = "EARLIER-CURRENT-REPORT: first Testing round of iteration 2.";
        const string LatestCurrentReport = "LATEST-CURRENT-REPORT: second Testing round of iteration 2.";

        // (1) An OLDER iteration's Testing report — must never be selected.
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = olderIteration,
            Occurrence = 1,
            WorkerOutput = OlderReport,
        });

        // (2) An EARLIER Testing occurrence of the CURRENT iteration — superseded below.
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Fail,
            Iteration = currentIteration,
            Occurrence = 1,
            WorkerOutput = EarlierCurrentReport,
        });

        // (3) THE WINNER: the latest current-iteration Testing entry with a non-null output.
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = currentIteration,
            Occurrence = 2,
            WorkerOutput = LatestCurrentReport,
        });

        // (4) A LATER null entry (a Testing phase that started but has not reported yet).
        // It must be SKIPPED, not treated as a clearing of the winner above.
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = currentIteration,
            Occurrence = 3,
            WorkerOutput = null,
        });

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Reviewer, "Review it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.True(capturedTask!.Metadata.ContainsKey("tester_report"),
            "the trailing null entry must be skipped, never clear the latest non-null report");
        Assert.Equal(LatestCurrentReport, capturedTask.Metadata["tester_report"]);
        Assert.DoesNotContain("OLDER-ITERATION-REPORT", capturedTask.Metadata["tester_report"]);
        Assert.DoesNotContain("EARLIER-CURRENT-REPORT", capturedTask.Metadata["tester_report"]);
    }

    /// <summary>
    /// When ONLY older iterations produced Testing output, the current iteration's reviewer gets
    /// NO tester_report at all — a previous iteration's report must never leak forward.
    /// </summary>
    [Fact]
    public async Task DispatchToRole_WhenOnlyOlderIterationTesterOutput_DoesNotSetTesterReport()
    {
        var config = CreateConfig();
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue) = CreateServiceWithPipeline(
            GoalPhase.Review, config, ModelTier.Default);

        Assert.True(pipeline.IterationBudget.TryConsume());
        var olderIteration = pipeline.Iteration - 1;

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = olderIteration,
            Occurrence = 1,
            WorkerOutput = "OLDER-ITERATION-REPORT: stale, must not be handed to this iteration.",
        });

        // A current-iteration Testing entry exists but has produced NO output yet.
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            WorkerOutput = null,
        });

        WorkTask? capturedTask = null;
        taskQueue.OnEnqueue = t => capturedTask = t;

        await service.DispatchToRole(pipeline, WorkerRole.Reviewer, "Review it", TestContext.Current.CancellationToken);

        Assert.NotNull(capturedTask);
        Assert.False(capturedTask!.Metadata.ContainsKey("tester_report"));
    }

    // ── THE CONTINUOUS FULL CHAIN: dispatch → completion → worker → tool ──

    /// <summary>
    /// THE ONE UNBROKEN PROOF (AC2–3). A single test carries ONE synthetic tester report through
    /// EVERY real hop of the handoff, never re-stating the expectation from an independently
    /// constructed value:
    /// <list type="number">
    ///   <item><description>the REAL <see cref="TaskDispatchService"/> dispatches an ADMITTED
    ///     tester task under a valid plan with configured tester/reviewer models and a current
    ///     Testing phase entry;</description></item>
    ///   <item><description>the REAL <see cref="TaskCompletionService"/> → <see cref="PipelineDriver"/>
    ///     completes it (the report enters ONLY as the tester's <c>Metrics.Summary</c>), stores it
    ///     on the Testing phase entry and advances the pipeline to Review;</description></item>
    ///   <item><description>the reviewer <see cref="WorkTask"/> is CAPTURED FROM THE QUEUE — the
    ///     production dispatch built its tester_report metadata, the test never writes
    ///     it;</description></item>
    ///   <item><description>that captured task round-trips through the REAL
    ///     <see cref="GrpcMapper"/> (<c>ToGrpc</c> → <c>ToDomain</c>), the orchestrator↔worker
    ///     transport;</description></item>
    ///   <item><description>the round-tripped task runs through the REAL
    ///     <see cref="TaskExecutor"/> with fake Git and no live provider, whose runner is the
    ///     test-only adapter around a REAL <see cref="SharpCoderRunner"/>;</description></item>
    ///   <item><description>the ACTUAL <c>get_test_report</c> AIFunction invoked DURING
    ///     <c>SendPromptAsync</c> returns the complete report — trailing evidence
    ///     included.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// WHY THE OBSERVED INVOCATION MATTERS. The final assertion reads
    /// <c>ObservedGetTestReportResults</c>, which is appended ONLY inside the adapter's
    /// <c>SendPromptAsync</c>. <see cref="TaskExecutor"/> catches its own exceptions and returns
    /// a Failed <see cref="TaskResult"/>, so an executor that blew up before the prompt would
    /// produce an EMPTY list. Asserting both <c>TaskOutcome.Completed</c> AND exactly one
    /// observed invocation therefore makes a swallowed executor error impossible to pass.
    /// <para>
    /// NOTHING IS BYPASSED: the expected string is the same <c>report</c> local that was handed
    /// to the tester's completion metrics. It is never placed into metadata or worker state by
    /// the test — every intermediate value is read back out of production structures.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TesterReport_FullChain_ReachesGetTestReportToolIntact()
    {
        var config = CreateConfig();
        config.Workers["tester"] = new WorkerConfig { Model = "tester-model" };
        config.Workers["reviewer"] = new WorkerConfig { Model = "reviewer-model" };

        var (service, pipeline, taskQueue, pipelineManager) = CreateServiceWithPipelineAndManager(
            GoalPhase.Testing, config, ModelTier.Default);

        // The ONE report — a synthetic ~8KB tester report with distinctive evidence beyond
        // character 4,000 and at the very end (shared with the worker-side vectors).
        var report = TaskExecutorTesterReportTests.BuildRealisticReport();
        Assert.True(report.Length > 4_000);

        pipeline.CoderBranch = "feature/test-branch"; // seed so the tester task has BranchInfo

        var dispatched = new List<WorkTask>();
        taskQueue.OnEnqueue = t => dispatched.Add(t);

        // ── HOP 1: the REAL dispatch admits the tester task ──────────────
        await service.DispatchToRole(pipeline, WorkerRole.Tester, "Test it", TestContext.Current.CancellationToken);
        var testerTaskId = Assert.Single(dispatched).TaskId;
        Assert.Equal($"{pipeline.GoalId}-tester-001-01-001", testerTaskId);

        // The CURRENT Testing phase entry the driver writes its WorkerOutput onto.
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing,
            Result = PhaseOutcome.Pass,
            Iteration = pipeline.Iteration,
            Occurrence = 1,
            StartedAt = DateTime.UtcNow,
        });

        // ── HOP 2: the REAL completion chain stores the report and advances ──
        var completionService = CreateCompletionService(pipelineManager, service, config, pipeline.Goal);
        await completionService.HandleTaskCompletionAsync(
            new TaskResult
            {
                TaskId = testerTaskId,
                Status = TaskOutcome.Completed,
                Output = "PASS",
                Metrics = new TaskMetrics { Verdict = "PASS", Summary = report },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Review, pipeline.Phase);
        var testingEntry = Assert.Single(pipeline.PhaseLog, e => e.Name == GoalPhase.Testing);
        Assert.Equal(report, testingEntry.WorkerOutput);

        // ── HOP 3: the ACTUAL reviewer task, captured from the queue ─────
        var reviewerTask = dispatched.Single(t => t.Role == WorkerRole.Reviewer);
        Assert.True(reviewerTask.Metadata.ContainsKey("tester_report"),
            "the production dispatch must have attached the tester report — the test never writes it");
        Assert.Equal(report, reviewerTask.Metadata["tester_report"]);

        // ── HOP 4: the REAL gRPC transport round-trip ────────────────────
        var roundTripped = GrpcMapper.ToDomain(GrpcMapper.ToGrpc(reviewerTask));
        Assert.Equal(WorkerRole.Reviewer, roundTripped.Role);
        Assert.Equal(report, roundTripped.Metadata["tester_report"]);

        // ── HOPS 5–6: the REAL TaskExecutor + REAL SharpCoderRunner tool ─
        await using var runner = new TesterReportRunner();
        runner.WireRole(WorkerRole.Reviewer); // role wiring precedes report injection (it clears the field)
        var executor = new TaskExecutor(runner, gitOperations: new NoOpTesterReportGit());

        var executionResult = await executor.ExecuteAsync(roundTripped, TestContext.Current.CancellationToken);

        // The run SUCCEEDED (TaskExecutor swallows its own exceptions into a Failed result)…
        Assert.Equal(TaskOutcome.Completed, executionResult.Status);

        // …AND the tool really ran, so a pre-prompt failure cannot masquerade as a pass.
        var modelFacingResult = Assert.Single(runner.ObservedGetTestReportResults);
        Assert.Equal(report, modelFacingResult);
        Assert.EndsWith(TaskExecutorTesterReportTests.TrailingEvidence, modelFacingResult);
        Assert.Contains(TaskExecutorTesterReportTests.Beyond4000Marker, modelFacingResult);
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
    /// <para>
    /// The goal manager here is backed by a REAL <see cref="InMemoryGoalStore"/> containing the
    /// goal (not the throwaway <see cref="DispatchTestGoalSource"/>): the completion chain can
    /// take lifecycle branches that call <see cref="GoalLifecycleService.MarkGoalFailedAsync"/>,
    /// whose persistence requires an <see cref="IGoalStore"/>-capable source, and a source
    /// without one throws mid-drive and masks the actual outcome.
    /// </para>
    /// </summary>
    private static TaskCompletionService CreateCompletionService(
        GoalPipelineManager pipelineManager, TaskDispatchService dispatchService, HiveConfigFile config, Goal goal)
    {
        var goalManager = new GoalManager();
        var goalStore = new InMemoryGoalStore();
        goalStore.CreateGoalAsync(goal).GetAwaiter().GetResult();
        goalManager.AddSource(goalStore);
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
/// THE STORED-OAUTH ASSIGNMENT CREDENTIAL: <see cref="TaskDispatchService.DispatchToRole"/>
/// resolves the CURRENT stored admin OAuth token → <c>GH_TOKEN</c> → <c>GITHUB_TOKEN</c> ONCE per
/// assignment, DURING the preparation (before the work-slot capture), and rebuilds the task's
/// repository URLs from the AUTHORITATIVE configured URLs.
/// </summary>
/// <remarks>
/// Every vector asserts against the ACTUAL queued/built <see cref="WorkTask"/>'s repository URLs,
/// captured through the <see cref="TaskQueue.OnEnqueue"/> seam — never against a helper's return
/// value. All credentials are fake and no network call is made; the environment is mutated only
/// inside the serialized <c>EnvVarMutation</c> collection and is always restored.
/// </remarks>
[Collection("EnvVarMutation")]
public sealed class TaskDispatchCredentialTests
{
    private const string RepoName = "cred-repo";
    private const string ConfiguredUrl = "https://github.com/org/cred-repo.git";

    // ── fixture ──────────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="body"/> with both aliases set, restoring BOTH afterwards.</summary>
    private static async Task WithTokensAsync(string? ghToken, string? githubToken, Func<Task> body)
    {
        var originalGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var originalGithub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", ghToken);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", githubToken);
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", originalGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalGithub);
        }
    }

    private static HiveConfigFile CreateConfig(string url = ConfiguredUrl)
    {
        var config = new HiveConfigFile();
        config.Repositories.Add(new RepositoryConfig
        {
            Name = RepoName,
            Url = url,
            DefaultBranch = "develop",
        });
        config.Workers["coder"] = new WorkerConfig { Model = "coder-model" };
        return config;
    }

    private sealed class CredentialGoalSource : IGoalSource
    {
        private readonly Goal _goal;
        public CredentialGoalSource(Goal goal) => _goal = goal;
        public string Name => "credential-test-fake";
        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([_goal]);
        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Builds a real <see cref="TaskDispatchService"/> wired exactly as <c>GoalDispatcher</c>
    /// wires it, plus the caller's stored-credential lookup delegate.
    /// </summary>
    private static (TaskDispatchService Service, GoalPipeline Pipeline, TaskQueue Queue, GoalPipelineManager Manager, HiveConfigFile Config)
        CreateFixture(
            Func<CancellationToken, Task<string?>>? storedCredentialLookup,
            ILogger<TaskDispatchService>? logger = null,
            HiveConfigFile? config = null)
    {
        config ??= CreateConfig();
        logger ??= NullLogger<TaskDispatchService>.Instance;

        var queue = new TaskQueue();
        var manager = new GoalPipelineManager();
        var workerGateway = new GrpcWorkerGateway(new WorkerPool());

        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Credential goal",
            RepositoryNames = [RepoName],
        };

        var goalManager = new GoalManager();
        goalManager.AddSource(new CredentialGoalSource(goal));
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipeline = manager.CreatePipeline(goal);
        pipeline.AdvanceTo(GoalPhase.Coding);
        var plan = IterationPlan.Default(includeImprove: true);
        pipeline.SetPlan(plan);
        pipeline.StateMachine.StartIteration(plan.Phases);
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, pipeline.Phase);

        var lifecycleService = new GoalLifecycleService(goalManager, logger);
        var maintenance = new DispatcherMaintenance(
            manager, goalManager, queue, workerGateway,
            brain: null, agentsManager: null, configRepo: null,
            new ConcurrentQueue<string>(), logger, config: config);

        var service = new TaskDispatchService(
            queue, workerGateway, new TaskBuilder(new BranchCoordinator()), config,
            logger, manager, lifecycleService, maintenance,
            storedCredentialLookup: storedCredentialLookup);

        return (service, pipeline, queue, manager, config);
    }

    /// <summary>Dispatches once and returns the ACTUAL enqueued task.</summary>
    private static async Task<WorkTask> DispatchAndCaptureAsync(
        TaskDispatchService service, GoalPipeline pipeline, TaskQueue queue)
    {
        WorkTask? captured = null;
        queue.OnEnqueue = t => captured = t;
        await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        return captured!;
    }

    private static string SingleRepoUrl(WorkTask task) => Assert.Single(task.Repositories).Url;

    // ── (1) OAuth-only ───────────────────────────────────────────────────────

    /// <summary>
    /// A stored OAuth token with NO environment alias present: the ACTUAL queued task's URL
    /// carries the OAuth credential in usable form.
    /// </summary>
    [Fact]
    public async Task Dispatch_OAuthOnly_QueuedTaskUrlCarriesTheStoredToken()
    {
        await WithTokensAsync(null, null, async () =>
        {
            var (service, pipeline, queue, _, config) = CreateFixture(_ => Task.FromResult<string?>("oauth-token"));

            var task = await DispatchAndCaptureAsync(service, pipeline, queue);

            Assert.Equal("https://x-access-token:oauth-token@github.com/org/cred-repo.git", SingleRepoUrl(task));
            // THE CONFIGURED URL IS UNTOUCHED — the credential lives only on the assignment.
            Assert.Equal(ConfiguredUrl, Assert.Single(config.Repositories).Url);
            // AND THE PROCESS ENVIRONMENT IS UNTOUCHED: no token was written back to an alias.
            Assert.Null(Environment.GetEnvironmentVariable("GH_TOKEN"));
            Assert.Null(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        });
    }

    // ── (2) OAuth outranks BOTH aliases ──────────────────────────────────────

    /// <summary>
    /// With a stored OAuth token AND both aliases set, the OAuth token WINS and neither alias
    /// value appears anywhere in the assignment.
    /// </summary>
    [Fact]
    public async Task Dispatch_OAuthAndBothAliases_OAuthOutranksBoth()
    {
        await WithTokensAsync("gh-alias-token", "github-alias-token", async () =>
        {
            var (service, pipeline, queue, _, _) = CreateFixture(_ => Task.FromResult<string?>("oauth-token"));

            var task = await DispatchAndCaptureAsync(service, pipeline, queue);
            var url = SingleRepoUrl(task);

            Assert.Equal("https://x-access-token:oauth-token@github.com/org/cred-repo.git", url);
            Assert.DoesNotContain("gh-alias-token", url, StringComparison.Ordinal);
            Assert.DoesNotContain("github-alias-token", url, StringComparison.Ordinal);
            // The aliases themselves are unchanged.
            Assert.Equal("gh-alias-token", Environment.GetEnvironmentVariable("GH_TOKEN"));
            Assert.Equal("github-alias-token", Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        });
    }

    /// <summary>
    /// A BLANK stored OAuth token falls through to <c>GH_TOKEN</c>; a blank <c>GH_TOKEN</c> falls
    /// through to <c>GITHUB_TOKEN</c> — the whitespace-is-absent rule at EVERY step.
    /// </summary>
    [Theory]
    [InlineData(null, "gh-alias-token", "github-alias-token", "gh-alias-token")]
    [InlineData("", "gh-alias-token", "github-alias-token", "gh-alias-token")]
    [InlineData("   ", "gh-alias-token", "github-alias-token", "gh-alias-token")]
    [InlineData(null, "  ", "github-alias-token", "github-alias-token")]
    [InlineData("\t", null, "github-alias-token", "github-alias-token")]
    public async Task Dispatch_BlankCandidatesFallThroughInOrder(
        string? oauthToken, string? ghToken, string? githubToken, string expectedCredential)
    {
        await WithTokensAsync(ghToken, githubToken, async () =>
        {
            var (service, pipeline, queue, _, _) = CreateFixture(_ => Task.FromResult(oauthToken));

            var task = await DispatchAndCaptureAsync(service, pipeline, queue);

            Assert.Equal(
                $"https://x-access-token:{expectedCredential}@github.com/org/cred-repo.git",
                SingleRepoUrl(task));
        });
    }

    // ── (3) blank/missing OAuth AND blank/missing aliases ────────────────────

    /// <summary>
    /// EVERY candidate blank or absent: the assignment carries the CONFIGURED URL verbatim — no
    /// userinfo, no empty credential artefact.
    /// </summary>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("   ", " ", "\t")]
    public async Task Dispatch_NoUsableCredentialAnywhere_QueuedTaskUrlIsTheConfiguredUrl(
        string? oauthToken, string? ghToken, string? githubToken)
    {
        await WithTokensAsync(ghToken, githubToken, async () =>
        {
            var (service, pipeline, queue, _, _) = CreateFixture(_ => Task.FromResult(oauthToken));

            var task = await DispatchAndCaptureAsync(service, pipeline, queue);

            Assert.Equal(ConfiguredUrl, SingleRepoUrl(task));
            Assert.DoesNotContain("@", SingleRepoUrl(task), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// NO lookup wired at all (the direct-construction / no-UserService case): the environment
    /// chain alone is resolved and the dispatch still succeeds.
    /// </summary>
    [Fact]
    public async Task Dispatch_NoLookupWired_UsesEnvironmentChainAlone()
    {
        await WithTokensAsync(null, "github-alias-token", async () =>
        {
            var (service, pipeline, queue, _, _) = CreateFixture(storedCredentialLookup: null);

            var task = await DispatchAndCaptureAsync(service, pipeline, queue);

            Assert.Equal(
                "https://x-access-token:github-alias-token@github.com/org/cred-repo.git",
                SingleRepoUrl(task));
        });
    }

    // ── (4) lookup failure → environment fallback + credential-free diagnostic ──

    /// <summary>
    /// A THROWING lookup degrades to the environment chain, the dispatch still succeeds, and the
    /// warning is the FIXED, credential-free diagnostic: the lookup's own (credential-bearing)
    /// message never reaches the log, and no logged line quotes any credential.
    /// </summary>
    [Fact]
    public async Task Dispatch_LookupThrows_FallsBackToEnvironmentAndRedactsTheDiagnostic()
    {
        const string secret = "super-secret-oauth-token";
        await WithTokensAsync("gh-alias-token", null, async () =>
        {
            var logger = new TestLogger<TaskDispatchService>();
            var (service, pipeline, queue, _, _) = CreateFixture(
                _ => throw new InvalidOperationException($"db failure while reading token '{secret}'"),
                logger);

            var task = await DispatchAndCaptureAsync(service, pipeline, queue);

            // (i) THE FALLBACK: the assignment still has a usable credential — from the alias.
            Assert.Equal(
                "https://x-access-token:gh-alias-token@github.com/org/cred-repo.git",
                SingleRepoUrl(task));

            // (ii) THE FIXED DIAGNOSTIC fired, naming only the goal and the exception TYPE.
            var warning = Assert.Single(
                logger.LogEntries,
                e => e.LogLevel == LogLevel.Warning && e.Message.Contains("Stored OAuth credential lookup failed", StringComparison.Ordinal));
            Assert.Contains(pipeline.GoalId, warning.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(InvalidOperationException), warning.Message, StringComparison.Ordinal);

            // (iii) THE REDACTION: no logged message (nor a captured exception) carries the raw
            // lookup message or any credential.
            Assert.All(logger.LogEntries, e =>
            {
                Assert.DoesNotContain(secret, e.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("gh-alias-token", e.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("db failure while reading token", e.Message, StringComparison.Ordinal);
                Assert.Null(e.Exception);
            });
        });
    }

    /// <summary>
    /// With NO alias to fall back to, a throwing lookup still leaves the dispatch working —
    /// the assignment simply carries the CONFIGURED URL and nothing is leaked.
    /// </summary>
    [Fact]
    public async Task Dispatch_LookupThrowsAndNoAliases_UrlIsUnchangedAndNothingIsLeaked()
    {
        await WithTokensAsync(null, null, async () =>
        {
            var logger = new TestLogger<TaskDispatchService>();
            var (service, pipeline, queue, _, _) = CreateFixture(
                _ => throw new InvalidOperationException("token='leaky-value'"), logger);

            var task = await DispatchAndCaptureAsync(service, pipeline, queue);

            Assert.Equal(ConfiguredUrl, SingleRepoUrl(task));
            Assert.All(logger.LogEntries, e =>
                Assert.DoesNotContain("leaky-value", e.Message, StringComparison.Ordinal));
        });
    }

    // ── (5) two dispatches observe rotation ──────────────────────────────────

    /// <summary>
    /// THE ROTATION VECTOR: the lookup is invoked ONCE PER ASSIGNMENT and is never cached, so a
    /// token rotated between two dispatches is observed by the SECOND one.
    /// </summary>
    /// <remarks>
    /// The two dispatches are two DIFFERENT phases of the same pipeline — the ordinary production
    /// sequence — so no admission invariant is bent to reach the second assignment.
    /// </remarks>
    [Fact]
    public async Task Dispatch_TwoDispatches_ObserveTokenRotation()
    {
        await WithTokensAsync(null, null, async () =>
        {
            var config = CreateConfig();
            config.Workers["tester"] = new WorkerConfig { Model = "tester-model" };

            var tokens = new Queue<string?>(["token-v1", "token-v2"]);
            var lookupCalls = 0;
            var (service, pipeline, queue, _, _) = CreateFixture(
                _ =>
                {
                    lookupCalls++;
                    return Task.FromResult(tokens.Dequeue());
                },
                config: config);

            var enqueued = new List<WorkTask>();
            queue.OnEnqueue = enqueued.Add;

            await service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", TestContext.Current.CancellationToken);

            // Advance to the next worker phase exactly as the completion path does: the pointer is
            // released through the ownership-checked clear and the pipeline moves on.
            pipeline.ClearActiveTaskIfCurrent(pipeline.ActiveTaskId!);
            var plan = pipeline.Plan!;
            pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Testing);
            pipeline.AdvanceTo(GoalPhase.Testing);

            await service.DispatchToRole(pipeline, WorkerRole.Tester, "Test it", TestContext.Current.CancellationToken);

            Assert.Equal(2, lookupCalls);
            Assert.Equal(2, enqueued.Count);
            Assert.Equal(
                "https://x-access-token:token-v1@github.com/org/cred-repo.git", SingleRepoUrl(enqueued[0]));
            Assert.Equal(
                "https://x-access-token:token-v2@github.com/org/cred-repo.git", SingleRepoUrl(enqueued[1]));
            // THE REBUILD PROOF: the second URL was built from the CONFIGURED url, not from the
            // first (already tokenized) one — no accumulation.
            Assert.DoesNotContain("token-v1", SingleRepoUrl(enqueued[1]), StringComparison.Ordinal);
            Assert.Equal(1, SingleRepoUrl(enqueued[1]).Count(c => c == '@'));
        });
    }

    // ── ineligible URLs keep existing behaviour ──────────────────────────────

    /// <summary>
    /// An INELIGIBLE configured URL (SSH, local path, plain HTTP, non-GitHub host, non-443 port)
    /// receives NO newly resolved token: the assignment carries the configured URL verbatim.
    /// </summary>
    [Theory]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("ssh://git@github.com/org/repo.git")]
    [InlineData("/srv/local/repo.git")]
    [InlineData("http://github.com/org/repo.git")]
    [InlineData("https://gitlab.com/org/repo.git")]
    [InlineData("https://github.com.evil.test/org/repo.git")]
    [InlineData("https://github.com:8443/org/repo.git")]
    public async Task Dispatch_IneligibleConfiguredUrl_ReceivesNoToken(string url)
    {
        await WithTokensAsync("gh-alias-token", "github-alias-token", async () =>
        {
            var (service, pipeline, queue, _, _) = CreateFixture(
                _ => Task.FromResult<string?>("oauth-token"), config: CreateConfig(url));

            var task = await DispatchAndCaptureAsync(service, pipeline, queue);

            Assert.Equal(url, SingleRepoUrl(task));
            Assert.DoesNotContain("oauth-token", SingleRepoUrl(task), StringComparison.Ordinal);
        });
    }

    // ── (6) preparation cancellation — NO admission, NO enqueue ──────────────

    /// <summary>
    /// A PRE-CANCELLED token is observed in the PREPARATION: the dispatch propagates the
    /// cancellation having captured no slot, claimed no pointer, registered no mapping and
    /// enqueued nothing — and the lookup is never even invoked.
    /// </summary>
    [Fact]
    public async Task Dispatch_PreCancelled_PropagatesWithNoAdmissionAndNoEnqueue()
    {
        await WithTokensAsync("gh-alias-token", null, async () =>
        {
            var lookupCalls = 0;
            var (service, pipeline, queue, manager, _) = CreateFixture(_ =>
            {
                lookupCalls++;
                return Task.FromResult<string?>("oauth-token");
            });

            var enqueued = new List<WorkTask>();
            queue.OnEnqueue = enqueued.Add;

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token));

            Assert.Equal(0, lookupCalls);
            AssertNoAdmission(pipeline, queue, manager, enqueued);
        });
    }

    /// <summary>
    /// A cancellation arriving DURING the lookup itself — triggered deterministically from inside
    /// the lookup delegate, no timing sleep — takes the same no-mutation path.
    /// </summary>
    [Fact]
    public async Task Dispatch_CancelledDuringLookup_PropagatesWithNoAdmissionAndNoEnqueue()
    {
        await WithTokensAsync("gh-alias-token", null, async () =>
        {
            using var cts = new CancellationTokenSource();
            var lookupEntered = false;

            var (service, pipeline, queue, manager, _) = CreateFixture(async ct =>
            {
                lookupEntered = true;
                // THE GATE: the cancellation becomes observable while the lookup is in flight,
                // then the lookup observes the token it was HANDED — a genuine caller-token OCE.
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return "never-reached";
            });

            var enqueued = new List<WorkTask>();
            queue.OnEnqueue = enqueued.Add;

            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token));

            Assert.True(lookupEntered, "the lookup must actually have been entered");
            Assert.Equal(cts.Token, thrown.CancellationToken);
            AssertNoAdmission(pipeline, queue, manager, enqueued);
        });
    }

    /// <summary>
    /// THE NO-MUTATION GUARANTEE of the preparation: no slot, no pointer (in memory), no
    /// task→goal mapping, nothing pending in the queue.
    /// </summary>
    private static void AssertNoAdmission(
        GoalPipeline pipeline, TaskQueue queue, GoalPipelineManager manager, List<WorkTask> enqueued)
    {
        Assert.Empty(pipeline.GetSlotsForTest());
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Empty(enqueued);
        Assert.Null(queue.TryDequeueAny());
        var expectedTaskId = $"{pipeline.GoalId}-coder-001-01-001";
        Assert.Null(manager.GetByTaskId(expectedTaskId));
    }

    // ── (7) THE POST-LOOKUP RECHECK — the cancellation the lookup never reports ──

    /// <summary>The bound every gate await carries, so a hang fails fast instead of stalling.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A deterministic two-phase gate around the lookup: the lookup signals it has been ENTERED
    /// and then blocks until the test RELEASES it. That lets the test cancel the caller's token
    /// at a precisely known instant — while the lookup is in flight — with no timing sleep.
    /// </summary>
    private sealed class LookupGate
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Number of times the gated lookup was invoked.</summary>
        public int Invocations { get; private set; }

        /// <summary>Called FROM the lookup: announce entry, then wait for the release.</summary>
        public async Task EnterAndWaitAsync()
        {
            Invocations++;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(GateTimeout);
        }

        /// <summary>Awaited BY the test: completes once the lookup has actually been entered.</summary>
        public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(GateTimeout);

        /// <summary>Lets the blocked lookup proceed to its outcome.</summary>
        public void Release() => _release.TrySetResult();
    }

    /// <summary>
    /// THE REMOVAL PROOF FOR PATH (a): the caller is cancelled WHILE the lookup runs, and the
    /// lookup then RETURNS A TOKEN NORMALLY — it never observes nor reports the cancellation. The
    /// dispatch must still refuse: no slot, no pointer, no mapping, no enqueue.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF: delete the post-lookup <c>ThrowSanitizedIfCancelled(ct)</c> in
    /// <c>ResolveAssignmentCredentialAsync</c> and this test goes RED — the dispatch proceeds
    /// through the capture, the claim, the admission and the enqueue (and, with no idle worker,
    /// even completes successfully). The gate makes the race deterministic: the cancellation is
    /// requested at a known instant with the lookup provably in flight, with no sleep.
    /// </remarks>
    [Fact]
    public async Task Dispatch_CancelledDuringLookupThatReturnsNormally_MakesNoAdmissionAndNoEnqueue()
    {
        await WithTokensAsync("gh-alias-token", null, async () =>
        {
            using var cts = new CancellationTokenSource();
            var gate = new LookupGate();

            var (service, pipeline, queue, manager, _) = CreateFixture(async _ =>
            {
                await gate.EnterAndWaitAsync();
                // THE POINT: a perfectly successful lookup. It neither observes the token nor
                // throws — the cancellation is invisible to it.
                return "oauth-token";
            });

            var enqueued = new List<WorkTask>();
            queue.OnEnqueue = enqueued.Add;

            var dispatch = service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token);

            // The lookup is provably IN FLIGHT; cancel exactly here, then let it finish.
            await gate.WaitUntilEnteredAsync();
            await cts.CancelAsync();
            gate.Release();

            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

            Assert.Equal(1, gate.Invocations);
            Assert.Equal(cts.Token, thrown.CancellationToken);
            AssertNoAdmission(pipeline, queue, manager, enqueued);
        });
    }

    /// <summary>
    /// THE REMOVAL PROOF FOR PATH (b): the caller is cancelled WHILE the lookup runs, and the
    /// lookup then faults with a NON-cancellation exception. The environment fallback swallows
    /// that failure — so the cancellation is again invisible to the lookup — and the dispatch
    /// must still refuse with no admission and no enqueue.
    /// </summary>
    /// <remarks>
    /// This is the second, independent vector the post-lookup recheck closes: the fallback path.
    /// Without the recheck the dispatch would proceed on the ALIAS credential and enqueue.
    /// </remarks>
    [Fact]
    public async Task Dispatch_CancelledDuringLookupThatFaultsNonCancellation_MakesNoAdmissionAndNoEnqueue()
    {
        await WithTokensAsync("gh-alias-token", null, async () =>
        {
            using var cts = new CancellationTokenSource();
            var gate = new LookupGate();
            var logger = new TestLogger<TaskDispatchService>();

            var (service, pipeline, queue, manager, _) = CreateFixture(
                async _ =>
                {
                    await gate.EnterAndWaitAsync();
                    // A NON-cancellation fault: the environment fallback handles it, so nothing
                    // about the cancellation surfaces from the lookup itself.
                    throw new InvalidOperationException("provider unavailable");
                },
                logger);

            var enqueued = new List<WorkTask>();
            queue.OnEnqueue = enqueued.Add;

            var dispatch = service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token);

            await gate.WaitUntilEnteredAsync();
            await cts.CancelAsync();
            gate.Release();

            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

            Assert.Equal(1, gate.Invocations);
            Assert.Equal(cts.Token, thrown.CancellationToken);
            // The fallback really ran (its credential-free warning fired) — and the dispatch was
            // STILL refused afterwards.
            Assert.Contains(
                logger.LogEntries,
                e => e.LogLevel == LogLevel.Warning &&
                     e.Message.Contains("Stored OAuth credential lookup failed", StringComparison.Ordinal));
            AssertNoAdmission(pipeline, queue, manager, enqueued);
        });
    }

    // ── (8) THE SANITIZED CANCELLATION BOUNDARY ──────────────────────────────

    /// <summary>
    /// A lookup cancellation whose exception carries SECRETS in BOTH its message AND its inner
    /// exception must never escape as-is: the dispatch raises a FRESH, credential-free
    /// cancellation carrying the caller's token and NO inner exception.
    /// </summary>
    /// <remarks>
    /// WHY THE INNER EXCEPTION MATTERS: downstream sinks render the whole chain.
    /// <c>PipelineDriver</c>'s improve-phase catch logs the exception AND <c>ex.Message</c>, and
    /// copies that message into the phase VERDICT and the goal-update NOTES — so a retained
    /// provider message or inner exception would be persisted in plain sight. Asserting on
    /// <c>ToString()</c> covers every renderer that walks the chain, not just <c>Message</c>.
    /// </remarks>
    [Fact]
    public async Task Dispatch_LookupCancellationCarryingSecrets_PropagatesSanitizedCancellation()
    {
        const string messageSecret = "ghp_message_secret_token";
        const string innerSecret = "ghp_inner_secret_token";

        await WithTokensAsync("gh-alias-token", null, async () =>
        {
            using var cts = new CancellationTokenSource();
            var logger = new TestLogger<TaskDispatchService>();

            var (service, pipeline, queue, manager, _) = CreateFixture(
                async ct =>
                {
                    await cts.CancelAsync();
                    // A CANCELLATION exception whose message AND inner exception both quote a
                    // credential — exactly what a provider error can look like.
                    throw new OperationCanceledException(
                        $"cancelled while using token '{messageSecret}'",
                        new InvalidOperationException($"connection string password={innerSecret}"),
                        ct);
                },
                logger);

            var enqueued = new List<WorkTask>();
            queue.OnEnqueue = enqueued.Add;

            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token));

            // (i) IT IS A CANCELLATION, and it carries the CALLER'S token.
            Assert.Equal(cts.Token, thrown.CancellationToken);
            // (ii) THE PROVIDER EXCEPTION IS DROPPED ENTIRELY — no inner chain to render.
            Assert.Null(thrown.InnerException);
            // (iii) THE FIXED MESSAGE, and NO secret anywhere in the escaping exception —
            // Message or the full ToString() a chain-walking renderer would emit.
            Assert.Equal(TaskDispatchService.StoredCredentialLookupCancelledMessage, thrown.Message);
            foreach (var secret in new[] { messageSecret, innerSecret })
            {
                Assert.DoesNotContain(secret, thrown.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, thrown.ToString(), StringComparison.Ordinal);
            }

            // (iv) NO DOWNSTREAM DIAGNOSTIC SURFACE carries a secret either — and a cancellation
            // is not a lookup FAILURE, so the fallback warning must not fire.
            Assert.All(logger.LogEntries, e =>
            {
                Assert.DoesNotContain(messageSecret, e.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(innerSecret, e.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("gh-alias-token", e.Message, StringComparison.Ordinal);
            });
            Assert.DoesNotContain(
                logger.LogEntries,
                e => e.Message.Contains("Stored OAuth credential lookup failed", StringComparison.Ordinal));

            // (v) AND NOTHING WAS ADMITTED.
            AssertNoAdmission(pipeline, queue, manager, enqueued);
        });
    }

    /// <summary>
    /// The SAME sanitization applies to the RESULT surface a downstream sink would persist: the
    /// escaping exception's message is what <c>PipelineDriver</c> copies into a phase verdict and
    /// a goal note, so rendering it exactly as that sink does must yield no secret.
    /// </summary>
    [Fact]
    public async Task Dispatch_LookupCancellationCarryingSecrets_RenderedVerdictAndNotesAreSecretFree()
    {
        const string secret = "ghp_verdict_secret_token";

        await WithTokensAsync(null, null, async () =>
        {
            using var cts = new CancellationTokenSource();
            var (service, pipeline, queue, _, _) = CreateFixture(async ct =>
            {
                await cts.CancelAsync();
                throw new OperationCanceledException(
                    $"provider failure token={secret}",
                    new InvalidOperationException($"inner {secret}"),
                    ct);
            });

            queue.OnEnqueue = _ => { };

            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.DispatchToRole(pipeline, WorkerRole.Coder, "Code it", cts.Token));

            // The EXACT downstream renderings: PipelineDriver's improve catch builds
            // $"Improver failed: {ex.Message}" for the verdict and
            // $"Improver skipped: {ex.Message}" for the goal note.
            var verdict = $"Improver failed: {thrown.Message}";
            var note = $"Improver skipped: {thrown.Message}";

            Assert.DoesNotContain(secret, verdict, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, note, StringComparison.Ordinal);
            Assert.Contains(TaskDispatchService.StoredCredentialLookupCancelledMessage, verdict, StringComparison.Ordinal);
        });
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
