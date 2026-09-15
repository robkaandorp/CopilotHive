namespace CopilotHive.Worker;

/// <summary>
/// THE OBSERVED OUTCOME OF ONE WORKER RUN — the distinction between the two ways
/// <see cref="WorkerService.RunAsync"/> can finish without a failure escaping it.
/// </summary>
/// <remarks>
/// <para>
/// This describes ONLY what the run itself observed. A returned value is deliberately NOT evidence
/// of remote shutdown intent, task success, the absence of a concurrent cancellation request,
/// retryability, or a failure reason: every escaping exception still propagates unchanged, so a
/// returned outcome means no failure left the method.
/// </para>
/// <para>
/// There is deliberately no synthetic "shutdown" member and no extra state: a successful run
/// reports exactly one of the two facts below.
/// </para>
/// </remarks>
public enum WorkerRunOutcome
{
    /// <summary>
    /// The orchestrator REJECTED the registration. Reported before any work stream was opened, any
    /// <see cref="WorkerConnection"/> was constructed, or anything was published.
    /// </summary>
    RegistrationRejected,

    /// <summary>
    /// The ACCEPTED registration's work stream finished normally: the message loop ended (a
    /// controlled EOF) and the whole run cleanup completed without a failure. The value is produced
    /// only once the entire <see cref="WorkerService.RunAsync"/> — including its lexical transport
    /// disposal — has finished.
    /// </summary>
    WorkStreamEnded,
}
