using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

using CopilotHive.Configuration;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CopilotHive.Tests;

/// <summary>
/// Catalog-safety tests for <see cref="HiveConfigFile"/>: the atomic catalog API,
/// <see cref="HiveConfigFile.CaptureConfigSnapshot"/>/<c>HiveConfigSnapshot</c>, the CompactionModel
/// get/set APIs, the rewritten <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/>, and the
/// public ownership boundary at <see cref="HiveConfigFile.Models"/> (synchronized cloning
/// getter/setter).
/// <para>
/// SCOPE: the lock coordinates <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/>, the
/// synchronized APIs, AND the <see cref="HiveConfigFile.Models"/> getter/setter — the Models
/// subtree is fully detached from external callers (returned DTOs and retained inputs are
/// clones), so direct list mutations through a returned DTO cannot occur at all.
/// </para>
/// <para>
/// Removal-proofing: the concurrency/atomicity tests fail if the lock is removed from
/// <c>TryAddAvailableModel</c>/<see cref="HiveConfigFile.CaptureConfigSnapshot"/>; the
/// post-reload source-mutation test fails if the snapshot-then-replace ordering (or the deep
/// copy) is removed from <see cref="HiveConfigFile.ReloadFrom(HiveConfigFile)"/>; the lock-
/// participation tests fail if the Models getter/setter stop acquiring <c>_catalogLock</c>; the
/// detachment tests fail if the Models getter/setter stop cloning.
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
            // Seed via local capture + whole-property reassign (fixture-seeding migration).
            var models = config.Models;
            models!.AvailableModels!.Add(MakeEntry("a-dup", 3000, null, "dup-2", true));
            config.Models = models;
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
    /// DETACHED DEEP copy: inputs and getter results are detached from the owner, and
    /// post-snapshot mutations of the caller-held lists used to construct the config must not
    /// affect an already-captured snapshot, and mutations of the snapshot must not leak back into
    /// the live catalog. <see cref="ModelEntry.Description"/> is included in the deep copy.
    /// </summary>
    [Fact]
    public void CaptureConfigSnapshot_IsDetachedDeepCopy_IncludingCallerHeldListsAndDescription()
    {
        // Construct the config with caller-held lists (the setter clones; the input stays
        // caller-owned and mutable as a standalone DTO).
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

        // ── Setter-side detachment: mutating the RETAINED input does not change the owner. ──
        heldAvailable.Add(MakeEntry("input-c", 555));
        heldSubAgent[0].ContextWindow = 777;
        var ownerAfterInputAttack = config.GetAvailableModelsSnapshot();
        Assert.NotNull(ownerAfterInputAttack);
        Assert.Equal(2, ownerAfterInputAttack!.Count);
        Assert.Equal(new EntryTuple("held-a", 111, null, "held-desc-a", true), TupleOf(ownerAfterInputAttack[0]));
        Assert.Equal(new EntryTuple("held-b", 222, null, null, null), TupleOf(ownerAfterInputAttack[1]));
        Assert.Equal(new EntryTuple("held-sub", 333, "high", "held-sub-desc", false),
            TupleOf(Assert.Single(config.GetSubAgentModelsSnapshot()!)));

        var snapshot = config.CaptureConfigSnapshot();
        var snapshotAvailable = snapshot.Models!.AvailableModels!;
        var snapshotSubAgents = snapshot.Models.SubAgentModels!;

        // Distinct instances: no shared references between live state and snapshot.
        Assert.NotSame(heldAvailable, snapshotAvailable);
        Assert.NotSame(heldSubAgent, snapshotSubAgents);
        Assert.NotSame(heldAvailable[0], snapshotAvailable[0]);
        Assert.NotSame(heldSubAgent[0], snapshotSubAgents[0]);
        Assert.Equal("cm-1", snapshot.Models.CompactionModel);

        // ── Getter-side compaction attack: a Models DTO from the getter is detached. ──────
        var fromGetter = config.Models;
        Assert.NotNull(fromGetter);
        fromGetter!.CompactionModel = "GETTER-ATTACK";
        fromGetter.AvailableModels!.Add(MakeEntry("getter-attack", 1));
        fromGetter.AvailableModels[0].Description = "GETTER-ENTRY-ATTACK";
        // Direct getter-derived CURATED list/entry attack (the curated half of the boundary).
        var fromGetterCurated = fromGetter.SubAgentModels!;
        fromGetterCurated.Add(MakeEntry("getter-curated-attack", 7, "none"));
        fromGetterCurated[0].Name = "GETTER-CURATED-NAME";
        fromGetterCurated[0].ContextWindow = -321;
        Assert.NotSame(fromGetter, config.Models);   // fresh clone each read

        // Fresh owner reads: ORIGINAL values on BOTH catalogs, untouched by the getter attacks.
        var ownerAfterGetterAttack = config.GetAvailableModelsSnapshot();
        Assert.NotNull(ownerAfterGetterAttack);
        Assert.Equal(2, ownerAfterGetterAttack!.Count);
        Assert.Equal(new EntryTuple("held-a", 111, null, "held-desc-a", true),
            TupleOf(ownerAfterGetterAttack[0]));
        Assert.Equal(new EntryTuple("held-b", 222, null, null, null),
            TupleOf(ownerAfterGetterAttack[1]));
        Assert.Equal(new EntryTuple("held-sub", 333, "high", "held-sub-desc", false),
            TupleOf(Assert.Single(config.GetSubAgentModelsSnapshot()!)));
        Assert.Equal("cm-1", config.GetCompactionModel());

        // ── Post-snapshot mutations of the caller-held lists must NOT affect the snapshot. ──
        heldAvailable.Add(MakeEntry("held-c", 444));
        heldAvailable.RemoveAt(0);
        heldAvailable[0].Description = "MUTATED-DESC"; // the retained entry's Description
        heldAvailable[0].ContextWindow = 999999;
        heldSubAgent[0].Name = "MUTATED-NAME";
        heldSubAgent[0].Description = "MUTATED-SUB-DESC";

        Assert.Equal(2, snapshotAvailable.Count);
        Assert.Equal(new EntryTuple("held-a", 111, null, "held-desc-a", true), TupleOf(snapshotAvailable[0]));
        Assert.Equal(new EntryTuple("held-b", 222, null, null, null), TupleOf(snapshotAvailable[1]));
        Assert.Equal("cm-1", snapshot.Models.CompactionModel);
        Assert.Equal(new EntryTuple("held-sub", 333, "high", "held-sub-desc", false), TupleOf(snapshotSubAgents[0]));

        // ── Real synchronized owner mutations, BEFORE any generation replacement. ──────────
        // Positive: the owner's fresh source state really changed; the original snapshot and the
        // getter-derived DTO stay frozen.
        Assert.True(config.TryUpdateAvailableModel(
            "held-a", new AvailableModelRequest("held-a", 321, "OWNER-MUTATED", false)));
        config.SetCompactionModel("OWNER-CM");

        var ownerAfterRealMutation = config.GetAvailableModelsSnapshot();
        Assert.NotNull(ownerAfterRealMutation);
        Assert.Equal(2, ownerAfterRealMutation!.Count);
        Assert.Equal(new EntryTuple("held-a", 321, null, "OWNER-MUTATED", false),
            TupleOf(ownerAfterRealMutation[0]));
        Assert.Equal("OWNER-CM", config.GetCompactionModel());

        // The captured snapshot is frozen at its pre-mutation generation.
        Assert.Equal(2, snapshotAvailable.Count);
        Assert.Equal(new EntryTuple("held-a", 111, null, "held-desc-a", true), TupleOf(snapshotAvailable[0]));
        Assert.Equal("cm-1", snapshot.Models.CompactionModel);
        // The earlier getter-derived DTO is likewise frozen.
        Assert.Equal("GETTER-ATTACK", fromGetter.CompactionModel);
        Assert.Equal(3, fromGetter.AvailableModels!.Count);

        // ── Mutations of the SNAPSHOT must not leak back into the live catalog. ─────────────
        ((List<ModelEntry>)snapshotAvailable).Add(MakeEntry("snapshot-extra"));
        snapshotAvailable[0].Description = "SNAPSHOT-SIDE-MUTATION";
        snapshot.Models.CompactionModel = "SNAPSHOT-CM";

        // The owner is unchanged by the snapshot-side attacks (fresh authoritative reads).
        var ownerAfterSnapshotAttack = config.GetAvailableModelsSnapshot();
        Assert.NotNull(ownerAfterSnapshotAttack);
        Assert.Equal(2, ownerAfterSnapshotAttack!.Count);
        Assert.Equal(new EntryTuple("held-a", 321, null, "OWNER-MUTATED", false),
            TupleOf(ownerAfterSnapshotAttack[0]));
        Assert.Equal("OWNER-CM", config.GetCompactionModel());
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
        // In-place alias probe: the description change and the added entry go through the
        // owner's synchronized APIs, so they hit the ACTUAL storage under either Models
        // property contract. The update preserves the entry's other stored values.
        Assert.True(config.TryUpdateAvailableModel("api-a", new AvailableModelRequest("api-a", 10, "LIVE-MUTATED", true)));
        Assert.True(config.TryAddAvailableModel(new AvailableModelRequest("extra", 1)));

        // Positive control: the owner really changed, checked before the frozen snapshots.
        var liveAfterUpdate = config.GetAvailableModelsSnapshot();
        Assert.NotNull(liveAfterUpdate);
        Assert.Equal(2, liveAfterUpdate!.Count);
        Assert.Equal(new EntryTuple("api-a", 10, null, "LIVE-MUTATED", true), TupleOf(liveAfterUpdate[0]));
        Assert.Equal("extra", liveAfterUpdate[1].Name);

        // Pre-replacement isolation checkpoint: with ONLY the in-place owner edits applied (no
        // Models reassignment yet), the previously returned snapshots must still be frozen at
        // the values captured before the mutations.
        Assert.NotNull(availableSnapshot);
        Assert.NotNull(subAgentSnapshot);
        Assert.Equal(new EntryTuple("api-a", 10, null, "avail-desc", true),
            TupleOf(Assert.Single(availableSnapshot!)));
        Assert.Equal(new EntryTuple("sub-a", 20, "medium", "sub-desc", false),
            TupleOf(Assert.Single(subAgentSnapshot!)));

        // Generation-replacement probe: the rename is not expressible through the update API
        // (which never renames), so capture Models, edit the local, and reassign the whole
        // property. This runs AFTER the complete in-place phase above (owner mutation + fresh
        // owner reads + isolation checkpoint) and complements it.
        var liveModels = config.Models;
        liveModels!.AvailableModels![0].Name = "renamed";
        config.Models = liveModels;
        Assert.Equal("renamed", config.GetAvailableModelsSnapshot()![0].Name);

        Assert.NotNull(availableSnapshot);
        Assert.NotNull(subAgentSnapshot);
        var entry = Assert.Single(availableSnapshot!);
        Assert.Equal(new EntryTuple("api-a", 10, null, "avail-desc", true), TupleOf(entry!));
        var subEntry = Assert.Single(subAgentSnapshot!);
        Assert.Equal(new EntryTuple("sub-a", 20, "medium", "sub-desc", false), TupleOf(subEntry!));

        // And mutations of the RETURNED snapshot list/entries do not affect the live catalog either.
        ((List<ModelEntry>)availableSnapshot!).Add(MakeEntry("snapshot-extra"));
        availableSnapshot[0].Description = "SNAPSHOT-MUTATION";
        Assert.Equal(2, config.Models!.AvailableModels!.Count);
        Assert.Equal("LIVE-MUTATED", config.Models.AvailableModels[0].Description);

        // Same conclusion read through the owner's accessor: unchanged by the snapshot-side attacks.
        var ownerAfterSnapshotMutation = config.GetAvailableModelsSnapshot();
        Assert.Equal(2, ownerAfterSnapshotMutation!.Count);
        Assert.Equal("LIVE-MUTATED", ownerAfterSnapshotMutation[0].Description);
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
        // Seed via local capture + whole-property reassign (fixture-seeding migration).
        var models = config.Models;
        models!.AvailableModels!.Add(MakeEntry("target", 2, "keep-me", "dup-desc", null));
        config.Models = models;

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
        // Seed via local capture + whole-property reassign (fixture-seeding migration).
        var models = config.Models;
        models!.SubAgentModels!.Add(MakeEntry("target", 2, "high", "dup-desc", null));
        config.Models = models;

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
        Assert.True(source.TryAddAvailableModel(new AvailableModelRequest("post-reload-extra", 300, null, null)));
        // In-place alias probe: the description/context edits go through the owner's
        // synchronized update APIs, so they hit the ACTUAL source storage under either Models
        // property contract. Unrelated stored fields are carried through unchanged.
        Assert.True(source.TryUpdateAvailableModel("orig-a", new AvailableModelRequest("orig-a", 100, "MUTATED-DESC", true)));
        Assert.True(source.TryUpdateSubAgentModel(
            "orig-sub", new SubAgentModelRequest("orig-sub", -5, ReasoningEffort.Medium, "orig-sub-desc", null)));
        source.SetCompactionModel("mutated-cm");

        // Positive control on the SOURCE, before any target assertion.
        var sourceAvailableAfterUpdate = source.GetAvailableModelsSnapshot()!;
        Assert.Equal(2, sourceAvailableAfterUpdate.Count);
        Assert.Equal(new EntryTuple("orig-a", 100, null, "MUTATED-DESC", true), TupleOf(sourceAvailableAfterUpdate[0]));
        Assert.Equal("post-reload-extra", sourceAvailableAfterUpdate[1].Name);
        Assert.Equal(new EntryTuple("orig-sub", -5, "medium", "orig-sub-desc", null),
            TupleOf(Assert.Single(source.GetSubAgentModelsSnapshot()!)));
        Assert.Equal("mutated-cm", source.GetCompactionModel());

        // Pre-replacement isolation checkpoint: with ONLY the in-place owner edits applied (no
        // Models reassignment yet), fresh target reads must still hold the pre-reload state.
        var preReplacementTargetAvailable = target.GetAvailableModelsSnapshot()!;
        Assert.Equal(new EntryTuple("orig-a", 100, null, "orig-desc", true),
            TupleOf(Assert.Single(preReplacementTargetAvailable)));
        Assert.Equal(new EntryTuple("orig-sub", 200, "medium", "orig-sub-desc", null),
            TupleOf(Assert.Single(target.GetSubAgentModelsSnapshot()!)));
        Assert.Equal("orig-cm", target.GetCompactionModel());

        // Generation-replacement probe: the raw rename cannot be expressed by the update API
        // (it never renames), so capture Models, edit the local, and reassign the whole
        // property. This comes AFTER the complete in-place phase above (owner mutation + fresh
        // source reads + isolation checkpoint) and complements it.
        var sourceModels = source.Models;
        sourceModels!.AvailableModels![0].Name = "MUTATED";
        source.Models = sourceModels;
        Assert.Equal("MUTATED", source.GetAvailableModelsSnapshot()![0].Name);

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
    // Preparatory _models backing-field refactor: sequential regression
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression for the internal <c>_models</c> backing field: after a WHOLE
    /// <see cref="HiveConfigFile.Models"/> replacement (and after initialization from
    /// <see cref="HiveConfigFile.Models"/> = <c>null</c>), the synchronized catalog and compaction
    /// operations act on the NEW storage and the owner's authoritative accessors
    /// (<see cref="HiveConfigFile.GetAvailableModelsSnapshot"/>,
    /// <see cref="HiveConfigFile.GetSubAgentModelsSnapshot"/>,
    /// <see cref="HiveConfigFile.GetCompactionModel"/>, <see cref="HiveConfigFile.CaptureConfigSnapshot"/>)
    /// reflect the resulting state, including across a <see cref="HiveConfigFile.ReloadFrom"/>.
    /// <para>
    /// The old temporary live-identity assertions are replaced by DEEP DETACHMENT: the retained
    /// assigned/replacement DTOs stay at their original values while the owner's APIs update the
    /// private <c>_models</c> storage. Returned-object and input attacks plus authoritative owner
    /// checks prove the ownership boundary at <see cref="HiveConfigFile.Models"/>.
    /// </para>
    /// <para>
    /// HELD-GETTER ordering: the <c>getterDto</c> captured from the getter is asserted frozen —
    /// by concrete values, against authoritative fresh owner reads — across the
    /// <see cref="HiveConfigFile.ReloadFrom"/> AND across the SECOND explicit replacement
    /// (<c>config.Models = assigned</c>), which the capture strictly precedes. A post-replacement
    /// mutation of the held DTO verifies it is not re-aliased to the replaced owner. The
    /// ReloadFrom retention proof stands on its own (capture → reload → assert); the
    /// capture → replace → assert ordering below additionally establishes the explicit-replacement
    /// retention proof.
    /// </para>
    /// </summary>
    [Fact]
    public void ModelsReplacementThenSynchronizedOperations_BothCatalogsAndCompaction_UseNewStorage()
    {
        var config = new HiveConfigFile();

        // ── Initialization from Models = null via the synchronized APIs. ──────
        Assert.Null(config.GetCompactionModel());
        Assert.Null(config.GetAvailableModelsSnapshot());
        Assert.Null(config.GetSubAgentModelsSnapshot());

        Assert.True(config.TryAddAvailableModel(new AvailableModelRequest("avail-a", 100, "desc-a", true)));
        Assert.True(config.TryAddSubAgentModel(new SubAgentModelRequest("sub-a", 200, ReasoningEffort.Medium, "sub-desc", null)));
        config.SetCompactionModel("cm-init");

        Assert.Single(config.GetAvailableModelsSnapshot()!);
        Assert.Single(config.GetSubAgentModelsSnapshot()!);
        Assert.Equal("cm-init", config.GetCompactionModel());

        // ── WHOLE Models replacement: the synchronized operations must follow the new storage. ──
        var replacement = new ModelsConfig
        {
            CompactionModel = "cm-new",
            AvailableModels = [MakeEntry("avail-b", 300, null, "desc-b", false)],
            SubAgentModels = [MakeEntry("sub-b", 400, "high", "sub-desc-b", true)],
        };
        config.Models = replacement;

        // Synchronized operations on BOTH catalogs + compaction against the replacement.
        Assert.True(config.TryAddAvailableModel(new AvailableModelRequest("avail-c", 500, null, null)));
        Assert.True(config.TryUpdateSubAgentModel("sub-b", new SubAgentModelRequest("ignored", 450, ReasoningEffort.Low, null, null)));
        config.SetCompactionModel("cm-set-after-replacement");

        // Authoritative owner state reflects the NEW storage only.
        var availableAfter = config.GetAvailableModelsSnapshot()!;
        Assert.Equal(2, availableAfter.Count);
        Assert.Equal(new EntryTuple("avail-b", 300, null, "desc-b", false), TupleOf(availableAfter[0]));
        Assert.Equal(new EntryTuple("avail-c", 500, null, null, null), TupleOf(availableAfter[1]));
        Assert.Equal(new EntryTuple("sub-b", 450, "low", null, null),
            TupleOf(config.GetSubAgentModelsSnapshot()!.Single()));
        Assert.Equal("cm-set-after-replacement", config.GetCompactionModel());

        // And the replacement INPUT is detached from the owner: synchronized operations after
        // the assignment did NOT mutate the retained replacement instance — the owner clones at
        // the setter boundary and mutates only its private storage.
        Assert.Single(replacement.AvailableModels!);
        Assert.Equal(new EntryTuple("avail-b", 300, null, "desc-b", false),
            TupleOf(replacement.AvailableModels![0]));
        Assert.Equal("cm-new", replacement.CompactionModel);

        // Getter-side attack: a DTO from the getter is detached; mutating it does not reach the
        // owner (authoritative fresh read below). Its ATTACKED values are recorded here so the
        // held DTO can be re-checked after the replacement/reload phases below.
        var getterDto = config.Models;
        Assert.NotNull(getterDto);
        var getterDtoCuratedList = getterDto!.SubAgentModels!;
        var getterDtoCuratedEntry = Assert.Single(getterDtoCuratedList);
        getterDto.CompactionModel = "GETTER-ATTACK-CM";
        getterDto.AvailableModels!.Add(MakeEntry("getter-attack", 1));
        // Direct getter-derived CURATED list/entry attack.
        getterDtoCuratedList.Add(MakeEntry("getter-curated-attack", 9, "none"));
        getterDtoCuratedEntry.ReasoningEffort = "GETTER-CURATED-EFFORT";
        Assert.NotSame(getterDto, config.Models);

        // Authoritative owner state after the getter-side attack: unchanged on BOTH catalogs.
        var availableAfterGetterAttack = config.GetAvailableModelsSnapshot()!;
        Assert.Equal(2, availableAfterGetterAttack.Count);
        Assert.Equal(new EntryTuple("avail-b", 300, null, "desc-b", false),
            TupleOf(availableAfterGetterAttack[0]));
        Assert.Equal(new EntryTuple("sub-b", 450, "low", null, null),
            TupleOf(Assert.Single(config.GetSubAgentModelsSnapshot()!)));
        Assert.Equal("cm-set-after-replacement", config.GetCompactionModel());

        // ── ReloadFrom: the destination adopts the snapshot's Models wholesale. ─────────────
        var source = new HiveConfigFile();
        source.TryAddAvailableModel(new AvailableModelRequest("reloaded-avail", 600, null, true));
        source.TryAddSubAgentModel(new SubAgentModelRequest("reloaded-sub", 700, ReasoningEffort.High, null, false));
        source.SetCompactionModel("cm-reloaded");

        config.ReloadFrom(source);

        Assert.Null(config.GetAvailableModelsSnapshot()!.OfType<ModelEntry>()
            .FirstOrDefault(e => e.Name is "avail-b" or "avail-c"));
        Assert.Equal(new EntryTuple("reloaded-avail", 600, null, null, true),
            TupleOf(config.GetAvailableModelsSnapshot()!.Single()));
        Assert.Equal(new EntryTuple("reloaded-sub", 700, "high", null, false),
            TupleOf(config.GetSubAgentModelsSnapshot()!.Single()));
        Assert.Equal("cm-reloaded", config.GetCompactionModel());
        Assert.Equal("cm-reloaded", config.CaptureConfigSnapshot().Models!.CompactionModel);

        // The HELD getter value survived BOTH the whole replacement and the ReloadFrom at its
        // own (attacked) values — no owner generation leaked into it.
        Assert.Equal("GETTER-ATTACK-CM", getterDto.CompactionModel);
        Assert.Equal(3, getterDto.AvailableModels!.Count);
        Assert.Equal(new EntryTuple("avail-b", 300, null, "desc-b", false),
            TupleOf(getterDto.AvailableModels[0]));
        Assert.Equal("getter-attack", getterDto.AvailableModels[2].Name);
        Assert.Equal(2, getterDtoCuratedList.Count);
        Assert.Equal("GETTER-CURATED-EFFORT", getterDtoCuratedEntry.ReasoningEffort);
        Assert.Equal("getter-curated-attack", getterDtoCuratedList[1].Name);
        // ...and the held getter list/entries are not the owner's post-reload generation.
        Assert.DoesNotContain(getterDto.AvailableModels, e => e.Name == "reloaded-avail");
        Assert.DoesNotContain(getterDtoCuratedList, e => e.Name == "reloaded-sub");

        // ── Held getter value across a SECOND explicit replacement (execution-order proof). ──
        // `getterDto` was captured from the getter BEFORE the ReloadFrom above — and therefore
        // also BEFORE the explicit replacement that follows — so retaining it across THAT
        // replacement is established by this ordering: first capture (1394), then replace
        // (below), then assert frozen held values alongside authoritative NEW owner values.
        var assigned = new ModelsConfig { CompactionModel = "identity-cm" };
        config.Models = assigned;
        // The setter clones: the owner's DTO is NOT the caller's instance, and the caller's
        // instance stays at its original value through owner mutations.
        Assert.NotSame(assigned, config.Models);

        // Authoritative fresh reads: the owner now carries the NEW assignment's values...
        Assert.Equal("identity-cm", config.GetCompactionModel());
        Assert.Null(config.GetAvailableModelsSnapshot());
        Assert.Null(config.GetSubAgentModelsSnapshot());
        // ...while the HELD getter DTO stays at its OLD (pre-replacement, attacked) values —
        // concrete values on compaction, both catalogs and their entries.
        Assert.Equal("GETTER-ATTACK-CM", getterDto.CompactionModel);
        Assert.Equal(3, getterDto.AvailableModels!.Count);
        Assert.Equal(new EntryTuple("avail-b", 300, null, "desc-b", false),
            TupleOf(getterDto.AvailableModels[0]));
        Assert.Equal("getter-attack", getterDto.AvailableModels[2].Name);
        Assert.Equal(2, getterDtoCuratedList.Count);
        Assert.Equal(new EntryTuple("sub-b", 450, "GETTER-CURATED-EFFORT", null, null),
            TupleOf(getterDtoCuratedList[0]));
        Assert.Equal("getter-curated-attack", getterDtoCuratedList[1].Name);

        // Post-replacement attack on the HELD DTO: mutating it still leaves the NEW owner
        // storage untouched (the held DTO is detached from the replaced owner, not re-aliased).
        getterDto.CompactionModel = "POST-REPLACEMENT-ATTACK";
        getterDto.AvailableModels!.Add(MakeEntry("post-replacement-attack", 1));
        Assert.Equal("identity-cm", config.GetCompactionModel());
        Assert.Null(config.GetAvailableModelsSnapshot());
        Assert.Null(config.GetSubAgentModelsSnapshot());

        config.SetCompactionModel("identity-final");
        Assert.Equal("identity-cm", assigned.CompactionModel);      // retained input unchanged
        Assert.Equal("identity-final", config.GetCompactionModel());
        Assert.Equal("identity-final", config.CaptureConfigSnapshot().Models!.CompactionModel);

        // Getter-side attack on the newly assigned DTO: detached, owner unaffected.
        var assignedGetterDto = config.Models;
        assignedGetterDto!.CompactionModel = "GETTER-ATTACK";
        Assert.Equal("identity-final", config.GetCompactionModel());

        // Input-side attack: mutating the retained assigned DTO still leaves the owner alone.
        assigned.AvailableModels = [MakeEntry("input-attack", 1)];
        Assert.Null(config.GetAvailableModelsSnapshot());

        // Null reset semantics preserved.
        config.Models = null;
        Assert.Null(config.GetCompactionModel());
        Assert.Null(config.GetAvailableModelsSnapshot());
        Assert.Null(config.GetSubAgentModelsSnapshot());
        Assert.Null(config.Models);
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

    // ═══════════════════════════════════════════════════════════════════════
    // Reader migration (checkpoint 2): paired-catalog and composer locked-pair
    // consistency across ReloadFrom
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The paired-catalog capture in <see cref="HiveConfigFile.GetSubAgentModels"/> (available +
    /// curated in ONE catalog-lock region) yields internally consistent observations across a
    /// <see cref="HiveConfigFile.ReloadFrom"/>: a result taken before the reload stays stable and
    /// self-consistent (merge fields all from the SAME generation), while the next call reflects
    /// the reloaded catalogs — proving the two catalogs cannot mix reload generations.
    /// </summary>
    [Fact]
    public void GetSubAgentModels_ReloadBetweenCalls_GenerationsDoNotMix()
    {
        var target = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        target.ReloadFrom(new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [MakeEntry("m", 100, description: "gen1-desc")],
                SubAgentModels = [MakeEntry("m", null, reasoningEffort: "low")]
            }
        });

        var before = Assert.Single(target.GetSubAgentModels());
        Assert.Equal(100, before.ContextWindow);
        Assert.Equal("gen1-desc", before.Description);
        Assert.Equal("low", before.ReasoningEffort);

        // Reload to a completely different generation (different available + curated catalogs).
        target.ReloadFrom(new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [MakeEntry("m", 200, description: "gen2-desc")],
                SubAgentModels = [MakeEntry("m", 250, reasoningEffort: "high")]
            }
        });

        // The prior result is a frozen generation: unchanged despite the reload.
        Assert.Equal(100, before.ContextWindow);
        Assert.Equal("gen1-desc", before.Description);
        Assert.Equal("low", before.ReasoningEffort);

        // The next observation is fully from the NEW generation — no mixed fields.
        var after = Assert.Single(target.GetSubAgentModels());
        Assert.Equal(250, after.ContextWindow);   // curated value wins (not available's 200)
        Assert.Equal("high", after.ReasoningEffort);
    }

    /// <summary>
    /// <see cref="HiveConfigFile.ResolveComposerDefaultModel"/> reads <c>Composer.Model</c> and the
    /// available catalog in one catalog-lock region: a reload replacing BOTH top-level sections
    /// cannot interleave between the composer-model read and the catalog read. A result captured
    /// before the reload stays null/stable per the old generation, and the next call resolves
    /// against the new generation's composer model and catalog.
    /// </summary>
    [Fact]
    public void ResolveComposerDefaultModel_ReloadOfComposerAndCatalog_PairsStayConsistent()
    {
        var target = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        target.ReloadFrom(new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { Model = "gen1-model" },
            Models = new ModelsConfig { AvailableModels = [MakeEntry("gen1-model")] }
        });

        var before = target.ResolveComposerDefaultModel();
        Assert.Equal("gen1-model", before);

        // New generation: composer model AND catalog change together.
        target.ReloadFrom(new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { Model = "gen2-model" },
            Models = new ModelsConfig { AvailableModels = [MakeEntry("gen2-model")] }
        });

        // The next resolution is consistent with the NEW generation on BOTH sides: the composer
        // model resolves against the reloaded catalog (not the previous one).
        Assert.Equal("gen2-model", target.ResolveComposerDefaultModel());

        // And a mismatched generation would NOT resolve: absent composer model ⇒ null.
        target.ReloadFrom(new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { Model = "gen1-model" },
            Models = new ModelsConfig { AvailableModels = [MakeEntry("gen2-model")] }
        });
        Assert.Null(target.ResolveComposerDefaultModel());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reader migration (checkpoint 2): bounded reader/locked-writer proofs
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The reflection-based handle for the instance's catalog monitor, resolved once and reused.
    /// This is test-only access to a private field; if the field is renamed/removed the test
    /// fails at setup rather than silently passing.
    /// </summary>
    private static readonly FieldInfo CatalogLockField = typeof(HiveConfigFile)
        .GetField("_catalogLock", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("HiveConfigFile._catalogLock field not found — test setup is stale.");

    /// <summary>
    /// Bound for the must-SUCCEED observation that a started thread actually reached the catalog
    /// monitor and is blocked on it. Exceeding the bound FAILS the test (it is not a
    /// "still running after N ms therefore blocked" proof — the observation itself is positive
    /// and state-based, and a reader that completes without contending fails immediately).
    /// </summary>
    private static readonly TimeSpan ContentionObservationBound = TimeSpan.FromSeconds(30);

    /// <summary>Bound for every thread join/drain, including on the failure path.</summary>
    private static readonly TimeSpan ThreadJoinBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A dedicated synchronous thread whose body cannot escape an exception into the runtime:
    /// faults are captured and marshaled back to the test thread via
    /// <see cref="ThrowIfFaulted"/> after a bounded join. Also exposes the bounded, positive
    /// "is actually blocked on a monitor" observation used by the contention tests.
    /// </summary>
    private sealed class CapturedThread
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _done = new(false);
        private Exception? _fault;

        public CapturedThread(string name, Action body)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    // Captured, never rethrown on this thread: an unhandled thread exception
                    // would tear down the whole test process.
                    _fault = ex;
                }
                finally
                {
                    _done.Set();
                }
            })
            {
                IsBackground = true,
                Name = name
            };
        }

        /// <summary>Whether <see cref="Start"/> succeeded (a non-started thread must not be joined).</summary>
        public bool Started { get; private set; }

        public void Start()
        {
            _thread.Start();
            Started = true;
        }

        /// <summary>
        /// Bounded POSITIVE observation of actual monitor contention: waits until the thread is
        /// observed in <see cref="System.Threading.ThreadState.WaitSleepJoin"/> — the state the
        /// CLR assigns to a thread blocked inside <c>Monitor.Enter</c>. The exercised reader/writer
        /// paths contain no other blocking construct, so this state can only mean the thread
        /// reached the catalog monitor. Returns <c>null</c> when contention was observed, otherwise
        /// a failure reason: completing without ever blocking (the lock-removal regression) fails
        /// immediately rather than after a fixed duration.
        /// </summary>
        public string? WaitUntilBlockedOnMonitor(TimeSpan bound)
        {
            var deadline = Environment.TickCount64 + (long)bound.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (_done.IsSet)
                    return $"Thread '{_thread.Name}' COMPLETED while the catalog monitor was held — it never contended for _catalogLock.";

                if ((_thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0)
                    return null;   // observed actually blocked on the monitor

                Thread.Yield();
            }

            return $"Thread '{_thread.Name}' was never observed blocked on the catalog monitor within {bound}.";
        }

        /// <summary>Bounded join; a never-started thread counts as drained.</summary>
        public bool JoinBounded(TimeSpan bound) => !Started || _thread.Join(bound);

        /// <summary>Rethrows a captured body exception on the calling (test) thread, preserving its stack.</summary>
        public void ThrowIfFaulted()
        {
            if (_fault is not null)
                ExceptionDispatchInfo.Capture(_fault).Throw();
        }

        /// <summary>
        /// Releases the completion event only once the body is provably done with it — never
        /// while a thread that timed out its join could still signal a disposed event.
        /// </summary>
        public void DisposeIfCompleted()
        {
            if (_done.IsSet)
                _done.Dispose();
        }
    }

    /// <summary>
    /// Shared bounded contention scenario for a single migrated reader.
    /// <para>
    /// Evidence chain (no fixed-duration non-completion assertion anywhere):
    /// (1) the test thread ENTERS the instance's catalog monitor;
    /// (2) a dedicated synchronous reader thread starts and is POSITIVELY observed blocked on
    /// that monitor (a reader that does not take the lock completes instead and fails the check);
    /// (3) while the reader is provably parked at the monitor, a synchronized writer commits a
    /// distinguishing change BEHIND it (reentrant on the held monitor);
    /// (4) the monitor is released and the reader's own returned value — its post-acquisition
    /// effect — must reflect the change committed behind it, which is only possible if the reader
    /// read the catalog after acquiring the monitor.
    /// </para>
    /// Monitor entry and thread start happen inside the protected region; the monitor is released
    /// and the thread is boundedly joined in <c>finally</c> even when an assertion or setup fails.
    /// </summary>
    private static T AssertReaderBlocksOnCatalogMonitor<T>(
        HiveConfigFile config, Func<T> reader, Action commitBehindBlockedReader)
    {
        var monitor = CatalogLockField.GetValue(config)!;
        Assert.Same(monitor, CatalogLockField.GetValue(config));

        T observed = default!;
        var readerThread = new CapturedThread("catalog-monitor-reader", () => observed = reader());

        string? contentionFailure = null;
        var committed = false;
        var monitorEntered = false;
        bool joined;
        try
        {
            Monitor.Enter(monitor);
            monitorEntered = true;
            readerThread.Start();

            contentionFailure = readerThread.WaitUntilBlockedOnMonitor(ContentionObservationBound);
            if (contentionFailure is null)
            {
                // Committed while the reader is parked at the monitor: the reader can only see
                // this state by acquiring the monitor AFTER this write.
                commitBehindBlockedReader();
                committed = true;
            }
        }
        finally
        {
            if (monitorEntered && Monitor.IsEntered(monitor))
                Monitor.Exit(monitor);

            joined = readerThread.JoinBounded(ThreadJoinBound);
            readerThread.DisposeIfCompleted();
        }

        // Establish termination first, then marshal any body fault BEFORE the contention/value
        // assertions. Otherwise a generic "completed without contending" assertion could mask the
        // reader's real exception instead of reporting it on the test thread.
        Assert.True(joined, "Reader thread did not finish within its join bound after the monitor was released.");
        readerThread.ThrowIfFaulted();
        Assert.True(contentionFailure is null, contentionFailure);
        Assert.True(committed, "The distinguishing write behind the blocked reader was never committed.");
        return observed;
    }

    /// <summary>
    /// Bounded reader/locked-writer proof for <see cref="HiveConfigFile.TryGetContextWindowForModel"/>.
    /// The reader is positively observed BLOCKED on the instance's <c>_catalogLock</c> monitor,
    /// a synchronized writer then commits a new context window behind it, and the reader's own
    /// returned value must be the post-commit one — a post-acquisition observation, not a
    /// fixed-duration non-completion proxy.
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_ReaderWaitsForHeldCatalogMonitor()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [MakeEntry("m", 42)] }
        };

        var observed = AssertReaderBlocksOnCatalogMonitor(
            config,
            () => config.TryGetContextWindowForModel("m"),
            () => Assert.True(config.TryUpdateAvailableModel("m", new AvailableModelRequest("ignored", 99, null, null))));

        // 42 was the value at the moment the reader was started and blocked; 99 was committed
        // behind it. Observing 99 proves the read happened after acquiring the monitor.
        Assert.Equal(99, observed);
    }

    /// <summary>
    /// The <see cref="HiveConfigFile.Models"/> GETTER participates in <c>_catalogLock</c>: a
    /// dedicated thread calling the getter is POSITIVELY observed blocked on the instance's
    /// catalog monitor, a synchronized writer then commits a distinguishing owner change behind
    /// it, and the getter's returned DTO must carry the post-commit value — a post-acquisition
    /// observation, not a fixed-duration non-completion proxy.
    /// </summary>
    [Fact]
    public void ModelsGetter_ReaderWaitsForHeldCatalogMonitor()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                CompactionModel = "pre-cm",
                AvailableModels = [MakeEntry("m", 42)]
            }
        };

        var observed = AssertReaderBlocksOnCatalogMonitor(
            config,
            () => config.Models,   // the getter under test
            () => config.SetCompactionModel("post-cm"));   // distinguishing owner change behind the blocked reader

        // The getter cloned AFTER acquiring the monitor: it carries the post-commit value.
        Assert.NotNull(observed);
        Assert.Equal("post-cm", observed!.CompactionModel);
        Assert.Equal(new EntryTuple("m", 42, null, null, null), TupleOf(Assert.Single(observed.AvailableModels!)));
        // And the owner is consistent with the observed generation.
        Assert.Equal("post-cm", config.GetCompactionModel());
    }

    /// <summary>
    /// The <see cref="HiveConfigFile.Models"/> SETTER participates in <c>_catalogLock</c>: a
    /// dedicated thread running a synchronous assignment is POSITIVELY observed blocked on the
    /// instance's catalog monitor, a distinguishing owner update is made while the monitor is
    /// still held (before the setter can acquire it), and after release/join the setter's own
    /// assignment is authoritative — proving the setter only published after acquiring the
    /// monitor. The supplied input object is NOT concurrently mutated during the setter's
    /// capture (the assignment is the callback; the input is built beforehand).
    /// </summary>
    [Fact]
    public void ModelsSetter_WaitsForHeldCatalogMonitor_PublishesAfterAcquisition()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { CompactionModel = "initial-cm" }
        };

        // The input is complete BEFORE the contention window: the setter clones it under the
        // lock; no concurrent mutation of the input occurs during capture.
        var replacement = new ModelsConfig
        {
            CompactionModel = "setter-cm",
            AvailableModels = [MakeEntry("setter-entry", 7)]
        };

        // The setter as a synchronous Func<bool> completion marker: assignment succeeded.
        bool Setter() { config.Models = replacement; return true; }
        var observed = AssertReaderBlocksOnCatalogMonitor(
            config,
            Setter,   // contention reader: the setter under test
            () => config.SetCompactionModel("held-by-monitor-cm"));   // distinguishing change made WHILE the monitor is held

        // The setter ran to completion (its marker returned true).
        Assert.True(observed, "The Models setter did not complete its assignment.");

        // While the monitor was held, the distinguishing update was visible through the owner's
        // synchronized accessor. The setter's publication happened strictly AFTER that update
        // (it had to acquire the monitor behind it), so the FINAL state is the setter's.
        Assert.Equal("setter-cm", config.GetCompactionModel());
        var stored = Assert.Single(config.GetAvailableModelsSnapshot()!);
        Assert.Equal(new EntryTuple("setter-entry", 7, null, null, null), TupleOf(stored));

        // Value semantics of the setter boundary: the input stays caller-owned and unchanged.
        Assert.Equal("setter-cm", replacement.CompactionModel);
        Assert.Single(replacement.AvailableModels!);
    }

    /// <summary>
    /// Same bounded contention proof for <see cref="HiveConfigFile.GetSubAgentModels"/> on the
    /// AVAILABLE-ONLY fallback path (curated absent): the reader is observed blocked on the
    /// monitor, a synchronized writer commits new entry fields behind it, and the reader's
    /// detached result carries the post-commit values on every field.
    /// </summary>
    [Fact]
    public void GetSubAgentModels_AvailableOnlyFallback_ReaderWaitsForHeldCatalogMonitor()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [MakeEntry("m", 10, description: "pre", supportsVision: true)] }
        };

        var observed = AssertReaderBlocksOnCatalogMonitor(
            config,
            config.GetSubAgentModels,
            () => Assert.True(config.TryUpdateAvailableModel("m", new AvailableModelRequest("ignored", 20, "post", false))));

        var entry = Assert.Single(observed);
        Assert.Equal(new EntryTuple("m", 20, null, "post", false), TupleOf(entry));
    }

    /// <summary>
    /// Same bounded contention proof for <see cref="HiveConfigFile.ResolveComposerDefaultModel"/>:
    /// the composer model is initially ABSENT from the catalog (resolution would be <c>null</c>),
    /// and the entry is added by a synchronized writer only while the reader is provably parked
    /// at the monitor. A non-null resolution is therefore only possible post-acquisition.
    /// </summary>
    [Fact]
    public void ResolveComposerDefaultModel_ReaderWaitsForHeldCatalogMonitor()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { Model = "m" },
            Models = new ModelsConfig { AvailableModels = [MakeEntry("other", 1)] }
        };

        // Pre-condition: before the contention window the composer model does NOT resolve.
        Assert.Null(config.ResolveComposerDefaultModel());

        var observed = AssertReaderBlocksOnCatalogMonitor(
            config,
            config.ResolveComposerDefaultModel,
            () => Assert.True(config.TryAddAvailableModel(new AvailableModelRequest("m", 100, null, null))));

        Assert.Equal("m", observed);
    }

    /// <summary>
    /// Reader AND synchronized writer both gated through the same monitor: both dedicated threads
    /// are POSITIVELY observed blocked on the instance's <c>_catalogLock</c> while the test holds
    /// it, then the monitor is released. The writer's commit succeeds under the lock, and the
    /// reader's result must be an internally consistent WHOLE observation (pre- or post-commit
    /// generation — never torn, never a stale live alias); after the release the two threads race
    /// legitimately, so ordering is not asserted. All monitors released and BOTH threads joined
    /// boundedly in <c>finally</c>, with thread faults marshaled back to the test thread.
    /// </summary>
    [Fact]
    public void GetSubAgentModels_ReaderObservesWriterChangeCommittedUnderMonitor()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [MakeEntry("m", 1)] }
        };

        var monitor = CatalogLockField.GetValue(config)!;

        IReadOnlyList<ModelEntry>? observed = null;
        var writerSucceeded = false;

        var readerThread = new CapturedThread("catalog-monitor-reader", () => observed = config.GetSubAgentModels());
        // The writer MUST run on its own thread: the synchronized APIs re-enter the SAME monitor,
        // so a write from the test thread (which holds it) is reentrant and could not be gated.
        var writerThread = new CapturedThread(
            "catalog-monitor-writer",
            () => writerSucceeded = config.TryUpdateAvailableModel("m", new AvailableModelRequest("ignored", 2, "post", null)));

        string? readerContentionFailure = null;
        string? writerContentionFailure = null;
        var monitorEntered = false;
        bool readerJoined;
        bool writerJoined;
        try
        {
            Monitor.Enter(monitor);
            monitorEntered = true;

            readerThread.Start();
            writerThread.Start();

            // Positive, bounded observation that BOTH threads actually reached the monitor.
            readerContentionFailure = readerThread.WaitUntilBlockedOnMonitor(ContentionObservationBound);
            writerContentionFailure = writerThread.WaitUntilBlockedOnMonitor(ContentionObservationBound);
        }
        finally
        {
            if (monitorEntered && Monitor.IsEntered(monitor))
                Monitor.Exit(monitor);

            // Drain BOTH started threads even when the observations above failed.
            readerJoined = readerThread.JoinBounded(ThreadJoinBound);
            writerJoined = writerThread.JoinBounded(ThreadJoinBound);
            readerThread.DisposeIfCompleted();
            writerThread.DisposeIfCompleted();
        }

        // Establish termination and marshal body faults BEFORE contention assertions so a real
        // reader/writer exception cannot be hidden behind a generic completion-state failure.
        Assert.True(readerJoined, "Reader thread did not finish within its join bound.");
        readerThread.ThrowIfFaulted();
        Assert.True(writerJoined, "Writer thread did not finish within its join bound.");
        writerThread.ThrowIfFaulted();
        Assert.True(readerContentionFailure is null, readerContentionFailure);
        Assert.True(writerContentionFailure is null, writerContentionFailure);

        Assert.True(writerSucceeded, "Synchronized writer failed to update the entry.");

        var entry = Assert.Single(observed!);
        Assert.True(
            new EntryTuple("m", 1, null, null, null).Equals(TupleOf(entry))
            || new EntryTuple("m", 2, null, "post", null).Equals(TupleOf(entry)),
            $"Reader observed a torn/foreign state: {TupleOf(entry)}.");

        // The commit is visible to a subsequent read regardless of which side won the race.
        Assert.Equal(new EntryTuple("m", 2, null, "post", null), TupleOf(Assert.Single(config.GetSubAgentModels())));
    }

    /// <summary>
    /// Supplemental STRESS (not deterministic removal proof): concurrent locked writers
    /// (ReloadFrom + synchronized CRUD) racing dedicated synchronous readers of ALL migrated
    /// readers. Invariants for every observation: (a) zero exceptions, including no
    /// <see cref="InvalidOperationException"/> from live-list enumeration; (b) context-window
    /// lookups resolve only against WHOLE generations (a name resolves ⇒ the lookup finds it in
    /// the same capture); (c) sub-agent results carry coherent merge fields. Bounded joins/drains
    /// with timeouts even on failure.
    /// </summary>
    [Fact]
    public async Task MigratedReaders_ConcurrentLockedWriters_NeverThrowAndNeverTear()
    {
        const int readerThreads = 4;
        const int iterationsPerReader = 300;

        var stateA = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { Model = "shared" },
            Models = new ModelsConfig
            {
                AvailableModels = [MakeEntry("shared", 100, description: "a", supportsVision: true)],
                SubAgentModels = [MakeEntry("shared", null, reasoningEffort: "low")]
            }
        };
        var stateB = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { Model = "other" },
            Models = new ModelsConfig
            {
                AvailableModels = [MakeEntry("other", 200, description: "b", supportsVision: false)],
                SubAgentModels = [MakeEntry("other", null, reasoningEffort: "high")]
            }
        };

        var target = new HiveConfigFile();
        target.ReloadFrom(stateA);
        var source = new HiveConfigFile();
        source.ReloadFrom(stateA);

        var exceptions = new ConcurrentBag<Exception>();
        var failures = new ConcurrentBag<string>();
        using var done = new CountdownEvent(readerThreads + 1);

        // Reader role: exercises every migrated reader against the same instance.
        var readerTasks = Enumerable.Range(0, readerThreads).Select(_ => Task.Factory.StartNew(() =>
        {
            try
            {
                for (var i = 0; i < iterationsPerReader; i++)
                {
                    // Per-call value-domain invariants: each reader call observes ONE whole
                    // generation (A: shared=100/desc-a/vision-true; B: other=200/desc-b/
                    // vision-false) plus the legal transient churn entry (5000, committed under
                    // the lock between the synchronized add and remove). A torn single-snapshot
                    // capture would produce out-of-domain values (e.g. 'shared' resolving with
                    // gen-B's 200 context).
                    var ctxShared = target.TryGetContextWindowForModel("shared");
                    var ctxOther = target.TryGetContextWindowForModel("other");
                    var ctxChurn = target.TryGetContextWindowForModel("churn");
                    var resolvedShared = target.ResolveAvailableModel("shared");
                    var resolvedOther = target.ResolveAvailableModel("other");
                    var composerDefault = target.ResolveComposerDefaultModel();

                    if (ctxShared is { } v && v != 100)
                        failures.Add($"TryGetContextWindowForModel(shared) returned foreign context {v}.");
                    if (ctxOther is { } v2 && v2 != 200)
                        failures.Add($"TryGetContextWindowForModel(other) returned foreign context {v2}.");
                    if (ctxChurn is { } v3 && v3 != 5000)
                        failures.Add($"Churn entry context mutated under lock: {v3}.");
                    if (resolvedShared is not null && resolvedShared != "shared")
                        failures.Add($"ResolveAvailableModel(shared) returned '{resolvedShared}'.");
                    if (resolvedOther is not null && resolvedOther != "other")
                        failures.Add($"ResolveAvailableModel(other) returned '{resolvedOther}'.");
                    // Composer.Model is reloaded as 'shared' or 'other' and resolved against the
                    // SAME generation's catalog (one lock region) — 'churn' can never appear.
                    if (composerDefault is not null && composerDefault != "shared" && composerDefault != "other")
                        failures.Add($"ResolveComposerDefaultModel returned foreign model '{composerDefault}'.");

                    // (c) Coherent merge observations. WITHIN one merged result, a curated entry
                    // inheriting from the available catalog must carry fields from the SAME
                    // generation: a merge of curated gen-A 'shared' against gen-B available (no
                    // 'shared') would yield a null ContextWindow/Description — a mixed-generation
                    // observation that fails below. 'churn' never exists in the curated catalog,
                    // so it can only appear via the available-only fallback path.
                    foreach (var entry in target.GetSubAgentModels())
                    {
                        switch (entry.Name)
                        {
                            case "shared":
                                if (entry.ReasoningEffort != "low")
                                    failures.Add($"Curated 'shared' reasoning mutated: {entry.ReasoningEffort}.");
                                else if (entry.ContextWindow != 100 || entry.Description != "a" || entry.SupportsVision != true)
                                    failures.Add(
                                        $"Merged 'shared' entry mixed generations: ctx={entry.ContextWindow}, desc={entry.Description}, vision={entry.SupportsVision}.");
                                break;
                            case "other":
                                if (entry.ReasoningEffort != "high")
                                    failures.Add($"Curated 'other' reasoning mutated: {entry.ReasoningEffort}.");
                                else if (entry.ContextWindow != 200 || entry.Description != "b" || entry.SupportsVision != false)
                                    failures.Add(
                                        $"Merged 'other' entry mixed generations: ctx={entry.ContextWindow}, desc={entry.Description}, vision={entry.SupportsVision}.");
                                break;
                            case "churn":
                                if (entry.ReasoningEffort != "medium" || entry.ContextWindow is not (null or 5000))
                                    failures.Add(
                                        $"Churn merged entry incoherent: ctx={entry.ContextWindow}, effort={entry.ReasoningEffort}.");
                                break;
                            default:
                                failures.Add($"GetSubAgentModels returned foreign entry '{entry.Name}'.");
                                break;
                        }
                    }

                    // Composer catalog normalization: only whole-generation names (base
                    // generation names plus the legal transient churn entry).
                    foreach (var name in target.GetComposerAvailableModels())
                    {
                        if (name is "shared" or "other" or "churn")
                            continue;
                        failures.Add($"GetComposerAvailableModels returned foreign model '{name}'.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
            finally
            {
                done.Signal();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        // Writer role: alternates the source and reloads the target (locked writer), plus churn
        // via the synchronized CRUD APIs that PRESERVE the two known generations' name sets.
        var writerTask = Task.Factory.StartNew(() =>
        {
            try
            {
                var flip = false;
                for (var i = 0; i < iterationsPerReader; i++)
                {
                    flip = !flip;
                    source.ReloadFrom(flip ? stateB : stateA);
                    target.ReloadFrom(source);

                    // Churn entry via synchronized APIs: add + remove leaves the catalog in one
                    // of the two known states, but exercises the CRUD writers' lock sections.
                    target.TryAddAvailableModel(new AvailableModelRequest("churn", 5000, null, null));
                    target.TryRemoveAvailableModel("churn");
                    target.TryAddSubAgentModel(new SubAgentModelRequest("churn", null, ReasoningEffort.Medium, null, null));
                    target.TryRemoveSubAgentModel("churn");
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
            finally
            {
                done.Signal();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Bounded drain even when a reader fails: the CountdownEvent completes when all roles
        // exit (their finally blocks signal it regardless of exceptions).
        Assert.True(done.Wait(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken),
            "Concurrency test did not drain within its bound.");

        await Task.WhenAll(readerTasks.Append(writerTask))
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(exceptions.IsEmpty,
            "Exceptions under concurrency: " + string.Join(" | ", exceptions.Select(e => e.GetType().Name + ": " + e.Message)));
        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(3)));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Null-placeholder guards: deterministic sequential regression matrix
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Seeds a fixture through a WHOLE <see cref="HiveConfigFile.Models"/> assignment so null
    /// entries, duplicates and raw payloads actually reach owner storage (the setter deep-copies
    /// but preserves null list elements). <paramref name="catalogIsAvailable"/> selects which of
    /// the two catalogs carries the fixture; the other stays null.
    /// </summary>
    private static HiveConfigFile SeedCatalogFixture(
        bool catalogIsAvailable, IReadOnlyList<ModelEntry?> entries)
    {
        var config = new HiveConfigFile();
        var models = new ModelsConfig();
        var list = new List<ModelEntry>();
        foreach (var entry in entries)
            list.Add(entry!);   // null placeholders intentionally stored as null elements
        if (catalogIsAvailable)
            models.AvailableModels = list;
        else
            models.SubAgentModels = list;
        config.Models = models;
        return config;
    }

    private static IReadOnlyList<ModelEntry>? SnapshotOf(HiveConfigFile config, bool catalogIsAvailable) =>
        catalogIsAvailable
            ? config.GetAvailableModelsSnapshot()
            : config.GetSubAgentModelsSnapshot();

    /// <summary>
    /// Asserts ONE slot of a fresh authoritative snapshot. A null <paramref name="expected"/>
    /// uses an explicit <see cref="Assert.Null"/> — tuple projections conflate null placeholders
    /// with non-null all-null-field entries, so null positions must be asserted directly.
    /// </summary>
    private static void AssertSlot(IReadOnlyList<ModelEntry>? snapshot, int index, ModelEntry? expected)
    {
        Assert.NotNull(snapshot);
        var actual = snapshot![index];
        if (expected is null)
            Assert.Null(actual);
        else
            Assert.Equal(TupleOf(expected), TupleOf(actual));
    }

    // ── Distinguishable fixture entries (shared by the theory below) ────────

    /// <summary>First match "Target": distinct metadata; raw reasoning for available updates.</summary>
    private static ModelEntry FirstTarget(bool catalogIsAvailable) => MakeEntry(
        "Target", 1000, catalogIsAvailable ? " raw-high " : "low", "first-target", true);

    /// <summary>
    /// Second case-insensitive duplicate "target": distinct metadata.
    /// </summary>
    private static ModelEntry SecondTarget(bool catalogIsAvailable) => MakeEntry(
        "target", 2000, catalogIsAvailable ? "raw-low" : "high", "second-target", false);

    /// <summary>
    /// A whitespace-bearing name whose TRIMMED form ("target") is distinct from the stored raw
    /// form: proves the CRUD predicates match ordinal-ignore-case WITHOUT trimming. Metadata is
    /// distinct from <see cref="FirstTarget"/>/<see cref="SecondTarget"/>.
    /// </summary>
    private static ModelEntry WhitespaceTarget(bool catalogIsAvailable) => MakeEntry(
        " target ", 1111, catalogIsAvailable ? " ws-raw " : "low", "ws-target", true);

    /// <summary>
    /// A NON-NULL entry whose <see cref="ModelEntry.Name"/> is null: retained as a real entry;
    /// valid-name operations must never match it.
    /// </summary>
    private static ModelEntry NullNameEntry() => new()
    {
        Name = null!,
        ContextWindow = 3000,
        ReasoningEffort = "raw-null-name",
        Description = "null-name-entry",
        SupportsVision = null
    };

    private static ModelEntry[] BaseFixture(bool catalogIsAvailable) =>
    [
        null!,
        FirstTarget(catalogIsAvailable),
        null!,
        SecondTarget(catalogIsAvailable),
        NullNameEntry(),
    ];

    /// <summary>
    /// Deterministic sequential regression matrix for the six catalog CRUD APIs against
    /// null placeholders, seeded via a whole <see cref="HiveConfigFile.Models"/> assignment
    /// (so null entries, duplicates and raw payloads actually reach owner storage). Each
    /// operation uses a FRESH fixture. For the catalog <c>[null, Target, null, target]</c>
    /// (plus a non-null null-Name entry and, for the add case, a trailing null slot) with
    /// distinguishable metadata per entry:
    /// <list type="bullet">
    /// <item>duplicate add fails and the entire list is unchanged;</item>
    /// <item>add of an absent name appends after the existing slots (including trailing null slots);</item>
    /// <item>update using a case-variant name changes only the FIRST matching non-null entry;</item>
    /// <item>remove deletes only that match and preserves the remaining null slots and order;</item>
    /// <item>absent-name update/remove return false with the list unchanged;</item>
    /// <item>all-null lists allow add but reject update/remove;</item>
    /// <item>missing-storage update/remove return false unchanged;</item>
    /// <item>whitespace-bearing raw names match ordinal-ignore-case WITHOUT trimming — the
    /// trimmed spelling neither matches nor duplicates them (add/update/remove), and stored
    /// names stay raw after add and update.</item>
    /// </list>
    /// Every operation asserts its RETURN VALUE and a fresh authoritative snapshot afterwards —
    /// never "no exception" and never retained initializer objects — with explicit
    /// <see cref="Assert.Null"/> at every null slot. Untrimmed names, available raw reasoning,
    /// the ignored <c>request.Name</c> on updates (no rename), unrelated duplicate metadata and
    /// the null-Name entry (retained as a real entry, never matched) are all preserved.
    /// </summary>
    [Theory]
    [InlineData(true)]   // AvailableModels catalog
    [InlineData(false)]  // SubAgentModels catalog
    public void CatalogMutationApis_NullPlaceholders_NeverMatchAndPreserveSlots(bool catalogIsAvailable)
    {
        // ── A. Duplicate add fails and the ENTIRE list is unchanged. ─────────
        {
            var config = SeedCatalogFixture(catalogIsAvailable, BaseFixture(catalogIsAvailable));

            // "TARGET" case-insensitively duplicates BOTH non-null entries; null slots and the
            // null-Name entry must never match.
            var added = catalogIsAvailable
                ? config.TryAddAvailableModel(new AvailableModelRequest("TARGET", 9999, "add-desc", true))
                : config.TryAddSubAgentModel(new SubAgentModelRequest("TARGET", 9999, ReasoningEffort.ExtraHigh, "add-desc", true));
            Assert.False(added);

            var snap = SnapshotOf(config, catalogIsAvailable);
            Assert.Equal(5, snap!.Count);
            AssertSlot(snap, 0, null);
            AssertSlot(snap, 1, FirstTarget(catalogIsAvailable));
            AssertSlot(snap, 2, null);
            AssertSlot(snap, 3, SecondTarget(catalogIsAvailable));
            AssertSlot(snap, 4, NullNameEntry());
        }

        // ── B. Add of an absent name appends after the existing slots, ───────
        //    including the trailing null slot.
        {
            var withTrailingNull = BaseFixture(catalogIsAvailable).Append(null!).ToArray();
            var config = SeedCatalogFixture(catalogIsAvailable, withTrailingNull);

            var added = catalogIsAvailable
                ? config.TryAddAvailableModel(new AvailableModelRequest("absent-model", 42, "added-desc", false))
                : config.TryAddSubAgentModel(new SubAgentModelRequest("absent-model", 42, ReasoningEffort.Medium, "added-desc", false));
            Assert.True(added);

            var snap = SnapshotOf(config, catalogIsAvailable);
            Assert.Equal(7, snap!.Count);
            AssertSlot(snap, 0, null);
            AssertSlot(snap, 1, FirstTarget(catalogIsAvailable));
            AssertSlot(snap, 2, null);
            AssertSlot(snap, 3, SecondTarget(catalogIsAvailable));
            AssertSlot(snap, 4, NullNameEntry());
            AssertSlot(snap, 5, null);   // the trailing null slot is preserved
            // The appended entry lands AFTER all existing slots (including the trailing null).

            // Fresh authoritative snapshot: the COMPLETE appended payload carries the request
            // fields — raw name as requested, context/description/vision from the request,
            // reasoning null for the available add and canonically formatted ("medium") for
            // the curated add.
            var fresh = SnapshotOf(config, catalogIsAvailable)!;
            AssertSlot(fresh, 6, catalogIsAvailable
                ? MakeEntry("absent-model", 42, null, "added-desc", false)       // available add: reasoning unset
                : MakeEntry("absent-model", 42, "medium", "added-desc", false)); // curated add: canonical format
        }

        // ── C. Update with a case-variant name changes only the FIRST match. ─
        {
            var config = SeedCatalogFixture(catalogIsAvailable, BaseFixture(catalogIsAvailable));

            var request = catalogIsAvailable
                ? null
                : new SubAgentModelRequest("ignored-name", 42, ReasoningEffort.ExtraHigh, "new-desc", true);
            var updated = catalogIsAvailable
                ? config.TryUpdateAvailableModel("tArGeT", new AvailableModelRequest("ignored-name", 42, "new-desc", false))
                : config.TryUpdateSubAgentModel("tArGeT", request!);
            Assert.True(updated);

            var snap = SnapshotOf(config, catalogIsAvailable);
            Assert.Equal(5, snap!.Count);
            AssertSlot(snap, 0, null);
            // FIRST match updated: context/description/vision from the request; the stored name
            // is NOT renamed (request.Name ignored); the available entry's raw reasoning is
            // preserved, the curated entry's reasoning becomes the canonical formatted value.
            AssertSlot(snap, 1, catalogIsAvailable
                ? MakeEntry("Target", 42, " raw-high ", "new-desc", false)
                : MakeEntry("Target", 42, "extra_high", "new-desc", true));
            AssertSlot(snap, 2, null);
            // The later duplicate is untouched (unrelated duplicate metadata preserved).
            AssertSlot(snap, 3, SecondTarget(catalogIsAvailable));
            // The null-Name entry is retained as a real entry and was never matched.
            AssertSlot(snap, 4, NullNameEntry());
        }

        // ── D. Remove deletes only that match, preserving null slots/order. ──
        {
            var config = SeedCatalogFixture(catalogIsAvailable, BaseFixture(catalogIsAvailable));

            var removed = catalogIsAvailable
                ? config.TryRemoveAvailableModel("TARGET")
                : config.TryRemoveSubAgentModel("TARGET");
            Assert.True(removed);

            var snap = SnapshotOf(config, catalogIsAvailable);
            Assert.Equal(4, snap!.Count);
            AssertSlot(snap, 0, null);   // leading null slot preserved
            AssertSlot(snap, 1, null);   // the interior null slot shifted into position 1
            AssertSlot(snap, 2, SecondTarget(catalogIsAvailable));   // later duplicate survives
            AssertSlot(snap, 3, NullNameEntry());
        }

        // ── E. Absent-name update returns false on a FRESH owner; list unchanged. ─────
        {
            var config = SeedCatalogFixture(catalogIsAvailable, BaseFixture(catalogIsAvailable));

            var updateRequest = catalogIsAvailable
                ? null
                : new SubAgentModelRequest("anything", 7, ReasoningEffort.High, "u", null);
            var updated = catalogIsAvailable
                ? config.TryUpdateAvailableModel("missing-model", new AvailableModelRequest("anything", 1, "u", true))
                : config.TryUpdateSubAgentModel("missing-model", updateRequest!);
            Assert.False(updated);

            // The update's IMMEDIATE postcondition is observed before any other operation.
            var snapAfterUpdate = SnapshotOf(config, catalogIsAvailable);
            Assert.Equal(5, snapAfterUpdate!.Count);
            AssertSlot(snapAfterUpdate, 0, null);
            AssertSlot(snapAfterUpdate, 1, FirstTarget(catalogIsAvailable));
            AssertSlot(snapAfterUpdate, 2, null);
            AssertSlot(snapAfterUpdate, 3, SecondTarget(catalogIsAvailable));
            AssertSlot(snapAfterUpdate, 4, NullNameEntry());
        }

        // ── E2. Absent-name remove returns false on its OWN fresh owner. ──────────────
        {
            var config = SeedCatalogFixture(catalogIsAvailable, BaseFixture(catalogIsAvailable));

            var removed = catalogIsAvailable
                ? config.TryRemoveAvailableModel("missing-model")
                : config.TryRemoveSubAgentModel("missing-model");
            Assert.False(removed);

            // The remove's IMMEDIATE postcondition on the required initial state.
            var snapAfterRemove = SnapshotOf(config, catalogIsAvailable);
            Assert.Equal(5, snapAfterRemove!.Count);
            AssertSlot(snapAfterRemove, 0, null);
            AssertSlot(snapAfterRemove, 1, FirstTarget(catalogIsAvailable));
            AssertSlot(snapAfterRemove, 2, null);
            AssertSlot(snapAfterRemove, 3, SecondTarget(catalogIsAvailable));
            AssertSlot(snapAfterRemove, 4, NullNameEntry());
        }

        // ── F1. All-null list: add appends after the null slots (fresh owner). ────────
        {
            var addConfig = SeedCatalogFixture(catalogIsAvailable, [null!, null!]);
            var added = catalogIsAvailable
                ? addConfig.TryAddAvailableModel(new AvailableModelRequest("into-nulls", 5, "d", null))
                : addConfig.TryAddSubAgentModel(new SubAgentModelRequest("into-nulls", 5, ReasoningEffort.Low, "d", null));
            Assert.True(added);

            // Immediate postcondition of the add alone.
            var addedSnap = SnapshotOf(addConfig, catalogIsAvailable);
            Assert.Equal(3, addedSnap!.Count);
            AssertSlot(addedSnap, 0, null);
            AssertSlot(addedSnap, 1, null);
            AssertSlot(addedSnap, 2, catalogIsAvailable
                ? MakeEntry("into-nulls", 5, null, "d", null)              // available add: reasoning unset
                : MakeEntry("into-nulls", 5, "low", "d", null));           // curated add: canonical format
        }

        // ── F2. All-null list: update rejected on a FRESH all-null owner. ─────────────
        {
            var allNullUpdate = SeedCatalogFixture(catalogIsAvailable, [null!, null!]);
            var updated = catalogIsAvailable
                ? allNullUpdate.TryUpdateAvailableModel("anything", new AvailableModelRequest("anything", 1, null, null))
                : allNullUpdate.TryUpdateSubAgentModel("anything", new SubAgentModelRequest("anything", 1, ReasoningEffort.High, null, null));
            Assert.False(updated);

            var unchangedAfterUpdate = SnapshotOf(allNullUpdate, catalogIsAvailable);
            Assert.Equal(2, unchangedAfterUpdate!.Count);
            AssertSlot(unchangedAfterUpdate, 0, null);
            AssertSlot(unchangedAfterUpdate, 1, null);
        }

        // ── F3. All-null list: remove rejected on a FRESH all-null owner. ─────────────
        {
            var allNullRemove = SeedCatalogFixture(catalogIsAvailable, [null!, null!]);
            var removed = catalogIsAvailable
                ? allNullRemove.TryRemoveAvailableModel("anything")
                : allNullRemove.TryRemoveSubAgentModel("anything");
            Assert.False(removed);

            var unchangedAfterRemove = SnapshotOf(allNullRemove, catalogIsAvailable);
            Assert.Equal(2, unchangedAfterRemove!.Count);
            AssertSlot(unchangedAfterRemove, 0, null);
            AssertSlot(unchangedAfterRemove, 1, null);
        }

        // ── G1. Missing storage (no Models): update returns false, storage untouched. ─
        {
            var noModelsUpdate = new HiveConfigFile();
            var updatedNoModels = catalogIsAvailable
                ? noModelsUpdate.TryUpdateAvailableModel("x", new AvailableModelRequest("x", 1, null, null))
                : noModelsUpdate.TryUpdateSubAgentModel("x", new SubAgentModelRequest("x", 1, ReasoningEffort.High, null, null));
            Assert.False(updatedNoModels);
            Assert.Null(noModelsUpdate.Models);
        }

        // ── G2. Missing storage (no Models): remove returns false, storage untouched. ─
        {
            var noModelsRemove = new HiveConfigFile();
            var removedNoModels = catalogIsAvailable
                ? noModelsRemove.TryRemoveAvailableModel("x")
                : noModelsRemove.TryRemoveSubAgentModel("x");
            Assert.False(removedNoModels);
            Assert.Null(noModelsRemove.Models);
        }

        // ── G3. Null catalog list: update returns false, list stays null. ────────────
        {
            var nullListUpdate = new HiveConfigFile { Models = new ModelsConfig() };
            var updatedNullList = catalogIsAvailable
                ? nullListUpdate.TryUpdateAvailableModel("x", new AvailableModelRequest("x", 1, null, null))
                : nullListUpdate.TryUpdateSubAgentModel("x", new SubAgentModelRequest("x", 1, ReasoningEffort.High, null, null));
            Assert.False(updatedNullList);
            Assert.Null(SnapshotOf(nullListUpdate, catalogIsAvailable));
        }

        // ── G4. Null catalog list: remove returns false, list stays null. ─────────────
        {
            var nullListRemove = new HiveConfigFile { Models = new ModelsConfig() };
            var removedNullList = catalogIsAvailable
                ? nullListRemove.TryRemoveAvailableModel("x")
                : nullListRemove.TryRemoveSubAgentModel("x");
            Assert.False(removedNullList);
            Assert.Null(SnapshotOf(nullListRemove, catalogIsAvailable));
        }

        // ── H. Whitespace-sensitive matching: ordinal-ignore-case WITHOUT trimming. ────
        //    A request name equal to the TRIMMED form must NOT match a whitespace-bearing
        //    entry; a request name equal to the RAW whitespace-bearing form must match it.
        //    Each operation runs on its own fresh fixture with full immediate postcondition
        //    assertions, for BOTH catalogs (add/update/remove).
        {
            // H1: duplicate add with the TRIMMED name is NOT a duplicate — the add succeeds
            // alongside the whitespace-bearing entry, whose stored name stays raw. The base
            // name is distinct from the fixture's other entries so only the trimming theory
            // is under test.
            {
                var config = SeedCatalogFixture(catalogIsAvailable,
                [
                    null!,
                    FirstTarget(catalogIsAvailable),
                    MakeEntry(" ws-model ", 2222, catalogIsAvailable ? " ws-effort " : "low", "ws-model-entry", false),
                ]);
                var added = catalogIsAvailable
                    ? config.TryAddAvailableModel(new AvailableModelRequest("ws-model", 44, "trim-desc", null))
                    : config.TryAddSubAgentModel(new SubAgentModelRequest("ws-model", 44, ReasoningEffort.Low, "trim-desc", null));
                Assert.True(added);   // trimmed "ws-model" ≠ stored " ws-model " without trimming

                var snap = SnapshotOf(config, catalogIsAvailable)!;
                Assert.Equal(4, snap.Count);
                AssertSlot(snap, 0, null);
                AssertSlot(snap, 1, FirstTarget(catalogIsAvailable));
                AssertSlot(snap, 2, MakeEntry(" ws-model ", 2222, catalogIsAvailable ? " ws-effort " : "low", "ws-model-entry", false));
                // Appended AFTER the whitespace-bearing entry: a distinct raw name, stored raw.
                AssertSlot(snap, 3, catalogIsAvailable
                    ? MakeEntry("ws-model", 44, null, "trim-desc", null)         // available add: reasoning unset
                    : MakeEntry("ws-model", 44, "low", "trim-desc", null));      // curated add: canonical format
            }

            // H2: duplicate add with the RAW whitespace-bearing name DOES match.
            {
                var config = SeedCatalogFixture(catalogIsAvailable,
                    [null!, FirstTarget(catalogIsAvailable), WhitespaceTarget(catalogIsAvailable)]);
                var added = catalogIsAvailable
                    ? config.TryAddAvailableModel(new AvailableModelRequest(" target ", 44, "ws-desc", null))
                    : config.TryAddSubAgentModel(new SubAgentModelRequest(" target ", 44, ReasoningEffort.Low, "ws-desc", null));
                Assert.False(added);

                var snap = SnapshotOf(config, catalogIsAvailable)!;
                Assert.Equal(3, snap.Count);
                AssertSlot(snap, 0, null);
                AssertSlot(snap, 1, FirstTarget(catalogIsAvailable));
                AssertSlot(snap, 2, WhitespaceTarget(catalogIsAvailable));   // name stays raw
            }

            // H3: update via the RAW whitespace-bearing name matches; the trimmed spelling
            // would not. Stored name remains raw (no rename), other fields take the request.
            {
                var config = SeedCatalogFixture(catalogIsAvailable,
                    [null!, FirstTarget(catalogIsAvailable), WhitespaceTarget(catalogIsAvailable)]);
                var updateRequest = catalogIsAvailable
                    ? null
                    : new SubAgentModelRequest("ignored-name", 55, ReasoningEffort.High, "ws-new-desc", false);
                var updated = catalogIsAvailable
                    ? config.TryUpdateAvailableModel(" target ", new AvailableModelRequest("ignored-name", 111, "ws-new-desc", false))
                    : config.TryUpdateSubAgentModel(" target ", updateRequest!);
                Assert.True(updated);

                var snap = SnapshotOf(config, catalogIsAvailable)!;
                Assert.Equal(3, snap.Count);
                AssertSlot(snap, 0, null);
                // First match ("Target") untouched — the trimmed form does not match it and
                // the raw form matches only the whitespace-bearing entry.
                AssertSlot(snap, 1, FirstTarget(catalogIsAvailable));
                AssertSlot(snap, 2, catalogIsAvailable
                    ? MakeEntry(" target ", 111, " ws-raw ", "ws-new-desc", false)   // name raw, reasoning preserved
                    : MakeEntry(" target ", 55, "high", "ws-new-desc", false));      // name raw, canonical effort
            }

            // H4: update with the TRIMMED name must not match the whitespace-bearing entry.
            {
                var config = SeedCatalogFixture(catalogIsAvailable,
                    [null!, WhitespaceTarget(catalogIsAvailable)]);
                var updateRequest = catalogIsAvailable
                    ? null
                    : new SubAgentModelRequest("ignored-name", 77, ReasoningEffort.High, "t-new-desc", true);
                var updated = catalogIsAvailable
                    ? config.TryUpdateAvailableModel("target", new AvailableModelRequest("ignored-name", 77, "t-new-desc", true))
                    : config.TryUpdateSubAgentModel("target", updateRequest!);
                Assert.False(updated);   // trimmed spelling ≠ stored " target " (no trimming)

                var snap = SnapshotOf(config, catalogIsAvailable)!;
                Assert.Equal(2, snap.Count);
                AssertSlot(snap, 0, null);
                AssertSlot(snap, 1, WhitespaceTarget(catalogIsAvailable));   // entirely unchanged
            }

            // H5: remove with the RAW whitespace-bearing name matches only that entry.
            {
                var config = SeedCatalogFixture(catalogIsAvailable,
                    [null!, FirstTarget(catalogIsAvailable), WhitespaceTarget(catalogIsAvailable)]);
                var removed = catalogIsAvailable
                    ? config.TryRemoveAvailableModel(" target ")
                    : config.TryRemoveSubAgentModel(" target ");
                Assert.True(removed);

                var snap = SnapshotOf(config, catalogIsAvailable)!;
                Assert.Equal(2, snap.Count);
                AssertSlot(snap, 0, null);
                // The non-whitespace first match survives; only the raw-named entry was removed.
                AssertSlot(snap, 1, FirstTarget(catalogIsAvailable));
            }

            // H6: remove with the TRIMMED name must not match the whitespace-bearing entry.
            {
                var config = SeedCatalogFixture(catalogIsAvailable,
                    [null!, WhitespaceTarget(catalogIsAvailable)]);
                var removed = catalogIsAvailable
                    ? config.TryRemoveAvailableModel("target")
                    : config.TryRemoveSubAgentModel("target");
                Assert.False(removed);   // trimmed spelling ≠ stored " target " (no trimming)

                var snap = SnapshotOf(config, catalogIsAvailable)!;
                Assert.Equal(2, snap.Count);
                AssertSlot(snap, 0, null);
                AssertSlot(snap, 1, WhitespaceTarget(catalogIsAvailable));   // entirely unchanged
            }
        }
    }

    /// <summary>
    /// Bulk reasoning (<see cref="HiveConfigFile.SetSubAgentModelReasoningEfforts"/>) with the
    /// full null-placeholder fixture, seeded through a whole <see cref="HiveConfigFile.Models"/>
    /// assignment: nulls before and between matching duplicates, a null-valued assignment, an
    /// unassigned entry and a whitespace-distinct name. BOTH intended duplicates ("Dup"/"dup")
    /// receive the canonical <c>extra_high</c>; every other field, entry and null slot stays
    /// unchanged — including the whitespace-distinct " Other " entry (matching is
    /// ordinal-ignore-case WITHOUT trimming) and the null-valued assignment's target.
    /// </summary>
    [Fact]
    public void SetSubAgentModelReasoningEfforts_NullPlaceholdersAndDuplicates_OnlyMatchingEntriesUpdated()
    {
        var config = SeedCatalogFixture(catalogIsAvailable: false,
        [
            null!,
            MakeEntry("Dup", 10, "low", "dup-first", true),
            null!,
            MakeEntry("dup", 20, "medium", "dup-second", false),
            MakeEntry(" Other ", 30, "high", "ws-distinct", null),
            MakeEntry("unassigned", 40, "none", "unassigned-entry", null),
            MakeEntry("nullvalued", 50, "high", "null-valued-entry", false),
        ]);

        config.SetSubAgentModelReasoningEfforts(new Dictionary<string, ReasoningEffort?>
        {
            ["DUP"] = ReasoningEffort.ExtraHigh,    // case-insensitive: matches "Dup" AND "dup"
            ["other"] = ReasoningEffort.Low,        // whitespace-distinct stored name → NO match
            ["NULLVALUED"] = null,                  // null value → no-op for "nullvalued"
            ["absent-key"] = ReasoningEffort.High,  // unknown name → ignored
        });

        var snap = config.GetSubAgentModelsSnapshot()!;
        Assert.Equal(7, snap.Count);
        AssertSlot(snap, 0, null);
        // First duplicate: reasoning updated to canonical extra_high; ALL other fields unchanged.
        AssertSlot(snap, 1, MakeEntry("Dup", 10, "extra_high", "dup-first", true));
        AssertSlot(snap, 2, null);
        // Second duplicate: likewise updated; its own metadata preserved.
        AssertSlot(snap, 3, MakeEntry("dup", 20, "extra_high", "dup-second", false));
        // Whitespace-distinct name never matched (no trimming) — entirely unchanged.
        AssertSlot(snap, 4, MakeEntry(" Other ", 30, "high", "ws-distinct", null));
        // Unassigned entry unchanged.
        AssertSlot(snap, 5, MakeEntry("unassigned", 40, "none", "unassigned-entry", null));
        // Null-valued assignment: the entry's existing effort survives untouched.
        AssertSlot(snap, 6, MakeEntry("nullvalued", 50, "high", "null-valued-entry", false));
    }

    /// <summary>
    /// Bulk reasoning no-op cases: an all-null curated list (no matches; null slots preserved and
    /// storage untouched), and missing storage (the call returns WITHOUT initializing
    /// <see cref="HiveConfigFile.Models"/> — fresh fixtures, no retained initializer objects).
    /// </summary>
    [Fact]
    public void SetSubAgentModelReasoningEfforts_AllNullListAndMissingStorage_AreNoOps()
    {
        // All-null curated list: nothing matches, null slots preserved, storage not rebuilt.
        var allNull = SeedCatalogFixture(catalogIsAvailable: false, [null!, null!]);
        allNull.SetSubAgentModelReasoningEfforts(
            new Dictionary<string, ReasoningEffort?> { ["dup"] = ReasoningEffort.ExtraHigh });
        var snap = allNull.GetSubAgentModelsSnapshot()!;
        Assert.Equal(2, snap.Count);
        AssertSlot(snap, 0, null);
        AssertSlot(snap, 1, null);

        // Missing storage entirely: the call must return without initializing Models.
        var noStorage = new HiveConfigFile();
        noStorage.SetSubAgentModelReasoningEfforts(
            new Dictionary<string, ReasoningEffort?> { ["dup"] = ReasoningEffort.ExtraHigh });
        Assert.Null(noStorage.Models);

        // Models present but the curated list is null: also a no-op, list stays null.
        var nullList = new HiveConfigFile { Models = new ModelsConfig() };
        nullList.SetSubAgentModelReasoningEfforts(
            new Dictionary<string, ReasoningEffort?> { ["dup"] = ReasoningEffort.ExtraHigh });
        Assert.Null(nullList.GetSubAgentModelsSnapshot());
    }
}