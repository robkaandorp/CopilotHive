using CopilotHive.Goals;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using GrpcBranchAction = CopilotHive.Shared.Grpc.BranchAction;
using GrpcTaskMetrics = CopilotHive.Shared.Grpc.TaskMetrics;
using GrpcUsageSourceKind = CopilotHive.Shared.Grpc.UsageSourceKind;
using SharpEstimatedTokenBreakdown = SharpCoder.EstimatedTokenBreakdown;
using SharpTokenUsage = SharpCoder.TokenUsage;
using SharpUsageEntry = SharpCoder.UsageEntry;
using SharpUsageSource = SharpCoder.UsageSource;
using SharpUsageSummary = SharpCoder.UsageSummary;

namespace CopilotHive.Services;

/// <summary>
/// Converts between domain types and gRPC protobuf types. Used only at communication boundaries.
/// </summary>
public static class GrpcMapper
{
    /// <summary>Converts a <see cref="WorkTask"/> to a gRPC <see cref="TaskAssignment"/>.</summary>
    public static TaskAssignment ToGrpc(WorkTask task)
    {
        var assignment = new TaskAssignment
        {
            TaskId = task.TaskId,
            GoalId = task.GoalId,
            GoalDescription = task.GoalDescription,
            Prompt = task.Prompt,
            Role = ToGrpcRole(task.Role),
            Model = task.Model,
            SessionId = task.SessionId,
            MaxContextTokens = task.MaxContextTokens,
            ReasoningEffort = ReasoningEffortConverter.Format(task.ReasoningEffort) ?? "",
        };
        foreach (var repo in task.Repositories)
        {
            assignment.Repositories.Add(new RepositoryInfo
            {
                Name = repo.Name,
                Url = repo.Url,
                DefaultBranch = repo.DefaultBranch,
            });
        }
        if (task.BranchInfo is not null)
        {
            assignment.BranchInfo = new BranchInfo
            {
                BaseBranch = task.BranchInfo.BaseBranch,
                FeatureBranch = task.BranchInfo.FeatureBranch,
                Action = ToGrpc(task.BranchInfo.Action),
            };
        }
        foreach (var (key, value) in task.Metadata)
        {
            assignment.Metadata[key] = value;
        }
        foreach (var m in task.SubAgentModels)
        {
            if (!string.IsNullOrWhiteSpace(m.Id))
            {
                assignment.SubAgentModels.Add(new SubAgentModel
                {
                    Id = m.Id,
                    ContextWindow = (m.ContextWindow is int cw && cw > 0) ? cw : 0,
                    Description = m.Description ?? "",
                    SupportsVision = m.SupportsVision,
                });
            }
        }
        return assignment;
    }

