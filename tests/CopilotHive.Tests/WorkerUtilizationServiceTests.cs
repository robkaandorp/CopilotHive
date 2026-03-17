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

    private static ConnectedWorker MakeWorker(WorkerPool pool, string id, WorkerRole role, bool busy)
    {
        var w = pool.RegisterWorker(id, role, []);
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
        MakeWorker(pool, "w1", WorkerRole.Coder, busy: true);
        MakeWorker(pool, "w2", WorkerRole.Coder, busy: true);
        MakeWorker(pool, "w3", WorkerRole.Coder, busy: false);
        MakeWorker(pool, "w4", WorkerRole.Coder, busy: false);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Equal(0.5, result.OverallUtilization);
    }

    [Fact]
    public void GetUtilization_AllBusy_ReturnsOne()
    {
        var pool = CreatePool();
        MakeWorker(pool, "w1", WorkerRole.Tester, busy: true);
        MakeWorker(pool, "w2", WorkerRole.Tester, busy: true);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Equal(1.0, result.OverallUtilization);
    }

    [Fact]
    public void GetUtilization_RoleBreakdown_IsAccurate()
    {
        var pool = CreatePool();
        MakeWorker(pool, "c1", WorkerRole.Coder, busy: true);
        MakeWorker(pool, "c2", WorkerRole.Coder, busy: false);
        MakeWorker(pool, "t1", WorkerRole.Tester, busy: true);
        MakeWorker(pool, "t2", WorkerRole.Tester, busy: true);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Equal(0.5, result.RoleBreakdown["Coder"]);
        Assert.Equal(1.0, result.RoleBreakdown["Tester"]);
    }

    [Fact]
    public void GetUtilization_BottleneckRoles_DetectsAbove80Percent()
    {
        var pool = CreatePool();
        // 9 of 10 busy → 0.9 > 0.8
        for (int i = 0; i < 9; i++)
            MakeWorker(pool, $"c{i}", WorkerRole.Coder, busy: true);
        MakeWorker(pool, "c9", WorkerRole.Coder, busy: false);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Contains("Coder", result.BottleneckRoles);
    }

    [Fact]
    public void GetUtilization_BottleneckRoles_ExcludesAt80Percent()
    {
        var pool = CreatePool();
        // 4 of 5 busy → exactly 0.8 (not > 0.8)
        for (int i = 0; i < 4; i++)
            MakeWorker(pool, $"r{i}", WorkerRole.Reviewer, busy: true);
        MakeWorker(pool, "r4", WorkerRole.Reviewer, busy: false);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.DoesNotContain("Reviewer", result.BottleneckRoles);
        Assert.Equal(0.8, result.RoleBreakdown["Reviewer"]);
    }
}

/// <summary>
/// Integration tests for the <c>GET /health/utilization</c> endpoint.
/// </summary>
public class UtilizationEndpointTests : IClassFixture<HiveTestFactory>
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
        var response = await _client.GetAsync("/health/utilization");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString());
    }
}
