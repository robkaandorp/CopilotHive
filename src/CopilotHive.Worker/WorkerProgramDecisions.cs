namespace CopilotHive.Worker;

/// <summary>
/// What the worker's attempt loop does with one RETURNED <see cref="WorkerRunOutcome"/>.
/// </summary>
internal enum WorkerOutcomeAction
{
    /// <summary>The attempt is over but the process is not: re-enter the SAME service after the backoff.</summary>
    Reconnect,

    /// <summary>The loop ends with the current exit code (a rejected registration is not retryable).</summary>
    Stop,

    /// <summary>The loop ends and the process fails: the outcome is not one this loop knows.</summary>
    Fatal,
}

/// <summary>
/// THE TWO PROGRAM DECISIONS, extracted VERBATIM from <c>Program.cs</c> so they can be exercised
/// directly: the returned-outcome decision of the attempt loop, and the final carried-assignment
/// drain whose failure turns the process into a failure.
/// </summary>
/// <remarks>
/// A PURE EXTRACTION, not a redesign. <c>Program.cs</c> still owns the loop, the backoff, every
/// diagnostic it wrote before, the ONE final disposal and the exit-code decision; these helpers only
/// return the decision it used to compute inline, so its observable behavior is unchanged. They hold
/// no state and add no retry, timer or fallback of their own.
/// </remarks>
internal static class WorkerProgramDecisions
{
    /// <summary>
    /// THE RETURNED-OUTCOME DECISION: <see cref="WorkerRunOutcome.WorkStreamEnded"/> reconnects on
    /// the same service, <see cref="WorkerRunOutcome.RegistrationRejected"/> stops, and any other
    /// value is FATAL.
    /// </summary>
    /// <remarks>
    /// The unknown case is deliberately an explicit <see cref="WorkerOutcomeAction.Fatal"/>, never a
    /// silent retry or a silent exit — exactly what the inline handling did before it was extracted.
    /// </remarks>
    /// <param name="outcome">The outcome the awaited run genuinely returned.</param>
    /// <returns>The action the attempt loop takes.</returns>
    internal static WorkerOutcomeAction DecideOutcome(WorkerRunOutcome outcome) => outcome switch
    {
        WorkerRunOutcome.WorkStreamEnded => WorkerOutcomeAction.Reconnect,
        WorkerRunOutcome.RegistrationRejected => WorkerOutcomeAction.Stop,
        _ => WorkerOutcomeAction.Fatal,
    };

    /// <summary>
    /// THE FINAL CARRIED-ASSIGNMENT DRAIN: runs <paramref name="drain"/> ONCE and reports whether it
    /// FAILED. A failure — any exception, cancellation included — is written through ONE guarded,
    /// sanitized <c>[Worker] Fatal error [...]</c> line on <see cref="Console.Error"/> (classification
    /// only, never the raw message) and is NEVER retried.
    /// </summary>
    /// <remarks>
    /// It never throws: the caller's ONE final disposal must always run after it, and the caller alone
    /// turns a <c>true</c> result into the fatal exit code.
    /// </remarks>
    /// <param name="drain">The drain to run — in production, the service's carried-assignment drain.</param>
    /// <returns><c>true</c> when the drain threw; otherwise <c>false</c>.</returns>
    internal static async Task<bool> DrainCarriedAssignmentFailedAsync(Func<Task> drain)
    {
        try
        {
            await drain();
            return false;
        }
        catch (Exception ex)
        {
            // Sanitized for the same reason as the attempt paths: this boundary reaches the
            // assignment's transport and the agent runner, whose errors can quote provisioned
            // configuration. GUARDED: a degraded sink can never change the outcome it reports.
            try
            {
                Console.Error.WriteLine($"[Worker] Fatal error [{SafeExceptionLog.Describe(ex)}]");
            }
            catch (Exception)
            {
                // A diagnostic must never change the outcome it is merely reporting.
            }

            return true;
        }
    }
}
