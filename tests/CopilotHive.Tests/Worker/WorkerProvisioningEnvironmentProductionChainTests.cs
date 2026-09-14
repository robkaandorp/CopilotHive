using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE PRODUCTION-CHAIN A/B LIFECYCLE: two FRESH <see cref="WorkerService"/> instances built through
/// the SAME internal attempt-construction path <c>Program.cs</c> uses, SHARING one
/// <see cref="WorkerProvisioningEnvironment"/>, each running the REAL
/// <see cref="WorkerService.RunAsync"/> over the fake call-invoker / duplex-stream seams with the
/// PRODUCTION-CREATED provisioner (never <c>TestProvisioner</c>).
/// <para>
/// <b>What it proves.</b> Attempt A registers (<c>worker-assigned-a</c>), provisions server-supplied
/// configuration into the shared fake environment through its own production provisioner, then loses
/// its transport: the loop drains, retires and unpublishes the connection, and the captured LAZY
/// runner callback a runner cached for A must afterwards start NO fetch — it fails with the EXISTING
/// disconnected error. Attempt B is then built through the SAME constructor path with the SAME state
/// object, registers under its OWN accepted identity, and provisions. Because the snapshot belongs to
/// the PROCESS (taken before A ever provisioned), B's response REPLACES the setting it carries and
/// CLEARS the A-owned values it omits instead of promoting them to operator overrides — while the
/// GENUINE original operator values survive both attempts.
/// </para>
/// <para>
/// Only environment provenance is shared: the two attempts keep separate clients, streams,
/// provisioners and response provenance, which the identity and reference assertions below pin.
/// </para>
/// <para>
/// <b>SEQUENTIAL QUIESCENCE IS PROVEN, NOT ASSUMED.</b> The production contract is for sequential
/// QUIESCENT attempts, so attempt A is fully wound down BEFORE attempt B is constructed: A's loop
/// token is cancelled, A's reader is completed, A's <c>RunAsync</c> handle and every wait its reader
/// ever created are DRAINED within the failure bound, and A's service is DISPOSED — all inside A's own
/// scope. A dedicated block of assertions then pins that state (no in-flight read, no pending waiter,
/// a completed run handle, a disposed service, an empty teardown ledger) before B exists at all.
/// </para>
/// <para>
/// <b>NO ABANDONED TASK EXISTS.</b> <see cref="ScriptedReader"/> deliberately avoids
/// <see cref="Task.WhenAny(Task[])"/>: a losing branch there would leave a real pending task behind
/// with nothing to await it. Instead every <c>MoveNext</c> awaits exactly ONE waiter that the fault,
/// the EOF and the cancellation registration all settle, so the task a read creates is always the task
/// that read observes.
/// </para>
/// <para>
/// <b>TEARDOWN ENFORCES COMPLETION.</b> <see cref="TeardownLedger"/> never swallows a failure to
/// drain: a handle still running when the bound expires is RECORDED and the recorded failures are
/// ASSERTED, so an abandoned producer fails this test by name instead of disappearing into a
/// catch-all. The bound is a failure bound only — it orders nothing.
/// </para>
/// <para>
/// <b>Removal demonstration.</b> Building B's service with FRESH provenance over the same fake
/// environment (the stale-operator regression) makes B's snapshot capture A's provisioned values, so
/// the "A-owned value is CLEARED" assertion observes A's value and fails.
/// </para>
/// <para>
/// No real credentials, no process-environment mutation and no network: the environment is a fake
/// in-memory dictionary and every RPC is answered by a fake invoker. Every gate is a
/// <see cref="TaskCompletionSource"/>, a counted write, or a channel fault/EOF — there are NO sleeps
/// and NO <c>Task.Delay</c> ordering anywhere in this fixture.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerProvisioningEnvironmentProductionChainTests
{
    private const string LocalWorkerId = "worker-local";
    private const string AssignedIdA = "worker-assigned-a";
    private const string AssignedIdB = "worker-assigned-b";
    private const string FixtureModel = "copilot/fixture-model";

    /// <summary>
    /// The FAILURE BOUND for every drain and every gate. It is never an ordering device: nothing waits
    /// for it to elapse, and its expiry always FAILS the test (either directly, through
    /// <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/>'s <see cref="TimeoutException"/>, or
    /// through a recorded — and asserted — <see cref="TeardownLedger"/> failure).
    /// </summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The INDEPENDENT cleanup bound used only AFTER a wait was abandoned by caller/test
    /// cancellation. It is deliberately a SEPARATE bound from <see cref="Failsafe"/>: the bound that
    /// was just abandoned cannot be relied upon to converge, so the post-cancellation join gets its
    /// own fresh budget (and an uncancelled token) to establish that no work is still active.
    /// </summary>
    private static readonly TimeSpan CleanupFailsafe = TimeSpan.FromSeconds(30);

    private const string OperatorOllamaUrl = "http://operator:11434";
    private const string OperatorGithubToken = "operator-original-github-token";
    private const string OperatorConfigRepoUrl = "https://github.com/operator/repo.git";

    /// <summary>
    /// TWO SEQUENTIAL ATTEMPTS, ONE SHARED PROVENANCE — the whole contract in one flow, with attempt A
    /// provably quiescent and disposed before attempt B is built.
    /// </summary>
    [Fact]
    public async Task TwoSequentialAttempts_SharedProvenance_ReplaceAndClearUnderEachAttemptsIdentity()
    {
        // Every drain in this test reports into ONE ledger, which is asserted empty at the end (and
        // again between the two attempts), so no failure to converge can be silently swallowed.
        var teardown = new TeardownLedger();

        // The FAKE process environment plus the ONE provenance object Program.cs would create
        // outside its retry loop. Nothing here touches the real process environment.
        var env = new FakeEnv(
            (WorkerConfigProvisioner.OllamaUrlVar, OperatorOllamaUrl),
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorGithubToken),
            (WorkerConfigProvisioner.ConfigRepoUrlVar, OperatorConfigRepoUrl));
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        // ══ Attempt A ═════════════════════════════════════════════════════════════
        var invokerA = new FakeInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedIdA,
        });
        invokerA.WorkerConfigToReturn = new GetWorkerConfigResponse
        {
            GithubToken = "ghp_attempt_a",
            LlmProvider = "ollama-cloud",
            OllamaModel = "attempt-a-model",
            OllamaApiKey = "attempt-a-key",
            ConfigRepoUrl = "https://github.com/org/attempt-a.git",
        };

        var runnerA = new CapturingRunner();
        var readerA = new ScriptedReader();
        var writerA = new RecordingWriter();

        // The observation is taken INSIDE the stream's disposal callback, so it records the state AT
        // the moment the transport is torn down. The connection is hoisted because by disposal time it
        // is already UNPUBLISHED, so reading the service field would only ever see null.
        WorkerConnection? connectionA = null;
        WorkerService? attemptA = null;
        var retiredAtStreamDisposalA = false;
        var unpublishedAtStreamDisposalA = false;
        var streamDisposalsA = 0;

        attemptA = BuildAttempt(shared, runnerA, invokerA, readerA, writerA, onStreamDisposed: () =>
        {
            Interlocked.Increment(ref streamDisposalsA);
            retiredAtStreamDisposalA = connectionA?.IsRetired ?? false;
            unpublishedAtStreamDisposalA = GetPublishedConnection(attemptA!) is null;
        });

        // Deliberately NOT `using`: A's disposal must happen inside A's own scope, BEFORE B is built —
        // a loop-scoped `using` would defer it past the whole of attempt B.
        var serviceA = attemptA;
        var loopCtsA = new CancellationTokenSource();
        var runA = Task.CompletedTask;
        var serviceADisposed = false;

        // Needed by attempt B's "A started no further fetch" assertion.
        var fetchCallsAfterRetiredCallback = 0;

        try
        {
            runA = serviceA.RunAsync(loopCtsA.Token);

            // BARRIER: the initial Ready is written strictly AFTER publication.
            await writerA.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, writerA.Writes[0].PayloadCase);
            Assert.Equal(AssignedIdA, writerA.Writes[0].WorkerId);

            connectionA = Assert.IsType<WorkerConnection>(GetPublishedConnection(serviceA));

            // The PRODUCTION provisioner is in place: the TestProvisioner seam is untouched, and the
            // connection built its own provisioner (with its own checked fetch).
            Assert.Null(serviceA.TestProvisioner);
            Assert.NotNull(connectionA.Provisioner);

            // The LAZY runner callback is the connection-owned wrapper, and reaching it provisions
            // through A's OWN production provisioner and A's OWN client identity.
            Assert.NotNull(runnerA.ConfigProvisioner);
            Assert.Equal(0, invokerA.WorkerConfigCalls);
            await runnerA.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken);
            Assert.Equal(1, invokerA.WorkerConfigCalls);
            Assert.Equal(AssignedIdA, invokerA.LastWorkerConfigWorkerId);

            // A's provisioned values are in the shared fake environment; the operator alias
            // suppressed the GH_TOKEN mirror, and the operator's OLLAMA_URL was never overwritten.
            Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
            Assert.Equal("attempt-a-model", env[WorkerConfigProvisioner.OllamaModelVar]);
            Assert.Equal("attempt-a-key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);
            Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
            Assert.Equal(OperatorOllamaUrl, env[WorkerConfigProvisioner.OllamaUrlVar]);
            Assert.Equal(OperatorGithubToken, env[WorkerConfigProvisioner.GitHubTokenVar]);
            Assert.Equal("ghp_attempt_a", connectionA.Provisioner.ResolveConfigRepoCredential());
            Assert.Equal("https://github.com/org/attempt-a.git", connectionA.Provisioner.ProvisionedConfigRepoUrl);

            // A's TRANSPORT FAILS: the stream surfaces an availability fault, so the real loop drains
            // and retires the connection and RunAsync faults with the retryable RpcException category.
            readerA.Fail(new RpcException(new Status(StatusCode.Unavailable, "transport lost")));

            var transportFailure = await Assert.ThrowsAsync<RpcException>(
                () => runA.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(StatusCode.Unavailable, transportFailure.StatusCode);

            // Retirement and unpublication completed, and both happened BEFORE the transport disposal.
            Assert.True(connectionA.IsRetired);
            Assert.Null(GetPublishedConnection(serviceA));
            Assert.True(retiredAtStreamDisposalA, "A's connection must be retired before its stream is disposed.");
            Assert.True(unpublishedAtStreamDisposalA, "A's connection must be unpublished before its stream is disposed.");
            Assert.Equal(1, streamDisposalsA);

            // A'S CAPTURED RETIRED CALLBACK STARTS NO FETCH. Retirement is checked before the fetch
            // delegate, so the override-free production path fails with the EXISTING disconnected
            // error and A's client sees no further RPC.
            var fetchCallsBeforeRetiredCallback = invokerA.WorkerConfigCalls;
            var retiredCallbackFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runnerA.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, retiredCallbackFailure.Message);
            Assert.Equal(fetchCallsBeforeRetiredCallback, invokerA.WorkerConfigCalls);
            fetchCallsAfterRetiredCallback = invokerA.WorkerConfigCalls;
        }
        finally
        {
            // ── A-SCOPE QUIESCENCE, BEFORE ATTEMPT B EXISTS ──────────────────────
            // Cancel A's loop token, settle A's reader, then DRAIN both the RunAsync handle and every
            // wait A's reader ever created. Each drain is bounded, and a handle still running at the
            // bound is RECORDED as a teardown failure rather than abandoned.
            await loopCtsA.CancelAsync();
            readerA.Complete();

            await teardown.DrainAsync("attempt A RunAsync", runA);
            await teardown.DrainAsync("attempt A reader waits", readerA.WhenAllWaitsSettledAsync());

            // A's service is disposed HERE — inside A's own scope, so its disposal provably completes
            // before attempt B is constructed.
            serviceA.Dispose();
            serviceADisposed = true;
            loopCtsA.Dispose();
        }

        // ── QUIESCENCE PROOF: attempt A is fully wound down before B is built ─────
        // Reached only when the A body succeeded (a failing body propagates out of the finally above),
        // which is exactly when this proof must hold.
        Assert.True(runA.IsCompleted, "Attempt A's RunAsync must be completed before attempt B starts.");
        Assert.Equal(0, readerA.InFlightReads);
        Assert.Equal(0, readerA.PendingWaiterCount);
        Assert.True(readerA.WhenAllWaitsSettledAsync().IsCompleted,
            "Every wait attempt A's reader created must be settled before attempt B starts.");
        Assert.True(serviceADisposed, "Attempt A's service must be disposed before attempt B is constructed.");
        AssertTeardownDrained(teardown);

        // ══ Attempt B: a FRESH service through the SAME attempt-construction path ══
        var invokerB = new FakeInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedIdB,
        });
        invokerB.WorkerConfigToReturn = new GetWorkerConfigResponse
        {
            LlmProvider = "copilot",
        };

        var runnerB = new CapturingRunner();
        var readerB = new ScriptedReader();
        var writerB = new RecordingWriter();

        // THE SAME state object that A used: this is what Program.cs does on every retry.
        var serviceB = BuildAttempt(shared, runnerB, invokerB, readerB, writerB);
        var loopCtsB = new CancellationTokenSource();
        var runB = Task.CompletedTask;
        var serviceBDisposed = false;

        try
        {
            runB = serviceB.RunAsync(loopCtsB.Token);

            await writerB.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(AssignedIdB, writerB.Writes[0].WorkerId);

            var connectionB = Assert.IsType<WorkerConnection>(GetPublishedConnection(serviceB));
            Assert.Null(serviceB.TestProvisioner);
            Assert.NotNull(connectionB.Provisioner);

            // Provenance is the ONLY thing shared: B has its OWN provisioner object, and neither
            // attempt is A's.
            Assert.NotSame(connectionA!.Provisioner, connectionB.Provisioner);

            Assert.NotNull(runnerB.ConfigProvisioner);
            await runnerB.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken);

            // B FETCHED THROUGH B: its own client and its own accepted identity.
            Assert.Equal(1, invokerB.WorkerConfigCalls);
            Assert.Equal(AssignedIdB, invokerB.LastWorkerConfigWorkerId);
            Assert.Equal(fetchCallsAfterRetiredCallback, invokerA.WorkerConfigCalls);

            // REPLACED — B's value, not A's.
            Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);
            // CLEARED — values only ever PROVISIONED (by A) are removed, NOT promoted to
            // operator overrides. With fresh provenance for B these would survive as A's values.
            Assert.Null(env[WorkerConfigProvisioner.OllamaModelVar]);
            Assert.Null(env[WorkerConfigProvisioner.OllamaApiKeyVar]);
            Assert.False(env.IsSet(WorkerConfigProvisioner.OllamaModelVar));
            Assert.False(env.IsSet(WorkerConfigProvisioner.OllamaApiKeyVar));

            // The GENUINE original operator values survive both attempts untouched.
            Assert.Equal(OperatorOllamaUrl, env[WorkerConfigProvisioner.OllamaUrlVar]);
            Assert.Equal(OperatorGithubToken, env[WorkerConfigProvisioner.GitHubTokenVar]);

            // B's RESPONSE provenance is its own: no URL or token came from A, so the chain falls
            // through to the ORIGINAL operator environment — the intentional documented fallback.
            Assert.Null(connectionB.Provisioner.ProvisionedConfigRepoUrl);
            Assert.Equal(OperatorConfigRepoUrl, connectionB.Provisioner.ResolvedConfigRepoUrl);
            Assert.Equal(OperatorGithubToken, connectionB.Provisioner.ResolveConfigRepoCredential());

            // A's PROVISIONED URL never reached the environment (only the operator value lives
            // there) and A's response provenance stays A's own.
            Assert.Equal(OperatorConfigRepoUrl, env[WorkerConfigProvisioner.ConfigRepoUrlVar]);
            Assert.Equal("https://github.com/org/attempt-a.git", connectionA.Provisioner!.ProvisionedConfigRepoUrl);

            // A's retirement is unaffected by B's attempt: it stays retired with its stale
            // in-memory response provenance, which no later attempt may inherit.
            Assert.True(connectionA.IsRetired);
            Assert.Equal("ghp_attempt_a", connectionA.Provisioner.ResolveConfigRepoCredential());

            // EOF ends B's loop cleanly, preserving the existing clean-return behavior.
            readerB.Complete();
            await runB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(connectionB.IsRetired);
            Assert.Null(GetPublishedConnection(serviceB));
        }
        finally
        {
            // ── B-SCOPE QUIESCENCE, mirroring attempt A's ────────────────────────
            await loopCtsB.CancelAsync();
            readerB.Complete();

            await teardown.DrainAsync("attempt B RunAsync", runB);
            await teardown.DrainAsync("attempt B reader waits", readerB.WhenAllWaitsSettledAsync());

            serviceB.Dispose();
            serviceBDisposed = true;
            loopCtsB.Dispose();
        }

        // ── QUIESCENCE PROOF for attempt B, and the ENFORCED teardown verdict ─────
        Assert.True(runB.IsCompleted, "Attempt B's RunAsync must be completed at the end of the test.");
        Assert.Equal(0, readerB.InFlightReads);
        Assert.Equal(0, readerB.PendingWaiterCount);
        Assert.True(serviceBDisposed, "Attempt B's service must be disposed at the end of the test.");

        // THE ENFORCEMENT: any handle that failed to drain within the bound was recorded, and a
        // recorded failure fails this test by name instead of being swallowed by a catch-all.
        AssertTeardownDrained(teardown);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // THE TEARDOWN-CLASSIFICATION BOUNDARY. These pin the contract the enforcing drainer above
    // depends on: a BOUND EXPIRY is classified from the OBSERVED TimeoutException and recorded
    // unconditionally, while terminal outcomes stay tolerated and test cancellation keeps its own
    // kind. Every case is driven by an exact (observed-outcome, handle-state) shape or by a ZERO
    // bound — so none of them sleeps, waits for a real bound, or depends on which schedule occurs.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REMOVAL-PROOF VECTOR FOR THE REVIEWER'S RACE, AT THE REAL <c>DrainAsync</c> CALL SITE.
    /// <para>
    /// The bound expires on a genuinely incomplete handle (a ZERO bound, so nothing waits), and the
    /// deterministic seam then COMPLETES THAT SAME ORIGINAL HANDLE before classification runs. The
    /// drain therefore classifies while <c>handle.IsCompleted</c> is <b>true</b> — exactly the legal
    /// schedule the old inference got wrong.
    /// </para>
    /// <para>
    /// This is the cell a classifier-only test cannot cover: reintroducing
    /// <c>if (!handle.IsCompleted)</c> inside the <c>catch (TimeoutException)</c> clause suppresses
    /// the record HERE and fails THIS test by name, because the guard is evaluated against a handle
    /// that has already completed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Drain_BoundExpiredThenHandleCompletesBeforeClassification_IsStillRecordedAsBoundExpiry()
    {
        var ledger = new TeardownLedger();

        // RETAINED so it is settled here and awaited in the finally — no outstanding handle is left.
        var handleSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedBeforeClassification = false;

        try
        {
            await ledger.DrainAsync(
                "attempt A RunAsync",
                handleSource.Task,
                // ZERO bound: an incomplete handle expires IMMEDIATELY — a pure failure bound, never
                // an ordering device, and nothing sleeps.
                bound: TimeSpan.Zero,
                onObservedBeforeClassification: () =>
                {
                    // THE RACE, made deterministic: the ORIGINAL handle completes AFTER the timeout
                    // was observed and BEFORE the outcome is classified.
                    handleSource.TrySetResult();
                    completedBeforeClassification = handleSource.Task.IsCompleted;
                });

            // Non-vacuity: the seam really did complete the handle before classification, so a
            // resurrected IsCompleted guard would genuinely have seen true and suppressed the record.
            Assert.True(
                completedBeforeClassification,
                "The original handle must be completed before classification for this race to be the one under test.");
            Assert.True(handleSource.Task.IsCompleted);

            // RECORDED anyway — the observed TimeoutException is the proof, not the handle's state.
            var failure = Assert.Single(ledger.Failures);
            Assert.Contains(TeardownFailureKind.BoundExpired, failure, StringComparison.Ordinal);
            Assert.Contains("attempt A RunAsync", failure, StringComparison.Ordinal);

            // ...and the verdict FAILS by name rather than passing on a completed-handle technicality.
            var verdict = Assert.ThrowsAny<Exception>(() => AssertTeardownDrained(ledger));
            Assert.Contains(TeardownFailureKind.BoundExpired, verdict.Message, StringComparison.Ordinal);
            Assert.Contains("not sequentially quiescent", verdict.Message, StringComparison.Ordinal);
        }
        finally
        {
            // Settle-and-await: the fixture leaves no outstanding handle and no undisposed owner.
            handleSource.TrySetResult();
            await handleSource.Task;
        }
    }

    /// <summary>
    /// The complement of the race above, through the SAME real <c>DrainAsync</c> path: the handle is
    /// still incomplete AT THE MOMENT OF OBSERVATION. It is SETTLEABLE — completed and awaited in the
    /// <c>finally</c> immediately after the assertions — so nothing is left outstanding.
    /// </summary>
    [Fact]
    public async Task Drain_HandleIncompleteWhenBoundExpires_IsRecorded_AndVerdictFailsByName()
    {
        var ledger = new TeardownLedger();

        // RETAINED: incomplete only for the observation, then settled in the finally below.
        var handleSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await ledger.DrainAsync("attempt A reader waits", handleSource.Task, bound: TimeSpan.Zero);

            // The case under test: still running when the bound elapsed.
            Assert.False(
                handleSource.Task.IsCompleted,
                "The handle must still be running at observation — that is the case under test.");

            var failure = Assert.Single(ledger.Failures);
            Assert.Contains(TeardownFailureKind.BoundExpired, failure, StringComparison.Ordinal);
            Assert.Contains("attempt A reader waits", failure, StringComparison.Ordinal);

            var verdict = Assert.ThrowsAny<Exception>(() => AssertTeardownDrained(ledger));
            Assert.Contains("attempt A reader waits", verdict.Message, StringComparison.Ordinal);
            Assert.Contains(TeardownFailureKind.BoundExpired, verdict.Message, StringComparison.Ordinal);
        }
        finally
        {
            // Settled immediately after the observation, per the no-outstanding-handle rule.
            handleSource.TrySetResult();
            await handleSource.Task;
        }
    }

    /// <summary>
    /// A GENUINELY CANCELLED WAIT, END TO END THROUGH THE REAL <c>DrainAsync</c>: the test supplies
    /// its OWN wait token and actually cancels it, so
    /// <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/> really throws an
    /// <see cref="OperationCanceledException"/> carrying THAT token — no synthesized exception and no
    /// otherwise-unreachable shape.
    /// <para>
    /// The bound is generous and cannot elapse, so the recorded kind can only come from the
    /// cancellation path. The assertion is POSITIVE on the cancellation kind AND explicitly asserts
    /// the bound-expiry kind is ABSENT, so mislabelling fails by name.
    /// </para>
    /// <para>
    /// Because the wait token is passed EXPLICITLY into the drain, a mutation that forwards a
    /// DIFFERENT token on to the classifier makes this cancellation look like the handle's own
    /// (terminal) and records nothing — which this test catches.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Drain_RealCancelledWait_RecordsCancellationKind_NotBoundExpiry_ThenJoinsHandle()
    {
        var ledger = new TeardownLedger();

        // RETAINED: the handle outlives the abandoned wait, then settles so the INDEPENDENT cleanup
        // join can establish that no work is still active.
        var handleSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var waitCts = new CancellationTokenSource();

        try
        {
            // GENUINE cancellation of the very token the drain waits on — not a constructed exception.
            // The handle is deliberately INCOMPLETE here: an already-completed handle would let
            // WaitAsync short-circuit and never observe the cancellation at all.
            await waitCts.CancelAsync();

            await ledger.DrainAsync(
                "attempt B RunAsync",
                handleSource.Task,
                // A GENEROUS bound that cannot elapse: any bound-expiry record would be a mislabel.
                bound: Failsafe,
                waitToken: waitCts.Token,
                // The handle settles AFTER the cancellation is observed and BEFORE the independent
                // cleanup join runs, so that join converges deterministically — no sleeping.
                onObservedBeforeClassification: () => handleSource.TrySetResult());

            // POSITIVE: recorded under the CANCELLATION kind…
            var failure = Assert.Single(ledger.Failures);
            Assert.Contains(TeardownFailureKind.TestCancelled, failure, StringComparison.Ordinal);
            Assert.Contains("attempt B RunAsync", failure, StringComparison.Ordinal);
            // …and NEVER as a bound expiry — the bound did not elapse here.
            Assert.DoesNotContain(TeardownFailureKind.BoundExpired, failure, StringComparison.Ordinal);

            // CLEANUP CONTINUED after recording: the original handle reached a terminal state, so no
            // RunAsync or reader work can still be active when teardown proceeds. The cleanup join
            // ran under its OWN bound and an uncancelled token, so it recorded nothing.
            Assert.True(handleSource.Task.IsCompleted);
            Assert.DoesNotContain(TeardownFailureKind.CleanupIncomplete, failure, StringComparison.Ordinal);
        }
        finally
        {
            handleSource.TrySetResult();
            await handleSource.Task;
        }
    }

    /// <summary>
    /// The post-cancellation cleanup join is BOUNDED and ENFORCED, not best-effort: when the wait is
    /// abandoned by cancellation and the handle does NOT settle, the drain records the distinct
    /// cleanup-incomplete kind — so a service can never be disposed while its work is still active
    /// without the fixture saying so.
    /// </summary>
    [Fact]
    public async Task Drain_CancelledWaitWhoseHandleDoesNotSettle_RecordsCleanupIncomplete()
    {
        var ledger = new TeardownLedger();

        // RETAINED: unsettled only for the observation, then settled and awaited in the finally.
        var handleSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var waitCts = new CancellationTokenSource();

        try
        {
            await waitCts.CancelAsync();

            await ledger.DrainAsync(
                "attempt B reader waits",
                handleSource.Task,
                bound: Failsafe,
                waitToken: waitCts.Token,
                // A ZERO cleanup bound keeps the enforced join instantaneous: the handle has not
                // settled, so the cleanup records rather than waiting out a real budget.
                cleanupBound: TimeSpan.Zero);

            // BOTH kinds are present: the abandoned wait AND the unfinished cleanup.
            Assert.Contains(
                ledger.Failures,
                recorded => recorded.Contains(TeardownFailureKind.TestCancelled, StringComparison.Ordinal));
            Assert.Contains(
                ledger.Failures,
                recorded => recorded.Contains(TeardownFailureKind.CleanupIncomplete, StringComparison.Ordinal));

            var verdict = Assert.ThrowsAny<Exception>(() => AssertTeardownDrained(ledger));
            Assert.Contains(TeardownFailureKind.CleanupIncomplete, verdict.Message, StringComparison.Ordinal);
        }
        finally
        {
            handleSource.TrySetResult();
            await handleSource.Task;
        }
    }

    /// <summary>
    /// The discriminator that makes the cancellation test meaningful: a cancellation carrying the
    /// HANDLE's own token (not the wait's) is a TERMINAL outcome and is tolerated, so the two
    /// cancellation sources cannot collapse into one classification. Driven through the REAL
    /// <c>DrainAsync</c> with a live, DIFFERENT wait token.
    /// </summary>
    [Fact]
    public async Task Drain_HandleOwnCancellation_IsTerminal_AndRecordsNothing()
    {
        var ledger = new TeardownLedger();

        using var handleCts = new CancellationTokenSource();
        await handleCts.CancelAsync();

        // The WAIT's token is a different, LIVE token — so the cancellation can only be attributed to
        // the handle itself.
        using var waitCts = new CancellationTokenSource();

        await ledger.DrainAsync(
            "cancelled attempt",
            Task.FromCanceled(handleCts.Token),
            bound: Failsafe,
            waitToken: waitCts.Token);

        Assert.Empty(ledger.Failures);
        AssertTeardownDrained(ledger); // does not throw
    }

    /// <summary>
    /// TERMINAL OUTCOMES STAY TOLERATED. A handle that faulted or was cancelled BY ITS OWN token has a
    /// real result the test body asserts, so the drainer records nothing and the verdict passes. This
    /// is the complement that keeps the strict expiry classification from degenerating into
    /// "record everything".
    /// </summary>
    [Fact]
    public async Task Teardown_TerminalOutcomes_AreToleratedAndRecordNothing()
    {
        var ledger = new TeardownLedger();

        // A FAULT — exactly attempt A's scripted transport failure shape.
        await ledger.DrainAsync(
            "faulted attempt",
            Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "transport lost"))));

        // A handle cancelled by ITS OWN token (not the test's).
        using var handleCts = new CancellationTokenSource();
        await handleCts.CancelAsync();
        await ledger.DrainAsync("cancelled attempt", Task.FromCanceled(handleCts.Token));

        // A normal completion, and a handle that was never started at all.
        await ledger.DrainAsync("completed attempt", Task.CompletedTask);
        await ledger.DrainAsync("never started", null);

        Assert.Empty(ledger.Failures);
        AssertTeardownDrained(ledger); // does not throw
    }

    /// <summary>
    /// The remaining classification cell: the wait RETURNED NORMALLY yet the handle is not terminal.
    /// Here — and only here — <c>IsCompleted</c> decides, which is what keeps it out of the
    /// bound-expiry decision entirely.
    /// </summary>
    [Fact]
    public async Task Teardown_WaitReturnedNormallyButHandleStillRunning_IsRecordedAsNonTerminal()
    {
        var ledger = new TeardownLedger();

        // RETAINED so the incomplete handle is settled and awaited in the finally.
        var handleSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            ledger.RecordObserved(
                "attempt A RunAsync",
                observed: null,
                handle: handleSource.Task,
                bound: Failsafe,
                waitToken: CancellationToken.None);

            var failure = Assert.Single(ledger.Failures);
            Assert.Contains(TeardownFailureKind.NotTerminal, failure, StringComparison.Ordinal);
            // NOT a bound expiry: the bound never elapsed on this path.
            Assert.DoesNotContain(TeardownFailureKind.BoundExpired, failure, StringComparison.Ordinal);
        }
        finally
        {
            handleSource.TrySetResult();
            await handleSource.Task;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Harness.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds ONE attempt's service through the INTERNAL attempt-construction path <c>Program.cs</c>
    /// uses — the shared-provenance constructor — with the fake invoker and duplex stream installed.
    /// The runner is injected through the same reflection seam the existing WorkerService fixtures use.
    /// </summary>
    private static WorkerService BuildAttempt(
        WorkerProvisioningEnvironment shared,
        IAgentRunner runner,
        FakeInvoker invoker,
        ScriptedReader reader,
        RecordingWriter writer,
        Action? onStreamDisposed = null)
    {
        // The EXACT constructor path Program.cs takes: a fresh service per attempt, carrying the
        // process's ONE shared environment provenance.
        var service = new WorkerService(
            "http://localhost:9999",
            LocalWorkerId,
            ["coder"],
            shared);

        ReplaceRunner(service, runner);

        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            writer, reader,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => onStreamDisposed?.Invoke(),
            null!);

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;
        return service;
    }

    private static void ReplaceRunner(WorkerService service, IAgentRunner runner)
    {
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");

        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();

        field.SetValue(service, runner);
    }

    /// <summary>Reflects the real published-connection field (observation only).</summary>
    private static WorkerConnection? GetPublishedConnection(WorkerService service) =>
        (WorkerConnection?)typeof(WorkerService)
            .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    /// <summary>
    /// THE TEARDOWN VERDICT: a handle that never drained is a TEST FAILURE, reported by name.
    /// </summary>
    private static void AssertTeardownDrained(TeardownLedger ledger)
    {
        var failures = ledger.Failures;
        Assert.True(
            failures.Count == 0,
            "Teardown did not converge — attempts were not sequentially quiescent: "
                + string.Join(" | ", failures));
    }

    /// <summary>The distinct failure kinds <see cref="TeardownLedger"/> can record.</summary>
    /// <remarks>
    /// These exist so a recorded failure names WHY teardown did not converge. Bound expiry and test
    /// cancellation are deliberately SEPARATE kinds: conflating them is precisely the mislabelling the
    /// classification below forbids.
    /// </remarks>
    private static class TeardownFailureKind
    {
        /// <summary>The failure bound elapsed while the handle was still awaited.</summary>
        internal const string BoundExpired = "EXCEEDED THE FAILURE BOUND";

        /// <summary>The wait was abandoned because the TEST's own token was cancelled.</summary>
        internal const string TestCancelled = "ABANDONED BY TEST CANCELLATION";

        /// <summary>The wait returned normally, yet the handle is not in a terminal state.</summary>
        internal const string NotTerminal = "DID NOT REACH A TERMINAL STATE";

        /// <summary>
        /// A wait abandoned by cancellation whose handle then failed to settle under the INDEPENDENT
        /// cleanup bound, so work may still be active while teardown proceeds.
        /// </summary>
        internal const string CleanupIncomplete = "DID NOT SETTLE AFTER AN ABANDONED WAIT";
    }

    /// <summary>
    /// THE ENFORCING TEARDOWN DRAINER. It records — never swallows — a handle that fails to reach a
    /// terminal state within the failure bound, and the test ASSERTS the record is empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>BOUND EXPIRY IS CLASSIFIED BY THE OBSERVED EXCEPTION, NEVER BY A LATER STATE SAMPLE.</b> An
    /// earlier version caught every exception together and then inferred expiry from a subsequent
    /// <c>handle.IsCompleted</c> read. That inference is unsound: a legal schedule exists in which the
    /// bound elapses, <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/> throws
    /// <see cref="TimeoutException"/>, the handle completes immediately afterwards, and the later
    /// sample then reads <c>true</c> — so a handle that DID exceed the bound was recorded as clean and
    /// the assertion passed. Here the <see cref="TimeoutException"/> catch is type-specific and records
    /// UNCONDITIONALLY at the moment it is observed, so no subsequent state can veto it.
    /// </para>
    /// <para>
    /// <b>Terminal outcomes stay tolerated.</b> A handle that FAULTED or was CANCELLED is drained — an
    /// attempt's real outcome is asserted in the test body, so rethrowing it here would mask that
    /// assertion. <c>IsCompleted</c> still participates, but ONLY to classify that terminal case; it
    /// never decides whether the bound expired.
    /// </para>
    /// <para>
    /// <b>Test cancellation is its own kind.</b> An <see cref="OperationCanceledException"/> carrying
    /// the TEST's own token can never reach the <see cref="TimeoutException"/> clause (the catch is
    /// type-specific), and is recorded under its own distinct kind rather than as a bound expiry.
    /// </para>
    /// </remarks>
    private sealed class TeardownLedger
    {
        private readonly object _gate = new();
        private readonly List<string> _failures = [];

        /// <summary>A snapshot of the recorded teardown failures.</summary>
        internal IReadOnlyList<string> Failures
        {
            get { lock (_gate) return [.. _failures]; }
        }

        /// <summary>
        /// Drains ONE handle within the failure bound, classifying the OBSERVED outcome.
        /// </summary>
        /// <param name="what">The handle's name, used verbatim in the recorded failure.</param>
        /// <param name="handle">The handle to drain; <c>null</c> means there was nothing started.</param>
        /// <param name="bound">
        /// The failure bound. Defaults to <see cref="Failsafe"/>. A boundary test may pass
        /// <see cref="TimeSpan.Zero"/>, which makes an incomplete handle expire IMMEDIATELY — so the
        /// expiry path is covered with no real waiting and no timing dependence.
        /// </param>
        /// <param name="waitToken">
        /// THE TOKEN THE WAIT IS PERFORMED WITH, passed EXPLICITLY on to the classifier so token
        /// identity is a real, testable contract rather than ambient state. Defaults to the test's own
        /// token, which is what the lifecycle drains use.
        /// </param>
        /// <param name="onObservedBeforeClassification">
        /// DETERMINISTIC SEAM, invoked AFTER the wait's outcome is observed and BEFORE that outcome is
        /// classified. It exists so a boundary test can reproduce — at the REAL call site — the legal
        /// schedule in which the bound elapses and the ORIGINAL handle then completes before
        /// classification runs. Production drains leave it <c>null</c>.
        /// </param>
        /// <param name="cleanupBound">
        /// The INDEPENDENT bound for the post-cancellation cleanup join. Defaults to
        /// <see cref="CleanupFailsafe"/> — deliberately a SEPARATE budget from <paramref name="bound"/>,
        /// since the bound that was just abandoned cannot be relied on to converge.
        /// </param>
        internal async Task DrainAsync(
            string what,
            Task? handle,
            TimeSpan? bound = null,
            CancellationToken? waitToken = null,
            Action? onObservedBeforeClassification = null,
            TimeSpan? cleanupBound = null)
        {
            if (handle is null) return;

            var effectiveBound = bound ?? Failsafe;
            var effectiveWaitToken = waitToken ?? TestContext.Current.CancellationToken;

            try
            {
                await handle.WaitAsync(effectiveBound, effectiveWaitToken);
            }
            catch (TimeoutException expiry)
            {
                // THE BOUND EXPIRED. The seam may complete the ORIGINAL handle right here, so the
                // record below is made while IsCompleted is TRUE — proving the classification is
                // driven by the observed exception and can never be vetoed by a racing completion.
                onObservedBeforeClassification?.Invoke();
                RecordObserved(what, expiry, handle, effectiveBound, effectiveWaitToken);
                return;
            }
            catch (Exception observed)
            {
                onObservedBeforeClassification?.Invoke();
                var kind = RecordObserved(what, observed, handle, effectiveBound, effectiveWaitToken);

                // CALLER/TEST CANCELLATION IS NOT THE END OF TEARDOWN. The wait was abandoned, so the
                // handle may still be running; joining it under an INDEPENDENT bound (never the one
                // that was just abandoned, and never the cancelled token) keeps lifecycle teardown
                // from disposing a service while RunAsync or reader work is still active.
                if (ReferenceEquals(kind, TeardownFailureKind.TestCancelled))
                    await JoinAfterAbandonedWaitAsync(what, handle, cleanupBound ?? CleanupFailsafe);

                return;
            }

            RecordObserved(what, observed: null, handle, effectiveBound, effectiveWaitToken);
        }

        /// <summary>
        /// The INDEPENDENT post-cancellation cleanup join: after a caller/test cancellation was
        /// recorded, the original handle is still awaited to a terminal state under its OWN bound and
        /// with <see cref="CancellationToken.None"/> — deliberately NOT the bound or the token that
        /// was just abandoned, neither of which could be relied on to converge. A handle that still
        /// fails to settle is recorded under its own kind.
        /// </summary>
        private async Task JoinAfterAbandonedWaitAsync(string what, Task handle, TimeSpan cleanupBound)
        {
            try
            {
                await handle.WaitAsync(cleanupBound, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                Record(
                    $"'{what}' {TeardownFailureKind.CleanupIncomplete} of "
                        + $"{cleanupBound.TotalSeconds:0.###}s — the wait was abandoned by "
                        + "cancellation and the handle never reached a terminal state afterwards, so "
                        + "work may still be active while teardown proceeds.");
            }
            catch (Exception)
            {
                // A fault or the handle's own cancellation is a TERMINAL state: the handle is no
                // longer active, which is all this cleanup join needs to establish.
            }
        }

        /// <summary>
        /// THE SINGLE CLASSIFICATION AUTHORITY, driven by the OUTCOME THAT WAS OBSERVED.
        /// </summary>
        /// <remarks>
        /// Exposed so the boundary tests can drive it directly with an exact
        /// (observed-outcome, handle-state) shape — including the reviewer's race shape, a
        /// <see cref="TimeoutException"/> alongside an ALREADY-COMPLETED handle. The decisive
        /// removal-proof vector for that race lives at the REAL <see cref="DrainAsync"/> call site
        /// (see the seam above); these direct drives are the complementary per-cell coverage.
        /// </remarks>
        /// <param name="what">The handle's name.</param>
        /// <param name="observed">The exception the wait produced, or <c>null</c> when it returned normally.</param>
        /// <param name="handle">The handle; consulted ONLY for the terminal-state classification.</param>
        /// <param name="bound">The bound that was applied, used in the recorded message.</param>
        /// <param name="waitToken">
        /// The token the wait was performed with. A cancellation carrying THIS token came from the
        /// caller/test, not from the handle — which is what keeps the two kinds apart. Supplied
        /// explicitly (rather than read from ambient state) so a boundary test can drive the
        /// caller-cancelled shape deterministically.
        /// </param>
        /// <returns>The failure kind that was recorded, or <c>null</c> when the outcome was tolerated.</returns>
        internal string? RecordObserved(
            string what, Exception? observed, Task handle, TimeSpan bound, CancellationToken waitToken)
        {
            switch (observed)
            {
                // FIRST and TYPE-SPECIFIC: the bound elapsed. Nothing about the handle's current
                // state may suppress this — the exception itself is the proof.
                case TimeoutException:
                    Record(
                        $"'{what}' {TeardownFailureKind.BoundExpired} of {bound.TotalSeconds:0.###}s "
                            + "— it was still being awaited when the bound elapsed, so this attempt "
                            + "was never quiescent.");
                    return TeardownFailureKind.BoundExpired;

                // The CALLER's/TEST's own token, not the bound: a distinct kind, never a timeout
                // failure. WaitAsync reports the token it was GIVEN when the caller cancels, whereas a
                // handle cancelled by its own token reports THAT token — so the two are separable with
                // no timing assumption.
                case OperationCanceledException canceled when canceled.CancellationToken == waitToken:
                    Record(
                        $"'{what}' {TeardownFailureKind.TestCancelled} — the drain could not establish "
                            + "quiescence because the test run itself was cancelled.");
                    return TeardownFailureKind.TestCancelled;

                // A genuine fault, or a cancellation of the HANDLE itself: a terminal outcome whose
                // real result the test body asserts. Tolerated.
                case not null:
                    return null;

                // The wait returned normally. IsCompleted is consulted HERE ONLY — to classify the
                // terminal case — and never to decide whether the bound expired.
                default:
                    if (!handle.IsCompleted)
                    {
                        Record(
                            $"'{what}' {TeardownFailureKind.NotTerminal} — the wait returned but the "
                                + "handle is STILL RUNNING, so this attempt was never quiescent.");
                        return TeardownFailureKind.NotTerminal;
                    }

                    return null;
            }
        }

        private void Record(string failure)
        {
            lock (_gate)
                _failures.Add(failure);
        }
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>
    /// An in-memory process environment backed by an <c>Ordinal</c> dictionary, so no test mutates
    /// the real process environment and a REMOVED variable stays distinguishable from a never-set one.
    /// </summary>
    private sealed class FakeEnv
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        internal FakeEnv(params (string Key, string? Value)[] initial)
        {
            foreach (var (key, value) in initial)
                _values[key] = value;
        }

        internal string? this[string name] => _values.TryGetValue(name, out var value) ? value : null;

        internal bool IsSet(string name) => _values.ContainsKey(name);

        internal string? Read(string name) =>
            _values.TryGetValue(name, out var value) ? value : null;

        internal void Write(string name, string? value)
        {
            if (value is null) _values.Remove(name);
            else _values[name] = value;
        }
    }

    /// <summary>
    /// A <see cref="CallInvoker"/> answering the unary RPCs the REAL <see cref="WorkerService.RunAsync"/>
    /// reaches — <c>Register</c>, <c>GetWorkerConfig</c>, <c>GetSession</c>, <c>SaveSession</c> and
    /// <c>Heartbeat</c> — recording the provisioning requests so a test can prove WHICH attempt's
    /// identity and client performed a fetch. Any other call is a fixture bug and throws loudly.
    /// </summary>
    private sealed class FakeInvoker(RegisterResponse registerResponse) : CallInvoker
    {
        private int _registerCalls;
        private int _workerConfigCalls;
        private string? _lastWorkerConfigWorkerId;
        private string? _lastRegisterWorkerId;

        internal GetWorkerConfigResponse WorkerConfigToReturn { get; set; } = new();

        internal int RegisterCalls => Volatile.Read(ref _registerCalls);
        internal int WorkerConfigCalls => Volatile.Read(ref _workerConfigCalls);
        internal string? LastWorkerConfigWorkerId => Volatile.Read(ref _lastWorkerConfigWorkerId);
        internal string? LastRegisterWorkerId => Volatile.Read(ref _lastRegisterWorkerId);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var payload = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/Register" => RespondRegister(request),
                "/copilothive.HiveOrchestrator/GetWorkerConfig" => RespondWorkerConfig(request),
                "/copilothive.HiveOrchestrator/GetSession" => new GetSessionResponse { Found = false },
                "/copilothive.HiveOrchestrator/SaveSession" => new SaveSessionResponse { Success = true },
                "/copilothive.HiveOrchestrator/Heartbeat" => new HeartbeatResponse { Acknowledged = true },
                _ => throw new NotSupportedException($"Unexpected unary call {method.FullName}."),
            };

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)payload),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private object RespondRegister<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _registerCalls);
            Volatile.Write(ref _lastRegisterWorkerId, (request as RegisterRequest)?.WorkerId);
            return registerResponse;
        }

        private object RespondWorkerConfig<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _workerConfigCalls);
            Volatile.Write(ref _lastWorkerConfigWorkerId, (request as GetWorkerConfigRequest)?.WorkerId);
            return WorkerConfigToReturn;
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected server-streaming call {method.FullName}.");

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected client-streaming call {method.FullName}.");

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException(
                $"Unexpected duplex call {method.FullName} — the fixture supplies the stream explicitly.");
    }

    /// <summary>
    /// The orchestrator-message reader for ONE attempt. It ends the production loop exactly two ways,
    /// both deterministic signals and neither a delay: an availability FAULT (<see cref="Fail"/> — the
    /// transport failure under test, rethrown VERBATIM so the production loop observes the real
    /// <see cref="RpcException"/> category) or a clean EOF (<see cref="Complete"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NO ABANDONED TASK.</b> An earlier version raced the fault signal against the channel read
    /// with <see cref="Task.WhenAny(Task[])"/>, which left the LOSING read pending with nothing to
    /// await it — so the attempt could not be proven quiescent. Here each <c>MoveNext</c> creates and
    /// awaits exactly ONE waiter, and the fault, the EOF and the cancellation registration all settle
    /// THAT waiter. The task a read creates is therefore always the task that read observes.
    /// </para>
    /// <para>
    /// <see cref="InFlightReads"/>, <see cref="PendingWaiterCount"/> and
    /// <see cref="WhenAllWaitsSettledAsync"/> make quiescence OBSERVABLE, so the test can assert that
    /// an attempt left nothing running instead of assuming it.
    /// </para>
    /// </remarks>
    private sealed class ScriptedReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly object _gate = new();

        /// <summary>The waiters no read has observed yet — zero once every read has unwound.</summary>
        private readonly List<TaskCompletionSource<bool>> _pendingWaiters = [];

        /// <summary>EVERY wait ever created, so teardown can prove they all settled.</summary>
        private readonly List<Task> _allWaits = [];

        private Exception? _failure;
        private bool _completed;
        private int _inFlightReads;

        public OrchestratorMessage Current { get; private set; } = null!;

        /// <summary>Reads that have ENTERED <c>MoveNext</c> and not yet returned.</summary>
        internal int InFlightReads => Volatile.Read(ref _inFlightReads);

        /// <summary>Waits that have not been observed by their own read yet.</summary>
        internal int PendingWaiterCount
        {
            get { lock (_gate) return _pendingWaiters.Count; }
        }

        /// <summary>
        /// Signals a transport fault. The waiting (or next) <c>MoveNext</c> throws it VERBATIM, so the
        /// production loop sees the real exception type rather than a wrapper.
        /// </summary>
        internal void Fail(Exception exception)
        {
            lock (_gate)
                _failure ??= exception;

            ReleaseWaiters();
        }

        /// <summary>Ends the stream cleanly (EOF). Idempotent.</summary>
        internal void Complete()
        {
            lock (_gate)
                _completed = true;

            ReleaseWaiters();
        }

        /// <summary>
        /// A handle completing once EVERY wait this reader ever created has settled. It never throws —
        /// each wait is observed — so teardown can bound it and record a genuine failure to drain
        /// rather than swallowing one.
        /// </summary>
        internal Task WhenAllWaitsSettledAsync()
        {
            List<Task> snapshot;
            lock (_gate)
                snapshot = [.. _allWaits];

            return Task.WhenAll(snapshot.Select(Observed));

            static Task Observed(Task wait) =>
                wait.ContinueWith(
                    static completed => { _ = completed.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _inFlightReads);
            try
            {
                while (true)
                {
                    TaskCompletionSource<bool> waiter;
                    lock (_gate)
                    {
                        // A fault always wins, then EOF: both are terminal and checked before any wait
                        // is created, so a signal that arrives first is never missed.
                        if (_failure is { } failure)
                            throw failure;

                        if (_completed)
                            return false;

                        waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _pendingWaiters.Add(waiter);
                        _allWaits.Add(waiter.Task);
                    }

                    // The cancellation registration settles THE SAME waiter, so a cancelled read
                    // unwinds with OperationCanceledException without leaving any other task pending.
                    await using var registration = cancellationToken.Register(
                        static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), waiter);

                    try
                    {
                        await waiter.Task;
                    }
                    finally
                    {
                        lock (_gate)
                            _pendingWaiters.Remove(waiter);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightReads);
            }
        }

        /// <summary>Settles every outstanding waiter so its own read can re-evaluate the state.</summary>
        private void ReleaseWaiters()
        {
            List<TaskCompletionSource<bool>> snapshot;
            lock (_gate)
                snapshot = [.. _pendingWaiters];

            foreach (var waiter in snapshot)
                waiter.TrySetResult(true);
        }
    }

    /// <summary>
    /// Records every <see cref="WorkerMessage"/> an attempt writes and lets a test await a specific
    /// write count deterministically. The CANCELLABLE write overload is implemented explicitly, since
    /// production writes with the live stream token.
    /// </summary>
    private sealed class RecordingWriter : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _writes = [];
        private readonly Dictionary<int, TaskCompletionSource> _countWaiters = [];

        internal IReadOnlyList<WorkerMessage> Writes
        {
            get { lock (_gate) return _writes.ToList(); }
        }

        /// <summary>
        /// Completes once at least <paramref name="count"/> writes were recorded. The bound is a
        /// FAILURE bound: its expiry throws <see cref="TimeoutException"/> and fails the test.
        /// </summary>
        internal Task WaitForWriteCountAsync(int count, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_writes.Count >= count)
                    return Task.CompletedTask;

                if (!_countWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _countWaiters[count] = waiter;
                }

                return waiter.Task.WaitAsync(Failsafe, ct);
            }
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message)
        {
            Record(message);
            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync() => Task.CompletedTask;

        private void Record(WorkerMessage message)
        {
            List<TaskCompletionSource> ready = [];
            List<int> satisfied = [];

            lock (_gate)
            {
                _writes.Add(message);

                foreach (var (threshold, waiter) in _countWaiters)
                {
                    if (_writes.Count >= threshold)
                    {
                        ready.Add(waiter);
                        satisfied.Add(threshold);
                    }
                }

                foreach (var threshold in satisfied)
                    _countWaiters.Remove(threshold);
            }

            foreach (var waiter in ready)
                waiter.TrySetResult();
        }
    }

    /// <summary>
    /// An <see cref="IAgentRunner"/> recording the provisioning callback the REAL lifecycle hands it,
    /// so a test can reach the EXACT production provisioner path (eager and lazy share it) without any
    /// <c>TestProvisioner</c> override.
    /// </summary>
    private sealed class CapturingRunner : IAgentRunner
    {
        internal Func<string?, CancellationToken, Task>? ConfigProvisioner { get; private set; }

        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) =>
            ConfigProvisioner = provisioner;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct) =>
            Task.FromResult(string.Empty);

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public void SetMaxContextTokens(int maxTokens) { }
        public int GetContextUsagePercent() => 0;
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
