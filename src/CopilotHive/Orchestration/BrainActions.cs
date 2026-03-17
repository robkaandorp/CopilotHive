using System.Text.Json;

namespace CopilotHive.Orchestration;

/// <summary>
/// Canonical snake_case action names used in Brain prompt templates.
/// Derived from <see cref="OrchestratorActionType"/> to stay in sync with the JSON protocol.
/// </summary>
public static class BrainActions
{
    private static readonly JsonNamingPolicy Naming = JsonNamingPolicy.SnakeCaseLower;

    // ── Spawn actions (visible to Brain in prompts) ──────────────────────

    /// <summary>Spawn a coder worker.</summary>
    public static readonly string SpawnCoder = ToSnakeCase(OrchestratorActionType.SpawnCoder);

    /// <summary>Spawn a reviewer worker.</summary>
    public static readonly string SpawnReviewer = ToSnakeCase(OrchestratorActionType.SpawnReviewer);

    /// <summary>Spawn a tester worker.</summary>
    public static readonly string SpawnTester = ToSnakeCase(OrchestratorActionType.SpawnTester);

    /// <summary>Spawn an improver worker.</summary>
    public static readonly string SpawnImprover = ToSnakeCase(OrchestratorActionType.SpawnImprover);

    /// <summary>Spawn a doc-writer worker.</summary>
    public static readonly string SpawnDocWriter = ToSnakeCase(OrchestratorActionType.SpawnDocWriter);

    // ── Flow-control actions ─────────────────────────────────────────────

    /// <summary>Request code changes from the coder.</summary>
    public static readonly string RequestChanges = ToSnakeCase(OrchestratorActionType.RequestChanges);

    /// <summary>Retry the current phase.</summary>
    public static readonly string Retry = ToSnakeCase(OrchestratorActionType.Retry);

    /// <summary>Merge the feature branch.</summary>
    public static readonly string Merge = ToSnakeCase(OrchestratorActionType.Merge);

    /// <summary>Goal is complete.</summary>
    public static readonly string Done = ToSnakeCase(OrchestratorActionType.Done);

    /// <summary>Skip the current phase.</summary>
    public static readonly string Skip = ToSnakeCase(OrchestratorActionType.Skip);

    // ── Grouped lists for prompt templates ────────────────────────────────

    /// <summary>All spawn actions (worker-dispatching).</summary>
    public static readonly string[] SpawnAll =
        [SpawnCoder, SpawnReviewer, SpawnTester, SpawnImprover, SpawnDocWriter];

    /// <summary>Actions available during goal planning (PlanGoalAsync).</summary>
    public static readonly string[] PlanningActions =
        [SpawnCoder, SpawnReviewer, SpawnTester, SpawnDocWriter, Done, Skip];

    /// <summary>Actions available when deciding next step (DecideNextStepAsync).</summary>
    public static readonly string[] NextStepActions =
        [SpawnCoder, SpawnReviewer, SpawnTester, SpawnDocWriter, Merge, Done, Skip];

    /// <summary>Formats an action list for inclusion in a Brain prompt template.</summary>
    public static string FormatForPrompt(string[] actions) => string.Join(", ", actions);

    private static string ToSnakeCase(OrchestratorActionType action) =>
        Naming.ConvertName(action.ToString());
}