    /// <summary>
    /// Converts a gRPC <see cref="TaskComplete"/> to a domain <see cref="TaskResult"/>.
    /// <para>
    /// The assigned <c>model</c> is copied VERBATIM. Field 7's presence bit is deliberately
    /// NOT collapsed into the domain value here: absence and a present-but-empty value both
    /// map to the domain empty default, and the RECEIVING transport keeps the protobuf
    /// presence bit (<c>HasModel</c>) so it can still distinguish a legacy sender from an
    /// upgraded sender that explicitly reports an unknown model. No nullable-domain or
    /// presence-field migration is needed.
    /// </para>
    /// <para>
    /// The usage report (field 8) keeps its presence as well, but here absence and emptiness are
    /// DIFFERENT domain states rather than one collapsed default: an ABSENT <c>usage</c> maps to
    /// <c>Usage == null</c> ("the sender did not report usage"), while a PRESENT <c>usage</c> — even
    /// with zero entries — maps to a non-null <see cref="SharpCoder.UsageSummary"/> ("usage was
    /// reported; the task made zero model calls"). Every entry's <c>TokenUsage</c>, its reported-calls
    /// counters and its estimated breakdown are copied verbatim; an entry <c>model</c> stays
    /// <c>null</c> when it is absent, never an empty string. A usage entry whose source is
    /// unspecified or unknown is a mapping failure and throws, exactly like an unknown
    /// <c>TaskStatus</c>, which the orchestrator already handles as a mapping failure.
    /// </para>
    /// </summary>
    public static TaskResult ToDomain(TaskComplete complete)
    {
        return new TaskResult
        {
            TaskId = complete.TaskId,
            Status = complete.Status switch
            {
                Shared.Grpc.TaskStatus.Completed => TaskOutcome.Completed,
                Shared.Grpc.TaskStatus.Failed => TaskOutcome.Failed,
                Shared.Grpc.TaskStatus.Cancelled => TaskOutcome.Cancelled,
                _ => throw new InvalidOperationException($"Unknown TaskStatus: {complete.Status}"),
            },
            Output = complete.Output,
            Metrics = complete.Metrics is not null ? ToDomain(complete.Metrics) : null,
            GitStatus = complete.GitStatus is not null ? ToDomain(complete.GitStatus) : null,
            Model = complete.Model,
            IterationStartSha = string.IsNullOrEmpty(complete.IterationStartSha) ? null : complete.IterationStartSha,
            // A message field has native presence: the generated property is null when the field
            // was never set, so null means "the sender did not report usage".
            Usage = complete.Usage is not null ? ToDomain(complete.Usage) : null,
        };
    }

    /// <summary>
    /// Converts a gRPC <see cref="TaskUsage"/> to a domain <see cref="SharpCoder.UsageSummary"/>.
    /// <para>
    /// A present <c>usage</c> with zero entries becomes an EMPTY but non-null summary — a real
    /// measurement meaning the task made no model calls. Entry <c>model</c> presence is preserved:
    /// an absent value becomes <c>null</c>, never the empty string. Every token count, reported-calls
    /// counter and estimated breakdown field is copied verbatim. An entry whose
    /// <see cref="GrpcUsageSourceKind"/> is unspecified or unknown is a mapping failure.
    /// </para>
    /// </summary>
    /// <param name="usage">The wire usage report; must not be <c>null</c>.</param>
    /// <returns>A detached <see cref="SharpCoder.UsageSummary"/> with one entry per wire entry.</returns>
    /// <exception cref="InvalidOperationException">An entry's source is unspecified or unknown.</exception>
    public static SharpUsageSummary ToDomain(TaskUsage usage)
    {
        var entries = new List<SharpUsageEntry>(usage.Entries.Count);
        foreach (var entry in usage.Entries)
        {
            var tokenUsage = new SharpTokenUsage
            {
                InputTokens = entry.InputTokens,
                OutputTokens = entry.OutputTokens,
                CachedInputTokens = entry.CachedInputTokens,
                ReasoningTokens = entry.ReasoningTokens,
                Calls = entry.Calls,
                CachedInputReportedCalls = entry.CachedInputReportedCalls,
                ReasoningReportedCalls = entry.ReasoningReportedCalls,
                Estimated = ToDomain(entry.Estimated),
            };
            // The entry-model presence bit carries "model unknown" (absent → null) versus "model
            // reported as empty" (present → ""). The generated getter returns "" for both, so the
            // presence bit must be checked explicitly.
            var model = entry.HasModel ? entry.Model : null;
            entries.Add(new SharpUsageEntry(ToDomain(entry.Source), model, tokenUsage));
        }
        return new SharpUsageSummary(entries);
    }

