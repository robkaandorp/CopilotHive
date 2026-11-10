using CopilotHive.Configuration;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Moq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// The identity-bound provider gate (<see cref="ProviderEligibility"/>) and its construction/recovery wiring through
/// <see cref="ComposerFactory"/> / <see cref="ComposerRuntimeManager"/>: an activated NON-original identity never uses
/// the original admin's Copilot credential — decided BEFORE any token probe, client creation or connect — while the
/// original identity, Ollama selections, open mode and the legacy token deferral stay unchanged.
/// </summary>
public sealed class ProviderEligibilityDecisionTests
{
    private static string ProviderOf(string model) => model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local";

    private static ProviderEligibility Evaluate(
        bool auth, ProviderIdentity? identity, string? primary, string? compaction = null) =>
        ProviderEligibility.Evaluate(auth, identity, primary, compaction, ProviderOf, logger: null);

    private static readonly ProviderIdentity NonOriginal = ProviderIdentity.ForAccount(enabled: true, roleDefined: true, isOriginal: false);
    private static readonly ProviderIdentity Original = ProviderIdentity.ForAccount(enabled: true, roleDefined: true, isOriginal: true);

    [Theory]
    [InlineData("copilot/main", null, false)]
    [InlineData("ollama-local/main", "copilot/compact", false)]
    [InlineData("ollama-local/main", "ollama-local/compact", true)]
    [InlineData("ollama-local/main", null, true)]
    [InlineData("ollama-local/main", "", true)]
    public void NonOriginal_IsDeniedOnlyForACopilotPrimaryOrCompaction(string primary, string? compaction, bool allowed)
    {
        var result = Evaluate(auth: true, NonOriginal, primary, compaction);

        Assert.Equal(allowed, result.IsAllowed);
        Assert.Equal(allowed ? ProviderDenyReason.None : ProviderDenyReason.NonOriginalCopilot, result.Reason);
    }

    [Fact]
    public void NonOriginal_WhitespaceCompactionModel_IsClassifiedAsGiven_NotTreatedAsInherited()
    {
        // The compaction value is handed VERBATIM to client creation, so a non-empty whitespace value is classified
        // as given. A resolver that throws for it must fail CLOSED for a non-original identity.
        var result = ProviderEligibility.Evaluate(true, NonOriginal, "ollama-local/main", "   ",
            m => m.Trim().Length == 0 ? throw new FormatException("unparseable") : "ollama-local", logger: null);

        Assert.False(result.IsAllowed);
        Assert.Equal(ProviderDenyReason.UnresolvableProvider, result.Reason);
    }

    [Fact]
    public void Original_IsAllowedRegardlessOfProvider()
    {
        Assert.True(Evaluate(true, Original, "copilot/main", "copilot/compact").IsAllowed);
    }

    [Fact]
    public void UnknownDisabledAndUndefinedRoleIdentities_AreDenied_EvenForOllama()
    {
        Assert.Equal(ProviderDenyReason.UnknownAccount, Evaluate(true, ProviderIdentity.Unknown, "ollama-local/main").Reason);
        Assert.Equal(ProviderDenyReason.UnknownAccount, Evaluate(true, null, "ollama-local/main").Reason);
        Assert.Equal(ProviderDenyReason.DisabledAccount,
            Evaluate(true, ProviderIdentity.ForAccount(false, true, isOriginal: true), "ollama-local/main").Reason);
        Assert.Equal(ProviderDenyReason.UndefinedRole,
            Evaluate(true, ProviderIdentity.ForAccount(true, false, isOriginal: true), "ollama-local/main").Reason);
    }

    [Fact]
    public void OpenMode_IsAlwaysAllowed_WithoutReadingTheIdentity()
    {
        Assert.True(Evaluate(auth: false, ProviderIdentity.Unknown, "copilot/main", "copilot/compact").IsAllowed);
        Assert.True(Evaluate(auth: false, null, "copilot/main").IsAllowed);
    }
}

public sealed partial class ComposerRuntimeManagerTests
{
    private static string CopilotAwareProvider(string model) =>
        model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local";

