using System.Collections.Concurrent;

using CopilotHive.Configuration;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CopilotHive.Tests;

/// <summary>
/// Checkpoint-1 catalog-safety tests for <see cref="HiveConfigFile"/>: the atomic catalog API,
/// <see cref="HiveConfigFile.CaptureConfigSnapshot"/>/<c>HiveConfigSnapshot</c>, the CompactionModel
/// get/set APIs, and the rewritten <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/>.
/// <para>
/// SCOPE (checkpoint 1): the lock coordinates ONLY <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/>
/// and the new synchronized APIs. Direct public-list mutations are NOT synchronized in this
/// checkpoint and are therefore EXCLUDED from the concurrency test's invariants — they appear
/// only in single-threaded SETUP and in the dedicated detachment tests.
/// </para>
/// <para>
/// Removal-proofing: the concurrency/atomicity tests fail if the lock is removed from
/// <c>TryAddAvailableModel</c>/<see cref="HiveConfigFile.CaptureConfigSnapshot"/>; the
/// post-reload source-mutation test fails if the snapshot-then-replace ordering (or the deep
/// copy) is removed from <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/>.
/// </para>
/// </summary>
public sealed class HiveConfigFileCatalogSafetyTests
{
    // ── Shared production-mirroring YAML infrastructure ─────────────────────

    /// <summary>
    /// The same deserializer configuration used by production code in
    /// <see cref="ConfigRepoManager"/> — underscored naming convention, ignore unmatched.
    /// </summary>
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// The same serializer configuration used by production code in
    /// <see cref="ConfigRepoManager"/> — underscored naming convention, omit defaults and nulls.
    /// Used as the CANONICAL serializer for semantic YAML comparisons.
    /// </summary>
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
        .Build();

    private static string CanonicalYaml(HiveConfigFile config) => YamlSerializer.Serialize(config);

    /// <summary>The canonical form of a YAML document: parse, then re-serialize canonically.</summary>
    private static string Canonicalize(string yaml) =>
        CanonicalYaml(YamlDeserializer.Deserialize<HiveConfigFile>(yaml)!);

    // ── Entry helpers ────────────────────────────────────────────────────────

    /// <summary>The COMPLETE field tuple of a <see cref="ModelEntry"/>, used for torn-read checks.</summary>
    private sealed record EntryTuple(
        string? Name, int? ContextWindow, string? ReasoningEffort, string? Description, bool? SupportsVision);

    private static EntryTuple TupleOf(ModelEntry? e) => e is null
        ? new EntryTuple(null, null, null, null, null)
        : new EntryTuple(e.Name, e.ContextWindow, e.ReasoningEffort, e.Description, e.SupportsVision);

    private static ModelEntry MakeEntry(
        string name, int? contextWindow = null, string? reasoningEffort = null,
        string? description = null, bool? supportsVision = null) => new()
        {
            Name = name,
            ContextWindow = contextWindow,
            ReasoningEffort = reasoningEffort,
            Description = description,
            SupportsVision = supportsVision
        };

    /// <summary>Asserts two entry lists are deep-equal element-by-element (order and multiplicity).</summary>
    private static void AssertSameEntries(IReadOnlyList<ModelEntry>? expected, IReadOnlyList<ModelEntry>? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual!.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(TupleOf(expected[i]), TupleOf(actual[i]));
    }

