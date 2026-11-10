using CopilotHive.Configuration;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// CONSTRUCTION SNAPSHOT semantics of <see cref="ComposerFactory"/>: the primary and compaction model values a
/// runtime was constructed with are captured ONCE in an immutable per-runtime configuration, and every later
/// deferral decision for that runtime is evaluated against the CAPTURE — never a live
/// <see cref="HiveConfigFile"/> compaction lookup. A configuration reload therefore cannot change the deferral
/// (or provider) decision of an already-constructed runtime, and the runtime and the factory always agree
/// because both read the same captured values.
/// </summary>
public sealed partial class ComposerRuntimeManagerTests
{
    /// <summary>The runtime under test plus the manager/factory that own it.</summary>
    private sealed record SnapshotContext(
        ComposerRuntimeManager Manager, ComposerFactory Factory, ServiceProvider Provider,
        ComposerUserRuntime Runtime, ComposerRuntimeResult Created);

    /// <summary>
    /// Creates the user's runtime and proves the capture IS the Composer's actual construction input: the
    /// compaction model handed to <see cref="ComposerAgentService"/> equals the captured one, the captured primary
    /// model equals <see cref="Composer.StartupDefaultModel"/>, and the factory's verdict for the capture is the
    /// outcome the manager acted on.
    /// </summary>
    private async Task<SnapshotContext> CreateRuntimeAsync(
        ServiceProvider sp, ComposerFactory factory, ActingUserPrincipal principal, string? expectedCompaction)
    {
        var manager = NewManager(sp, factory: factory);
        var created = await Request(manager, principal);
        Assert.True(created.IsGranted);
        var runtime = created.Runtime!;

        // The Composer really was constructed with the captured compaction model (and was not re-read later).
        Assert.Equal(expectedCompaction,
            InstanceField<string?>(Field<ComposerAgentService>(runtime.Composer, "_agentService"), "_compactionModel"));
        // The runtime carries exactly those values in its immutable capture.
        Assert.Equal(expectedCompaction, runtime.Configuration.CompactionModel);
        Assert.Equal(runtime.Composer.StartupDefaultModel, runtime.Configuration.PrimaryModel);
        // The factory's verdict for the capture is the status the manager settled on.
        Assert.Equal(created.Status == ComposerRuntimeStatus.Deferred, factory.ShouldDeferConnect(runtime.Configuration));
        return new SnapshotContext(manager, factory, sp, runtime, created);
    }