    /// <summary>
    /// Converts a gRPC <see cref="EstimatedTokenCounts"/> to a domain
    /// <see cref="SharpCoder.EstimatedTokenBreakdown"/>, copying every category and both estimated-calls
    /// counters verbatim.
    /// </summary>
    /// <param name="estimated">The wire estimate; <c>null</c> yields an empty breakdown.</param>
    /// <returns>The mapped breakdown.</returns>
    public static SharpEstimatedTokenBreakdown ToDomain(EstimatedTokenCounts? estimated) => new SharpEstimatedTokenBreakdown
    {
        SystemPrompt = estimated?.SystemPrompt ?? 0,
        ToolDefinitions = estimated?.ToolDefinitions ?? 0,
        UserText = estimated?.UserText ?? 0,
        AssistantText = estimated?.AssistantText ?? 0,
        ToolCalls = estimated?.ToolCalls ?? 0,
        ToolResults = estimated?.ToolResults ?? 0,
        Reasoning = estimated?.Reasoning ?? 0,
        Images = estimated?.Images ?? 0,
        OutputText = estimated?.OutputText ?? 0,
        OutputToolCalls = estimated?.OutputToolCalls ?? 0,
        OutputReasoning = estimated?.OutputReasoning ?? 0,
        InputEstimatedCalls = estimated?.InputEstimatedCalls ?? 0,
        OutputEstimatedCalls = estimated?.OutputEstimatedCalls ?? 0,
    };

    /// <summary>
    /// Converts a gRPC <see cref="GrpcUsageSourceKind"/> to the SharpCoder equivalent. Unspecified and
    /// unknown numeric values are mapping failures — they are never silently resolved to a source.
    /// </summary>
    /// <param name="source">The wire source kind.</param>
    /// <returns>The matching SharpCoder source.</returns>
    /// <exception cref="InvalidOperationException">The value is unspecified or unknown.</exception>
    public static SharpUsageSource ToDomain(GrpcUsageSourceKind source) => source switch
    {
        GrpcUsageSourceKind.Agent => SharpUsageSource.Agent,
        GrpcUsageSourceKind.Compaction => SharpUsageSource.Compaction,
        GrpcUsageSourceKind.SubAgent => SharpUsageSource.SubAgent,
        GrpcUsageSourceKind.SubAgentCompaction => SharpUsageSource.SubAgentCompaction,
        _ => throw new InvalidOperationException($"Unknown UsageSourceKind: {source}"),
    };

    /// <summary>
    /// Converts a domain <see cref="SharpCoder.UsageSource"/> to the gRPC equivalent. Every domain value
    /// maps explicitly; nothing falls back to Unspecified.
    /// </summary>
    /// <param name="source">The domain source.</param>
    /// <returns>The matching wire source kind.</returns>
    /// <exception cref="InvalidOperationException">The value has no wire equivalent.</exception>
    public static GrpcUsageSourceKind ToGrpc(SharpUsageSource source) => source switch
    {
        SharpUsageSource.Agent => GrpcUsageSourceKind.Agent,
        SharpUsageSource.Compaction => GrpcUsageSourceKind.Compaction,
        SharpUsageSource.SubAgent => GrpcUsageSourceKind.SubAgent,
        SharpUsageSource.SubAgentCompaction => GrpcUsageSourceKind.SubAgentCompaction,
        _ => throw new InvalidOperationException($"UsageSource '{source}' has no gRPC equivalent"),
    };

    /// <summary>Converts a gRPC <see cref="GrpcTaskMetrics"/> to a domain <see cref="TaskMetrics"/>.</summary>
    public static TaskMetrics ToDomain(GrpcTaskMetrics metrics)
    {
        return new TaskMetrics
        {
            Verdict = metrics.Verdict,
            BuildSuccess = metrics.BuildSuccess,
            TotalTests = metrics.TotalTests,
            PassedTests = metrics.PassedTests,
            FailedTests = metrics.FailedTests,
            CoveragePercent = metrics.CoveragePercent,
            Issues = [.. metrics.Issues],
            Summary = metrics.Summary,
        };
    }

    /// <summary>Converts a gRPC <see cref="GitStatus"/> to a domain <see cref="GitChangeSummary"/>.</summary>
    public static GitChangeSummary ToDomain(GitStatus status)
    {
        return new GitChangeSummary
        {
            FilesChanged = status.FilesChanged,
            Insertions = status.Insertions,
            Deletions = status.Deletions,
            Pushed = status.Pushed,
            ChangedFiles = [.. status.ChangedFiles],
        };
    }

