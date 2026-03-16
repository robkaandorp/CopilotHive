using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Hosted background service that periodically scans the worker pool for stale workers
/// (workers whose heartbeat has not been received within <see cref="StaleTimeoutMinutes"/>
/// minutes) and removes them.
/// </summary>
public sealed class StaleWorkerCleanupService : BackgroundService
{
    /// <summary>How often (in seconds) the cleanup pass runs.</summary>
    public const int CleanupIntervalSeconds = 60;

    /// <summary>Maximum minutes a worker may be silent before being considered stale.</summary>
    public const int StaleTimeoutMinutes = 2;

    private readonly IWorkerPool _workerPool;
    private readonly ILogger<StaleWorkerCleanupService> _logger;

    /// <summary>
    /// Delay between cleanup passes. Defaults to <see cref="CleanupIntervalSeconds"/>.
    /// Settable internally to enable fast-cycle testing without waiting 60 s.
    /// </summary>
    internal TimeSpan CleanupDelay { get; set; } = TimeSpan.FromSeconds(CleanupIntervalSeconds);

    /// <summary>
    /// Initialises the service with the worker pool and a logger.
    /// </summary>
    /// <param name="workerPool">The pool from which stale workers are removed.</param>
    /// <param name="logger">Logger used to emit warnings for each evicted worker.</param>
    public StaleWorkerCleanupService(
        IWorkerPool workerPool,
        ILogger<StaleWorkerCleanupService> logger)
    {
        _workerPool = workerPool;
        _logger = logger;
    }

    /// <summary>
    /// Runs a cleanup loop that waits <see cref="CleanupIntervalSeconds"/> seconds between
    /// passes. On each pass it finds and removes all workers that have not sent a heartbeat
    /// within <see cref="StaleTimeoutMinutes"/> minutes, logging a warning per eviction.
    /// The loop exits when <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    /// <param name="stoppingToken">Token that signals the host is stopping.</param>
    /// <returns>A <see cref="Task"/> that completes when the service has stopped.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupDelay, stoppingToken);
                await RunCleanupCycleAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is stopping — exit cleanly.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup cycle failed");
            }
        }
    }

    /// <summary>
    /// Performs a single cleanup pass: atomically purges all stale workers and
    /// logs a warning for each eviction.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when the pass is finished.</returns>
    internal Task RunCleanupCycleAsync()
    {
        var removed = _workerPool.PurgeStaleWorkers(TimeSpan.FromMinutes(StaleTimeoutMinutes));

        foreach (var worker in removed)
            _logger.LogWarning("Removing stale worker {WorkerId}", worker.Id);

        return Task.CompletedTask;
    }
}
