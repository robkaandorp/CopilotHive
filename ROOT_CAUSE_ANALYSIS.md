# ROOT CAUSE ANALYSIS: Goal Completed 4 Times Instead of Once

## Problem Summary
- **Task ID**: ail-on-unknown-repository-improver-001
- **Observed**: Same improver task dispatched 4 times, each completion re-triggered full goal completion cycle
- **Result**: 4x goal completed messages, 4x goals.yaml commits

## ROOT CAUSE: MISSING IDEMPOTENCY GUARD

The bug is in HandleTaskCompletionAsync (GoalDispatcher.cs, lines 145-174).

When a task completes, the method:
1. Retrieves the pipeline by task ID: _pipelineManager.GetByTaskId(complete.TaskId) (line 147)
2. **Does NOT check if the pipeline is already Done/Failed**
3. Proceeds to call DriveNextPhaseAsync which can call MarkGoalCompleted (line 389)

**MISSING GUARD**:
\\\csharp
if (pipeline.Phase == GoalPhase.Done || pipeline.Phase == GoalPhase.Failed)
{
    _logger.LogInformation("Goal already finished, ignoring duplicate completion");
    return;
}
\\\

## How Multiple Completions Happen

When improver completes (lines 223-264 in DriveNextPhaseAsync):
1. Agent file validation loop at line 242 re-dispatches improver inline (line 250)
2. Each re-dispatch creates a NEW task with a NEW TaskId
3. When each task completes, a new TaskComplete event arrives
4. Each event calls HandleTaskCompletionAsync with a different TaskId
5. But each TaskId maps to the SAME goal via _pipelineManager.GetByTaskId()
6. **Without the phase guard, each completion event processes the entire pipeline AGAIN**
7. Each call reaches MarkGoalCompleted → goal marked Done 4 times

## Evidence

### No Phase Guard in HandleTaskCompletionAsync (Lines 145-174)
\\\csharp
public async Task HandleTaskCompletionAsync(TaskComplete complete, CancellationToken ct = default)
{
    var pipeline = _pipelineManager.GetByTaskId(complete.TaskId);
    if (pipeline is null)
    {
        _logger.LogWarning("No pipeline found for completed task {TaskId}", complete.TaskId);
        return;
    }
    
    // ⚠️ NO GUARD HERE - could already be Done!
    
    _logger.LogInformation("Pipeline {GoalId} task completed (phase={Phase}, status={Status})",
        pipeline.GoalId, pipeline.Phase, complete.Status);

    if (_brain is null)
    {
        await MarkGoalCompleted(pipeline, ct);  // Can be called when phase=Done
        return;
    }

    try
    {
        await DriveNextPhaseAsync(pipeline, complete, ct);  // Re-processes Done phase
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error driving pipeline {GoalId} to next phase", pipeline.GoalId);
        await MarkGoalFailed(pipeline, ex.Message, ct);
    }

    _pipelineManager.PersistFull(pipeline);
}
\\\

### No Guard in MarkGoalCompleted (Lines 1074-1149)
\\\csharp
private async Task MarkGoalCompleted(GoalPipeline pipeline, CancellationToken ct)
{
    pipeline.AdvanceTo(GoalPhase.Done);  // ⚠️ Can set Done when already Done
    var completedMeta = new GoalUpdateMetadata { ... };
    await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Completed, completedMeta, ct);
    await CommitGoalsToConfigRepoAsync($"Goal '{pipeline.GoalId}' completed", ct);  // Commits 4x
    // ... more processing
}
\\\

### ClearActiveTask Never Called
The method exists (lines 226-233) but is NEVER invoked:
- After task completion, ActiveTaskId persists
- Task→goal mapping persists (GoalPipelineManager._taskToGoal)
- Old tasks can be re-looked-up and re-processed

## Why Improver Was Dispatched 4 Times

The improver re-dispatch loop (lines 242-264):
\\\csharp
while (content.Length > MaxAgentsMdChars && pipeline.ImproverRetries < MaxImproverRetries)
{
    pipeline.IncrementImproverRetry();
    var retryPrompt = "...";
    await DispatchToRole(pipeline, WorkerRole.Improver, retryPrompt, ct);  // Dispatch
    await SyncAgentsFromConfigRepoAsync(ct);
    content = await File.ReadAllTextAsync(agentsMdPath, ct);
}
\\\

