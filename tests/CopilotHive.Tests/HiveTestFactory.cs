using CopilotHive;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Defines the xUnit collection that shares a single <see cref="HiveTestFactory"/> instance
/// across all integration test classes annotated with <c>[Collection("HiveIntegration")]</c>.
/// Sharing one factory prevents parallel SQLite write conflicts (Error 8: readonly database).
/// </summary>
[CollectionDefinition("HiveIntegration")]
public class HiveTestCollection : ICollectionFixture<HiveTestFactory>
{
}

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that boots the real CopilotHive application
/// using an isolated temporary directory for state (SQLite database, metrics) so tests do not
/// touch production storage paths such as <c>/app/state</c>.
/// </summary>
/// <remarks>
/// The <c>STATE_DIR</c> environment variable must be set before the entry point reads it (before
/// <c>WebApplicationFactory</c> triggers host creation via <c>CreateClient()</c>), so it is set
/// eagerly in the constructor.
/// </remarks>
public sealed class HiveTestFactory : WebApplicationFactory<Program>
{
    private readonly string _stateDir =
        Path.Combine(Path.GetTempPath(), $"copilothive-test-{Guid.NewGuid():N}");
    private readonly string? _previousStateDir;

    /// <summary>
    /// Optional mock for IBrainRepoManager. If set, it will replace the real implementation in DI.
    /// </summary>
    public IBrainRepoManager? MockRepoManager { get; set; }

    /// <summary>
    /// Optional stub for GoalReviewService. If set, it will replace the real implementation in DI.
    /// </summary>
    public GoalReviewService? MockGoalReviewService { get; set; }

    /// <summary>
    /// Optional chat-client factory used to stub out the LLM calls in <see cref="GoalReviewService"/>.
    /// When set, the real GoalReviewService is re-registered with this factory.
    /// </summary>
    public Func<string, Microsoft.Extensions.AI.IChatClient>? ReviewChatClientFactory { get; set; }

    /// <summary>
    /// Initialises the factory and points <c>STATE_DIR</c> at a temporary directory.
    /// </summary>
    public HiveTestFactory()
    {
        _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
        Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
        Directory.CreateDirectory(_stateDir);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Replace IBrainRepoManager with mock if set
        if (MockRepoManager is not null)
        {
            builder.ConfigureServices(services =>
            {
                // Remove existing IBrainRepoManager registration
                var existingDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IBrainRepoManager));
                if (existingDescriptor is not null)
                    services.Remove(existingDescriptor);

                // Add mock
                services.AddSingleton(MockRepoManager);
            });
        }

        // Replace GoalReviewService with stub if set
        if (MockGoalReviewService is not null)
        {
            builder.ConfigureServices(services =>
            {
                var existingDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(GoalReviewService));
                if (existingDescriptor is not null)
                    services.Remove(existingDescriptor);

                services.AddSingleton(MockGoalReviewService);
            });
        }

        // Re-register GoalReviewService with a custom chat-client factory to avoid real LLM calls.
        if (ReviewChatClientFactory is not null)
        {
            builder.ConfigureServices(services =>
            {
                var existingDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(GoalReviewService));
                if (existingDescriptor is not null)
                    services.Remove(existingDescriptor);

                services.AddSingleton(sp => new GoalReviewService(
                    sp.GetService<CopilotHive.Knowledge.KnowledgeGraph>(),
                    sp.GetService<CopilotHive.Configuration.ConfigRepoManager>(),
                    sp.GetService<CopilotHive.Configuration.HiveConfigFile>(),
                    sp.GetRequiredService<IGoalStore>(),
                    sp.GetService<CopilotHive.Git.IBrainRepoManager>(),
                    Environment.GetEnvironmentVariable("STATE_DIR") ?? Path.GetTempPath(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GoalReviewService>>(),
                    ReviewChatClientFactory));
            });
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);

        if (!disposing || !Directory.Exists(_stateDir))
            return;

        // Best-effort cleanup — SQLite may still hold file locks briefly after host shutdown.
        // Files are in the OS temp directory and will be cleaned up eventually.
        try
        {
            Directory.Delete(_stateDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
