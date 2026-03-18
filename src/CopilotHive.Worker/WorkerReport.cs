using CopilotHive.Workers;

namespace CopilotHive.Worker;

/// <summary>
/// Structured report from a worker (reviewer, coder, or doc-writer) via tool call.
/// Eliminates free-text parsing (REVIEW_REPORT, DOC_REPORT blocks) and
/// Brain interpretation for structured data extraction.
/// </summary>
public sealed record WorkerReport
{
    /// <summary>Task verdict for coders/doc-writers (Pass/Fail). Null for reviewers.</summary>
    public TaskVerdict? TaskVerdict { get; init; }

    /// <summary>Review verdict for reviewers (Approve/RequestChanges). Null for coders/doc-writers.</summary>
    public ReviewVerdict? ReviewVerdict { get; init; }

    /// <summary>Issues found (review issues, build failures, etc.). Empty if none.</summary>
    public List<string> Issues { get; init; } = [];

    /// <summary>Human-readable summary of what was done or found.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Files that were modified (coder/doc-writer) or reviewed (reviewer).</summary>
    public List<string> FilesChanged { get; init; } = [];
}