    private ServiceProvider EligibilityProvider(string primary, string? compaction, bool authEnabled = true)
    {
        var models = new[] { primary, compaction, "ollama-local/other" }
            .OfType<string>().Distinct(StringComparer.Ordinal).Select(n => new ModelEntry { Name = n }).ToList();
        return BuildProvider(authEnabled, new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = models, CompactionModel = compaction },
            Composer = new ComposerConfig { Model = primary },
        });
    }

    /// <summary>Counts every evaluation of the ORIGINAL admin's token probe.</summary>
    private int _tokenProbes;

    private void CountTokenProbes() => _tokenProbeHook = () => Interlocked.Increment(ref _tokenProbes);

    [Theory]
    [InlineData(UserRole.Viewer)]
    [InlineData(UserRole.Member)]
    [InlineData(UserRole.Operator)]
    [InlineData(UserRole.Admin)]
    public async Task NonOriginal_CopilotPrimary_SettlesDeferred_BeforeAnyProbeClientOrConnect_RegardlessOfRole(UserRole role)
    {
        var (_, invited) = await TwoUsersAsync(role);
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = true; // the ORIGINAL admin's token IS available: it must still never serve the invited user
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("copilot/main", null));
        await using var gate = Park();
        gate.Release();

        var created = await Request(manager, invited);

        Assert.Equal(ComposerRuntimeStatus.Deferred, created.Status);
        Assert.True(created.IsGranted);
        Assert.Null(created.Failure);
        Assert.False(created.Runtime!.Composer.IsConnected);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes)); // denied BEFORE the original token was probed
        Assert.Equal(0, _clientCreations);                // ... and before any client creation
        Assert.Equal(0, gate.Calls);                      // ... and before any connect

        var retry = await Retry(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Deferred, retry.Status);
        Assert.True(retry.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.None, retry.Refusal);
        Assert.Null(retry.Failure);
        Assert.Same(created.Runtime, retry.Runtime);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(0, _clientCreations);
        Assert.Equal(0, gate.Calls);
    }

    [Fact]
    public async Task NonOriginal_ExplicitCopilotCompaction_SettlesDeferred_WithoutProbeOrClient()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = true;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("ollama-local/main", "copilot/compact"));
        await using var gate = Park();
        gate.Release();

        var created = await Request(manager, invited);

        Assert.Equal(ComposerRuntimeStatus.Deferred, created.Status);
        Assert.True(created.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.None, created.Refusal);
        Assert.Null(created.Failure);
        Assert.False(created.Runtime!.Composer.IsConnected);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(0, _clientCreations);
        Assert.Equal(0, gate.Calls);

        var retry = await Retry(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Deferred, retry.Status);
        Assert.True(retry.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.None, retry.Refusal);
        Assert.Null(retry.Failure);
        Assert.Same(created.Runtime, retry.Runtime);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(0, _clientCreations);
        Assert.Equal(0, gate.Calls);
    }

    [Fact]
    public async Task NonOriginal_Ollama_StaysEligible_AndConnectsWithoutEverTouchingTheOriginalToken()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = false; // no original token at all: an Ollama runtime must not need one
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("ollama-local/main", "ollama-local/compact"));

        var created = await Request(manager, invited);

        Assert.Equal(ComposerRuntimeStatus.Connected, created.Status);
        Assert.True(created.Runtime!.Composer.IsConnected);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
        Assert.Equal(2, _clientCreations);
    }

    [Fact]
    public async Task NonOriginal_Denial_NeverRecoversWhenTheOriginalTokenBecomesAvailable()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = false;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("copilot/main", null));
        await using var gate = Park();
        gate.Release();
        var deferred = await Request(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);

        // The original admin's token becomes available.
        _tokenAvailable = true;

        var retry = await Retry(manager, invited);

        Assert.Equal(ComposerRuntimeStatus.Deferred, retry.Status);
        Assert.Same(deferred.Runtime, retry.Runtime);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, _clientCreations);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
    }

    [Theory]
    [InlineData(UserRole.Viewer)]
    [InlineData(UserRole.Member)]
    [InlineData(UserRole.Operator)]
    [InlineData(UserRole.Admin)]
    public async Task OriginalIdentity_KeepsTheLegacyTokenDeferral_AndRecoversWhenTheTokenArrives_RegardlessOfRole(UserRole role)
    {
        var (original, invited) = await TwoUsersAsync(UserRole.Admin);
        // A second admin exists: test every defined current role without changing original IDENTITY.
        // The directory rejects no-op role changes, so the existing Admin row needs no mutation.
        if (role != original.Role)
            await _users.ChangeUserRoleAsync(original.UserId, role, Ct);
        Assert.Equal(role, (await _users.GetActivatedUserByIdAsync(original.UserId, Ct))!.Role);
        var demoted = ActingUserPrincipal.ForActivatedAccount(original.UserId, "100", role, true, isOriginal: true);
        Assert.NotNull(invited);
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = false;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("copilot/main", null));
        await using var gate = Park();
        gate.Release();

        var deferred = await Request(manager, demoted);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        Assert.True(Volatile.Read(ref _tokenProbes) >= 1); // the ORIGINAL runtime does consult the token

        _tokenAvailable = true;
        var recovered = await Retry(manager, demoted);

        Assert.Equal(ComposerRuntimeStatus.Connected, recovered.Status);
        Assert.Same(deferred.Runtime, recovered.Runtime);
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task OpenMode_NonOriginalCopilot_ConstructionIsUnchanged_NoIdentityGateOrTokenProbe()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = false;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("copilot/main", null, authEnabled: false));

        var created = await Request(manager, invited);

        // Open mode never defers (the legacy predicate needs OAuth): byte-unchanged, and no token probe at all.
        Assert.Equal(ComposerRuntimeStatus.Connected, created.Status);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
    }

    [Fact]
    public async Task OriginalRuntime_DisabledAfterConstruction_IsDeniedTheNextConnect_WithoutProbeOrClient()
    {
        var (original, invited) = await TwoUsersAsync(UserRole.Admin);
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = false;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("copilot/main", null));
        await using var gate = Park();
        gate.Release();
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, original)).Status);
        var probesBefore = Volatile.Read(ref _tokenProbes);

        // The account is disabled AFTER construction, then the original token becomes available.
        await _users.SetUserEnabledAsync(original.UserId, false, Ct);
        _tokenAvailable = true;
        var retry = await Retry(manager, original);

        Assert.Equal(ComposerRuntimeStatus.Refused, retry.Status);
        Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, retry.Refusal);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, _clientCreations);
        Assert.Equal(probesBefore, Volatile.Read(ref _tokenProbes)); // the denied identity never reached the token probe
        Assert.NotNull(invited);
    }

    [Fact]
    public async Task NonOriginal_DeferredOnCopilotPrimary_SelectingAnEligibleOllamaPrimary_ConnectsThroughTheActor_AndStatusReflectsIt()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = true;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("copilot/main", null));
        await using var gate = Park();
        gate.Release();
        var deferred = await Request(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        var composer = deferred.Runtime!.Composer;
        Assert.False(composer.IsConnected);

        // The selection runs through the ACTOR's first-selection path; the gate allows the Ollama primary.
        await composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct);

        Assert.True(composer.IsConnected);
        Assert.Equal("ollama-local/other", composer.CurrentModel); // committed runtime state
        Assert.Equal(1, _clientCreations);
        Assert.Equal(0, gate.Calls);                               // the manager's connect was NOT the path
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));

        // Status reflects the ACTUAL connection — never a stale cached Deferred — for retry and create alike.
        var retry = await Retry(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Connected, retry.Status);
        Assert.Same(deferred.Runtime, retry.Runtime);
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Request(manager, invited)).Status);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(1, _clientCreations); // no second connect tore the live connection down
    }

    /// <summary>
    /// RESOLVED RECOVERY-CONTRACT INTERPRETATION. Two distinct paths: (a) the ACTOR first-selection path may connect
    /// (the test above: selection itself connects, status then reports Connected); (b) manager recovery probing never
    /// bypasses the gate. Here the selection's connect FAILS: the eligible Ollama selection stays COMMITTED in runtime
    /// state (not global config) while the runtime remains not-connected/Deferred, and the manager's recovery is then
    /// granted purely because the ACTUAL current primary and the effective compaction are both eligible.
    /// </summary>
    [Fact]
    public async Task NonOriginal_DeferredRuntime_CommittedEligibleSelection_StaysDeferredUntilManagerRecovery_FollowsActualSelection()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = true;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("copilot/main", null));
        await using var gate = Park();
        gate.Release();
        var deferred = await Request(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        var composer = deferred.Runtime!.Composer;
        Assert.Equal("copilot/main", composer.CurrentModel);

        // The selection runs through the actor first-selection path, but its connect FAILS (client creation throws).
        _clientFactory = _ => throw new InvalidOperationException("selection connect failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct));

        Assert.False(composer.IsConnected);
        Assert.Equal("ollama-local/other", composer.CurrentModel); // committed in runtime state
        Assert.Equal(ComposerRuntimeStatus.Deferred, (await Request(manager, invited)).Status); // no stale/false Connected

        // Manager recovery evaluates the ACTUAL current primary (eligible Ollama), not the captured Copilot primary.
        _clientFactory = _ => new Mock<IChatClient>().Object;
        var recovered = await Retry(manager, invited);

        Assert.Equal(ComposerRuntimeStatus.Connected, recovered.Status);
        Assert.Same(deferred.Runtime, recovered.Runtime);
        Assert.True(composer.IsConnected);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
    }

    [Fact]
    public async Task NonOriginal_ExplicitCopilotCompaction_CannotBeBypassedByAPrimarySwitch_AndStaysDeferred()
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = true;
        CountTokenProbes();
        var manager = NewManager(EligibilityProvider("ollama-local/main", "copilot/compact"));
        await using var gate = Park();
        gate.Release();
        var deferred = await Request(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        var composer = deferred.Runtime!.Composer;

        await Assert.ThrowsAsync<ProviderNotEligibleException>(() =>
            composer.SwitchModelAsync("ollama-local/other", ReasoningEffort.Medium, Ct));

        Assert.False(composer.IsConnected);
        Assert.Equal("ollama-local/main", composer.CurrentModel); // the denied selection committed nothing
        Assert.Equal(0, _clientCreations);
        var retry = await Retry(manager, invited);
        Assert.Equal(ComposerRuntimeStatus.Deferred, retry.Status);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
    }

    [Theory]
    [InlineData("copilot/compact", null, ComposerRuntimeStatus.Deferred)]
    [InlineData(null, "copilot/compact", ComposerRuntimeStatus.Connected)]
    public async Task Recovery_UsesTheRuntimesActualCapture_NotAReloadedGlobalConfig(
        string? compaction, string? reloadedCompaction, ComposerRuntimeStatus expectedStatus)
    {
        var (_, invited) = await TwoUsersAsync();
        _providerResolver = CopilotAwareProvider;
        _tokenAvailable = true; // only the identity/provider gate, not legacy token absence, can defer this runtime
        CountTokenProbes();
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "ollama-local/main" }, new ModelEntry { Name = "copilot/compact" }],
                CompactionModel = compaction,
            },
            Composer = new ComposerConfig { Model = "ollama-local/main" },
        };
        var sp = BuildProvider(authEnabled: true, config: config);
        var factory = NewFactory(sp);
        var manager = NewManager(sp, factory: factory);
        await using var gate = Park();
        gate.Release();
        var created = await Request(manager, invited);
        Assert.Equal(expectedStatus, created.Status);
        var runtime = created.Runtime!;
        var decisionBefore = await factory.DecideConnectAsync(runtime.Configuration, invited.UserId, runtime.Composer.CurrentModel, Ct);

        // Both directions: clearing Copilot cannot allow a denied capture, and adding Copilot cannot deny an
        // eligible capture. Check the actual identity-aware factory verdict too: a connected manager cache hit
        // alone would not prove the second direction because it never asks the deferral gate again.
        config.SetCompactionModel(reloadedCompaction);
        Assert.Equal(reloadedCompaction, sp.GetRequiredService<HiveConfigFile>().GetCompactionModel());
        var decisionAfter = await factory.DecideConnectAsync(runtime.Configuration, invited.UserId, runtime.Composer.CurrentModel, Ct);
        var liveDecision = await factory.DecideConnectAsync(
            new ComposerConstructionSnapshot(runtime.Composer.CurrentModel, reloadedCompaction), invited.UserId, runtime.Composer.CurrentModel, Ct);
        Assert.Equal(decisionBefore, decisionAfter);
        Assert.NotEqual(decisionAfter.Defer, liveDecision.Defer); // the reload really WOULD flip a live lookup
        Assert.Equal(expectedStatus == ComposerRuntimeStatus.Deferred, decisionAfter.Defer);
        Assert.Equal(compaction, runtime.Configuration.CompactionModel);
        Assert.Equal("ollama-local/main", runtime.Composer.CurrentModel);

        var retry = await Retry(manager, invited);
        Assert.Equal(expectedStatus, retry.Status);
        Assert.Same(runtime, retry.Runtime);
        Assert.Null(retry.Failure);
        Assert.Equal(expectedStatus == ComposerRuntimeStatus.Deferred ? 0 : 1, gate.Calls);
        Assert.Equal(expectedStatus == ComposerRuntimeStatus.Deferred ? 0 : 1, _clientCreations);
        Assert.Equal(0, Volatile.Read(ref _tokenProbes));
    }
}