    /// <summary>
    /// AC1: after a LIVE configuration reload changes the compaction model, the runtime's deferral decision is
    /// still the one its construction-captured values dictate — and the factory, asked about the capture, agrees.
    /// </summary>
    /// <remarks>
    /// Each row mutates the live compaction model AFTER construction. Rows where the reload genuinely flips the
    /// predicate are the load-bearing ones: a live lookup inside the factory would defer (or connect) a runtime
    /// whose Composer was built from the old value, and the "flip is observable" assertion below pins that the
    /// two answers differ, so the capture cannot be silently replaced by the live value.
    /// </remarks>
    [Theory]
    // Deferred through a Copilot COMPACTION model; the reload makes the compaction model non-Copilot.
    [InlineData("ollama-local/main", "copilot/compact", "ollama-local/compact")]
    // Deferred through a Copilot compaction model; the reload clears it entirely.
    [InlineData("ollama-local/main", "copilot/compact", null)]
    // Deferred through the PRIMARY model; the reload only changes the (irrelevant) compaction model.
    [InlineData("copilot/main", "ollama-local/compact", "ollama-local/other")]
    // NOT deferred; the reload ADDS a Copilot compaction model that would otherwise defer.
    [InlineData("ollama-local/main", "ollama-local/compact", "copilot/compact")]
    // NOT deferred and no compaction model; the reload adds a Copilot one.
    [InlineData("ollama-local/main", null, "copilot/compact")]
    public async Task RuntimeDeferral_AfterLiveConfigReload_StillUsesTheConstructionCapturedModels(
        string primary, string? compaction, string? reloadedCompaction)
    {
        var (second, _) = await OriginalAsSecondAsync();
        _providerResolver = model => model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local";
        // No usable token for the whole construction phase: only the captured models can defer.
        _tokenAvailable = false;

        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = new[] { primary, compaction, reloadedCompaction }
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Select(name => new ModelEntry { Name = name })
                    .ToList(),
                CompactionModel = compaction,
            },
            Composer = new ComposerConfig { Model = primary },
        };
        var sp = BuildProvider(authEnabled: true, config: config);
        var factory = NewFactory(sp);
        var constructedDefers = IsCopilotBacked(primary) || IsCopilotBacked(compaction);

        var context = await CreateRuntimeAsync(sp, factory, second, compaction);
        Assert.Equal(constructedDefers ? ComposerRuntimeStatus.Deferred : ComposerRuntimeStatus.Connected,
            context.Created.Status);

        // ── LIVE reload: the singleton every caller resolves now reports a DIFFERENT compaction model ──
        config.SetCompactionModel(reloadedCompaction);
        Assert.Equal(reloadedCompaction, sp.GetRequiredService<HiveConfigFile>().GetCompactionModel());
        var reloadedDefers = IsCopilotBacked(primary) || IsCopilotBacked(reloadedCompaction);

        // The factory asked about the RUNTIME'S CAPTURE keeps the construction verdict ...
        Assert.Equal(constructedDefers, factory.ShouldDeferConnect(context.Runtime.Configuration));
        // ... while a capture built from the LIVE value yields the reload's verdict. When the reload flipped the
        // predicate the two answers therefore DIFFER — a live lookup inside the factory would have changed this
        // already-constructed runtime's decision, which is exactly what must not happen.
        Assert.Equal(reloadedDefers,
            factory.ShouldDeferConnect(new ComposerConstructionSnapshot(primary, reloadedCompaction)));
        Assert.Equal(constructedDefers != reloadedDefers,
            factory.ShouldDeferConnect(context.Runtime.Configuration)
                != factory.ShouldDeferConnect(new ComposerConstructionSnapshot(primary, reloadedCompaction)));

        // Production assertion: a retry after the reload settles on the construction verdict, on the SAME runtime.
        var afterReload = await Retry(context.Manager, second);
        Assert.Equal(constructedDefers ? ComposerRuntimeStatus.Deferred : ComposerRuntimeStatus.Connected, afterReload.Status);
        Assert.Same(context.Runtime, afterReload.Runtime);
        Assert.Null(afterReload.Failure);

        // And the runtime stays fully usable: once a token exists, a captured Copilot involvement is satisfied and
        // the deferred runtime connects normally — the capture never blocks a legitimate connect.
        _tokenAvailable = true;
        var settled = await Retry(context.Manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, settled.Status);
        Assert.Same(context.Runtime, settled.Runtime);
        Assert.True(context.Runtime.Composer.IsConnected);
        // The reload never rewrote the Composer's own construction input either.
        Assert.Equal(compaction,
            InstanceField<string?>(Field<ComposerAgentService>(context.Runtime.Composer, "_agentService"), "_compactionModel"));
    }

    /// <summary>
    /// The same guarantee pinned on ONE runtime instance the manager serves: clearing the live compaction model
    /// must not turn a Copilot-compaction runtime into a connecting one, and re-adding it must not retroactively
    /// defer an already-connected runtime.
    /// </summary>
    [Fact]
    public async Task FactoryVerdict_ForARuntime_FollowsTheCapture_NotTheReload_AndMatchesTheManager()
    {
        var (second, _) = await OriginalAsSecondAsync();
        _providerResolver = model => model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local";
        _tokenAvailable = false;
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "ollama-local/main" }, new ModelEntry { Name = "copilot/compact" }],
                CompactionModel = "copilot/compact",
            },
            Composer = new ComposerConfig { Model = "ollama-local/main" },
        };
        var sp = BuildProvider(authEnabled: true, config: config);
        var factory = NewFactory(sp);
        var context = await CreateRuntimeAsync(sp, factory, second, "copilot/compact");
        Assert.Equal(ComposerRuntimeStatus.Deferred, context.Created.Status);
        Assert.True(factory.ShouldDeferConnect(context.Runtime.Configuration));
        // The captured compaction model is the ONLY reason this runtime defers (its primary is not Copilot-backed).
        Assert.Equal("ollama-local/main", context.Runtime.Configuration.PrimaryModel);

        // Live reload: the compaction model is cleared, so a live lookup would no longer defer.
        config.SetCompactionModel(null);
        Assert.Null(sp.GetRequiredService<HiveConfigFile>().GetCompactionModel());
        Assert.False(factory.ShouldDeferConnect(new ComposerConstructionSnapshot("ollama-local/main", null)));

        // The runtime keeps its construction verdict: a retry stays Deferred and starts NO connect.
        await using var gate = Park();
        var stillDeferred = await Retry(context.Manager, second);
        Assert.Equal(ComposerRuntimeStatus.Deferred, stillDeferred.Status);
        Assert.Same(context.Runtime, stillDeferred.Runtime);
        Assert.Equal(0, gate.Calls); // a live lookup would have connected here
        Assert.Equal(0, _clientCreations);
        Assert.True(factory.ShouldDeferConnect(context.Runtime.Configuration)); // runtime and factory still agree

        // Control: with a token the SAME runtime connects.
        _tokenAvailable = true;
        gate.Release();
        var connected = await Retry(context.Manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, connected.Status);
        Assert.Same(context.Runtime, connected.Runtime);
        Assert.Equal(1, gate.Calls);
        Assert.True(context.Runtime.Composer.IsConnected);

        // And once connected, restoring the Copilot compaction model cannot retroactively defer it.
        config.SetCompactionModel("copilot/compact");
        Assert.Equal(ComposerRuntimeStatus.Connected, (await Retry(context.Manager, second)).Status);
        Assert.Equal(1, gate.Calls);
        Assert.Same(context.Runtime, (await Request(context.Manager, second)).Runtime);
    }

    /// <summary>
    /// A model-less runtime is an explicit shell: its capture carries a <c>null</c> primary model, and a later
    /// reload that configures a model never retrofits it. A FRESH runtime built after the reload does capture
    /// the new values — the positive control that the capture comes from construction, not from a constant.
    /// </summary>
    [Fact]
    public async Task ModelLessRuntime_CapturesNoPrimary_AndALaterReloadNeverRetrofitsIt()
    {
        var (_, second) = await TwoUsersAsync();
        _providerResolver = model => model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local";
        _tokenAvailable = false;
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "copilot/main" }, new ModelEntry { Name = "copilot/compact" }],
            },
            // No composer.model: the resolver yields null ⇒ disconnected shell.
        };
        var sp = BuildProvider(authEnabled: true, config: config);
        var factory = NewFactory(sp);
        var context = await CreateRuntimeAsync(sp, factory, second, expectedCompaction: null);

        Assert.Equal(ComposerRuntimeStatus.DisconnectedShell, context.Created.Status);
        Assert.Null(context.Runtime.Configuration.PrimaryModel);
        Assert.Null(context.Runtime.Composer.StartupDefaultModel);
        Assert.False(factory.ShouldDeferConnect(context.Runtime.Configuration));

        // Live reload configures a Copilot-backed model AND a Copilot compaction model: still no retrofit.
        config.Composer = new ComposerConfig { Model = "copilot/main" };
        config.SetCompactionModel("copilot/compact");
        Assert.Equal("copilot/main", sp.GetRequiredService<HiveConfigFile>().ResolveComposerDefaultModel());
        Assert.Null(context.Runtime.Configuration.PrimaryModel);
        Assert.Null(context.Runtime.Configuration.CompactionModel);
        Assert.False(factory.ShouldDeferConnect(context.Runtime.Configuration));
        Assert.Equal(ComposerRuntimeStatus.DisconnectedShell, (await Retry(context.Manager, second)).Status);
        Assert.Same(context.Runtime, (await Request(context.Manager, second)).Runtime);

        // Positive control: a fresh runtime built now captures the reloaded models and defers.
        Assert.True(await Remove(context.Manager, second.UserId));
        var freshFactory = NewFactory(sp);
        var fresh = await CreateRuntimeAsync(sp, freshFactory, second, expectedCompaction: "copilot/compact");
        Assert.Equal("copilot/main", fresh.Runtime.Configuration.PrimaryModel);
        Assert.Equal(ComposerRuntimeStatus.Deferred, fresh.Created.Status);
        Assert.NotSame(context.Runtime, fresh.Runtime);
    }

    /// <summary>
    /// AC1, PRIMARY-MODEL AXIS: a runtime captured with a Copilot-backed PRIMARY model keeps its
    /// construction-captured deferral decision when the live <see cref="HiveConfigFile"/> replaces that
    /// primary model with a non-Copilot one — exactly the flip a LIVE lookup would have produced.
    /// </summary>
    /// <remarks>
    /// The compaction-model axis is covered by <see cref="RuntimeDeferral_AfterLiveConfigReload_StillUsesTheConstructionCapturedModels"/>;
    /// this pins the OTHER captured value the snapshot carries. The positive control at the end re-creates the
    /// runtime AFTER the reload, proving the reload really is observable — so the runtime above kept its capture
    /// for a substantive reason and not because the reload never happened.
    /// </remarks>
    [Fact]
    public async Task RuntimeDeferral_AfterLivePrimaryModelReload_StillUsesTheConstructionCapturedPrimary()
    {
        var (_, second) = await TwoUsersAsync();
        _providerResolver = model => model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local";
        _tokenAvailable = false;
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/main" },
                    new ModelEntry { Name = "ollama-local/main" },
                    new ModelEntry { Name = "ollama-local/compact" },
                ],
                CompactionModel = "ollama-local/compact",
            },
            Composer = new ComposerConfig { Model = "copilot/main" },
        };
        var sp = BuildProvider(authEnabled: true, config: config);
        var factory = NewFactory(sp);
        await using var gate = Park();

        var context = await CreateRuntimeAsync(sp, factory, second, "ollama-local/compact");
        Assert.Equal(ComposerRuntimeStatus.Deferred, context.Created.Status);
        Assert.Equal("copilot/main", context.Runtime.Configuration.PrimaryModel);
        Assert.True(factory.ShouldDeferConnect(context.Runtime.Configuration));

        // ── LIVE reload: the primary model is replaced by a non-Copilot one ──
        config.Composer = new ComposerConfig { Model = "ollama-local/main" };
        Assert.Equal("ollama-local/main", sp.GetRequiredService<HiveConfigFile>().ResolveComposerDefaultModel());

        // The LIVE-shaped capture would NOT defer any more: the decision really flips ...
        Assert.False(factory.ShouldDeferConnect(
            new ComposerConstructionSnapshot("ollama-local/main", "ollama-local/compact")));
        // ... but the runtime's own capture still defers: the runtime and the factory agree.
        Assert.True(factory.ShouldDeferConnect(context.Runtime.Configuration));
        // The Composer's own construction input was never rewritten either.
        Assert.Equal("copilot/main", context.Runtime.Composer.StartupDefaultModel);

        // Production assertion: the manager keeps the runtime Deferred and starts NO connect.
        var afterReload = await Retry(context.Manager, second);
        Assert.Equal(ComposerRuntimeStatus.Deferred, afterReload.Status);
        Assert.Same(context.Runtime, afterReload.Runtime);
        Assert.Null(afterReload.Failure);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, _clientCreations);

        // Positive control: a FRESH runtime built after the reload captures the new primary and connects —
        // the reload is genuinely observable, so the runtime above kept its capture for a real reason.
        gate.Release();
        Assert.True(await Remove(context.Manager, second.UserId));
        var freshFactory = NewFactory(sp);
        var fresh = await CreateRuntimeAsync(sp, freshFactory, second, "ollama-local/compact");
        Assert.Equal("ollama-local/main", fresh.Runtime.Configuration.PrimaryModel);
        Assert.Equal(ComposerRuntimeStatus.Connected, fresh.Created.Status);
        Assert.Equal(1, gate.Calls);
    }

    /// <summary>
    /// AC1, PRIMARY-MODEL AXIS, DEFER-FALSE SHAPE: a runtime captured with a NON-Copilot primary model (and
    /// no compaction model) is NOT deferred, and a live reload that installs a Copilot-backed primary model —
    /// which WOULD defer a live lookup — never turns that runtime's decision into a deferral.
    /// </summary>
    /// <remarks>
    /// The positive control at the end re-creates the runtime AFTER the reload: the fresh runtime DOES defer on
    /// the reloaded primary, so the reload is genuinely observable — the already-constructed runtime kept its
    /// capture for a substantive reason and not because the reload never happened.
    /// </remarks>
    [Fact]
    public async Task RuntimeDeferral_AfterLivePrimaryModelReload_ForANonDeferredCapture_StaysNonDeferred()
    {
        var (_, second) = await TwoUsersAsync();
        _providerResolver = model => model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local";
        _tokenAvailable = false;
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "ollama-local/main" },
                    new ModelEntry { Name = "copilot/other" },
                ],
            },
            Composer = new ComposerConfig { Model = "ollama-local/main" },
        };
        var sp = BuildProvider(authEnabled: true, config: config);
        var factory = NewFactory(sp);

        // No compaction model and a non-Copilot primary: nothing defers, the runtime connects.
        var context = await CreateRuntimeAsync(sp, factory, second, expectedCompaction: null);
        Assert.Equal(ComposerRuntimeStatus.Connected, context.Created.Status);
        Assert.Equal("ollama-local/main", context.Runtime.Configuration.PrimaryModel);
        Assert.Null(context.Runtime.Configuration.CompactionModel);
        Assert.False(factory.ShouldDeferConnect(context.Runtime.Configuration));
        Assert.True(context.Runtime.Composer.IsConnected);

        await using var gate = Park();

        // ── LIVE reload: a Copilot-backed primary replaces the captured one ──
        config.Composer = new ComposerConfig { Model = "copilot/other" };
        Assert.Equal("copilot/other", sp.GetRequiredService<HiveConfigFile>().ResolveComposerDefaultModel());

        // The LIVE-shaped capture WOULD defer (Copilot primary, still no usable token): the decision really flips ...
        Assert.True(factory.ShouldDeferConnect(new ComposerConstructionSnapshot("copilot/other", null)));
        // ... but the runtime's own capture does not: the runtime and the factory still agree.
        Assert.False(factory.ShouldDeferConnect(context.Runtime.Configuration));

        // Production assertion: the connected runtime stays connected and starts NO new connect.
        var afterReload = await Retry(context.Manager, second);
        Assert.Equal(ComposerRuntimeStatus.Connected, afterReload.Status);
        Assert.Same(context.Runtime, afterReload.Runtime);
        Assert.Null(afterReload.Failure);
        Assert.Equal(0, gate.Calls);

        // Positive control: a FRESH runtime built after the reload captures the Copilot primary and DEFERS —
        // the reload is genuinely observable, so the runtime above kept its capture for a real reason.
        Assert.True(await Remove(context.Manager, second.UserId));
        var freshFactory = NewFactory(sp);
        var fresh = await CreateRuntimeAsync(sp, freshFactory, second, expectedCompaction: null);
        Assert.Equal("copilot/other", fresh.Runtime.Configuration.PrimaryModel);
        Assert.Equal(ComposerRuntimeStatus.Deferred, fresh.Created.Status);
        Assert.Equal(0, gate.Calls);
    }

    /// <summary>
    /// STRUCTURAL PIN: the factory reads the compaction model exactly ONCE — in <c>Build</c> — and resolves
    /// <see cref="HiveConfigFile"/> exactly once there too, so no code path can reach a live compaction value for
    /// an already-constructed runtime. <c>Composer.cs</c> does not grow a snapshot accessor: the capture is
    /// threaded through the runtime instead.
    /// </summary>
    [Fact]
    public void FactorySource_ReadsTheCompactionModelOnlyOnce_InBuild()
    {
        var factorySource = File.ReadAllText(Path.Combine(
            TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration", "ComposerFactory.cs")).ReplaceLineEndings("\n");

        Assert.Equal(1, CountOccurrences(factorySource, "GetCompactionModel()"));
        Assert.Equal(1, CountOccurrences(factorySource, "GetRequiredService<HiveConfigFile>()"));
        Assert.Equal(1, CountOccurrences(factorySource, "new ComposerConstructionSnapshot("));
        // The deferral predicate is a function of the CAPTURE only: it takes no Composer (whose startup inputs would
        // have to be re-read) and resolves no configuration of its own.
        Assert.Contains("internal bool ShouldDeferConnect(ComposerConstructionSnapshot configuration)", factorySource);
        Assert.DoesNotContain("ShouldDeferConnect(Composer composer)", factorySource);

        // Composer.cs stays untouched by the snapshot.
        var composerSource = File.ReadAllText(Path.Combine(
            TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration", "Composer.cs")).ReplaceLineEndings("\n");
        Assert.DoesNotContain("ComposerConstructionSnapshot", composerSource);
        Assert.DoesNotContain("ConstructionConfiguration", composerSource);

        // The manager never evaluates the deferral predicate over a live configuration: both call sites pass the
        // runtime's or the build result's CAPTURE.
        var managerSource = File.ReadAllText(Path.Combine(
            TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration", "ComposerRuntimeManager.cs")).ReplaceLineEndings("\n");
        // The manager reaches the predicate ONLY through the identity-bound decision (eligibility first), and both
        // call sites still pass a CAPTURE.
        Assert.Equal(0, CountOccurrences(managerSource, "_factory.ShouldDeferConnect("));
        Assert.Equal(2, CountOccurrences(managerSource, "_factory.DecideConnectAsync("));
        Assert.Contains("_factory.DecideConnectAsync(deferred.Configuration,", managerSource);
        Assert.Contains("_factory.DecideConnectAsync(build.Configuration,", managerSource);
    }

    private static bool IsCopilotBacked(string? model) =>
        model?.StartsWith("copilot/", StringComparison.Ordinal) == true;

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