    private static void AssertSameModels(ModelsConfig? expected, ModelsConfig? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected!.CompactionModel, actual!.CompactionModel);
        AssertSameEntries(expected.AvailableModels, actual.AvailableModels);
        AssertSameEntries(expected.SubAgentModels, actual.SubAgentModels);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. Concurrency test with precise invariants
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writers restricted to <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/> and the new
    /// locked APIs mutate/reload the catalog while readers call
    /// <see cref="HiveConfigFile.CaptureConfigSnapshot"/>. Invariants for EVERY snapshot:
    /// (a) the snapshot is a CORRELATED COMPLETE state — the base (A or B) is determined by the
    /// top-level version field, and ALL fields (both catalogs, compaction model, orchestrator,
    /// workers) must be consistent with that base, plus optional churn entries; (b) every entry
    /// carries the COMPLETE field tuple of a reachable state (no torn entries); (c) zero
    /// exceptions. No <c>Task.Delay</c>.
    /// <para>
    /// OVERLAP IS STRUCTURAL, not incidental. Each of the four writer roles owns its OWN gate,
    /// opened only AFTER that role's first REAL mutation completes (never at role entry), and
    /// the three-step ordering is:
    /// <list type="number">
    /// <item>every reader takes snapshot #1 and only THEN signals <c>readersInLoop</c>, so the
    /// signal proves the reader is inside its snapshot loop;</item>
    /// <item>every writer waits for <c>readersInLoop</c> BEFORE its first mutation, then
    /// performs that mutation, then opens its own per-role gate;</item>
    /// <item>every reader waits for ALL FOUR per-role gates before snapshot #2.</item>
    /// </list>
    /// Therefore, for every reader, all four roles — including the source alternator and the
    /// target reloader — completed a real mutation strictly BETWEEN that reader's snapshot #1
    /// and snapshot #2. Per-role mutation counters are asserted non-zero afterwards, so a
    /// stalled or descheduled writer cannot silently vacate the proof.
    /// </para>
    /// <para>
    /// The reload SOURCE is itself alternated between two distinguishable COMPLETE states
    /// (different <c>AvailableModels</c>, different <c>SubAgentModels</c>, and varied top-level
    /// fields) via <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/> while the target is
    /// reloaded from it — so the before/after states of BOTH catalogs differ. Each published
    /// state is OBSERVABLE BY CONSTRUCTION: the alternator publishes a state, then HOLDS it
    /// until a reader has actually captured a target snapshot carrying that version, and only
    /// then advances. Readers must observe both correlated states; because every published
    /// state is held until acknowledged, no legal schedule can produce a false failure of that
    /// assertion, while an implementation that genuinely fails to publish still fails it.
    /// </para>
    /// <para>
    /// The handshake gates only the ADVANCE past a published state — it never serializes away
    /// the racing transition. Each cycle first performs an un-handshaked burst of back-to-back
    /// source flips that races the target reloader, which is what keeps the torn-source mutant
    /// (snapshot-then-replace ordering removed) deterministically detectable.
    /// </para>
    /// <para>
    /// Removal-proof: if the catalog lock is removed from
    /// <see cref="HiveConfigFile.CaptureConfigSnapshot"/> or from
    /// <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/>'s replacement, a reader can
    /// observe a snapshot mixing fields from state A and state B (torn top-level replacement),
    /// which the correlated validator deterministically rejects. If the snapshot-then-replace
    /// ordering is removed from the target's reload, the target reads a TORN SOURCE (the
    /// source is mid-alternation) — also rejected. If the deep copy is removed from
    /// <see cref="HiveConfigFile.CaptureConfigSnapshot"/>, readers observe live references
    /// mutating underneath them — likewise rejected (and the detachment tests fail too).
    /// </para>
    /// </summary>
    [Fact]
    public async Task CaptureConfigSnapshot_ConcurrentLockedWriters_NeverTornAndNeverThrows()
    {
        const int readers = 6;
        const int snapshotsPerReader = 200;
        var failures = new ConcurrentBag<string>();
        var exceptions = new ConcurrentBag<Exception>();

        // ── Two distinguishable COMPLETE states (A and B) ─────────────────────
        // Both catalogs differ, plus compaction model, orchestrator, workers and version,
        // so a snapshot mixing fields from different states is detectable.
        var stateA = new HiveConfigFile();
        SeedStateA(stateA);
        var stateB = new HiveConfigFile();
        SeedStateB(stateB);

        // Reference states captured from the immutable sources.
        var availableA = stateA.CaptureConfigSnapshot().Models!.AvailableModels!;
        var subAgentsA = stateA.CaptureConfigSnapshot().Models!.SubAgentModels!;
        var availableB = stateB.CaptureConfigSnapshot().Models!.AvailableModels!;
        var subAgentsB = stateB.CaptureConfigSnapshot().Models!.SubAgentModels!;

        // Churn states: base + one churn entry (the churn writers add/remove these on the target).
        var churnAvailA = availableA.Append(MakeEntry("churn-avail", 5000, null, "churn-desc", false)).ToList();
        var churnAvailB = availableB.Append(MakeEntry("churn-avail", 5000, null, "churn-desc", false)).ToList();
        var churnSubA = subAgentsA.Append(MakeEntry("churn-sub", 7000, "low", "churn-sub-desc", true)).ToList();
        var churnSubB = subAgentsB.Append(MakeEntry("churn-sub", 7000, "low", "churn-sub-desc", true)).ToList();

        // The shared reload SOURCE, alternated between state A and state B by a writer.
        var source = new HiveConfigFile();
        source.ReloadFrom(stateA);   // source starts in state A

        // The shared target that readers snapshot and writers mutate/reload.
        var target = new HiveConfigFile();
        target.ReloadFrom(stateA);   // target starts in state A

        // ── Correlated validator ──────────────────────────────────────────────
        // Every snapshot must be a COMPLETE state: the base (A or B) is determined by the
        // version field, and ALL fields must be consistent with that base (plus optional
        // churn entries). A snapshot mixing A's models with B's orchestrator (or missing
        // the deep copy) deterministically fails.
        string? ValidateSnapshot(HiveConfigSnapshot snapshot)
        {
            var isA = snapshot.Version == "A";
            var isB = snapshot.Version == "B";
            if (!isA && !isB)
                return $"Unknown version '{snapshot.Version}' — torn top-level replacement.";

            if (snapshot.Models is null)
                return "Snapshot has null Models — torn replacement.";

            var expectedAvailable = isA ? availableA : availableB;
            var expectedSubAgents = isA ? subAgentsA : subAgentsB;
            var expectedChurnAvail = isA ? churnAvailA : churnAvailB;
            var expectedChurnSub = isA ? churnSubA : churnSubB;
            var expectedCompaction = isA ? "cm-a" : "cm-b";
            var expectedOrchModel = isA ? "orch-a" : "orch-b";
            var expectedOrchIterations = isA ? 1 : 2;
            var expectedWorkerModel = isA ? "worker-a" : "worker-b";

            var available = snapshot.Models.AvailableModels;
            var subAgents = snapshot.Models.SubAgentModels;
            var availableValid = ListsMatch(available, expectedAvailable)
                || ListsMatch(available, expectedChurnAvail);
            var subAgentsValid = ListsMatch(subAgents, expectedSubAgents)
                || ListsMatch(subAgents, expectedChurnSub);
            var compactionValid = snapshot.Models.CompactionModel == expectedCompaction;
            var orchValid = snapshot.Orchestrator is not null
                && snapshot.Orchestrator.Model == expectedOrchModel
                && snapshot.Orchestrator.MaxIterations == expectedOrchIterations;
            var workerValid = snapshot.Workers is not null
                && snapshot.Workers.TryGetValue("coder", out var wc)
                && wc is not null
                && wc.Model == expectedWorkerModel;

            if (availableValid && subAgentsValid && compactionValid && orchValid && workerValid)
                return null;

            return
                "Torn/unknown snapshot observed. " +
                $"version={snapshot.Version}, compaction={snapshot.Models.CompactionModel}, " +
                $"orchModel={snapshot.Orchestrator?.Model}, orchIterations={snapshot.Orchestrator?.MaxIterations}, " +
                $"workerModel={snapshot.Workers?.GetValueOrDefault("coder")?.Model}, " +
                $"available=[{Describe(available)}], subAgent=[{Describe(subAgents)}]";
        }

        static string Describe(IReadOnlyList<ModelEntry>? list) =>
            list is null ? "<null>" : string.Join("; ", list.Select(e => TupleOf(e)));

        static bool ListsMatch(IReadOnlyList<ModelEntry>? actual, IReadOnlyList<ModelEntry> expected)
        {
            if (actual is null || actual.Count != expected.Count)
                return false;
            for (var i = 0; i < expected.Count; i++)
            {
                if (TupleOf(actual[i]) != TupleOf(expected[i]))
                    return false;
            }
            return true;
        }

        // ── Per-writer-role progress gates: PROVEN overlap, no Task.Delay ────
        // The ordering enforced below makes overlap a structural property, not a chance
        // interleaving:
        //   1. Every reader takes snapshot #1, then signals `readersInLoop`. The signal
        //      therefore proves the reader is INSIDE its snapshot loop.
        //   2. Every writer FIRST waits for `readersInLoop` (all readers demonstrably in
        //      their loop), THEN performs its role's real mutation, and only AFTER that
        //      mutation completes does it open its OWN per-role gate.
        //   3. Every reader waits for ALL FOUR per-role gates before taking snapshot #2.
        // Consequently, for every reader, each of the four roles performed at least one
        // REAL mutation strictly BETWEEN that reader's snapshot #1 and snapshot #2 — the
        // mutations provably overlap the snapshot loops. A gate that signalled at role
        // ENTRY (before any mutation) or a single shared gate for all roles could not
        // establish this.
        const int roleCount = 4;
        const int sourceAlternatorRole = 0;
        const int targetReloaderRole = 1;
        const int availableChurnRole = 2;
        const int subAgentChurnRole = 3;
        // Un-handshaked back-to-back source flips per cycle. These preserve the genuinely
        // RACING transition the torn-source mutant depends on; the per-state handshake
        // gates only the advance past a published state, never this burst.
        const int sourceRacingBurst = 8;

        using var cts = new CancellationTokenSource();
        using var readersInLoop = new CountdownEvent(readers);

        // One gate PER ROLE — opened only after that role's first REAL mutation completes.
        var roleMutated = Enumerable.Range(0, roleCount)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var allRolesMutated = roleMutated.Select(g => g.Task).ToArray();

        // Per-role mutation counters: a stalled or blocked writer cannot silently vacate
        // the proof — every role must be shown to have done real work.
        var roleMutationCounts = new long[roleCount];

        // Evidence that reloads executed DURING the snapshot interval: readers must observe
        // BOTH correlated states. An all-A run (no reload overlap) fails the assertion below.
        var sawVersionA = 0;
        var sawVersionB = 0;

        // ── Per-state rendezvous: every state the assertions require readers to observe
        // is HELD BY CONSTRUCTION until a reader has actually captured it ────────────────
        // Previously the alternator ran `ReloadFrom(stateB)` immediately followed by
        // `ReloadFrom(stateA)` and only yielded once A was restored. On a single-core or
        // unlucky schedule the source was therefore never *seen* at B: every target reload
        // observed A, all four gates and counters still succeeded, and the sawVersionB
        // assertion failed even though production code was correct.
        //
        // Now each publication is a lockstep handshake:
        //   publish X to the source → wait until a reader has captured a target snapshot
        //   whose version is X → only then publish the next state.
        // `publishedSourceVersion` is what the alternator last published;
        // `observedTargetVersion` is what a reader last captured from the TARGET. The
        // alternator advances only when they agree, so the state is observable by
        // construction rather than by timing.
        var publishedSourceVersion = "A";
        var observedTargetVersion = "A";
        // Liveness guard for the handshake: readers decrement this as they exit. The
        // alternator's wait also terminates when no reader remains, so an early reader
        // exit (exception or normal completion) can never strand the source writer.
        var activeReaders = readers;

        // Unreachable in practice (the writers alternate the source continuously); a bound
        // keeps a pathological schedule from spinning forever instead of failing loudly.
        const int maxReaderIterations = 200_000;

        var readerTasks = Enumerable.Range(0, readers).Select(_ => Task.Factory.StartNew(() =>
        {
            var signalled = false;
            try
            {
                // Snapshot #1 — taken BEFORE signalling, so the signal proves in-loop.
                RecordSnapshot();

                readersInLoop.Signal();
                signalled = true;

                // Gate wait WITHOUT the test token (iteration-2 lesson): a late-scheduled
                // writer must never make this wait throw. The outer WaitAsync timeout is
                // what catches a genuine hang.
                Task.WaitAll(allRolesMutated);

                // Every role mutated between snapshot #1 and snapshot #2 for THIS reader.
                var i = 1;
                while (!cts.IsCancellationRequested
                    && i < maxReaderIterations
                    && (i < snapshotsPerReader
                        || Volatile.Read(ref sawVersionA) == 0
                        || Volatile.Read(ref sawVersionB) == 0))
                {
                    RecordSnapshot();
                    i++;
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                cts.Cancel();
            }
            finally
            {
                // Never strand the writers behind the readers' gate.
                if (!signalled)
                    readersInLoop.Signal();
                // Never strand the source alternator behind the per-state handshake: once
                // the last reader has exited, the alternator's acknowledgement wait ends.
                Interlocked.Decrement(ref activeReaders);
            }

            void RecordSnapshot()
            {
                var snapshot = target.CaptureConfigSnapshot();
                if (snapshot.Version == "A")
                    Volatile.Write(ref sawVersionA, 1);
                else if (snapshot.Version == "B")
                    Volatile.Write(ref sawVersionB, 1);

                // Acknowledge to the source alternator what this reader actually captured
                // FROM THE TARGET. The alternator holds each published state until this
                // matches, so no state it publishes can be missed by construction.
                if (snapshot.Version is not null)
                    Volatile.Write(ref observedTargetVersion, snapshot.Version);

                var failure = ValidateSnapshot(snapshot);
                if (failure is not null)
                    failures.Add(failure);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var writerTasks = Enumerable.Range(0, roleCount).Select(role => Task.Factory.StartNew(() =>
        {
            // Alternation cursor for the source writer: each handshaked publication flips it.
            var publishStateB = false;

            try
            {
                // Wait until EVERY reader is inside its snapshot loop, so the first mutation
                // below is guaranteed to land between reader snapshots — real overlap.
                readersInLoop.Wait();

                while (!cts.IsCancellationRequested)
                {
                    var mutated = false;
                    switch (role)
                    {
                        case sourceAlternatorRole:
                            // (i) RACING transition — deliberately NOT handshaked. These
                            // back-to-back reloads flip the source at full speed while the
                            // target reloader concurrently reads it, which is what makes the
                            // torn-source mutant (snapshot-then-replace ordering removed)
                            // fail deterministically. The handshake in (ii) gates only the
                            // ADVANCE past a published state — it never serializes this
                            // racing window away.
                            for (var burst = 0; burst < sourceRacingBurst; burst++)
                            {
                                source.ReloadFrom(stateB);
                                source.ReloadFrom(stateA);
                            }

                            // (ii) HANDSHAKED publication — publish the next state and record
                            // it. The hold that makes it observable happens AFTER this role's
                            // gate opens (see below), so readers waiting on the gate can
                            // always reach their loop and acknowledge: no circular wait.
                            publishStateB = !publishStateB;
                            source.ReloadFrom(publishStateB ? stateB : stateA);
                            Volatile.Write(ref publishedSourceVersion, publishStateB ? "B" : "A");
                            mutated = true;
                            break;
                        case targetReloaderRole:
                            // Reloads the TARGET from the (concurrently alternating) source.
                            // If the snapshot-then-replace ordering is removed, this reads a
                            // TORN source — the correlated validator rejects the result.
                            target.ReloadFrom(source);
                            mutated = true;
                            break;
                        case availableChurnRole:
                            // Atomic add/remove churn on the target's available catalog.
                            if (target.TryAddAvailableModel(new AvailableModelRequest("churn-avail", 5000, "churn-desc", false)))
                            {
                                target.TryRemoveAvailableModel("churn-avail");
                                mutated = true;
                            }
                            break;
                        default:
                            // Atomic add/remove churn on the target's sub-agent catalog.
                            if (target.TryAddSubAgentModel(new SubAgentModelRequest("churn-sub", 7000, ReasoningEffort.Low, "churn-sub-desc", true)))
                            {
                                target.TryRemoveSubAgentModel("churn-sub");
                                mutated = true;
                            }
                            break;
                    }

                    if (mutated)
                    {
                        Interlocked.Increment(ref roleMutationCounts[role]);
                        // Open this role's gate ONLY now — after a real mutation completed.
                        roleMutated[role].TrySetResult();
                    }

                    // HOLD the just-published source state until a reader has actually
                    // captured it from the target. Deliberately placed AFTER the gate opens,
                    // so a reader still blocked on `allRolesMutated` can always progress to
                    // its snapshot loop and acknowledge. The wait also releases on
                    // cancellation or once no reader remains, so an early reader exit can
                    // never strand this writer.
                    if (role == sourceAlternatorRole && mutated)
                        HoldUntilPublishedStateObserved();

                    Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                cts.Cancel();
            }
            finally
            {
                // Never strand the readers behind a role gate that can no longer open.
                roleMutated[role].TrySetResult();
            }

            void HoldUntilPublishedStateObserved()
            {
                var wanted = Volatile.Read(ref publishedSourceVersion);
                while (!cts.IsCancellationRequested
                    && Volatile.Read(ref activeReaders) > 0
                    && !string.Equals(Volatile.Read(ref observedTargetVersion), wanted, StringComparison.Ordinal))
                {
                    // Yield-based spin (no Task.Delay, no timed sleep): the target reloader
                    // and the readers are both running, so this resolves promptly.
                    Thread.Yield();
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try
        {
            await Task.WhenAll(readerTasks).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
        finally
        {
            cts.Cancel();
        }
        await Task.WhenAll(writerTasks).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // (c) Zero exceptions — including any InvalidOperationException from torn enumeration.
        Assert.True(exceptions.IsEmpty,
            "Exceptions observed under concurrency: " +
            string.Join(" | ", exceptions.Select(e => e.GetType().Name + ": " + e.Message)));

        // (a)+(b) Every snapshot matched a correlated complete state exactly.
        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(3)));

        // EVERY writer role performed at least one real mutation — no role may silently
        // vacate the overlap proof by stalling or being descheduled.
        Assert.True(roleMutationCounts[sourceAlternatorRole] > 0,
            "The source-alternation writer never completed a mutation — overlap unproven.");
        Assert.True(roleMutationCounts[targetReloaderRole] > 0,
            "The target-reload writer never completed a mutation — overlap unproven.");
        Assert.True(roleMutationCounts[availableChurnRole] > 0,
            "The available-catalog churn writer never completed a mutation — overlap unproven.");
        Assert.True(roleMutationCounts[subAgentChurnRole] > 0,
            "The sub-agent churn writer never completed a mutation — overlap unproven.");

        // Readers observed BOTH correlated states: the target was genuinely reloaded from the
        // alternating source WHILE the snapshot loops ran. This is SATISFIABLE BY CONSTRUCTION
        // — the alternator holds each published state until a reader acknowledges capturing it
        // — so a legal schedule cannot produce a false failure here; only an implementation
        // that fails to publish the alternating state can trip these.
        Assert.True(Volatile.Read(ref sawVersionA) == 1,
            "Readers never observed correlated state A — reload overlap unproven.");
        Assert.True(Volatile.Read(ref sawVersionB) == 1,
            "Readers never observed correlated state B — reload overlap unproven.");

        // Sanity: the final state is a valid correlated state (A or B base, with or without
        // churn entries — a churn writer may be cancelled mid-cycle).
        var finalFailure = ValidateSnapshot(target.CaptureConfigSnapshot());
        Assert.Null(finalFailure);

        static void SeedStateA(HiveConfigFile config)
        {
            config.Version = "A";
            config.Orchestrator = new OrchestratorConfig { Model = "orch-a", MaxIterations = 1 };
            config.Workers = new Dictionary<string, WorkerConfig> { ["coder"] = new() { Model = "worker-a" } };
            config.TryAddAvailableModel(new AvailableModelRequest("a1", 1000, "desc-a1", true));
            config.TryAddAvailableModel(new AvailableModelRequest("a2", 2000, "desc-a2", null));
            // Duplicate multiplicity is part of the preserved state: an exact duplicate pair.
            config.TryAddAvailableModel(new AvailableModelRequest("a-dup", 3000, "dup-1", false));
            config.Models!.AvailableModels!.Add(MakeEntry("a-dup", 3000, null, "dup-2", true));
            config.TryAddSubAgentModel(new SubAgentModelRequest("sa1", 4000, ReasoningEffort.High, "sub-a1", null));
            config.TryAddSubAgentModel(new SubAgentModelRequest("sa2", 5000, ReasoningEffort.Low, "sub-a2", false));
            config.SetCompactionModel("cm-a");
        }

        static void SeedStateB(HiveConfigFile config)
        {
            config.Version = "B";
            config.Orchestrator = new OrchestratorConfig { Model = "orch-b", MaxIterations = 2 };
            config.Workers = new Dictionary<string, WorkerConfig> { ["coder"] = new() { Model = "worker-b" } };
            config.TryAddAvailableModel(new AvailableModelRequest("b1", 1100, "desc-b1", false));
            config.TryAddAvailableModel(new AvailableModelRequest("b2", 2200, "desc-b2", true));
            config.TryAddAvailableModel(new AvailableModelRequest("b3", 3300, "desc-b3", null));
            config.TryAddSubAgentModel(new SubAgentModelRequest("sb1", 4400, ReasoningEffort.Medium, "sub-b1", true));
            config.SetCompactionModel("cm-b");
        }
    }

    /// <summary>
    /// DETACHED DEEP copy: post-snapshot mutations — including of the caller-held lists used to
    /// construct the config — must not affect an already-captured snapshot, and mutations of the
    /// snapshot must not leak back into the live catalog. <see cref="ModelEntry.Description"/> is
    /// included in the deep copy.
    /// </summary>
    [Fact]
    public void CaptureConfigSnapshot_IsDetachedDeepCopy_IncludingCallerHeldListsAndDescription()
    {
        // Construct the config with caller-held lists (public property setters, checkpoint-1 live).
        var heldAvailable = new List<ModelEntry>
        {
            MakeEntry("held-a", 111, null, "held-desc-a", true),
            MakeEntry("held-b", 222, null, null, null),
        };
        var heldSubAgent = new List<ModelEntry>
        {
            MakeEntry("held-sub", 333, "high", "held-sub-desc", false),
        };
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                CompactionModel = "cm-1",
                AvailableModels = heldAvailable,
                SubAgentModels = heldSubAgent,
            }
        };

        var snapshot = config.CaptureConfigSnapshot();
        var snapshotAvailable = snapshot.Models!.AvailableModels!;
        var snapshotSubAgents = snapshot.Models.SubAgentModels!;

        // Distinct instances: no shared references between live state and snapshot.
        Assert.NotSame(heldAvailable, snapshotAvailable);
        Assert.NotSame(heldSubAgent, snapshotSubAgents);
        Assert.NotSame(heldAvailable[0], snapshotAvailable[0]);
        Assert.NotSame(heldSubAgent[0], snapshotSubAgents[0]);
        Assert.Equal("cm-1", snapshot.Models.CompactionModel);

        // ── Post-snapshot mutations of the caller-held lists must NOT affect the snapshot. ──
        heldAvailable.Add(MakeEntry("held-c", 444));
        heldAvailable.RemoveAt(0);
        heldAvailable[0].Description = "MUTATED-DESC"; // the retained entry's Description
        heldAvailable[0].ContextWindow = 999999;
        heldSubAgent[0].Name = "MUTATED-NAME";
        heldSubAgent[0].Description = "MUTATED-SUB-DESC";
        config.Models!.CompactionModel = "MUTATED-CM";

        Assert.Equal(2, snapshotAvailable.Count);
        Assert.Equal(new EntryTuple("held-a", 111, null, "held-desc-a", true), TupleOf(snapshotAvailable[0]));
        Assert.Equal(new EntryTuple("held-b", 222, null, null, null), TupleOf(snapshotAvailable[1]));
        Assert.Equal("cm-1", snapshot.Models.CompactionModel);
        Assert.Equal(new EntryTuple("held-sub", 333, "high", "held-sub-desc", false), TupleOf(snapshotSubAgents[0]));

        // ── Mutations of the SNAPSHOT must not leak back into the live catalog. ─────────────
        ((List<ModelEntry>)snapshotAvailable).Add(MakeEntry("snapshot-extra"));
        snapshotAvailable[0].Description = "SNAPSHOT-SIDE-MUTATION";
        snapshot.Models.CompactionModel = "SNAPSHOT-CM";

        Assert.Equal(2, config.Models!.AvailableModels!.Count); // 1 + the "held-c" added above
        Assert.Equal(new EntryTuple("held-b", 999999, null, "MUTATED-DESC", null),
            TupleOf(config.Models.AvailableModels[0]));
        Assert.Equal("MUTATED-NAME", config.Models.SubAgentModels![0].Name);
        Assert.Equal("MUTATED-CM", config.GetCompactionModel());
    }

    /// <summary>
    /// Detachment for the list-returning snapshot APIs
    /// (<see cref="HiveConfigFile.GetAvailableModelsSnapshot"/> /
    /// <see cref="HiveConfigFile.GetSubAgentModelsSnapshot"/>): deep copies detached from the
    /// live catalog, with <see cref="ModelEntry.Description"/> carried over.
    /// </summary>
    [Fact]
    public void CatalogSnapshots_AreDetachedDeepCopies_IncludingDescription()
    {
        var config = new HiveConfigFile();
        config.TryAddAvailableModel(new AvailableModelRequest("api-a", 10, "avail-desc", true));
        config.TryAddSubAgentModel(new SubAgentModelRequest("sub-a", 20, ReasoningEffort.Medium, "sub-desc", false));

        var availableSnapshot = config.GetAvailableModelsSnapshot();
        var subAgentSnapshot = config.GetSubAgentModelsSnapshot();

        // Mutate the LIVE catalog after the snapshots were taken.
        config.Models!.AvailableModels![0].Description = "LIVE-MUTATED";
        config.Models.AvailableModels[0].Name = "renamed";
        config.Models.AvailableModels.Add(MakeEntry("extra", 1));

        Assert.NotNull(availableSnapshot);
        Assert.NotNull(subAgentSnapshot);
        var entry = Assert.Single(availableSnapshot!);
        Assert.Equal(new EntryTuple("api-a", 10, null, "avail-desc", true), TupleOf(entry!));
        var subEntry = Assert.Single(subAgentSnapshot!);
        Assert.Equal(new EntryTuple("sub-a", 20, "medium", "sub-desc", false), TupleOf(subEntry!));

        // And mutations of the snapshots do not affect the live catalog either.
        ((List<ModelEntry>)availableSnapshot!).Add(MakeEntry("snapshot-extra"));
        availableSnapshot[0].Description = "SNAPSHOT-MUTATION";
        Assert.Equal(2, config.Models!.AvailableModels!.Count);
        Assert.Equal("LIVE-MUTATED", config.Models.AvailableModels[0].Description);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Atomicity (externally observable)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Many concurrent duplicate <see cref="HiveConfigFile.TryAddAvailableModel"/> calls yield
    /// EXACTLY one success and one stored entry — including case-insensitive duplicates.
    /// Fails if the internal check-and-add is not atomic.
    /// </summary>
    [Fact]
    public async Task TryAddAvailableModel_ConcurrentDuplicates_ExactlyOneSuccessAndOneEntry()
    {
        const int writers = 16;
        var config = new HiveConfigFile();
        using var startGate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(() =>
        {
            startGate.Wait();
            // Odd writers use a different case for the SAME name — still duplicates.
            var name = i % 2 == 0 ? "shared-model" : "SHARED-model";
            return config.TryAddAvailableModel(new AvailableModelRequest(name, 1234, "shared-desc", true));
        })).ToList();

        startGate.Set();
        var outcomes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, outcomes.Count(success => success));

        var snapshot = config.GetAvailableModelsSnapshot();
        Assert.NotNull(snapshot);
        var entry = Assert.Single(snapshot!);
        // The single entry carries whichever case won the race — both spellings are the same
        // case-insensitive name; the assertion is case-insensitive.
        Assert.Equal("shared-model", entry!.Name, ignoreCase: true);
        Assert.Equal(new EntryTuple(entry.Name, 1234, null, "shared-desc", true), TupleOf(entry));
    }

    /// <summary>Same atomicity guarantee for <see cref="HiveConfigFile.TryAddSubAgentModel"/>.</summary>
    [Fact]
    public async Task TryAddSubAgentModel_ConcurrentDuplicates_ExactlyOneSuccessAndOneEntry()
    {
        const int writers = 16;
        var config = new HiveConfigFile();
        using var startGate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(() =>
        {
            startGate.Wait();
            return config.TryAddSubAgentModel(
                new SubAgentModelRequest(i % 2 == 0 ? "sub-dup" : "SUB-DUP", 4321, ReasoningEffort.Medium, "d", null));
        })).ToList();

        startGate.Set();
        var outcomes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, outcomes.Count(success => success));

        var snapshot = config.GetSubAgentModelsSnapshot();
        Assert.NotNull(snapshot);
        var entry = Assert.Single(snapshot!);
        // The single entry carries whichever case won the race — both spellings are the same
        // case-insensitive name; the assertion is case-insensitive.
        Assert.Equal("sub-dup", entry!.Name, ignoreCase: true);
        Assert.Equal(new EntryTuple(entry.Name, 4321, "medium", "d", null), TupleOf(entry));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2b. Atomic CRUD — missing-entry paths return false
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void TryUpdateAvailableModel_MissingEntry_ReturnsFalse()
    {
        var config = new HiveConfigFile();
        config.TryAddAvailableModel(new AvailableModelRequest("present", 1, null, null));

        // No catalog at all.
        Assert.False(new HiveConfigFile().TryUpdateAvailableModel("present", new AvailableModelRequest("x", 2, null, null)));
        // Catalog present, entry missing.
        Assert.False(config.TryUpdateAvailableModel("missing", new AvailableModelRequest("x", 1, null, null)));
        // The present entry is untouched by the failed updates.
        Assert.Equal(new EntryTuple("present", 1, null, null, null),
            TupleOf(config.GetAvailableModelsSnapshot()!.Single()));
    }

    [Fact]
    public void TryRemoveAvailableModel_MissingEntry_ReturnsFalse()
    {
        var config = new HiveConfigFile();
        config.TryAddAvailableModel(new AvailableModelRequest("present", 1, null, null));

        Assert.False(new HiveConfigFile().TryRemoveAvailableModel("present")); // null catalog
        var emptyModels = new HiveConfigFile { Models = new ModelsConfig() };
        Assert.False(emptyModels.TryRemoveAvailableModel("present"));          // null list
        Assert.False(config.TryRemoveAvailableModel("missing"));               // missing entry
        Assert.Equal(new EntryTuple("present", 1, null, null, null),
            TupleOf(config.GetAvailableModelsSnapshot()!.Single()));
    }

    [Fact]
    public void TryUpdateSubAgentModel_MissingEntry_ReturnsFalse()
    {
        var config = new HiveConfigFile();
        config.TryAddSubAgentModel(new SubAgentModelRequest("present", 1, ReasoningEffort.Low, null, null));

        Assert.False(new HiveConfigFile().TryUpdateSubAgentModel("present", new SubAgentModelRequest("x", 1, ReasoningEffort.High, null, null)));
        Assert.False(config.TryUpdateSubAgentModel("missing", new SubAgentModelRequest("x", 1, ReasoningEffort.High, null, null)));
        Assert.Equal(new EntryTuple("present", 1, "low", null, null),
            TupleOf(config.GetSubAgentModelsSnapshot()!.Single()));
    }

    [Fact]
    public void TryRemoveSubAgentModel_MissingEntry_ReturnsFalse()
    {
        var config = new HiveConfigFile();
        config.TryAddSubAgentModel(new SubAgentModelRequest("present", 1, ReasoningEffort.Low, null, null));

        Assert.False(new HiveConfigFile().TryRemoveSubAgentModel("present"));
        Assert.False(config.TryRemoveSubAgentModel("missing"));
        Assert.Equal(new EntryTuple("present", 1, "low", null, null),
            TupleOf(config.GetSubAgentModelsSnapshot()!.Single()));
    }

    /// <summary>
    /// Update semantics: the route name argument identifies the entry and <c>request.Name</c> is
    /// IGNORED (no rename); the first case-insensitive match wins; the available-model update
    /// preserves the entry's existing <see cref="ModelEntry.ReasoningEffort"/>.
    /// </summary>
    [Fact]
    public void TryUpdateAvailableModel_UpdatesFirstCaseInsensitiveMatch_IgnoringRequestName()
    {
        var config = new HiveConfigFile();
        config.TryAddAvailableModel(new AvailableModelRequest("Target", 1, "old-desc", null));
        config.Models!.AvailableModels!.Add(MakeEntry("target", 2, "keep-me", "dup-desc", null));

        Assert.True(config.TryUpdateAvailableModel(
            "TARGET", new AvailableModelRequest("ignored-name", 77, "new-desc", false)));

        var snapshot = config.GetAvailableModelsSnapshot()!;
        Assert.Equal(2, snapshot.Count);
        // FIRST case-insensitive match updated; request.Name ignored (no rename);
        // SupportsVision updated to the request value (false).
        Assert.Equal(new EntryTuple("Target", 77, null, "new-desc", false), TupleOf(snapshot[0]));
        // The duplicate is untouched.
        Assert.Equal(new EntryTuple("target", 2, "keep-me", "dup-desc", null), TupleOf(snapshot[1]));
    }

    [Fact]
    public void TryUpdateSubAgentModel_UpdatesFirstCaseInsensitiveMatch_IgnoringRequestName()
    {
        var config = new HiveConfigFile();
        config.TryAddSubAgentModel(new SubAgentModelRequest("Target", 1, ReasoningEffort.Low, null, null));
        config.Models!.SubAgentModels!.Add(MakeEntry("target", 2, "high", "dup-desc", null));

        Assert.True(config.TryUpdateSubAgentModel(
            "TARGET", new SubAgentModelRequest("ignored-name", 88, ReasoningEffort.None, "new-desc", true)));

        var snapshot = config.GetSubAgentModelsSnapshot()!;
        Assert.Equal(2, snapshot.Count);
        // FIRST case-insensitive match updated; request.Name ignored (no rename);
        // the reasoning effort is updated to the request value (None → "none").
        Assert.Equal(new EntryTuple("Target", 88, "none", "new-desc", true), TupleOf(snapshot[0]));
        Assert.Equal(new EntryTuple("target", 2, "high", "dup-desc", null), TupleOf(snapshot[1]));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. YAML round-trip with SEMANTIC equality
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Round-trips BOTH <c>available_models</c> and <c>sub_agent_models</c> through YAML and
    /// asserts semantic equality: order, duplicates, and every <see cref="ModelEntry"/> value
    /// survive — compared via canonical serializer output, not bytes.
    /// </summary>
    [Fact]
    public void YamlRoundTrip_BothCatalogs_PreserveOrderDuplicatesAndAllEntryValues()
    {
        var config = new HiveConfigFile
        {
            Version = "1.0",
            Models = new ModelsConfig
            {
                CompactionModel = "copilot/gpt-5.4-mini",
                AvailableModels =
                [
                    // Every ModelEntry field populated.
                    new ModelEntry
                    {
                        Name = "copilot/claude-sonnet-4.6",
                        ContextWindow = 200_000,
                        ReasoningEffort = "medium",
                        Description = "Balanced coder model",
                        SupportsVision = true,
                    },
                    // Every nullable field left at its default (null).
                    new ModelEntry { Name = "minimal-model" },
                    // A duplicate of the first entry (order + multiplicity preserved).
                    new ModelEntry
                    {
                        Name = "copilot/claude-sonnet-4.6",
                        ContextWindow = 200_000,
                        ReasoningEffort = "medium",
                        Description = "Balanced coder model",
                        SupportsVision = true,
                    },
                ],
                SubAgentModels =
                [
                    new ModelEntry
                    {
                        Name = "copilot/o4-mini",
                        ContextWindow = 128_000,
                        ReasoningEffort = "extra_high",
                        Description = "Fast sub-agent",
                        SupportsVision = false,
                    },
                    new ModelEntry { Name = "plain-sub", ReasoningEffort = "low" },
                    // Case-variant duplicate (ordinal-ignore-case duplicate storage).
                    new ModelEntry { Name = "PLAIN-SUB", ReasoningEffort = "low" },
                ],
            },
        };

        var yaml = CanonicalYaml(config);
        var roundTripped = YamlDeserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped!.Models);

        // Semantic equality on the models section: order, duplicates, all values, BOTH lists.
        Assert.Equal(config.Models!.CompactionModel, roundTripped.Models!.CompactionModel);
        AssertSameEntries(config.Models.AvailableModels, roundTripped.Models.AvailableModels);
        AssertSameEntries(config.Models.SubAgentModels, roundTripped.Models.SubAgentModels);

        // Canonical serializer equality: serializing the round-tripped config reproduces the
        // same canonical document — semantic YAML equality, independent of whitespace/bytes.
        Assert.Equal(Canonicalize(yaml), CanonicalYaml(roundTripped));
    }

    /// <summary>
    /// The null-vs-empty boundary survives the round trip for BOTH catalog lists and in ALL
    /// four combinations: a serialized empty list stays an empty list (not null), and a null
    /// catalog stays null — for <c>available_models</c> AND <c>sub_agent_models</c>.
    /// </summary>
    [Theory]
    [InlineData(false, true)]   // AvailableModels = [], SubAgentModels = null
    [InlineData(true, false)]   // AvailableModels = null, SubAgentModels = []
    [InlineData(true, true)]    // both null
    [InlineData(false, false)]  // both empty
    public void YamlRoundTrip_NullVersusEmpty_IsPreservedForBothCatalogs(bool availableIsNull, bool subAgentIsNull)
    {
        var config = new HiveConfigFile
        {
            Version = "1.0",
            Models = new ModelsConfig
            {
                CompactionModel = null,          // null default field
                AvailableModels = availableIsNull ? null : [],  // null or EMPTY
                SubAgentModels = subAgentIsNull ? null : [],     // null or EMPTY
            },
        };

        var yaml = CanonicalYaml(config);
        var roundTripped = YamlDeserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped!.Models);
        Assert.Null(roundTripped.Models!.CompactionModel);   // null field preserved

        // Object semantics: the null-vs-[] distinction survives for EACH field.
        if (availableIsNull)
            Assert.Null(roundTripped.Models.AvailableModels);
        else
        {
            Assert.NotNull(roundTripped.Models.AvailableModels);
            Assert.Empty(roundTripped.Models.AvailableModels!);
        }

        if (subAgentIsNull)
            Assert.Null(roundTripped.Models.SubAgentModels);
        else
        {
            Assert.NotNull(roundTripped.Models.SubAgentModels);
            Assert.Empty(roundTripped.Models.SubAgentModels!);
        }

        // Semantic equality via canonical output.
        Assert.Equal(Canonicalize(yaml), CanonicalYaml(roundTripped));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. CompactionModel get/set
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="HiveConfigFile.SetCompactionModel"/>/<see cref="HiveConfigFile.GetCompactionModel"/>
    /// round-trips — this closes the lost-assignment bug — and the ??= new ModelsConfig()
    /// creation behavior is preserved when <see cref="HiveConfigFile.Models"/> was null.
    /// </summary>
    [Fact]
    public void CompactionModel_SetGetRoundTrips_AndCreatesModelsWhenNull()
    {
        var config = new HiveConfigFile();
        Assert.Null(config.Models);
        Assert.Null(config.GetCompactionModel());

        config.SetCompactionModel("copilot/gpt-5.4-mini");
        Assert.Equal("copilot/gpt-5.4-mini", config.GetCompactionModel());
        Assert.NotNull(config.Models);                       // ??= creation behavior preserved
        Assert.Equal("copilot/gpt-5.4-mini", config.Models!.CompactionModel);

        config.SetCompactionModel("second-value");
        Assert.Equal("second-value", config.GetCompactionModel());

        config.SetCompactionModel(null);
        Assert.Null(config.GetCompactionModel());
        Assert.NotNull(config.Models);                       // the ModelsConfig itself is kept
    }

    /// <summary>
    /// The lost-assignment bug is closed at the SERVICE level too:
    /// <see cref="ConfigModelService.SaveModelConfigAsync"/>'s CompactionModel path now goes
    /// through <see cref="HiveConfigFile.SetCompactionModel"/> and the value is observable via
    /// <see cref="HiveConfigFile.GetCompactionModel"/> afterwards.
    /// </summary>
    [Fact]
    public async Task SaveModelConfigAsync_CompactionModel_IsAssignedAndReadable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-catalogtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var config = new HiveConfigFile();
            var repo = new FakeConfigRepoManager("https://example.com/config.git", tempDir);
            var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
            var update = new ModelConfigUpdate(null, null, null, null, "cm-from-service");

            await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

            Assert.Equal("cm-from-service", config.GetCompactionModel());
            Assert.Equal("cm-from-service", config.Models!.CompactionModel);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. Self-reload (no deadlock)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/> from an instance onto ITSELF
    /// completes without deadlock (the private monitor is reentrant) and preserves the state.
    /// Run with a timeout so a deadlock regression FAILS instead of hanging the suite.
    /// </summary>
    [Fact]
    public async Task ReloadFrom_SelfReload_CompletesWithoutDeadlock()
    {
        var config = new HiveConfigFile();
        config.TryAddAvailableModel(new AvailableModelRequest("self-a", 10, "d", true));
        config.TryAddSubAgentModel(new SubAgentModelRequest("self-sub", 20, ReasoningEffort.High, "sd", null));
        config.SetCompactionModel("self-cm");
        var before = config.CaptureConfigSnapshot();

        await Task.Run(() => config.ReloadFrom(config), TestContext.Current.CancellationToken);

        // The state is preserved (deep-equal to the pre-reload snapshot).
        Assert.Equal(before.Version, config.Version);
        AssertSameModels(before.Models, config.CaptureConfigSnapshot().Models);
        Assert.Equal("self-cm", config.GetCompactionModel());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. IsConfigured preservation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/> preserves the TARGET's
    /// <see cref="HiveConfigFile.IsConfigured"/> value — in both directions — while everything
    /// else is replaced from the source.
    /// </summary>
    [Fact]
    public void ReloadFrom_PreservesTargetIsConfigured()
    {
        // true target, false source → stays true.
        var configuredTarget = new HiveConfigFile { Version = "old" };
        configuredTarget.IsConfigured = true;
        var source = new HiveConfigFile { Version = "new" };
        source.TryAddAvailableModel(new AvailableModelRequest("src-model", 1, null, null));

        configuredTarget.ReloadFrom(source);

        Assert.True(configuredTarget.IsConfigured);
        Assert.Equal("new", configuredTarget.Version);
        Assert.Equal(new EntryTuple("src-model", 1, null, null, null),
            TupleOf(configuredTarget.GetAvailableModelsSnapshot()!.Single()));

        // false target, true source → stays false.
        var plainTarget = new HiveConfigFile { Version = "old" };
        var configuredSource = new HiveConfigFile { Version = "new" };
        configuredSource.IsConfigured = true;

        plainTarget.ReloadFrom(configuredSource);

        Assert.False(plainTarget.IsConfigured);
        Assert.Equal("new", plainTarget.Version);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ReloadFrom: snapshot-then-replace ordering (removal-proof)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Removal-proof for the snapshot-then-replace ordering: if
    /// <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/> stopped capturing one detached
    /// snapshot (or stopped deep-copying) and instead shared the source's live references, these
    /// post-reload source mutations would leak into the target.
    /// </summary>
    [Fact]
    public void ReloadFrom_PostReloadSourceMutations_DoNotAffectTarget()
    {
        var source = new HiveConfigFile();
        source.TryAddAvailableModel(new AvailableModelRequest("orig-a", 100, "orig-desc", true));
        source.TryAddSubAgentModel(new SubAgentModelRequest("orig-sub", 200, ReasoningEffort.Medium, "orig-sub-desc", null));
        source.SetCompactionModel("orig-cm");

        var target = new HiveConfigFile();
        target.ReloadFrom(source);

        // Mutate the source AFTER the reload — through BOTH the locked API and direct mutations.
        source.TryAddAvailableModel(new AvailableModelRequest("post-reload-extra", 300, null, null));
        source.Models!.AvailableModels![0].Name = "MUTATED";
        source.Models.AvailableModels[0].Description = "MUTATED-DESC";
        source.Models.SubAgentModels![0].ContextWindow = -5;
        source.SetCompactionModel("mutated-cm");

        // The target is unaffected: it holds its own deep copy of the pre-reload state.
        var targetAvailable = target.GetAvailableModelsSnapshot()!;
        var entry = Assert.Single(targetAvailable);
        Assert.Equal(new EntryTuple("orig-a", 100, null, "orig-desc", true), TupleOf(entry));
        var targetSub = target.GetSubAgentModelsSnapshot()!;
        var subEntry = Assert.Single(targetSub);
        Assert.Equal(new EntryTuple("orig-sub", 200, "medium", "orig-sub-desc", null), TupleOf(subEntry));
        Assert.Equal("orig-cm", target.GetCompactionModel());
    }

    /// <summary>
    /// ReloadFrom replaces ALL top-level properties from the source snapshot — including
    /// workers, repositories, orchestrator and composer — and the target's old collections are
    /// replaced with NEW instances (callers holding the singleton see the update immediately).
    /// </summary>
    [Fact]
    public void ReloadFrom_ReplacesAllTopLevelProperties_WithNewInstances()
    {
        var source = new HiveConfigFile
        {
            Version = "2.0",
            Repositories = [new RepositoryConfig { Name = "RepoA", Url = "https://example.com/a", DefaultBranch = "trunk" }],
            Workers = new Dictionary<string, WorkerConfig> { ["coder"] = new() { Model = "worker-model", ContextWindow = 64000 } },
            Orchestrator = new OrchestratorConfig { Model = "orch-model", MaxIterations = 7 },
            Composer = new ComposerConfig { Model = "composer-model", MaxSteps = 9 },
        };
        source.TryAddAvailableModel(new AvailableModelRequest("snap-a", 1, "d", null));

        var target = new HiveConfigFile
        {
            Version = "1.0",
            Repositories = [new RepositoryConfig { Name = "Old", Url = "https://example.com/old", DefaultBranch = "main" }],
            Workers = new Dictionary<string, WorkerConfig> { ["coder"] = new() { Model = "old-worker" } },
            Orchestrator = new OrchestratorConfig { Model = "old-orch", MaxIterations = 1 },
        };

        target.ReloadFrom(source);

        Assert.Equal("2.0", target.Version);
        var repo = Assert.Single(target.Repositories);
        Assert.Equal("RepoA", repo.Name);
        Assert.Equal("trunk", repo.DefaultBranch);
        Assert.Equal("worker-model", target.Workers["coder"].Model);
        Assert.Equal(64000, target.Workers["coder"].ContextWindow);
        Assert.Equal("orch-model", target.Orchestrator.Model);
        Assert.Equal(7, target.Orchestrator.MaxIterations);
        Assert.NotNull(target.Composer);
        Assert.Equal("composer-model", target.Composer!.Model);
        Assert.Equal("snap-a", target.GetAvailableModelsSnapshot()!.Single().Name);

        // Wholesale replacement: no shared references remain with the source.
        Assert.NotSame(source.Repositories, target.Repositories);
        Assert.NotSame(source.Workers, target.Workers);
        Assert.NotSame(source.Models, target.Models);
        Assert.NotSame(source.Workers["coder"], target.Workers["coder"]);

        // Mutating the source afterwards does not affect the target (detached snapshot).
        source.Workers["coder"].Model = "POST-RELOAD";
        source.Orchestrator.MaxIterations = 999;
        Assert.Equal("worker-model", target.Workers["coder"].Model);
        Assert.Equal(7, target.Orchestrator.MaxIterations);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SetSubAgentModelReasoningEfforts semantics
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Semantics: matching is case-insensitive and unknown names are ignored. The null-VALUE
    /// no-op case is covered by <see cref="SetSubAgentModelReasoningEfforts_NullValueForKnownName_IsNoOp"/> and by the
    /// <see cref="SetSubAgentModelReasoningEfforts_NullValueForKnownName_IsNoOp"/> and by the
    /// side-by-side handler-equivalence test (which carries a null value in its input).
    /// </summary>
    [Fact]
    public void SetSubAgentModelReasoningEfforts_CaseInsensitive_UnknownNamesIgnored()
    {
        var config = new HiveConfigFile();
        config.TryAddSubAgentModel(new SubAgentModelRequest("model-a", 1, ReasoningEffort.Low, null, null));
        config.TryAddSubAgentModel(new SubAgentModelRequest("model-b", 2, ReasoningEffort.Medium, null, null));

        config.SetSubAgentModelReasoningEfforts(new Dictionary<string, ReasoningEffort?>
        {
            ["MODEL-A"] = ReasoningEffort.ExtraHigh,   // case-insensitive name match
            ["unknown-name"] = ReasoningEffort.High,   // unknown names are ignored
        });

        var snapshot = config.GetSubAgentModelsSnapshot()!;
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(new EntryTuple("model-a", 1, "extra_high", null, null), TupleOf(snapshot[0]));
        Assert.Equal(new EntryTuple("model-b", 2, "medium", null, null), TupleOf(snapshot[1]));
    }

    [Fact]
    public void SetSubAgentModelReasoningEfforts_NullValueForKnownName_IsNoOp()
    {
        var config = new HiveConfigFile();
        config.TryAddSubAgentModel(new SubAgentModelRequest("known", 1, ReasoningEffort.Low, null, null));

        // A present key with a null value: the entry's existing effort must survive untouched.
        var efforts = new Dictionary<string, ReasoningEffort?>();
        efforts.Add("known", null);          // present key, null value
        efforts.Add("other", ReasoningEffort.High); // unknown name ignored

        config.SetSubAgentModelReasoningEfforts(efforts);

        Assert.Equal(new EntryTuple("known", 1, "low", null, null),
            TupleOf(config.GetSubAgentModelsSnapshot()!.Single()));
    }

    [Fact]
    public void SetSubAgentModelReasoningEfforts_NullArgumentsAreNoOps()
    {
        var config = new HiveConfigFile();
        config.TryAddSubAgentModel(new SubAgentModelRequest("entry", 1, ReasoningEffort.Low, null, null));

        config.SetSubAgentModelReasoningEfforts(null!);   // null dictionary
        Assert.Equal(new EntryTuple("entry", 1, "low", null, null),
            TupleOf(config.GetSubAgentModelsSnapshot()!.Single()));

        var noModels = new HiveConfigFile();
        noModels.SetSubAgentModelReasoningEfforts(        // null catalog
            new Dictionary<string, ReasoningEffort?> { ["entry"] = ReasoningEffort.High });
        Assert.Null(noModels.Models);
    }

    /// <summary>
    /// <see cref="HiveConfigFile.SetSubAgentModelReasoningEfforts"/> produces IDENTICAL outcomes
    /// to the current <see cref="ConfigModelService.SaveModelConfigAsync"/> sub-agent reasoning
    /// path for the same inputs (null-value no-op, case-insensitive matching, unknown names
    /// ignored) — verified side by side on two identically seeded configs.
    /// </summary>
    [Fact]
    public async Task SetSubAgentModelReasoningEfforts_MatchesSaveModelConfigAsyncOutcomes()
    {
        var efforts = new Dictionary<string, ReasoningEffort?>
        {
            ["SUB-A"] = ReasoningEffort.High,      // case-insensitive match
            ["unknown"] = ReasoningEffort.Low,     // unknown name ignored
        };
        efforts.Add("sub-b", null);                // null value → no-op

        // ── Path 1: the new locked API. ──────────────────────────────────────
        var apiConfig = new HiveConfigFile();
        SeedSubAgents(apiConfig);
        apiConfig.SetSubAgentModelReasoningEfforts(efforts);

        // ── Path 2: the current SaveModelConfigAsync handler. ────────────────
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-catalogtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var svcConfig = new HiveConfigFile();
            SeedSubAgents(svcConfig);
            var repo = new FakeConfigRepoManager("https://example.com/config.git", tempDir);
            var svc = new ConfigModelService(svcConfig, repo, NullLogger<ConfigModelService>.Instance);
            await svc.SaveModelConfigAsync(
                new ModelConfigUpdate(null, null, null, null, null, SubAgentModelReasoning: efforts),
                TestContext.Current.CancellationToken);

            AssertSameEntries(
                svcConfig.GetSubAgentModelsSnapshot(),
                apiConfig.GetSubAgentModelsSnapshot());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }

        static void SeedSubAgents(HiveConfigFile config)
        {
            config.TryAddSubAgentModel(new SubAgentModelRequest("sub-a", 10, ReasoningEffort.Low, null, null));
            config.TryAddSubAgentModel(new SubAgentModelRequest("sub-b", 20, ReasoningEffort.Medium, null, null));
            config.TryAddSubAgentModel(new SubAgentModelRequest("sub-c", 30, null, null, null));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Snapshot: runtime-null tolerance
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="HiveConfigFile.CaptureConfigSnapshot"/> tolerates runtime nulls in ALL
    /// YAML-bound collections — a null collection stays null in the snapshot and nothing throws
    /// (including the nullable catalog lists and the top-level sections).
    /// </summary>
    [Fact]
    public void CaptureConfigSnapshot_RuntimeNullCollections_StayNullAndDoNotThrow()
    {
        var config = new HiveConfigFile
        {
            Version = "1.0",
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                CompactionModel = null,
                AvailableModels = null,           // null catalog lists stay null
                SubAgentModels = null,
            },
        };
        // A null Packages list inside a PublishNuGet section (runtime null despite the
        // non-nullable initializer).
        config.Repositories = new List<RepositoryConfig>
        {
            new() { Name = "r", Url = "u", DefaultBranch = "b", PublishNuGet = new NuGetPublishConfig { Packages = null! } },
        };

        var snapshot = config.CaptureConfigSnapshot();   // must not throw

        Assert.NotNull(snapshot.Repositories);
        var repo = Assert.Single(snapshot.Repositories!);
        Assert.Null(repo.PublishNuGet!.Packages);        // null stays null
        Assert.NotNull(snapshot.Models);
        Assert.Null(snapshot.Models!.AvailableModels);
        Assert.Null(snapshot.Models.SubAgentModels);

        // Round two: all-null everything, including Models itself.
        var bare = new HiveConfigFile { Models = null, Composer = null };
        bare.Orchestrator = null!;
        bare.Workers = null!;
        bare.Repositories = null!;
        var bareSnapshot = bare.CaptureConfigSnapshot();
        Assert.Null(bareSnapshot.Models);
        Assert.Null(bareSnapshot.Composer);
        Assert.Null(bareSnapshot.Orchestrator);
        Assert.Null(bareSnapshot.Workers);
        Assert.Null(bareSnapshot.Repositories);
    }

    /// <summary>
    /// ReloadFrom of a source whose runtime collections are null must not throw and must leave
    /// the target with null sections — the snapshot tolerates runtime nulls end to end.
    /// </summary>
    [Fact]
    public void ReloadFrom_RuntimeNullSource_DoesNotThrow()
    {
        var source = new HiveConfigFile { Models = null, Composer = null };
        source.Orchestrator = null!;
        source.Workers = null!;
        source.Repositories = null!;

        var target = new HiveConfigFile { Models = new ModelsConfig() };

        target.ReloadFrom(source);   // must not throw

        Assert.Null(target.Models);
        Assert.Null(target.Orchestrator);
        Assert.Null(target.Workers);
        Assert.Null(target.Repositories);
    }
}