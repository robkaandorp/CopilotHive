using CopilotHive.Dashboard;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE PRODUCTION REGISTRATION of <see cref="IOperationalFacade"/> in a REAL host: the facade the
/// Orchestrator page injects resolves unconditionally, is a SINGLETON (it holds only the stateless
/// resolver/policy and the trusted services, so per-circuit state cannot leak through it), and its
/// every operation answers consistently with the host's registered
/// <see cref="HiveAuthMode"/> — so an open-mode installation keeps working and an
/// authentication-enabled installation refuses an identity-free caller.
/// </summary>
[Collection("HiveIntegration")]
public sealed class OperationalFacadeRegistrationTests
{
    private readonly HiveTestFactory _factory;

    /// <summary>Receives the shared host fixture for this test class.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/>.</param>
    public OperationalFacadeRegistrationTests(HiveTestFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The facade is registered as a SINGLETON over the SAME trusted service instances the host
    /// already exposes, so the page's facade and anything else reading the dashboard state observe
    /// one authoritative live source — never a second, detached instance.
    /// </summary>
    [Fact]
    public void TheFacade_IsRegisteredAsASingletonOverTheHostsOwnServices()
    {
        var first = _factory.Services.GetRequiredService<IOperationalFacade>();
        var second = _factory.Services.GetRequiredService<IOperationalFacade>();

        Assert.Same(first, second);
        Assert.IsType<OperationalFacade>(first);

        // The host's own registry is the one the facade projects from: a session registered through
        // the host is the one the shared projection filters.
        var registry = _factory.Services.GetRequiredService<LlmSessionRegistry>();
        Assert.Same(registry, _factory.Services.GetRequiredService<LlmSessionRegistry>());
    }

    /// <summary>
    /// EVERY OPERATION answers consistently with the registered auth mode:
    /// <list type="bullet">
    ///   <item>open mode — the resolver's local operator holds every capability, so the reset and all
    ///   three shared reads SUCCEED for the identity-free circuit (the legacy single-operator
    ///   installation is unchanged);</item>
    ///   <item>authentication enabled — the same identity-free calls are REFUSED with the sanitized
    ///   Forbidden result, for both the read (SharedRead) and the mutation (ExecutionOperator)
    ///   halves.</item>
    /// </list>
    /// The test therefore proves the anti-escalation property in either host mode instead of
    /// assuming the environment.
    /// </summary>
    [Fact]
    public async Task EveryOperation_AnswersConsistentlyWithTheRegisteredAuthMode()
    {
        var mode = _factory.Services.GetRequiredService<HiveAuthMode>();
        var facade = _factory.Services.GetRequiredService<IOperationalFacade>();

        var reset = await facade.ResetBrainSessionAsync(null, Ct);
        var brain = await facade.GetBrainStatsAsync(null, Ct);
        var info = await facade.GetOrchestratorInfoAsync(null, Ct);
        var sessions = await facade.GetSharedLlmSessionsAsync(null, Ct);
        var dashboardDisplay = await facade.GetDashboardDisplayAsync(null, Ct);
        var workerDisplay = await facade.GetWorkerDisplayAsync(null, Ct);
        var progress = await facade.GetWorkerProgressAsync("any-worker", null, Ct);

        if (mode.AuthEnabled)
        {
            foreach (var (scenario, kind, error) in new[]
            {
                ("brain stats", brain.Kind, brain.Error),
                ("orchestrator info", info.Kind, info.Error),
                ("shared sessions", sessions.Kind, sessions.Error),
                ("dashboard display", dashboardDisplay.Kind, dashboardDisplay.Error),
                ("worker display", workerDisplay.Kind, workerDisplay.Error),
                ("worker progress", progress.Kind, progress.Error),
            })
            {
                Assert.Equal(FacadeErrorKind.Forbidden, kind);
                Assert.Equal(FacadeAuthorization.ForbiddenMessage, error);
            }

            // A denial on the new projections carries NO value either.
            Assert.Null(dashboardDisplay.Value);
            Assert.Null(workerDisplay.Value);
            Assert.Null(progress.Value);

            Assert.Equal(FacadeErrorKind.Forbidden, reset.Kind);
            Assert.Equal(FacadeAuthorization.ForbiddenMessage, reset.Error);
        }
        else
        {
            Assert.True(reset.Success, reset.Error);
            Assert.True(brain.Success, brain.Error);
            Assert.True(info.Success, info.Error);
            Assert.True(sessions.Success, sessions.Error);
            Assert.True(dashboardDisplay.Success, dashboardDisplay.Error);
            Assert.True(workerDisplay.Success, workerDisplay.Error);
            Assert.True(progress.Success, progress.Error);

            // The projection is still sanitized in open mode: no Composer session is ever shared.
            Assert.DoesNotContain(sessions.Value!, s => s.SessionType == LlmSessionType.Composer);

            // The dashboard display carries the two worker counts and NO worker row.
            Assert.NotNull(dashboardDisplay.Value);
            Assert.Equal(
                workerDisplay.Value!.TotalWorkers, dashboardDisplay.Value!.TotalWorkers);

            // The progress read is wired to the host's OWN registered aggregation: an unknown worker
            // simply has no reports, which is a success with no rows rather than a missing dependency.
            Assert.NotNull(progress.Value);
        }
    }

    /// <summary>
    /// THE PROGRESS-RESOURCE IS THE HOST'S OWN REGISTERED AGGREGATION, injected at the factory and
    /// nowhere else: the facade resolved from the host holds the very
    /// <see cref="ProgressReportService"/> instance the host's container exposes as a singleton, so the
    /// worker-progress read cannot drift into a second collection of its own.
    /// </summary>
    [Fact]
    public void TheFacade_HoldsTheHostsOwnRegisteredProgressAggregation()
    {
        var facade = _factory.Services.GetRequiredService<IOperationalFacade>();
        var hostService = _factory.Services.GetRequiredService<ProgressReportService>();
        var sameService = _factory.Services.GetRequiredService<ProgressReportService>();

        Assert.Same(hostService, sameService);

        var field = typeof(OperationalFacade)
            .GetField("_progressReports", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Same(hostService, field.GetValue(facade));
    }

    /// <summary>
    /// THE SHARED PROJECTION NEVER EXPOSES A COMPOSER SESSION through the resolution the page
    /// actually receives: an entry registered on the host's own registry is absent from the facade's
    /// projection while the registry keeps it — in WHICHEVER mode this host is running (open mode
    /// succeeds and must filter; authentication-enabled mode refuses and exposes nothing at all).
    /// </summary>
    [Fact]
    public async Task TheHostsOwnComposerSession_IsNeverExposedThroughTheFacade()
    {
        var registry = _factory.Services.GetRequiredService<LlmSessionRegistry>();
        var facade = _factory.Services.GetRequiredService<IOperationalFacade>();

        var composerId = $"facade-composer-{Guid.NewGuid():N}";
        var brainId = $"facade-brain-{Guid.NewGuid():N}";
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = composerId,
            SessionType = LlmSessionType.Composer,
            Model = "composer-model",
            Status = "active",
            MaxTokens = 1,
        });
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = brainId,
            SessionType = LlmSessionType.Brain,
            Model = "brain-model",
            Status = "active",
            MaxTokens = 1,
        });

        try
        {
            // The registry keeps BOTH entries — its semantics are unchanged.
            var all = registry.GetAll();
            Assert.Contains(all, s => s.SessionId == composerId);
            Assert.Contains(all, s => s.SessionId == brainId);

            var result = await facade.GetSharedLlmSessionsAsync(null, Ct);
            var exposed = result.Success ? result.Value! : [];

            Assert.DoesNotContain(exposed, s => s.SessionId == composerId);
            Assert.DoesNotContain(exposed, s => s.SessionType == LlmSessionType.Composer);

            // Positive control in open mode: the shared execution session IS reported, so the
            // absence above is the Composer exclusion and not an empty projection.
            if (result.Success)
                Assert.Contains(exposed, s => s.SessionId == brainId);
        }
        finally
        {
            registry.Unregister(composerId);
            registry.Unregister(brainId);
        }
    }
}
