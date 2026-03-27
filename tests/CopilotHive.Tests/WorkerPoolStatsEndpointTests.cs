using System.Net;
using System.Text.Json;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the <c>worker_pool</c> statistics nested inside the <c>GET /health</c> response.
/// Uses <see cref="HiveTestFactory"/> (<see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>)
/// to boot the real application — no routes are re-implemented here.
/// </summary>
[Collection("HiveIntegration")]
public class WorkerPoolStatsEndpointTests
{
    private readonly HttpClient _client;
    private readonly HiveTestFactory _factory;

    /// <summary>
    /// Receives the shared factory and creates an <see cref="HttpClient"/> backed by the test server.
    /// </summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/> fixture for this test class.</param>
    public WorkerPoolStatsEndpointTests(HiveTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Sends GET /health and parses the JSON response body.</summary>
    /// <returns>A parsed <see cref="JsonDocument"/> of the response body.</returns>
    private async Task<JsonDocument> GetHealthJsonAsync()
    {
        var response = await _client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Extracts the <c>worker_pool</c> element from the health response.</summary>
    private async Task<JsonElement> GetWorkerPoolElementAsync()
    {
        using var json = await GetHealthJsonAsync();
        Assert.True(json.RootElement.TryGetProperty("worker_pool", out var wp),
            "Expected 'worker_pool' property in health response");
        // Clone so the document can be disposed.
        return wp.Clone();
    }

    [Fact]
    public async Task GetHealth_Returns200_WithWorkerPool()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_IncludesTotalWorkersField()
    {
        var wp = await GetWorkerPoolElementAsync();
        Assert.True(wp.TryGetProperty("total_workers", out var val),
            "Expected 'total_workers' inside worker_pool");
        Assert.True(val.GetInt32() >= 0);
    }

    [Fact]
    public async Task GetHealth_IncludesIdleAndBusyFields()
    {
        var wp = await GetWorkerPoolElementAsync();

        Assert.True(wp.TryGetProperty("idle_workers", out var idle),
            "Expected 'idle_workers' inside worker_pool");
        Assert.True(idle.GetInt32() >= 0);

        Assert.True(wp.TryGetProperty("busy_workers", out var busy),
            "Expected 'busy_workers' inside worker_pool");
        Assert.True(busy.GetInt32() >= 0);
    }

    [Fact]
    public async Task GetHealth_IncludesWorkersArray()
    {
        var wp = await GetWorkerPoolElementAsync();
        Assert.True(wp.TryGetProperty("workers", out var workers),
            "Expected 'workers' array inside worker_pool");
        Assert.Equal(JsonValueKind.Array, workers.ValueKind);
    }

    [Fact]
    public async Task GetHealth_BusyWorkerReflectedInStats()
    {
        var pool = _factory.Services.GetRequiredService<WorkerPool>();
        var workerId = "stats-busy-" + Guid.NewGuid();

        pool.RegisterWorker(workerId, []);
        pool.MarkBusy(workerId, "task-busy-test");

        try
        {
            var wp = await GetWorkerPoolElementAsync();
            var busyCount = wp.GetProperty("busy_workers").GetInt32();
            Assert.True(busyCount >= 1,
                $"Expected busy_workers >= 1 after marking a worker busy, got {busyCount}");

            // Verify the individual worker entry
            var workers = wp.GetProperty("workers");
            var found = false;
            foreach (var w in workers.EnumerateArray())
            {
                if (w.GetProperty("id").GetString() == workerId)
                {
                    Assert.True(w.GetProperty("is_busy").GetBoolean());
                    Assert.Equal("task-busy-test", w.GetProperty("current_task_id").GetString());
                    found = true;
                    break;
                }
            }
            Assert.True(found, $"Worker '{workerId}' not found in workers array");
        }
        finally
        {
            pool.RemoveWorker(workerId);
        }
    }

    [Fact]
    public async Task GetHealth_IdleWorkerReflectedInStats()
    {
        var pool = _factory.Services.GetRequiredService<WorkerPool>();
        var workerId = "stats-idle-" + Guid.NewGuid();

        pool.RegisterWorker(workerId, []);

        try
        {
            var wp = await GetWorkerPoolElementAsync();
            var idleCount = wp.GetProperty("idle_workers").GetInt32();
            Assert.True(idleCount >= 1,
                $"Expected idle_workers >= 1 after registering an idle worker, got {idleCount}");
        }
        finally
        {
            pool.RemoveWorker(workerId);
        }
    }

    [Fact]
    public async Task GetHealth_WorkerEntryHasExpectedFields()
    {
        var pool = _factory.Services.GetRequiredService<WorkerPool>();
        var workerId = "stats-fields-" + Guid.NewGuid();

        pool.RegisterWorker(workerId, []);
        pool.MarkBusy(workerId, "task-xyz");

        try
        {
            var wp = await GetWorkerPoolElementAsync();
            var workers = wp.GetProperty("workers");
            JsonElement? target = null;
            foreach (var w in workers.EnumerateArray())
            {
                if (w.GetProperty("id").GetString() == workerId)
                {
                    target = w;
                    break;
                }
            }

            Assert.NotNull(target);
            var entry = target.Value;

            Assert.Equal(workerId, entry.GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("role").ValueKind);
            Assert.True(entry.GetProperty("is_busy").GetBoolean());
            Assert.Equal("task-xyz", entry.GetProperty("current_task_id").GetString());
        }
        finally
        {
            pool.RemoveWorker(workerId);
        }
    }
}