    /// <summary>Converts a gRPC <see cref="BranchInfo"/> to a domain <see cref="BranchSpec"/>.</summary>
    public static BranchSpec ToDomain(BranchInfo info)
    {
        return new BranchSpec
        {
            BaseBranch = info.BaseBranch,
            FeatureBranch = info.FeatureBranch,
            Action = ToDomain(info.Action),
        };
    }

    /// <summary>Converts a domain <see cref="BranchAction"/> to the gRPC equivalent.</summary>
    public static GrpcBranchAction ToGrpc(BranchAction action) => action switch
    {
        BranchAction.Create => GrpcBranchAction.Create,
        BranchAction.Checkout => GrpcBranchAction.Checkout,
        BranchAction.Merge => GrpcBranchAction.Merge,
        BranchAction.Unspecified => GrpcBranchAction.Unspecified,
        _ => throw new InvalidOperationException($"Unknown BranchAction: {action}"),
    };

    /// <summary>Converts a gRPC <see cref="GrpcBranchAction"/> to the domain equivalent.</summary>
    public static BranchAction ToDomain(GrpcBranchAction action) => action switch
    {
        GrpcBranchAction.Create => BranchAction.Create,
        GrpcBranchAction.Checkout => BranchAction.Checkout,
        GrpcBranchAction.Merge => BranchAction.Merge,
        GrpcBranchAction.Unspecified => BranchAction.Unspecified,
        _ => throw new InvalidOperationException($"Unknown BranchAction: {action}"),
    };

