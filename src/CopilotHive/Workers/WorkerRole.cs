namespace CopilotHive.Workers;

/// <summary>Role assigned to a worker container.</summary>
public enum WorkerRole
{
    /// <summary>Implements code changes to fulfil a goal.</summary>
    Coder,
    /// <summary>Runs and writes automated tests.</summary>
    Tester,
    /// <summary>Reviews code changes and produces a REVIEW_REPORT.</summary>
    Reviewer,
    /// <summary>Improves AGENTS.md files based on iteration feedback.</summary>
    Improver,
    /// <summary>LLM-powered orchestrator that plans and directs the other workers.</summary>
    Orchestrator,
    /// <summary>Writes or updates documentation.</summary>
    DocWriter,
    /// <summary>Merges feature branches into the main branch.</summary>
    MergeWorker,
}