Each DispatchToRole call:
- Builds a new Task via TaskBuilder (line 674-682)
- Assigns a UNIQUE TaskId to each task
- Registers the mapping: _pipelineManager.RegisterTask(task.TaskId, goalId) (line 698)

So if the agents.md file exceeded 4000 chars on validation, the loop could dispatch 1-3 retries inline. Plus the initial dispatch = 1-4 improver tasks total.

All 4 task completion events then find the same pipeline and re-complete the goal.

## Impact Timeline

| Time | Event | Result |
|------|-------|--------|
| ~23 min | Improver completes, inline retries attempted, goal transitions to Merging→Done→Completed | **Completion #1** (expected) |
| ~26 min | Improver retry task #2 completes, HandleTaskCompletionAsync processes it | **Completion #2** (duplicate) |
| ~27 min | Improver retry task #3 completes, HandleTaskCompletionAsync processes it | **Completion #3** (duplicate) |
| ~27.3 min | Improver retry task #4 completes, HandleTaskCompletionAsync processes it | **Completion #4** (duplicate) |

Each completion:
- Calls UpdateGoalStatusAsync with Completed status
- Commits goals.yaml to config repo

## Required Fixes

### CRITICAL: Add Phase Guard in HandleTaskCompletionAsync
\\\csharp
public async Task HandleTaskCompletionAsync(TaskComplete complete, CancellationToken ct = default)
{
    var pipeline = _pipelineManager.GetByTaskId(complete.TaskId);
    if (pipeline is null)
    {
        _logger.LogWarning("No pipeline found for completed task {TaskId}", complete.TaskId);
        return;
    }

    // ADD THIS:
    if (pipeline.Phase == GoalPhase.Done || pipeline.Phase == GoalPhase.Failed)
    {
        _logger.LogInformation(
            "Task {TaskId} completed but goal {GoalId} already finished (phase={Phase}). " +
            "Ignoring duplicate completion.", 
            complete.TaskId, pipeline.GoalId, pipeline.Phase);
        return;
    }

    _logger.LogInformation("Pipeline {GoalId} task completed (phase={Phase}, status={Status})",
        pipeline.GoalId, pipeline.Phase, complete.Status);

    // ... rest of method unchanged
}
\\\

### HIGH: Add Defensive Guard in MarkGoalCompleted
\\\csharp
private async Task MarkGoalCompleted(GoalPipeline pipeline, CancellationToken ct)
{
    if (pipeline.Phase == GoalPhase.Done)
    {
        _logger.LogWarning("Goal {GoalId} already completed, skipping duplicate", pipeline.GoalId);
        return;
    }

    pipeline.AdvanceTo(GoalPhase.Done);
    // ... rest unchanged
}
\\\

### MEDIUM: Clear Active Task
\\\csharp
// In HandleTaskCompletionAsync after successful processing:
pipeline.ClearActiveTask();
\\\

### OPTIONAL: Clean Up Completed Pipelines
\\\csharp
// In MarkGoalCompleted after recording metrics:
_pipelineManager.RemovePipeline(pipeline.GoalId);
\\\

## Files to Review

1. **GoalDispatcher.cs** (lines 145-174): Add phase guard in HandleTaskCompletionAsync
2. **GoalDispatcher.cs** (lines 1074-1149): Add guard in MarkGoalCompleted
3. **GoalPipeline.cs** (lines 226-233): ClearActiveTask method exists but unused
4. **GoalPipelineManager.cs** (lines 42-43): GetByTaskId maps any taskId to pipeline, no validation

## Summary

The improver was dispatched 4 times due to the retry validation loop (expected behavior). Each dispatch created a new task. However, when multiple task completions arrived for the same goal:

1. **No idempotency guard** in HandleTaskCompletionAsync allowed each completion to process the pipeline
2. **No phase check** prevented re-processing of already-Done pipelines  
3. **No active task cleanup** meant old tasks persisted in the mapping
4. Each of the 4 task completions called MarkGoalCompleted, resulting in:
   - 4× UpdateGoalStatusAsync calls
   - 4× CommitGoalsToConfigRepoAsync calls
   - 4× 🎉 completion log messages

**Root cause: MISSING GUARD on pipeline.Phase in HandleTaskCompletionAsync**
