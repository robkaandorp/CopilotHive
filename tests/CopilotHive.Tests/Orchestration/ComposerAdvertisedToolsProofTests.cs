using System.Runtime.CompilerServices;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// FINDING 7 — THE ADVERTISED-TOOLS PROOF, non-vacuous.
/// <para>
/// The tool list the model is actually offered is read from a REAL request's
/// <see cref="ChatOptions.Tools"/> — the options object SharpCoder builds and hands to the chat
/// client — not from the request history (which only shows what the model happened to CALL, and is
/// therefore empty for a tool that was never called).
/// </para>
/// <para>
/// The case is POSITIVE-CONTROLLED: a known ENABLED Composer tool is first proven to be present in
/// that advertised list (so a captured-but-empty list cannot make the absence assertions vacuous),
/// and only then are write_file, edit_file, execute_bash_command, load_skill and list_skills asserted
/// ABSENT.
/// </para>
/// </summary>
public sealed class ComposerAdvertisedToolsProofTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-advertised-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerAdvertisedToolsProofTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(StateDir);
        _factory = new MemoryDbFactory();
        _users = new UserService(_factory, NullLogger<UserService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StateDir => Path.Combine(_dir, "state");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var composer in _composers)
        {
            try { await composer.DisposeAsync(); }
            catch (Exception) { /* cleanup must not mask a test failure */ }
        }

        foreach (var provider in _providers)
        {
            try { await provider.DisposeAsync(); }
            catch (Exception) { /* cleanup must not mask a test failure */ }
        }

        try { _factory.Dispose(); }
        catch (Exception) { /* cleanup must not mask a test failure */ }

        TestHelpers.ForceDeleteDirectory(_dir);
    }

    private sealed class MemoryDbFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        public MemoryDbFactory()
        {
            var name = $"advertised-{Guid.NewGuid():N}";
            _keeper = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
            _keeper.Open();
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_keeper).Options;
            using var ctx = new CopilotHiveDbContext(_options);
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);

        public void Dispose() => _keeper.Dispose();
    }

    private async Task<(int OriginalId, int SecondId)> SeedAccountsAsync(UserRole secondRole)
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "original", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", secondRole, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        return (original.Id, second.Id);
    }

    private ActingUserPrincipal Resolve(int userId)
    {
        var resolver = new HivePrincipalResolver(
            _users, HiveAuthMode.AuthenticationEnabled, NullLogger<HivePrincipalResolver>.Instance);
        var resolution = resolver.ResolveByUserIdAsync(userId, Ct).GetAwaiter().GetResult();
        Assert.True(resolution.IsResolved, $"Account {userId} did not resolve: {resolution.RefusalReason}.");
        return resolution.Principal!;
    }

    private ComposerUserState StateOf(int userId) =>
        ComposerUserState.ResolveAsync(_users, userId, Ct).GetAwaiter().GetResult();

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_users);
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddSingleton<IGoalStore>(new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// Captures the tool list a REAL request advertised: the <see cref="ChatOptions.Tools"/> SharpCoder
    /// built and handed to the chat client. The client records it verbatim.
    /// </summary>
    private async Task<IReadOnlyList<string>> CaptureAdvertisedToolNamesAsync(
        ServiceProvider provider, int userId, bool withRepoManager)
    {
        var client = new CapturingChatClient();

        var repoManager = withRepoManager
            ? new BrainRepoManager(_dir, NullLogger<BrainRepoManager>.Instance)
            : null;

        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            repoManager: repoManager,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => client,
            userState: StateOf(userId),
            principal: Resolve(userId));
        _composers.Add(composer);

        var service = (ComposerAgentService)typeof(Composer)
            .GetField("_agentService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(composer)!;

        await service.ConnectAsync(Ct);
        var agent = service.Agent ?? throw new InvalidOperationException("The agent was not created.");
        var session = SharpCoder.AgentSession.Create("composer");
        await foreach (var _ in agent.ExecuteStreamingAsync(session, "hello", Ct)) { }

        Assert.NotNull(client.CapturedOptions);
        Assert.NotNull(client.CapturedOptions!.Tools);
        return client.CapturedOptions.Tools!.Select(tool => tool.Name).ToArray();
    }

    /// <summary>
    /// THE ADVERTISED LIST REALLY CONTAINS THE ENABLED TOOLS, AND REALLY OMITS THE DISABLED ONES.
    /// <para>
    /// The positive control comes FIRST and is asserted on the SAME captured list, so the absence
    /// assertions below cannot pass on an empty or wrongly-captured list.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheAdvertisedToolList_ContainsTheEnabledComposerToolsAndOmitsTheDisabledOnes()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();

        var advertised = await CaptureAdvertisedToolNamesAsync(provider, secondId, withRepoManager: true);

        // ── POSITIVE CONTROL: the capture is real and non-empty. A known ENABLED Composer tool is
        //    present, so the absence assertions below are not vacuous.
        Assert.Contains("get_current_time", advertised, StringComparer.Ordinal);
        Assert.Contains("list_goals", advertised, StringComparer.Ordinal);
        Assert.Contains("approve_goal", advertised, StringComparer.Ordinal);
        Assert.Contains("update_goal", advertised, StringComparer.Ordinal);
        // The built-in read tools are advertised too (the agent has file ops enabled).
        Assert.Contains("read_file", advertised, StringComparer.Ordinal);
        Assert.Contains("glob", advertised, StringComparer.Ordinal);
        Assert.Contains("grep", advertised, StringComparer.Ordinal);

        // ── THE DISABLED SHAPE: none of these is ever advertised to the model.
        foreach (var disabled in new[]
        {
            "write_file", "edit_file", "execute_bash_command", "load_skill", "list_skills",
        })
        {
            Assert.DoesNotContain(disabled, advertised, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// THE DISABLED SHAPE HOLDS WITHOUT FILE OPS TOO: even an agent built with file operations
    /// DISABLED (no repo manager) never advertises a write tool, and never advertises bash or the
    /// skill tools.
    /// </summary>
    [Fact]
    public async Task TheAdvertisedToolList_OmitsTheDisabledOnesEvenWithoutFileOps()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();

        var advertised = await CaptureAdvertisedToolNamesAsync(provider, secondId, withRepoManager: false);

        // POSITIVE CONTROL: the capture is still real.
        Assert.Contains("get_current_time", advertised, StringComparer.Ordinal);
        Assert.Contains("list_goals", advertised, StringComparer.Ordinal);

        // Without file ops there is no read tool either…
        Assert.DoesNotContain("read_file", advertised, StringComparer.Ordinal);

        // …and the disabled shape is unchanged.
        foreach (var disabled in new[]
        {
            "write_file", "edit_file", "execute_bash_command", "load_skill", "list_skills",
        })
        {
            Assert.DoesNotContain(disabled, advertised, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// A chat client that records the <see cref="ChatOptions"/> of the FIRST request it serves — the
    /// options SharpCoder built for the model, including the tool list it advertised.
    /// </summary>
    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? CapturedOptions { get; private set; }

        public ChatClientMetadata Metadata => new("capturing", null, "capturing-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CapturedOptions ??= options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            CapturedOptions ??= options;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
