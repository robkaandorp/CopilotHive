using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    private static bool IsValidGoalId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        try { GoalId.Validate(id); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";

    [GeneratedRegex(@"^\s*#{1,6}\s+", RegexOptions.Compiled)]
    private static partial Regex HeadingMarkerRegex();

    /// <summary>
    /// Cleans a description for display in list contexts by stripping leading headings
    ///, collapsing whitespace, and truncating to the specified max length.
    /// </summary>
    private static string CleanDescription(string? description, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        var cleaned = description.TrimStart();
        cleaned = HeadingMarkerRegex().Replace(cleaned, string.Empty);
        cleaned = cleaned.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
        // Collapse any extra spaces that may have resulted from newline replacement
        while (cleaned.Contains("  "))
            cleaned = cleaned.Replace("  ", " ");

        return Truncate(cleaned, maxLength);
    }

    [Description("Create a new goal as Draft status. Returns the created goal summary.")]
    internal async Task<string> CreateGoalAsync(
        [Description("Unique goal ID in lowercase-kebab-case (e.g. 'add-user-auth')")] string id,
        [Description("Clear description including acceptance criteria")] string description,
        [Description("Comma-separated repository names this goal applies to")] string? repositories = null,
        [Description("Priority: Low, Normal, High, or Critical. Default: Normal")] string? priority = null,
        [Description("Comma-separated goal IDs this goal depends on")] string? depends_on = null,
        [Description("Scope: Patch, Feature, or Breaking. Default: Patch")] string? scope = null,
        [Description("Comma-separated knowledge document IDs to attach to this goal")] string? documents = null)
    {
        var isValidId = IsValidGoalId(id);
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"),
            (!string.IsNullOrWhiteSpace(description), "description is required"),
            (isValidId, $"invalid goal ID '{id}': must be lowercase-kebab-case (a-z, 0-9, hyphens)"));
        if (error is not null) return error;

        var existing = await _goalStore.GetGoalAsync(id);
        if (existing is not null)
            return $"❌ Goal '{id}' already exists (status: {existing.Status.ToDisplayName()}).";

        var goalPriority = GoalPriority.Normal;
        if (!string.IsNullOrEmpty(priority) && Enum.TryParse<GoalPriority>(priority, ignoreCase: true, out var p))
            goalPriority = p;

        var goalScope = GoalScope.Patch;
        if (!string.IsNullOrEmpty(scope) && Enum.TryParse<GoalScope>(scope, ignoreCase: true, out var s))
            goalScope = s;

        var repos = string.IsNullOrWhiteSpace(repositories)
            ? new List<string>()
            : repositories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var deps = string.IsNullOrWhiteSpace(depends_on)
            ? new List<string>()
            : depends_on.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var docs = string.IsNullOrWhiteSpace(documents)
            ? new List<string>()
            : documents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var goal = new Goal
        {
            Id = id,
            Description = description,
            Priority = goalPriority,
            Scope = goalScope,
            Status = GoalStatus.Draft,
            RepositoryNames = repos,
            DependsOn = deps,
            Documents = docs,
        };

        await _goalStore.CreateGoalAsync(goal);
        _logger.LogInformation("Composer created draft goal '{GoalId}'", id);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("✅ Goal created as Draft:");
        sb.AppendLine($"- ID: {id}");
        sb.AppendLine($"- Priority: {goalPriority}");
        sb.AppendLine($"- Scope: {goalScope}");
        sb.AppendLine($"- Repositories: {(repos.Count > 0 ? string.Join(", ", repos) : "(none)")}");
        sb.AppendLine($"- Dependencies: {(deps.Count > 0 ? string.Join(", ", deps) : "(none)")}");
        sb.Append("- Status: Draft (not yet dispatched — use approve_goal to queue it)");
        AppendDocumentsList(sb, goal.Documents);
        return sb.ToString();
    }

    [Description("Approve a Draft goal, changing its status to Pending for dispatch.")]
    internal async Task<string> ApproveGoalAsync(
        [Description("Goal ID to approve")] string id)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"❌ Goal '{id}' not found.";

        if (goal.Status != GoalStatus.Draft)
            return $"❌ Goal '{id}' is {goal.Status.ToDisplayName()}, not Draft. Only Draft goals can be approved.";

        goal.Status = GoalStatus.Pending;
        await _goalStore.UpdateGoalAsync(goal);
        _logger.LogInformation("Composer approved goal '{GoalId}' → Pending", id);

        return $"✅ Goal '{id}' approved — status changed to Pending. It will be dispatched in the next cycle.";
    }

    [Description("Permanently delete a goal. Only Draft or Failed goals can be deleted.")]
    internal async Task<string> DeleteGoalAsync(
        [Description("Goal ID to delete")] string id)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"❌ Goal '{id}' not found.";

        if (goal.Status is not (GoalStatus.Draft or GoalStatus.Failed))
            return $"❌ Goal '{id}' is {goal.Status.ToDisplayName()}. Only Draft or Failed goals can be deleted.";

        var deleted = await _goalStore.DeleteGoalAsync(id);
        if (!deleted)
            return $"❌ Failed to delete goal '{id}'.";

        _logger.LogInformation("Composer deleted goal '{GoalId}'", id);

        // Best-effort cleanup of remote feature branches for Failed goals
        if (_repoManager is not null && goal.Status == GoalStatus.Failed)
        {
            var branchName = $"copilothive/{id}";
            foreach (var repoName in goal.RepositoryNames)
            {
                _ = await _repoManager.DeleteRemoteBranchAsync(repoName, branchName);
            }
        }

        return $"✅ Goal '{id}' has been permanently deleted.";
    }

    [Description("Cancel an InProgress or Pending goal, stopping its execution.")]
    internal async Task<string> CancelGoalAsync(
        [Description("Goal ID to cancel")] string id)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"❌ Goal '{id}' not found.";

        if (goal.Status is not (GoalStatus.InProgress or GoalStatus.Pending))
            return $"❌ Goal '{id}' is {goal.Status.ToDisplayName()}. Only InProgress or Pending goals can be cancelled.";

        var goalDispatcher = _serviceProvider?.GetService<GoalDispatcher>();
        if (goalDispatcher is null)
            return "❌ Goal dispatcher is not available — cannot cancel goals.";

        var cancelled = await goalDispatcher.CancelGoalAsync(id);
        if (!cancelled)
            return $"❌ Goal '{id}' could not be cancelled (it may have already completed or failed).";

        _logger.LogInformation("Composer cancelled goal '{GoalId}'", id);
        return $"✅ Goal '{id}' has been cancelled.";
    }

    [Description("Update a field on an existing goal.")]
    internal async Task<string> UpdateGoalAsync(
        [Description("Goal ID to update")] string id,
        [Description("Field to update: description, priority, scope, repositories, depends_on, documents, status, or release")] string field,
        [Description("New value for the field")] string value)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"),
            (!string.IsNullOrWhiteSpace(field), "field is required"),
            (string.Equals(field, "release", StringComparison.OrdinalIgnoreCase)
             || string.Equals(field, "depends_on", StringComparison.OrdinalIgnoreCase)
             || string.Equals(field, "documents", StringComparison.OrdinalIgnoreCase)
             || !string.IsNullOrWhiteSpace(value), "value is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"❌ Goal '{id}' not found.";

        switch (field.ToLowerInvariant())
        {
            case "description":
                if (goal.Status != GoalStatus.Draft)
                    return $"❌ Cannot edit description of a goal in '{goal.Status}' status. Only Draft goals can be edited.";
                goal.Description = value;
                await _goalStore.UpdateGoalAsync(goal);
                _logger.LogInformation("Composer updated goal '{GoalId}' description", id);
                return AppendDocuments($"✅ Goal '{id}' description updated.", goal);

            case "priority":
                if (int.TryParse(value, out _))
                    return $"❌ Invalid priority '{value}'. Valid: Low, Normal, High, Critical.";
                if (!Enum.TryParse<GoalPriority>(value, ignoreCase: true, out var newPriority))
                    return $"❌ Invalid priority '{value}'. Valid: Low, Normal, High, Critical.";
                if (!Enum.IsDefined(typeof(GoalPriority), newPriority))
                    return $"❌ Invalid priority '{value}'. Valid: Low, Normal, High, Critical.";
                if (goal.Status != GoalStatus.Draft)
                    return $"❌ Cannot edit priority of a goal in '{goal.Status}' status. Only Draft goals can be edited.";
                goal.Priority = newPriority;
                await _goalStore.UpdateGoalAsync(goal);
                _logger.LogInformation("Composer updated goal '{GoalId}' priority to {Priority}", id, newPriority);
                return AppendDocuments($"✅ Goal '{id}' priority updated to {newPriority}.", goal);

            case "status":
                if (!Enum.TryParse<GoalStatus>(value, ignoreCase: true, out var newStatus))
                    return $"❌ Invalid status '{value}'. Valid: Draft, Pending.";
                if (newStatus is not (GoalStatus.Draft or GoalStatus.Pending))
                    return $"❌ Can only set status to Draft or Pending via update_goal.";
                var validTransition =
                    (goal.Status == GoalStatus.Draft && newStatus == GoalStatus.Pending) ||
                    (goal.Status == GoalStatus.Pending && newStatus == GoalStatus.Draft) ||
                    (goal.Status == GoalStatus.Failed && newStatus == GoalStatus.Draft);
                if (!validTransition)
                    return $"❌ Invalid transition from {goal.Status.ToDisplayName()} to {newStatus.ToDisplayName()}. Only Draft→Pending, Pending→Draft, and Failed→Draft are allowed.";

                // Failed→Draft: reset iteration data and clean up feature branch (best-effort)
                if (goal.Status == GoalStatus.Failed && newStatus == GoalStatus.Draft)
                {
                    await _goalStore.ResetGoalIterationDataAsync(id);

                    // Clear GoalDispatcher runtime state so the goal can be re-dispatched fresh
                    _serviceProvider?.GetService<GoalDispatcher>()?.ClearGoalRetryState(id);

                    // Clear iteration data on the goal object to prevent overwriting with old values
                    goal.FailureReason = null;
                    goal.Iterations = 0;
                    goal.TotalDurationSeconds = null;
                    goal.StartedAt = null;
                    goal.CompletedAt = null;
                    goal.IterationSummaries = [];

                    if (_repoManager is not null)
                    {
                        var branchName = $"copilothive/{id}";
                        foreach (var repoName in goal.RepositoryNames)
                        {
                            _ = await _repoManager.DeleteRemoteBranchAsync(repoName, branchName);
                        }
                    }
                }

                goal.Status = newStatus;
                await _goalStore.UpdateGoalAsync(goal);
                _logger.LogInformation("Composer updated goal '{GoalId}' status to {Status}", id, newStatus);
                return AppendDocuments($"✅ Goal '{id}' status updated to {newStatus.ToDisplayName()}.", goal);

            case "repositories":
                if (goal.Status != GoalStatus.Draft)
                    return $"❌ Cannot edit repositories of a goal in '{goal.Status}' status. Only Draft goals can be edited.";
                var repos = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                goal.RepositoryNames = repos;
                await _goalStore.UpdateGoalAsync(goal);
                _logger.LogInformation("Composer updated goal '{GoalId}' repositories to {Repositories}", id, string.Join(", ", repos));
                return AppendDocuments($"✅ Goal '{id}' repositories updated to: {(repos.Count > 0 ? string.Join(", ", repos) : "(none)")}.", goal);

            case "scope":
                if (int.TryParse(value, out _))
                    return $"❌ Invalid scope '{value}'. Valid: Patch, Feature, Breaking.";
                if (!Enum.TryParse<GoalScope>(value, ignoreCase: true, out var newScope))
                    return $"❌ Invalid scope '{value}'. Valid: Patch, Feature, Breaking.";
                if (!Enum.IsDefined(typeof(GoalScope), newScope))
                    return $"❌ Invalid scope '{value}'. Valid: Patch, Feature, Breaking.";
                if (goal.Status != GoalStatus.Draft)
                    return $"❌ Cannot edit scope of a goal in '{goal.Status}' status. Only Draft goals can be edited.";
                goal.Scope = newScope;
                await _goalStore.UpdateGoalAsync(goal);
                _logger.LogInformation("Composer updated goal '{GoalId}' scope to {Scope}", id, newScope);
                return AppendDocuments($"✅ Goal '{id}' scope updated to {newScope}.", goal);

            case "depends_on":
                if (goal.Status != GoalStatus.Draft)
                    return $"❌ Cannot edit depends_on of a goal in '{goal.Status}' status. Only Draft goals can be edited.";
                var deps = string.IsNullOrWhiteSpace(value)
                    ? new List<string>()
                    : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                goal.DependsOn = deps;
                await _goalStore.UpdateGoalAsync(goal);
                _logger.LogInformation("Composer updated goal '{GoalId}' depends_on to {DependsOn}", id, string.Join(", ", deps));
                return AppendDocuments($"✅ Goal '{id}' depends_on updated to: {(deps.Count > 0 ? string.Join(", ", deps) : "(none)")}.", goal);

            case "documents":
                if (goal.Status != GoalStatus.Draft)
                    return $"❌ Cannot edit documents of a goal in '{goal.Status}' status. Only Draft goals can be edited.";
                var docs = string.IsNullOrWhiteSpace(value)
                    ? new List<string>()
                    : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                goal.Documents = docs;
                await _goalStore.UpdateGoalAsync(goal);
                _logger.LogInformation("Composer updated goal '{GoalId}' documents", id);
                return AppendDocuments($"✅ Goal '{id}' documents updated.", goal);

            case "release":
                if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
                {
                    goal.ReleaseId = null;
                    await _goalStore.UpdateGoalAsync(goal);
                    _logger.LogInformation("Composer cleared release on goal '{GoalId}'", id);
                    return AppendDocuments($"✅ Goal '{id}' release cleared.", goal);
                }
                var release = await _goalStore.GetReleaseAsync(value);
                if (release is null)
                    return $"❌ Release '{value}' not found.";
                goal.ReleaseId = release.Id;
                await _goalStore.UpdateGoalAsync(goal);
                _logger.LogInformation("Composer set release on goal '{GoalId}' to '{ReleaseId}'", id, release.Id);
                return AppendDocuments($"✅ Goal '{id}' release set to '{release.Id}'.", goal);

            default:
                return $"❌ Unknown field '{field}'. Valid fields: description, priority, scope, repositories, depends_on, documents, status, release.";
        }
    }

    [Description("Get full details for a goal including iteration history.")]
    internal async Task<string> GetGoalAsync(
        [Description("Goal ID to look up")] string id)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"Goal '{id}' not found.";

        var iterations = await _goalStore.GetIterationsAsync(id);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Goal: {goal.Id}");
        sb.AppendLine($"- **Status:** {goal.Status.ToDisplayName()}");
        sb.AppendLine($"- **Priority:** {goal.Priority}");
        sb.AppendLine($"- **Created:** {goal.CreatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- **Repositories:** {(goal.RepositoryNames.Count > 0 ? string.Join(", ", goal.RepositoryNames) : "(none)")}");
        sb.AppendLine($"- **Description:** {goal.Description}");

        if (goal.Documents.Count > 0)
            sb.AppendLine($"- **Documents:** {string.Join(", ", goal.Documents)}");

        if (goal.FailureReason is not null)
            sb.AppendLine($"- **Failure:** {goal.FailureReason}");

        if (goal.TotalDurationSeconds.HasValue)
            sb.AppendLine($"- **Duration:** {TimeSpan.FromSeconds(goal.TotalDurationSeconds.Value):hh\\:mm\\:ss}");

        if (iterations.Count > 0)
        {
            sb.AppendLine($"\n### Iterations ({iterations.Count})");
            foreach (var iter in iterations)
            {
                var reviewSuffix = iter.ReviewVerdict is not null ? $" (review: {iter.ReviewVerdict})" : "";
                sb.AppendLine($"\n### Iteration {iter.Iteration}{reviewSuffix}");
                foreach (var phase in iter.Phases)
                {
                    var durationStr = phase.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
                    var resultStr = phase.Result.ToString().ToLowerInvariant();
                    var line = $"- {phase.Name}: {resultStr} ({durationStr})";
                    if (phase.Name == GoalPhase.Testing && iter.TestCounts is not null)
                        line += $" — {iter.TestCounts.Passed}/{iter.TestCounts.Total}";
                    sb.AppendLine(line);
                }

                if (iter.Clarifications is { Count: > 0 })
                {
                    sb.AppendLine("  Clarifications:");
                    foreach (var c in iter.Clarifications)
                    {
                        sb.AppendLine($"  - [{c.AnsweredBy}] {c.WorkerRole} ({c.Phase}): Q: {c.Question}");
                        sb.AppendLine($"    A: {c.Answer}");
                    }
                }
            }
        }

        if (goal.Notes.Count > 0)
        {
            sb.AppendLine($"\n### Notes");
            foreach (var note in goal.Notes)
                sb.AppendLine($"- {note}");
        }

        return sb.ToString().Replace("\r\n", "\n");
    }

    [Description("Get the raw worker output, brain prompt, or worker prompt for a specific phase within an iteration.")]
    internal async Task<string> GetPhaseOutputAsync(
        [Description("Goal ID")] string id,
        [Description("Iteration number (1-based)")] int iteration,
        [Description("Phase name: Coding, Testing, Review, DocWriting, or Improve")] string phase,
        [Description("Maximum lines to return. Default: 200")] int max_lines = 200,
        [Description("What to return: output (default), brain_prompt, or worker_prompt")] string content = "output")
    {
        // 1. Explicit required-param validation with EXACT messages FIRST
        if (string.IsNullOrWhiteSpace(id))
            return "Goal ID is required";
        if (iteration <= 0)
            return "Iteration must be a positive number";
        if (string.IsNullOrWhiteSpace(phase))
            return "ERROR: Invalid parameters: phase is required";

        // 2. Whitelist check SECOND
        // Reject numeric strings that Enum.TryParse would accept (e.g. "1" → GoalPhase.Coding)
        if (int.TryParse(phase, out _))
            return $"Unknown phase '{phase}'. Supported phases: Coding, Testing, Review, DocWriting, Improve.";

        if (!Enum.TryParse<GoalPhase>(phase, ignoreCase: true, out var goalPhase))
            return $"Unknown phase '{phase}'. Supported phases: Coding, Testing, Review, DocWriting, Improve.";
        var rolePrefix = goalPhase.ToRoleName();
        if (string.IsNullOrEmpty(rolePrefix))
            return $"Phase '{phase}' does not have a worker output key.";

        // 3. Validate content parameter
        if (content is not "output" and not "brain_prompt" and not "worker_prompt")
            return $"Invalid content '{content}'. Valid values: output, brain_prompt, worker_prompt.";

        // 4. Handle brain_prompt / worker_prompt via PhaseResult entries
        if (content is "brain_prompt" or "worker_prompt")
        {
            // Verify the goal still exists before retrieving prompt data
            var promptGoal = await _goalStore.GetGoalAsync(id);
            if (promptGoal is null)
                return $"No {content.Replace('_', ' ')} is available for phase '{phase}' in iteration {iteration} of goal '{id}'.";

            // Look for the prompt in persisted iteration summaries
            var iterationsForPrompt = await _goalStore.GetIterationsAsync(id);
            var iterSummaryForPrompt = iterationsForPrompt.FirstOrDefault(i => i.Iteration == iteration);
            PhaseResult? phaseEntry = null;
            if (iterSummaryForPrompt is not null)
            {
                phaseEntry = iterSummaryForPrompt.Phases
                    .LastOrDefault(p => p.Name.ToString().Equals(phase, StringComparison.OrdinalIgnoreCase));
            }

            if (phaseEntry is null)
                return $"No {content.Replace('_', ' ')} is available for phase '{phase}' in iteration {iteration} of goal '{id}'.";

            var promptText = content == "brain_prompt" ? phaseEntry.BrainPrompt : phaseEntry.WorkerPrompt;
            if (string.IsNullOrEmpty(promptText))
                return $"No {content.Replace('_', ' ')} is available for phase '{phase}' in iteration {iteration} of goal '{id}'.";

            var promptLines = promptText.Split('\n');
            if (promptLines.Length <= max_lines)
                return promptText;

            var truncatedPrompt = string.Join('\n', promptLines.Take(max_lines));
            return truncatedPrompt + $"\n... (truncated, {promptLines.Length} lines total)";
        }

        // 5. Fetch goal (output mode)
        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return "Goal not found";

        // 6. Fetch iterations and find the requested iteration
        var iterations = await _goalStore.GetIterationsAsync(id);
        var iterSummary = iterations.FirstOrDefault(i => i.Iteration == iteration);
        if (iterSummary is null)
            return $"Iteration {iteration} not found";

        // 7. Find the phase in the iteration
        var phaseResult = iterSummary.Phases
            .FirstOrDefault(p => p.Name.ToString().Equals(phase, StringComparison.OrdinalIgnoreCase));
        if (phaseResult is null)
            return $"Phase '{phase}' not found in iteration {iteration}";

        // 8. Check worker output, then fall back to PhaseOutputs dictionary
        string? output = phaseResult.WorkerOutput;

        if (string.IsNullOrEmpty(output))
        {
            var outputKey = $"{rolePrefix}-{iteration}";
            iterSummary.PhaseOutputs.TryGetValue(outputKey, out output);
        }

        if (string.IsNullOrEmpty(output))
            return $"No output recorded for phase {phase} in iteration {iteration}";

        var lines = output.Split('\n');
        if (lines.Length <= max_lines)
            return output;

        var truncated = string.Join('\n', lines.Take(max_lines));
        return truncated + $"\n... (truncated, {lines.Length} lines total)";
    }

    [Description("List goals, optionally filtered by status.")]
    internal async Task<string> ListGoalsAsync(
        [Description("Optional status filter: Draft, Pending, InProgress, Completed, Failed")] string? status = null)
    {
        IReadOnlyList<Goal> goals;

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var filter))
        {
            goals = await _goalStore.GetGoalsByStatusAsync(filter);
        }
        else
        {
            goals = await _goalStore.GetAllGoalsAsync();
        }

        if (goals.Count == 0)
            return status is not null ? $"No goals with status '{status}'." : "No goals found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**{goals.Count} goal(s):**\n");

        foreach (var g in goals.OrderByDescending(g => g.CreatedAt))
        {
            sb.AppendLine($"- `{g.Id}` [{g.Status.ToDisplayName()}] {g.Priority} — {CleanDescription(g.Description, 150)}");
        }

        return sb.ToString();
    }

    [Description("Search goals using tokenized multi-term search (AND logic) across ID, description, and failure reason.")]
    internal async Task<string> SearchGoalsAsync(
        [Description("Search query text")] string query,
        [Description("Optional status filter: Draft, Pending, InProgress, Completed, Failed")] string? status = null)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(query), "query is required"));
        if (error is not null) return error;

        GoalStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var s))
            statusFilter = s;

        var results = await _goalStore.SearchGoalsAsync(query, statusFilter);

        if (results.Count == 0)
            return $"No goals matching '{query}'" + (statusFilter.HasValue ? $" with status {statusFilter}" : "") + ".";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**{results.Count} result(s) for '{query}':**\n");

        foreach (var g in results)
        {
            sb.AppendLine($"- `{g.Id}` [{g.Status.ToDisplayName()}] — {CleanDescription(g.Description, 150)}");
        }

        return sb.ToString();
    }

    [Description("List all configured repositories with their names, URLs, and default branches.")]
    internal Task<string> ListRepositoriesAsync()
    {
        var repos = _hiveConfig?.Repositories;
        if (repos is null || repos.Count == 0)
            return Task.FromResult("No repositories configured.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Configured Repositories ({repos.Count})");
        foreach (var repo in repos)
            sb.AppendLine($"- **{repo.Name}** — {repo.Url} (branch: {repo.DefaultBranch})");
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    [Description("Create a new release in Planning status.")]
    internal async Task<string> CreateReleaseAsync(
        [Description("Unique release ID (e.g. 'v1.2.0')")] string id,
        [Description("Version tag for the release (e.g. 'v1.2.0')")] string tag,
        [Description("Optional notes or changelog summary")] string? notes = null,
        [Description("Comma-separated repository names this release applies to")] string? repositories = null)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"),
            (!string.IsNullOrWhiteSpace(tag), "tag is required"));
        if (error is not null) return error;

        var existing = await _goalStore.GetReleaseAsync(id);
        if (existing is not null)
            return $"❌ Release '{id}' already exists (status: {existing.Status}).";

        var repos = string.IsNullOrWhiteSpace(repositories)
            ? new List<string>()
            : repositories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var release = new Release
        {
            Id = id,
            Tag = tag,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
            RepositoryNames = repos,
        };

        await _goalStore.CreateReleaseAsync(release);
        _logger.LogInformation("Composer created release '{ReleaseId}'", id);

        return $"""
            ✅ Release created:
            - ID: {id}
            - Tag: {tag}
            - Status: Planning
            - Repositories: {(repos.Count > 0 ? string.Join(", ", repos) : "(none)")}
            """;
    }

    [Description("List all releases with their status and goal count.")]
    internal async Task<string> ListReleasesAsync()
    {
        var releases = await _goalStore.GetReleasesAsync();

        if (releases.Count == 0)
            return "No releases found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**{releases.Count} release(s):**\n");

        foreach (var r in releases)
        {
            var goals = await _goalStore.GetGoalsByReleaseAsync(r.Id);
            sb.AppendLine($"- `{r.Id}` [{r.Status}] tag={r.Tag} — {goals.Count} goal(s)");
        }

        return sb.ToString();
    }

    [Description("Update a field on a Planning release. Only tag, notes, and repositories can be changed. Non-Planning releases cannot be edited.")]
    internal async Task<string> UpdateReleaseAsync(
        [Description("Release ID to update")] string id,
        [Description("Field to update: tag, notes, or repositories")] string field,
        [Description("New value for the field. For repositories, provide a comma-separated list (or empty to clear)")] string value)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"),
            (!string.IsNullOrWhiteSpace(field), "field is required"));
        if (error is not null) return error;

        var release = await _goalStore.GetReleaseAsync(id);
        if (release is null)
            return $"❌ Release '{id}' not found.";

        if (release.Status != ReleaseStatus.Planning)
            return $"❌ Release '{id}' is in '{release.Status}' status and cannot be edited. Only Planning releases can be updated.";

        ReleaseUpdateData update;
        switch (field.ToLowerInvariant())
        {
            case "tag":
                if (string.IsNullOrWhiteSpace(value))
                    return "❌ tag cannot be empty.";
                update = new ReleaseUpdateData { Tag = value.Trim() };
                break;

            case "notes":
                update = new ReleaseUpdateData { Notes = value };
                break;

            case "repositories":
                var repos = string.IsNullOrWhiteSpace(value)
                    ? new List<string>()
                    : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                update = new ReleaseUpdateData { Repositories = repos };
                break;

            default:
                return $"❌ Unknown field '{field}'. Valid fields: tag, notes, repositories.";
        }

        try
        {
            await _goalStore.UpdateReleaseAsync(id, update);
        }
        catch (InvalidOperationException ex)
        {
            return $"❌ {ex.Message}";
        }

        _logger.LogInformation("Composer updated release '{ReleaseId}' field '{Field}'", id, field);
        return $"✅ Release '{id}' {field} updated.";
    }

    /// <summary>
    /// Appends the Documents list to a response string if the goal has documents.
    /// Format: "- Documents: id (Title), id2 (Title2)"
    /// </summary>
    private string AppendDocuments(string response, Goal goal)
    {
        if (goal.Documents.Count == 0)
            return response;
        var sb = new System.Text.StringBuilder(response);
        AppendDocumentsList(sb, goal.Documents);
        return sb.ToString();
    }

    /// <summary>
    /// Appends a newline + "- Documents: id (Title), ..." to the StringBuilder.
    /// Does nothing if the list is empty.
    /// </summary>
    private void AppendDocumentsList(System.Text.StringBuilder sb, List<string> documentIds)
    {
        if (documentIds.Count == 0)
            return;

        var items = documentIds.Select(docId =>
        {
            var title = _knowledgeGraph?.GetDocument(docId)?.Title;
            return title is not null ? $"{docId} ({title})" : docId;
        });
        sb.AppendLine();
        sb.Append($"- Documents: {string.Join(", ", items)}");
    }
}
