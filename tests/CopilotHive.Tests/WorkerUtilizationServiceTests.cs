using System.Net;
using CopilotHive.Models;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Unit and integration tests for <see cref="WorkerUtilizationService"/>.
/// </summary>
public class WorkerUtilizationServiceTests
{
    private static WorkerPool CreatePool() => new WorkerPool();

    private static ConnectedWorker MakeWorker(WorkerPool pool, string id, bool busy)
    {
        var w = pool.RegisterWorker(id, []);
        if (busy) pool.MarkBusy(id, "task-" + id);
        return w;
    }

    [Fact]
    public void GetUtilization_EmptyPool_ReturnsZeroUtilization()
    {
        var pool = CreatePool();
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Equal(0.0, result.OverallUtilization);
        Assert.Empty(result.RoleBreakdown);
        Assert.Empty(result.BottleneckRoles);
    }

    [Fact]
    public void GetUtilization_SomeBusy_ReturnsCorrectFraction()
    {
        var pool = CreatePool();
        MakeWorker(pool, "w1", busy: true);
        MakeWorker(pool, "w2", busy: true);
        MakeWorker(pool, "w3", busy: false);
        MakeWorker(pool, "w4", busy: false);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Equal(0.5, result.OverallUtilization);
    }

    [Fact]
    public void GetUtilization_AllBusy_ReturnsOne()
    {
        var pool = CreatePool();
        MakeWorker(pool, "w1", busy: true);
        MakeWorker(pool, "w2", busy: true);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Equal(1.0, result.OverallUtilization);
    }

    [Fact]
    public void GetUtilization_RoleBreakdown_IsAccurate()
    {
        var pool = CreatePool();
        MakeWorker(pool, "c1", busy: true);
        MakeWorker(pool, "c2", busy: false);
        MakeWorker(pool, "t1", busy: true);
        MakeWorker(pool, "t2", busy: true);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        // All workers are Unspecified — 3 of 4 busy = 0.75
        Assert.Single(result.RoleBreakdown);
        Assert.Equal(0.75, result.RoleBreakdown["Unspecified"]);
    }

    [Fact]
    public void GetUtilization_BottleneckRoles_DetectsAbove80Percent()
    {
        var pool = CreatePool();
        // 9 of 10 busy → 0.9 > 0.8
        for (int i = 0; i < 9; i++)
            MakeWorker(pool, $"c{i}", busy: true);
        MakeWorker(pool, "c9", busy: false);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Contains("Unspecified", result.BottleneckRoles);
    }

    [Fact]
    public void GetUtilization_BottleneckRoles_ExcludesAt80Percent()
    {
        var pool = CreatePool();
        // 4 of 5 busy → exactly 0.8 (not > 0.8)
        for (int i = 0; i < 4; i++)
            MakeWorker(pool, $"r{i}", busy: true);
        MakeWorker(pool, "r4", busy: false);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.DoesNotContain("Unspecified", result.BottleneckRoles);
        Assert.Equal(0.8, result.RoleBreakdown["Unspecified"]);
    }
}

/// <summary>
/// Integration tests for the <c>GET /health/utilization</c> endpoint.
/// </summary>
[Collection("HiveIntegration")]
public class UtilizationEndpointTests
{
    private readonly HttpClient _client;

    /// <summary>Initialises with the shared <see cref="HiveTestFactory"/> fixture.</summary>
    /// <param name="factory">The shared test factory.</param>
    public UtilizationEndpointTests(HiveTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task UtilizationEndpoint_Returns200WithJsonContentType()
    {
        var response = await _client.GetAsync("/health/utilization", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString());
    }
}