    /// <summary>Converts a gRPC <see cref="TaskAssignment"/> to a domain <see cref="WorkTask"/>.</summary>
    public static WorkTask ToDomain(TaskAssignment assignment)
    {
        return new WorkTask
        {
            TaskId = assignment.TaskId,
            GoalId = assignment.GoalId,
            GoalDescription = assignment.GoalDescription,
            Prompt = assignment.Prompt,
            Role = ToDomainRole(assignment.Role),
            Model = assignment.Model,
            SessionId = assignment.SessionId,
            BranchInfo = assignment.BranchInfo is not null ? ToDomain(assignment.BranchInfo) : null,
            Repositories = [.. assignment.Repositories.Select(r => new TargetRepository
            {
                Name = r.Name,
                Url = r.Url,
                DefaultBranch = r.DefaultBranch,
            })],
            Metadata = new Dictionary<string, string>(assignment.Metadata),
            MaxContextTokens = assignment.MaxContextTokens > 0 ? assignment.MaxContextTokens : SharedConstants.DefaultBrainContextWindow,
            SubAgentModels = [.. assignment.SubAgentModels
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new SubAgentModelDto
                {
                    Id = m.Id,
                    ContextWindow = m.ContextWindow > 0 ? m.ContextWindow : null,
                    Description = m.Description ?? "",
                    SupportsVision = m.SupportsVision,
                })],
            ReasoningEffort = ReasoningEffortConverter.Parse(assignment.ReasoningEffort),
        };
    }

    /// <summary>
    /// Converts a domain <see cref="TaskResult"/> to a gRPC <see cref="TaskComplete"/>.
    /// <para>
    /// The assigned <c>model</c> (field 7) is ALWAYS written, so every completion produced by
    /// an upgraded worker has explicit presence. A runtime-null domain <c>Model</c> is
    /// normalized to the empty string and still written, which means "upgraded sender,
    /// assigned model unknown/empty" — never absence. All other completion fields keep their
    /// existing mapping.
    /// </para>
    /// <para>
    /// The usage report (field 8) is written ONLY when the domain result carries one: a non-null
    /// <see cref="SharpCoder.UsageSummary"/> becomes a PRESENT <c>usage</c> message with one entry per
    /// summary entry — an empty summary is therefore written as PRESENT with zero entries, which is
    /// a real measurement ("the task made zero model calls"), not absence. A null summary leaves the
    /// field ABSENT ("usage not reported").
    /// </para>
    /// <para>
    /// Each entry copies every token count and reported-calls counter verbatim and its full estimated
    /// breakdown. The entry <c>model</c> (field 2 of <c>TaskUsageEntry</c>) has explicit presence and
    /// is set ONLY when <see cref="SharpCoder.UsageEntry.Model"/> is non-null: unlike the completion's
    /// assigned model (field 7), a null model is NOT normalized to an empty string, so a receiver can
    /// still tell "unknown model" from "model reported as empty".
    /// </para>
    /// </summary>
    public static TaskComplete ToGrpc(TaskResult result)
    {
        var complete = new TaskComplete
        {
            TaskId = result.TaskId,
            Status = result.Status switch
            {
                TaskOutcome.Completed => Shared.Grpc.TaskStatus.Completed,
                TaskOutcome.Failed => Shared.Grpc.TaskStatus.Failed,
                TaskOutcome.Cancelled => Shared.Grpc.TaskStatus.Cancelled,
                _ => throw new InvalidOperationException($"Unknown TaskOutcome: {result.Status}"),
            },
            Output = result.Output,
            // ALWAYS set (even when empty): assigning the field sets proto3's explicit
            // presence bit, which is what tells the receiver this is an upgraded sender.
            Model = result.Model ?? "",
            IterationStartSha = result.IterationStartSha ?? "",
        };
        if (result.Usage is not null)
        {
            // Assigning the message sets the field's presence bit: an empty summary is a PRESENT
            // report with zero entries, never absence.
            complete.Usage = ToGrpc(result.Usage);
        }
        if (result.Metrics is not null)
        {
            complete.Metrics = new GrpcTaskMetrics
            {
                Verdict = result.Metrics.Verdict,
                BuildSuccess = result.Metrics.BuildSuccess,
                TotalTests = result.Metrics.TotalTests,
                PassedTests = result.Metrics.PassedTests,
                FailedTests = result.Metrics.FailedTests,
                CoveragePercent = result.Metrics.CoveragePercent,
                Summary = result.Metrics.Summary,
            };
            complete.Metrics.Issues.AddRange(result.Metrics.Issues);
        }
        if (result.GitStatus is not null)
        {
            complete.GitStatus = new GitStatus
            {
                FilesChanged = result.GitStatus.FilesChanged,
                Insertions = result.GitStatus.Insertions,
                Deletions = result.GitStatus.Deletions,
                Pushed = result.GitStatus.Pushed,
                ChangedFiles = { result.GitStatus.ChangedFiles },
            };
        }
        return complete;
    }

    /// <summary>
    /// Converts a domain <see cref="SharpCoder.UsageSummary"/> to the gRPC <see cref="TaskUsage"/>
    /// equivalent: one wire entry per domain entry, each copying every token count, reported-calls
    /// counter and the full estimated breakdown verbatim.
    /// </summary>
    /// <remarks>
    /// THE RETURNED MESSAGE IS ALWAYS PRESENT — an empty summary becomes a message with zero entries,
    /// which is a real measurement ("no model calls"), never absence. CALLERS OWN THE PRESENCE BIT:
    /// assigning the result to a message field is what records "usage was reported".
    /// <para>
    /// The entry <c>model</c> (field 2 of <c>TaskUsageEntry</c>) has explicit presence and is set
    /// ONLY when <see cref="SharpCoder.UsageEntry.Model"/> is non-null, so a receiver can still tell
    /// "unknown model" from "model reported as empty".
    /// </para>
    /// </remarks>
    /// <param name="usage">The domain usage report; must not be <c>null</c>. An empty summary is valid.</param>
    /// <returns>A detached wire usage message.</returns>
    /// <exception cref="InvalidOperationException">
    /// An entry's source has no wire equivalent (see <see cref="ToGrpc(SharpUsageSource)"/>).
    /// </exception>
    public static TaskUsage ToGrpc(SharpUsageSummary usage)
    {
        var wire = new TaskUsage();
        foreach (var entry in usage.Entries)
        {
            var tokenUsage = entry.Usage;
            var wireEntry = new TaskUsageEntry
            {
                Source = ToGrpc(entry.Source),
                InputTokens = tokenUsage.InputTokens,
                OutputTokens = tokenUsage.OutputTokens,
                CachedInputTokens = tokenUsage.CachedInputTokens,
                ReasoningTokens = tokenUsage.ReasoningTokens,
                Calls = tokenUsage.Calls,
                CachedInputReportedCalls = tokenUsage.CachedInputReportedCalls,
                ReasoningReportedCalls = tokenUsage.ReasoningReportedCalls,
                Estimated = ToGrpc(tokenUsage.Estimated),
            };
            // ONLY set when non-null: never normalize a null model to an empty string.
            if (entry.Model is not null)
            {
                wireEntry.Model = entry.Model;
            }
            wire.Entries.Add(wireEntry);
        }
        return wire;
    }

    /// <summary>
    /// Converts a domain <see cref="SharpCoder.EstimatedTokenBreakdown"/> to the gRPC
    /// <see cref="EstimatedTokenCounts"/> equivalent, copying every category and both estimated-calls
    /// counters verbatim. <c>null</c> yields an empty message (all-zero counts).
    /// </summary>
    /// <param name="estimated">The domain estimate; may be <c>null</c>.</param>
    /// <returns>The mapped wire estimate.</returns>
    private static EstimatedTokenCounts ToGrpc(SharpEstimatedTokenBreakdown? estimated) => new EstimatedTokenCounts
    {
        SystemPrompt = estimated?.SystemPrompt ?? 0,
        ToolDefinitions = estimated?.ToolDefinitions ?? 0,
        UserText = estimated?.UserText ?? 0,
        AssistantText = estimated?.AssistantText ?? 0,
        ToolCalls = estimated?.ToolCalls ?? 0,
        ToolResults = estimated?.ToolResults ?? 0,
        Reasoning = estimated?.Reasoning ?? 0,
        Images = estimated?.Images ?? 0,
        OutputText = estimated?.OutputText ?? 0,
        OutputToolCalls = estimated?.OutputToolCalls ?? 0,
        OutputReasoning = estimated?.OutputReasoning ?? 0,
        InputEstimatedCalls = estimated?.InputEstimatedCalls ?? 0,
        OutputEstimatedCalls = estimated?.OutputEstimatedCalls ?? 0,
    };

    private static Shared.Grpc.WorkerRole ToGrpcRole(Workers.WorkerRole role) => role switch
    {
        Workers.WorkerRole.Unspecified => Shared.Grpc.WorkerRole.Unspecified,
        Workers.WorkerRole.Coder => Shared.Grpc.WorkerRole.Coder,
        Workers.WorkerRole.Tester => Shared.Grpc.WorkerRole.Tester,
        Workers.WorkerRole.Reviewer => Shared.Grpc.WorkerRole.Reviewer,
        Workers.WorkerRole.Improver => Shared.Grpc.WorkerRole.Improver,
        Workers.WorkerRole.DocWriter => Shared.Grpc.WorkerRole.DocWriter,
        _ => throw new InvalidOperationException($"WorkerRole '{role}' has no gRPC equivalent"),
    };

    private static Workers.WorkerRole ToDomainRole(Shared.Grpc.WorkerRole grpcRole) => grpcRole switch
    {
        Shared.Grpc.WorkerRole.Unspecified => Workers.WorkerRole.Unspecified,
        Shared.Grpc.WorkerRole.Coder => Workers.WorkerRole.Coder,
        Shared.Grpc.WorkerRole.Tester => Workers.WorkerRole.Tester,
        Shared.Grpc.WorkerRole.Reviewer => Workers.WorkerRole.Reviewer,
        Shared.Grpc.WorkerRole.Improver => Workers.WorkerRole.Improver,
        Shared.Grpc.WorkerRole.DocWriter => Workers.WorkerRole.DocWriter,
        _ => throw new InvalidOperationException($"gRPC WorkerRole '{grpcRole}' has no domain equivalent"),
    };
}
