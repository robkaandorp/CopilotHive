using System.ComponentModel;
using System.Globalization;
using System.Text;
using CopilotHive.Goals;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    /// <summary>
    /// Converts an enum value to its snake_case string representation
    /// (e.g. <c>IssueType.CodeQuality</c> → <c>"code_quality"</c>).
    /// </summary>
    private static string ToSnakeCase(Enum value)
    {
        var name = value.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Formats a single issue as a one-line list entry with snake_case
    /// type/severity/status values.
    /// </summary>
    private static string FormatIssueListLine(Issue issue) =>
        $"{issue.Id} | {issue.Title} | {ToSnakeCase(issue.Type)} | {ToSnakeCase(issue.Severity)} | {ToSnakeCase(issue.Status)}";

    /// <summary>
    /// Formats the full details of an issue for display.
    /// </summary>
    private static string FormatIssueDetails(Issue issue)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Issue: {issue.Id}");
        sb.AppendLine($"- **Type:** {ToSnakeCase(issue.Type)}");
        sb.AppendLine($"- **Title:** {issue.Title}");
        sb.AppendLine($"- **Description:** {issue.Description}");
        sb.AppendLine($"- **Severity:** {ToSnakeCase(issue.Severity)}");
        sb.AppendLine($"- **Status:** {ToSnakeCase(issue.Status)}");
        sb.AppendLine($"- **RepositoryNames:** {(issue.RepositoryNames.Count > 0 ? string.Join(", ", issue.RepositoryNames) : "(none)")}");
        sb.AppendLine($"- **SourceGoalId:** {(issue.SourceGoalId is null ? "(none)" : issue.SourceGoalId)}");
        sb.AppendLine($"- **SourceRole:** {(issue.SourceRole is null ? "(none)" : issue.SourceRole)}");
        sb.AppendLine($"- **SourceIteration:** {(issue.SourceIteration.HasValue ? issue.SourceIteration.Value.ToString(CultureInfo.InvariantCulture) : "(none)")}");
        sb.AppendLine($"- **CreatedAt:** {issue.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **UpdatedAt:** {(issue.UpdatedAt.HasValue ? issue.UpdatedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "(none)")}");
        sb.AppendLine($"- **ResolvedAt:** {(issue.ResolvedAt.HasValue ? issue.ResolvedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "(none)")}");
        sb.Append($"- **LinkedGoalId:** {(issue.LinkedGoalId is null ? "(none)" : issue.LinkedGoalId)}");
        return sb.ToString();
    }

    /// <summary>
    /// Creates a new issue when the user reports a bug, code quality problem,
    /// suggestion, concern, or workflow issue.
    /// </summary>
    [Description("Create a new issue when the user reports a bug, code quality problem, suggestion, concern, or workflow issue.")]
    internal async Task<string> CreateIssueAsync(
        [Description("Issue type: bug, suggestion, concern, code_quality, or workflow")] string type,
        [Description("Short title for the issue")] string title,
        [Description("Detailed description of the issue")] string description,
        [Description("Severity: low, medium, or high (defaults to low)")] string? severity = "low",
        [Description("Optional repository names this issue applies to")] string[]? repository_names = null,
        CancellationToken ct = default)
    {
        if (_issueStore is null)
            return "Issue tracking not available.";

        if (string.IsNullOrWhiteSpace(type))
            return "Type is required.";

        IssueType parsedType;
        try
        {
            parsedType = IssueIdGenerator.ParseIssueType(type);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        if (string.IsNullOrWhiteSpace(title))
            return "Title is required.";

        if (string.IsNullOrWhiteSpace(description))
            return "Description is required.";

        IssueSeverity parsedSeverity;
        try
        {
            parsedSeverity = IssueIdGenerator.ParseIssueSeverity(severity);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        var id = await IssueIdGenerator.GenerateAsync(title, _issueStore, ct);

        // IssueIdGenerator.GenerateAsync only probes that a candidate is currently
        // absent — another Composer, worker, Brain, or API request can insert the
        // same ID before CreateIssueAsync runs. Build the issue through a local
        // factory so the duplicate-ID race can be retried with a GUID-based ID
        // while preserving every field (matches BrainTools / HiveOrchestratorService).
        Issue BuildIssue(string issueId) => new()
        {
            Id = issueId,
            Type = parsedType,
            Title = title,
            Description = description,
            Severity = parsedSeverity,
            Status = IssueStatus.Open,
            RepositoryNames = repository_names is not null ? [.. repository_names] : [],
            SourceGoalId = null,
            SourceRole = null,
            SourceIteration = null,
        };

        var issue = BuildIssue(id);

        try
        {
            await _issueStore.CreateIssueAsync(issue, ct);
        }
        catch (InvalidOperationException)
        {
            // Duplicate ID (race): retry with a GUID-based ID, preserving all fields.
            issue = BuildIssue($"issue-{Guid.NewGuid():N}");
            await _issueStore.CreateIssueAsync(issue, ct);
        }

        _logger.LogInformation("Composer created issue '{IssueId}': {Title}", issue.Id, title);

        return $"Issue created: {issue.Id}";
    }

    /// <summary>
    /// Lists issues, optionally filtered by status, type, and severity.
    /// </summary>
    [Description("List issues, optionally filtered by status, type, and severity.")]
    internal async Task<string> ListIssuesAsync(
        [Description("Optional status filter: open, triaged, acknowledged, in_progress, resolved, closed")] string? status = null,
        [Description("Optional type filter: bug, suggestion, concern, code_quality, workflow")] string? type = null,
        [Description("Optional severity filter: low, medium, high")] string? severity = null,
        CancellationToken ct = default)
    {
        if (_issueStore is null)
            return "Issue tracking not available.";

        IssueStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status))
        {
            try
            {
                statusFilter = ParseIssueStatus(status);
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        IssueType? typeFilter = null;
        if (!string.IsNullOrEmpty(type))
        {
            try
            {
                typeFilter = IssueIdGenerator.ParseIssueType(type);
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        IssueSeverity? severityFilter = null;
        if (!string.IsNullOrEmpty(severity))
        {
            try
            {
                severityFilter = IssueIdGenerator.ParseIssueSeverity(severity);
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        var issues = await _issueStore.GetIssuesAsync(statusFilter, typeFilter, severityFilter, null, null, ct);

        if (issues.Count == 0)
            return "No issues found.";

        var sb = new StringBuilder();
        sb.AppendLine($"**{issues.Count} issue(s):**\n");
        foreach (var issue in issues)
            sb.AppendLine($"- {FormatIssueListLine(issue)}");

        return sb.ToString();
    }

    /// <summary>
    /// Gets full details for an issue by ID.
    /// </summary>
    [Description("Get full details for an issue by ID.")]
    internal async Task<string> GetIssueAsync(
        [Description("Issue ID to look up")] string issue_id,
        CancellationToken ct = default)
    {
        if (_issueStore is null)
            return "Issue tracking not available.";

        if (string.IsNullOrWhiteSpace(issue_id))
            return "issue_id is required.";

        var issue = await _issueStore.GetIssueAsync(issue_id, ct);
        if (issue is null)
            return $"Issue '{issue_id}' not found.";

        return FormatIssueDetails(issue);
    }

    /// <summary>
    /// Triage or update an issue: change status, severity, type, title, description, or linked goal.
    /// Only non-null fields are updated.
    /// </summary>
    [Description("Triage or update an issue: change status, severity, type, title, description, or linked goal. Only provided fields are changed.")]
    internal async Task<string> UpdateIssueAsync(
        [Description("Issue ID to update")] string issue_id,
        [Description("Optional new status: open, triaged, acknowledged, in_progress, resolved, closed")] string? status = null,
        [Description("Optional new severity: low, medium, high")] string? severity = null,
        [Description("Optional new type: bug, suggestion, concern, code_quality, workflow")] string? type = null,
        [Description("Optional new title")] string? title = null,
        [Description("Optional new description")] string? description = null,
        [Description("Optional linked goal ID. null = no change, empty string = clear, non-empty = set.")] string? linked_goal_id = null,
        CancellationToken ct = default)
    {
        if (_issueStore is null)
            return "Issue tracking not available.";

        if (string.IsNullOrWhiteSpace(issue_id))
            return "issue_id is required.";

        // Serialize the whole read-modify-write cycle. UpdateIssueAsync takes a full
        // replacement entity and Issue carries no concurrency token, so the lock must
        // span the initial read, the construction of the replacement, the authoritative
        // update, and the re-fetch — otherwise a concurrent partial update that changed a
        // different field would be overwritten with this caller's stale copied values.
        await _issueUpdateLock.WaitAsync(ct);
        try
        {
            var existing = await _issueStore.GetIssueAsync(issue_id, ct);
            if (existing is null)
                return $"Issue '{issue_id}' not found.";

            IssueStatus? newStatus = null;
            if (status is not null)
            {
                try
                {
                    newStatus = ParseIssueStatus(status);
                }
                catch (ArgumentException ex)
                {
                    return ex.Message;
                }
            }

            IssueSeverity? newSeverity = null;
            if (severity is not null)
            {
                try
                {
                    newSeverity = IssueIdGenerator.ParseIssueSeverity(severity);
                }
                catch (ArgumentException ex)
                {
                    return ex.Message;
                }
            }

            IssueType? newType = null;
            if (type is not null)
            {
                try
                {
                    newType = IssueIdGenerator.ParseIssueType(type);
                }
                catch (ArgumentException ex)
                {
                    return ex.Message;
                }
            }

            if (title is not null && string.IsNullOrWhiteSpace(title))
                return "Title is required.";

            if (description is not null && string.IsNullOrWhiteSpace(description))
                return "Description is required.";

            // Issue is a sealed class (not a record) — construct a new instance with
            // immutable fields copied from the original and mutable fields updated.
            var updatedIssue = new Issue
            {
                Id = existing.Id,
                Type = newType ?? existing.Type,
                Title = title ?? existing.Title,
                Description = description ?? existing.Description,
                Severity = newSeverity ?? existing.Severity,
                Status = newStatus ?? existing.Status,
                RepositoryNames = existing.RepositoryNames,
                SourceGoalId = existing.SourceGoalId,
                SourceRole = existing.SourceRole,
                SourceIteration = existing.SourceIteration,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = existing.UpdatedAt,
                ResolvedAt = existing.ResolvedAt,
                LinkedGoalId = linked_goal_id is null ? existing.LinkedGoalId : (linked_goal_id.Length == 0 ? null : linked_goal_id),
            };

            await _issueStore.UpdateIssueAsync(updatedIssue, ct);
            _logger.LogInformation("Composer updated issue '{IssueId}'", issue_id);

            // Re-fetch for accurate store-managed timestamps (UpdatedAt / ResolvedAt).
            var refreshed = await _issueStore.GetIssueAsync(issue_id, ct);
            return refreshed is null
                ? $"Issue '{issue_id}' not found."
                : FormatIssueDetails(refreshed);
        }
        finally
        {
            _issueUpdateLock.Release();
        }
    }
}
