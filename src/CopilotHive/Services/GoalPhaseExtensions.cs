using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Extension methods for the <see cref="GoalPhase"/> enum.
/// </summary>
public static class GoalPhaseExtensions
{
    /// <summary>
    /// Returns a human-friendly display name for the given <see cref="GoalPhase"/>.
    /// </summary>
    /// <param name="phase">The pipeline phase to get a display name for.</param>
    /// <returns>A human-readable string representation of the phase.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="phase"/> is not a known <see cref="GoalPhase"/> value.</exception>
    public static string ToDisplayName(this GoalPhase phase) => phase switch
    {
        GoalPhase.Planning   => "Planning",
        GoalPhase.Coding     => "Coding",
        GoalPhase.Review     => "Review",
        GoalPhase.Testing    => "Testing",
        GoalPhase.DocWriting => "Doc Writing",
        GoalPhase.Improve    => "Improvement",
        GoalPhase.Merging    => "Merging",
        GoalPhase.Done       => "Done",
        GoalPhase.Failed     => "Failed",
        _                    => throw new InvalidOperationException($"Unknown GoalPhase: {phase}")
    };

    /// <summary>
    /// Maps a <see cref="GoalPhase"/> to the corresponding <see cref="WorkerRole"/>.
    /// Only valid for phases that dispatch to a worker.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown for phases that have no worker (Planning, Merging, Done, Failed).</exception>
    public static WorkerRole ToWorkerRole(this GoalPhase phase) => phase switch
    {
        GoalPhase.Coding     => WorkerRole.Coder,
        GoalPhase.Testing    => WorkerRole.Tester,
        GoalPhase.Review     => WorkerRole.Reviewer,
        GoalPhase.DocWriting => WorkerRole.DocWriter,
        GoalPhase.Improve    => WorkerRole.Improver,
        GoalPhase.Planning
            or GoalPhase.Merging
            or GoalPhase.Done
            or GoalPhase.Failed => throw new InvalidOperationException(
                $"GoalPhase '{phase}' does not map to a WorkerRole — it has no worker."),
        _ => throw new InvalidOperationException($"Unknown GoalPhase: {phase}"),
    };
}
