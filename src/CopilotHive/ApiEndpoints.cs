using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Models;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace CopilotHive;

/// <summary>
/// Extension methods that register the orchestrator's REST API endpoints on a
/// <see cref="WebApplication"/>. Extracted from <c>Program.cs</c> to keep endpoint
/// definitions grouped and discoverable.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>
    /// Registers the <c>/health</c> and <c>/health/utilization</c> endpoints.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    /// <param name="serverStartTime">The UTC time the server started, used to compute uptime.</param>
    /// <param name="version">The informational version string reported by the health endpoint.</param>
    public static void MapHealthEndpoints(this WebApplication app, DateTime serverStartTime, string version)
    {
        var checkCount = 0;

        app.MapGet("/health", async (IGoalStore goalStore, WorkerPool workerPool) =>
        {
            var count = Interlocked.Increment(ref checkCount);
            var uptime = DateTime.UtcNow - serverStartTime;
            var goals = await goalStore.GetAllGoalsAsync();
            return Results.Ok(new HealthResponse
            {
                Status = "Healthy",
                Uptime = $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}",
                UptimeSpan = uptime,
                ActiveGoals = goals.Count(g => g.Status is GoalStatus.Pending or GoalStatus.InProgress),
                CompletedGoals = goals.Count(g => g.Status == GoalStatus.Completed),
                ConnectedWorkers = workerPool.GetAllWorkers().Count,
                Version = version,
                SharpCoderVersion = typeof(SharpCoder.CodingAgent).Assembly.GetName().Version?.ToString(),
                ServerTime = DateTime.UtcNow,
                CheckNumber = count,
                WorkerPool = workerPool.GetDetailedStats(),
            });
        });

        app.MapGet("/health/utilization", (WorkerUtilizationService svc) => Results.Ok(svc.GetUtilization()));
    }

    /// <summary>
    /// Registers the goals REST API endpoints under <c>/api/goals</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapGoalEndpoints(this WebApplication app)
    {
        // ── Goals REST API ───────────────────────────────────────────────────────
        var goalsApi = app.MapGroup("/api/goals");

        goalsApi.MapGet("/", async (IGoalStore store) =>
            Results.Ok(await store.GetAllGoalsAsync()));

        goalsApi.MapGet("/{id}", async (string id, IGoalStore store) =>
        {
            var goal = await store.GetGoalAsync(id);
            return goal is null ? Results.NotFound(new { error = $"Goal '{id}' not found." }) : Results.Ok(goal);
        });

        goalsApi.MapPost("/", async (Goal goal, IGoalStore store) =>
        {
            try
            {
                var created = await store.CreateGoalAsync(goal);
                return Results.Created($"/api/goals/{created.Id}", created);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (duplicate primary key)
            {
                return Results.Conflict(new { error = $"Goal '{goal.Id}' already exists." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        goalsApi.MapPatch("/{id}/status", async (string id, GoalStatusUpdate update, IGoalStore store,
            IBrainRepoManager? repoManager, GoalDispatcher? dispatcher, ILogger<Program> endpointLogger) =>
        {
            try
            {
                var status = Enum.Parse<GoalStatus>(update.Status, ignoreCase: true);

                var existing = await store.GetGoalAsync(id);
                if (existing is null)
                    return Results.NotFound(new { error = $"Goal '{id}' not found." });

                // Allowed transitions via the public API:
                //   Draft ↔ Pending (approve / revert)
                //   Failed → Draft (retry — resets iteration data and cleans up feature branch)
                var validTransition =
                    existing.Status == GoalStatus.Draft && status == GoalStatus.Pending ||
                    existing.Status == GoalStatus.Pending && status == GoalStatus.Draft ||
                    existing.Status == GoalStatus.Failed && status == GoalStatus.Draft;
                if (!validTransition)
                    return Results.BadRequest(new { error = $"Invalid transition from {existing.Status} to {status}. Only Draft→Pending, Pending→Draft, and Failed→Draft are allowed." });

                // Failed→Draft: reset iteration data and delete the feature branch (best-effort)
                if (existing.Status == GoalStatus.Failed && status == GoalStatus.Draft)
                {
                    await store.ResetGoalIterationDataAsync(id);

                    // Clear GoalDispatcher runtime state so the goal can be re-dispatched fresh
                    dispatcher?.ClearGoalRetryState(id);

                    if (repoManager is not null)
                    {
                        var branchName = $"copilothive/{id}";
                        foreach (var repoName in existing.RepositoryNames)
                        {
                            _ = await repoManager.DeleteRemoteBranchAsync(repoName, branchName);
                        }
                    }
                }

                await store.UpdateGoalStatusAsync(id, status);
                var goal = await store.GetGoalAsync(id);
                return Results.Ok(goal);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Goal '{id}' not found." });
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = $"Invalid status '{update.Status}'." });
            }
        });

        goalsApi.MapDelete("/{id}", async (string id, IGoalStore store, IBrainRepoManager? repoManager, ILogger<Program> logger) =>
        {
            var goal = await store.GetGoalAsync(id);
            if (goal is null)
                return Results.NotFound(new { error = $"Goal '{id}' not found." });

            if (goal.Status is not (GoalStatus.Draft or GoalStatus.Failed))
                return Results.BadRequest(new { error = "Only Draft or Failed goals can be deleted" });

            var deleted = await store.DeleteGoalAsync(id);
            if (!deleted)
                return Results.NotFound(new { error = $"Goal '{id}' not found." });

            // Best-effort cleanup of remote feature branches for Failed goals
            if (goal.Status == GoalStatus.Failed)
            {
                if (repoManager is not null)
                {
                    var branchName = $"copilothive/{id}";
                    foreach (var repoName in goal.RepositoryNames)
                    {
                        _ = await repoManager.DeleteRemoteBranchAsync(repoName, branchName);
                    }
                }
            }

            return Results.NoContent();
        });

        goalsApi.MapPost("/{id}/cancel", async (string id, GoalDispatcher dispatcher, IGoalStore store) =>
        {
            var goal = await store.GetGoalAsync(id);
            if (goal is null)
                return Results.NotFound(new { error = $"Goal '{id}' not found." });

            if (goal.Status is not (GoalStatus.InProgress or GoalStatus.Pending))
                return Results.BadRequest(new { error = $"Goal '{id}' is {goal.Status} and cannot be cancelled. Only InProgress or Pending goals can be cancelled." });

            var cancelled = await dispatcher.CancelGoalAsync(id);
            return cancelled
                ? Results.Ok(new { message = $"Goal '{id}' has been cancelled." })
                : Results.BadRequest(new { error = $"Goal '{id}' could not be cancelled (it may have already completed or failed)." });
        });

        goalsApi.MapGet("/search", async (string q, string? status, IGoalStore store) =>
        {
            GoalStatus? statusFilter = null;
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var s))
                statusFilter = s;
            var results = await store.SearchGoalsAsync(q, statusFilter);
            return Results.Ok(results);
        });

        goalsApi.MapPatch("/{id}/release", async (string id, AssignGoalReleaseRequest request, IGoalStore store) =>
        {
            var release = await store.GetReleaseAsync(request.ReleaseId);
            if (release is null)
                return Results.NotFound(new { error = $"Release '{request.ReleaseId}' not found." });

            var goal = await store.GetGoalAsync(id);
            if (goal is null)
                return Results.NotFound(new { error = $"Goal '{id}' not found." });

            goal.ReleaseId = request.ReleaseId;
            await store.UpdateGoalAsync(goal);
            return Results.Ok(goal);
        });
    }

    /// <summary>
    /// Registers the releases REST API endpoints under <c>/api/releases</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapReleaseEndpoints(this WebApplication app)
    {
        // ── Releases REST API ────────────────────────────────────────────────────
        var releasesApi = app.MapGroup("/api/releases");

        releasesApi.MapPost("/", async (CreateReleaseRequest request, IGoalStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.Version))
                return Results.BadRequest(new { error = "Version is required." });

            var release = new Release
            {
                Id = request.Version,
                Tag = request.Version,
                RepositoryNames = string.IsNullOrEmpty(request.Repository) ? [] : [request.Repository],
            };

            try
            {
                var created = await store.CreateReleaseAsync(release);
                return Results.Created($"/api/releases/{created.Id}", created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        releasesApi.MapPatch("/{id}/status", async (string id, UpdateReleaseStatusRequest request, IGoalStore store) =>
        {
            var existing = await store.GetReleaseAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Release '{id}' not found." });

            if (string.IsNullOrEmpty(request.Status))
                return Results.BadRequest(new { error = "Status is required." });

            if (!Enum.TryParse<ReleaseStatus>(request.Status, ignoreCase: true, out var newStatus))
                return Results.BadRequest(new { error = $"Invalid status '{request.Status}'." });

            existing.Status = newStatus;
            if (newStatus == ReleaseStatus.Released && existing.ReleasedAt is null)
                existing.ReleasedAt = DateTime.UtcNow;

            await store.UpdateReleaseAsync(existing);
            return Results.Ok(existing);
        });

        releasesApi.MapPatch("/{id}/notes", async (string id, UpdateReleaseNotesRequest request, IGoalStore store) =>
        {
            var existing = await store.GetReleaseAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Release '{id}' not found." });

            existing.Notes = request.Notes;
            await store.UpdateReleaseAsync(existing);
            return Results.Ok(existing);
        });

        releasesApi.MapPatch("/{id}/tag", async (string id, UpdateReleaseTagRequest request, IGoalStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.Tag))
                return Results.BadRequest(new { error = "Tag is required." });

            try
            {
                await store.UpdateReleaseAsync(id, new ReleaseUpdateData { Tag = request.Tag.Trim() });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Release '{id}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var updated = await store.GetReleaseAsync(id);
            return Results.Ok(updated);
        });

        releasesApi.MapPatch("/{id}/repositories", async (string id, UpdateReleaseRepositoriesRequest request, IGoalStore store) =>
        {
            try
            {
                await store.UpdateReleaseAsync(id, new ReleaseUpdateData { Repositories = request.Repositories });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Release '{id}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var updated = await store.GetReleaseAsync(id);
            return Results.Ok(updated);
        });
    }

    /// <summary>
    /// Registers the clarifications REST API endpoints under <c>/api/clarifications</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapClarificationEndpoints(this WebApplication app)
    {
        // ── Clarifications REST API ──────────────────────────────────────────────
        var clarificationsApi = app.MapGroup("/api/clarifications");

        clarificationsApi.MapGet("/", (ClarificationQueueService queue) =>
            Results.Ok(queue.GetAllRequests()));

        clarificationsApi.MapGet("/pending", (ClarificationQueueService queue) =>
            Results.Ok(queue.GetPendingHumanRequests()));

        clarificationsApi.MapGet("/count", (ClarificationQueueService queue) =>
            Results.Ok(new { count = queue.PendingHumanCount }));

        clarificationsApi.MapPost("/{id}/answer", (string id, SubmitClarificationRequest body, ClarificationQueueService queue) =>
        {
            if (string.IsNullOrWhiteSpace(body.Answer))
                return Results.BadRequest(new { error = "Answer is required." });

            var answered = queue.SubmitAnswer(id, body.Answer, "human");
            if (!answered)
                return Results.NotFound(new { error = $"Clarification '{id}' not found." });

            return Results.Ok(new { message = $"Answer submitted for clarification '{id}'." });
        });
    }

    /// <summary>
    /// Registers the backup REST API endpoints under <c>/api/backup</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapBackupEndpoints(this WebApplication app)
    {
        // ── Backup REST API ──────────────────────────────────────────────────────
        var backupApi = app.MapGroup("/api/backup");

        backupApi.MapPost("/", async ([FromServices] BackupService svc) =>
        {
            var path = await svc.CreateBackupAsync();
            var fileName = Path.GetFileName(path);
            var info = svc.ListBackups().First(b => b.FileName == fileName);
            return Results.Ok(info);
        });

        backupApi.MapGet("/", ([FromServices] BackupService svc) =>
            Results.Ok(svc.ListBackups()));

        backupApi.MapGet("/{fileName}", (string fileName, [FromServices] BackupService svc) =>
        {
            // Path traversal protection: reject any fileName that is not a bare file name.
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.Contains('/')
                || fileName.Contains('\\')
                || fileName.Contains("..")
                || Path.GetFileName(fileName) != fileName)
            {
                return Results.BadRequest(new { error = "Invalid file name." });
            }

            var backupDir = Path.GetFullPath(svc.BackupDirectory);
            var fullPath = Path.GetFullPath(Path.Combine(backupDir, fileName));

            // Ensure the resolved path stays within the backup directory.
            var backupDirWithSep = backupDir.EndsWith(Path.DirectorySeparatorChar)
                ? backupDir
                : backupDir + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(backupDirWithSep, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Invalid file name." });

            if (!File.Exists(fullPath))
                return Results.NotFound(new { error = "Backup not found." });

            return Results.File(fullPath, "application/gzip", fileName);
        });

        backupApi.MapPost("/restore", async (RestoreRequest request, [FromServices] BackupService svc) =>
        {
            if (string.IsNullOrWhiteSpace(request.FileName))
                return Results.BadRequest(new { error = "FileName is required." });

            var fileName = request.FileName;
            if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")
                || Path.GetFileName(fileName) != fileName)
            {
                return Results.BadRequest(new { error = "Invalid file name." });
            }

            var fullPath = Path.GetFullPath(Path.Combine(svc.BackupDirectory, fileName));
            var backupDirWithSep = Path.GetFullPath(svc.BackupDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(backupDirWithSep, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Invalid file name." });

            if (!File.Exists(fullPath))
                return Results.NotFound(new { error = "Backup not found." });

            var result = await svc.RestoreBackupAsync(fullPath);
            return Results.Ok(new
            {
                message = "Restore complete. Restart the orchestrator for changes to take effect.",
                result.DatabaseRestored,
                result.BrainMasterSession,
                result.BrainGoalSessionCount,
                result.ComposerSession,
                result.MetricsCount,
                result.KeysCount,
                result.SafetyBackupPath,
            });
        });
    }
}

/// <summary>Request body for updating the status of a goal via the HTTP API.</summary>
/// <param name="Status">New status string (e.g. "completed", "failed").</param>
public record GoalStatusUpdate(string Status);

/// <summary>Request body for creating a new release via the HTTP API.</summary>
/// <param name="Version">Version tag for the release (e.g. "v1.2.0").</param>
/// <param name="Repository">Optional repository name this release belongs to.</param>
public record CreateReleaseRequest(string Version, string? Repository = null);

/// <summary>Request body for updating the status of a release via the HTTP API.</summary>
/// <param name="Status">New status string (e.g. "Planning" or "Released").</param>
public record UpdateReleaseStatusRequest(string Status);

/// <summary>Request body for updating the notes of a release via the HTTP API.</summary>
/// <param name="Notes">Updated release notes.</param>
public record UpdateReleaseNotesRequest(string? Notes);

/// <summary>Request body for updating the tag of a Planning release via the HTTP API.</summary>
/// <param name="Tag">New version tag (e.g. "v1.2.1").</param>
public record UpdateReleaseTagRequest(string Tag);

/// <summary>Request body for updating the repository list of a Planning release via the HTTP API.</summary>
/// <param name="Repositories">New list of repository names. An empty list clears all repositories.</param>
public record UpdateReleaseRepositoriesRequest(List<string>? Repositories);

/// <summary>Request body for assigning a goal to a release via the HTTP API.</summary>
/// <param name="ReleaseId">The release ID to assign this goal to.</param>
public record AssignGoalReleaseRequest(string ReleaseId);

/// <summary>Request body for submitting an answer to a clarification request via the HTTP API.</summary>
/// <param name="Answer">The answer text to submit.</param>
public record SubmitClarificationRequest(string Answer);

/// <summary>Request body for restoring a backup archive via the HTTP API.</summary>
/// <param name="FileName">The backup archive file name to restore.</param>
public record RestoreRequest(string FileName);
