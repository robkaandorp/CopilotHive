using System.Text.Json;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the <c>checkNumber</c> field on <c>GET /health</c>.
/// Verifies that the counter increments by exactly 1 with each successive request,
/// exercising the thread-safe <c>Interlocked.Increment</c> logic in
/// <c>Program.cs</c> via the real application.
/// </summary>
[Collection("HiveIntegration")]
public class HealthCheckTests
{
    private readonly HiveTestFactory _factory;

    /// <summary>Receives the shared factory fixture for this test class.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/> for this test class.</param>
    public HealthCheckTests(HiveTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Sends GET /health and returns the <c>checkNumber</c> value.</summary>
    /// <returns>The <c>checkNumber</c> integer from the response body.</returns>
    private async Task<int> GetCheckNumberAsync()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("checkNumber").GetInt32();
    }

    [Fact]
    public async Task HealthEndpoint_CheckNumber_IsPositive()
    {
        var count = await GetCheckNumberAsync();
        Assert.True(count >= 1, "checkNumber must be >= 1 after at least one request");
    }

    [Fact]
    public async Task HealthEndpoint_SequentialCalls_IncrementCheckNumberByOne()
    {
        var first = await GetCheckNumberAsync();
        var second = await GetCheckNumberAsync();
        Assert.Equal(first + 1, second);
    }

    [Fact]
    public async Task HealthEndpoint_MultipleCalls_IncrementCheckNumberMonotonically()
    {
        int? prev = null;
        for (var i = 0; i < 5; i++)
        {
            var count = await GetCheckNumberAsync();
            if (prev.HasValue)
                Assert.Equal(prev + 1, count);
            prev = count;
        }
    }
}
