using System.Collections.Concurrent;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;

namespace CopilotHive.Tests.Orchestration;

public sealed partial class ComposerRuntimeManagerTests
{
    [Fact]
    public async Task LegacyCoordinator_TokenWakeup_ConnectsLegacyButCannotResurrectNonOriginalCopilotRuntime()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = false;
        CountTokenProbes();
        var sp = EligibilityProvider("copilot/main", null);
        var manager = NewManager(sp);
        var deferred = await Request(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);

        // The global coordinator owns only the LEGACY Composer. Exercise its real deferred-retry path,
        // with independently counted fake clients so a successful legacy wakeup cannot mask a private client.
        var legacyClients = 0;
        var legacyConnects = 0;
        var legacyProbes = 0;
        var legacyFactory = new ComposerFactory(sp, StateDir, null,
            chatClientFactory: _ =>
            {
                Interlocked.Increment(ref legacyClients);
                return new Mock<IChatClient>().Object;
            },
            isTokenAvailable: () => _tokenAvailable, providerOf: CopilotAwareProvider);
        await using var legacy = legacyFactory.CreateLegacy();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentQueue<ComposerState>();
        var coordinator = new LlmConnectionCoordinator(legacy.StartupDefaultModel, null, oauthEnabled: true,
            async ct =>
            {
                Interlocked.Increment(ref legacyConnects);
                await legacy.ConnectAsync(ct);
            },
            () => { Interlocked.Increment(ref legacyProbes); return _tokenAvailable; }, CopilotAwareProvider);
        coordinator.StateChanged += state =>
        {
            states.Enqueue(state);
            if (state == ComposerState.Connected) connected.TrySetResult();
        };
        try
        {
            await GuardAsync(coordinator.StartAsync(), "legacy coordinator initial deferral");
            Assert.True(coordinator.WasDeferred);
            Assert.Equal(ComposerState.PendingConnect, coordinator.State);
            Assert.Equal(0, Volatile.Read(ref legacyClients));
            Assert.Equal(0, Volatile.Read(ref legacyConnects));
            Assert.Equal(1, Volatile.Read(ref legacyProbes));

            _tokenAvailable = true;
            coordinator.OnTokenAvailable();
            await GuardAsync(connected.Task, "legacy token wakeup reaches Connected");

            Assert.True(legacy.IsConnected);
            Assert.Equal(1, Volatile.Read(ref legacyClients));
            Assert.Equal(1, Volatile.Read(ref legacyConnects));
            Assert.Equal(new[] { ComposerState.PendingConnect, ComposerState.Connecting, ComposerState.Connected }, states.ToArray());

            var retry = await Retry(manager, invited);
            var cacheHit = await Request(manager, invited);
            foreach (var result in new[] { retry, cacheHit })
            {
                Assert.Equal(ComposerRuntimeStatus.Deferred, result.Status);
                Assert.True(result.IsGranted);
                Assert.Null(result.Failure);
                Assert.Same(deferred.Runtime, result.Runtime);
                Assert.False(result.Runtime!.Composer.IsConnected);
            }
            Assert.Equal(0, Volatile.Read(ref _tokenProbes));
            Assert.Equal(0, _clientCreations);
        }
        finally
        {
            await GuardAsync(coordinator.DisposeAsync().AsTask(), "token-wakeup coordinator disposal");
        }
    }

    [Fact]
    public async Task OriginalRuntime_DeletedAfterConstruction_NextConnectRefusesStalePrincipalWithoutProbeOrClient()
    {
        var (original, _) = await TwoUsersAsync(UserRole.Admin);
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = false;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("copilot/main", null));
        var deferred = await Request(manager, original);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        var probesBefore = Volatile.Read(ref _tokenProbes);
        Assert.True(probesBefore > 0);
        await DeleteStoredAccountAsync(original.UserId);
        _tokenAvailable = true;

        var retry = await Retry(manager, original);
        var cacheHit = await Request(manager, original);

        AssertUnknownAccountRefusal(retry);
        AssertUnknownAccountRefusal(cacheHit);
        Assert.Equal(probesBefore, Volatile.Read(ref _tokenProbes));
        Assert.Equal(0, _clientCreations);
        Assert.False(deferred.Runtime!.Composer.IsConnected);
    }

    [Fact]
    public async Task NonOriginalOllamaRuntime_DeletedAfterConstruction_NextOperationRefusesTheCapturedPrincipal()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("ollama-local/main", null));
        var created = await Request(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Connected, created.Status);
        Assert.Equal(1, _clientCreations);
        await DeleteStoredAccountAsync(invited.UserId);

        AssertUnknownAccountRefusal(await Retry(manager, invited));
        AssertUnknownAccountRefusal(await Request(manager, invited));
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(1, _clientCreations);
    }

    private async Task DeleteStoredAccountAsync(int userId)
    {
        // No delete API exists in this slice. Change stored state directly to exercise the existing manager
        // RecheckAsync seam, rather than passing a fabricated "deleted" principal or replacing UserService.
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Users.Where(user => user.Id == userId).ExecuteDeleteAsync(Ct));
    }

    private static void AssertUnknownAccountRefusal(ComposerRuntimeResult result)
    {
        Assert.Equal(ComposerRuntimeStatus.Refused, result.Status);
        Assert.Equal(ComposerRuntimeRefusal.UnknownAccount, result.Refusal);
        Assert.False(result.IsGranted);
        Assert.Null(result.Runtime);
        Assert.Null(result.Failure);
    }
}
