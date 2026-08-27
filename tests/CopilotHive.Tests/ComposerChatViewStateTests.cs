using CopilotHive.Components.Pages;
using CopilotHive.Services;

using Microsoft.Extensions.AI;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the Composer chat disconnected-shell view state and the first-connect refresh
/// sequence. The view-state record (<see cref="ComposerChat.ComposerChatViewState"/>) is the
/// production seam the Razor markup reads DIRECTLY — tests construct it from mock Composer
/// state and assert its flags, never markup tokens or an HTML string. The handler-level seam
/// (<see cref="ComposerChat.IModelSwitchHost"/>) drives the PRODUCTION model-selection body
/// (<see cref="ComposerChat.HandleModelChangeCoreAsync"/>) to prove the first-connect
/// refresh sequence is wired.
/// </summary>
public sealed class ComposerChatViewStateTests
{
    // ── View-state record: disconnected ────────────────────────────────────

    [Fact]
    public void ComputeViewState_Disconnected_ShowsPickerAndNotice_HidesConnectedControls()
    {
        var state = ComposerChat.ComputeViewState(isConnected: false);

        Assert.False(state.IsConnected);
        Assert.True(state.ShowModelPicker, "The model picker must be reachable while disconnected");
        Assert.False(state.ShowConnectedControls, "Connected-only controls must NOT render while disconnected");
        Assert.True(state.ShowNotConfiguredNotice, "The not-configured notice must show while disconnected");
        Assert.Equal(ComposerChat.ModelSelectPlaceholder, state.Placeholder);
    }

    [Fact]
    public void ComputeViewState_Disconnected_PlaceholderIsExplicitNoSelectionOption()
    {
        var state = ComposerChat.ComputeViewState(isConnected: false);

        // The placeholder is the explicit no-selection option: a non-empty list with no valid
        // default must NOT visually auto-select the first option without firing a change event.
        Assert.Equal("— select a model —", state.Placeholder);
        Assert.NotNull(state.Placeholder);
    }

    // ── View-state record: connected ────────────────────────────────────────

    [Fact]
    public void ComputeViewState_Connected_ShowsNormalLayout()
    {
        var state = ComposerChat.ComputeViewState(isConnected: true);

        Assert.True(state.IsConnected);
        Assert.True(state.ShowModelPicker, "The picker is part of the normal connected layout");
        Assert.True(state.ShowConnectedControls, "Connected-only controls (thread, Send, Compact, Reset, reasoning) must render");
        Assert.False(state.ShowNotConfiguredNotice, "No not-configured notice while connected");
        Assert.Equal(ComposerChat.ModelSelectPlaceholder, state.Placeholder);
    }

    // ── Paste-module install decision ───────────────────────────────────────

    [Fact]
    public void ShouldInstallPasteModule_DisconnectedFirstRender_DoesNotInstall()
    {
        // Disconnected-first render: no composer-input textarea exists → no install.
        Assert.False(ComposerChat.ShouldInstallPasteModule(
            alreadyInstalled: false, showConnectedControls: false));
    }

    [Fact]
    public void ShouldInstallPasteModule_ConnectedFirstRender_Installs()
    {
        // Connected-first render: the textarea is present on the first render → install.
        Assert.True(ComposerChat.ShouldInstallPasteModule(
            alreadyInstalled: false, showConnectedControls: true));
    }

    [Fact]
    public void ShouldInstallPasteModule_AfterConnect_InstallsExactlyOnce()
    {
        // Post-connect render (disconnected-first page): the textarea first appears → install.
        Assert.True(ComposerChat.ShouldInstallPasteModule(
            alreadyInstalled: false, showConnectedControls: true));

        // Subsequent renders: already installed → NO re-install / duplicate listeners.
        Assert.False(ComposerChat.ShouldInstallPasteModule(
            alreadyInstalled: true, showConnectedControls: true));
    }

    [Fact]
    public void ShouldInstallPasteModule_DisconnectedAfterInstall_NeverReinstalls()
    {
        // Even if the Composer were to disconnect again, the module is never re-installed —
        // install state is tracked exactly once.
        Assert.False(ComposerChat.ShouldInstallPasteModule(
            alreadyInstalled: true, showConnectedControls: false));
    }

    // ── Handler-level seam: first-connect refresh sequence ──────────────────

    [Fact]
    public async Task HandleModelChangeCoreAsync_FirstConnect_Success_RefreshesHistoryAndCurrentModel()
    {
        var host = new RecordingModelSwitchHost(isConnected: false);
        host.SwitchResult = RecordingModelSwitchHost.Ok("model-a", ReasoningEffort.Medium);

        await ComposerChat.HandleModelChangeCoreAsync(host, "model-a", ReasoningEffort.Medium);

        // The facade switch was invoked with the selected model + reasoning.
        Assert.Equal("model-a", host.SwitchedModel);
        Assert.Equal(ReasoningEffort.Medium, host.SwitchedReasoning);

        // The selected model was recorded.
        Assert.Equal("model-a", host.SelectedModel);

        // First-connect refresh: history + current model were both loaded.
        Assert.Equal(1, host.HistoryLoadCount);
        Assert.Equal(1, host.CurrentModelLoadCount);

        // The view was re-rendered so the connected layout (thread + controls) shows.
        Assert.Equal(1, host.NotifyCount);
    }

    [Fact]
    public async Task HandleModelChangeCoreAsync_FirstConnect_Success_CallOrderIsSwitchThenSetThenHistoryThenCurrentModelThenNotify()
    {
        // The production sequence must be wired in the EXACT order: switch → set selection →
        // load history → refresh current model → notify (re-render). This test fails if the
        // sequence is reordered or any step is removed — not just an end-state check.
        var host = new RecordingModelSwitchHost(isConnected: false);
        host.SwitchResult = RecordingModelSwitchHost.Ok("model-a", ReasoningEffort.Medium);

        await ComposerChat.HandleModelChangeCoreAsync(host, "model-a", ReasoningEffort.Medium);

        Assert.Equal(
            ["SwitchAsync", "SetSelectedModel", "LoadHistory", "LoadCurrentModelAsync", "Notify"],
            host.CallOrder);
    }

    [Fact]
    public async Task HandleModelChangeCoreAsync_AlreadyConnected_Success_DoesNotRefreshHistory()
    {
        var host = new RecordingModelSwitchHost(isConnected: true);
        host.SwitchResult = RecordingModelSwitchHost.Ok("model-b", ReasoningEffort.High);

        await ComposerChat.HandleModelChangeCoreAsync(host, "model-b", ReasoningEffort.High);

        Assert.Equal("model-b", host.SwitchedModel);
        Assert.Equal("model-b", host.SelectedModel);

        // Already connected: a plain switch — NO history/current-model refresh, NO re-render.
        Assert.Equal(0, host.HistoryLoadCount);
        Assert.Equal(0, host.CurrentModelLoadCount);
        Assert.Equal(0, host.NotifyCount);
    }

    [Fact]
    public async Task HandleModelChangeCoreAsync_AlreadyConnected_Success_CallOrderIsSwitchThenSetOnly()
    {
        // A plain switch on an already-connected Composer must NOT refresh history or
        // re-render — only switch + set the selection. This proves the first-connect refresh
        // is conditionally gated on wasDisconnected, not unconditional.
        var host = new RecordingModelSwitchHost(isConnected: true);
        host.SwitchResult = RecordingModelSwitchHost.Ok("model-b", ReasoningEffort.High);

        await ComposerChat.HandleModelChangeCoreAsync(host, "model-b", ReasoningEffort.High);

        Assert.Equal(["SwitchAsync", "SetSelectedModel"], host.CallOrder);
    }

    [Fact]
    public async Task HandleModelChangeCoreAsync_SwitchFails_NoRefreshNoSelection()
    {
        var host = new RecordingModelSwitchHost(isConnected: false);
        // An UNSUCCESSFUL facade result carries the error text the production code logs — this
        // replaces the old non-2xx HTTP response with an error JSON body.
        host.SwitchResult = new FacadeResult<SwitchResultDto>(
            false, null, "model not available", FacadeErrorKind.BadRequest);

        await ComposerChat.HandleModelChangeCoreAsync(host, "model-a", ReasoningEffort.Medium);

        Assert.Equal("model-a", host.SwitchedModel);
        Assert.Null(host.SelectedModel);
        Assert.Equal(0, host.HistoryLoadCount);
        Assert.Equal(0, host.CurrentModelLoadCount);
        Assert.Equal(0, host.NotifyCount);
    }

    [Fact]
    public async Task HandleModelChangeCoreAsync_SwitchThrows_NoRefreshNoSelection()
    {
        var host = new RecordingModelSwitchHost(isConnected: false)
        {
            SwitchException = new InvalidOperationException("network down"),
        };

        await ComposerChat.HandleModelChangeCoreAsync(host, "model-a", ReasoningEffort.Medium);

        Assert.Null(host.SelectedModel);
        Assert.Equal(0, host.HistoryLoadCount);
        Assert.Equal(0, host.CurrentModelLoadCount);
        Assert.Equal(0, host.NotifyCount);
    }

    // ── Shared current-model DTO: the two DISTINCT result paths ─────────────

    /// <summary>
    /// The unconfigured Composer is reported as a SUCCESSFUL result carrying a null model —
    /// not a failure. The page must clear the selection SILENTLY on this path.
    /// </summary>
    [Fact]
    public void CurrentModel_SuccessWithNullModel_IsANormalResult()
    {
        var result = new FacadeResult<CurrentModelDto>(
            true, new CurrentModelDto(null), null, FacadeErrorKind.None);

        Assert.True(result.Success);
        Assert.Null(result.Value!.Model);
        Assert.Null(result.Error);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
    }

    [Fact]
    public void CurrentModel_SuccessWithModel_CarriesTheModel()
    {
        var result = new FacadeResult<CurrentModelDto>(
            true, new CurrentModelDto("claude-opus"), null, FacadeErrorKind.None);

        Assert.True(result.Success);
        Assert.Equal("claude-opus", result.Value!.Model);
    }

    /// <summary>
    /// The failure path is DISTINCT from the successful-null-model path: it carries an error
    /// message, which is what the page console-logs before clearing the selection.
    /// </summary>
    [Fact]
    public void CurrentModel_Failure_CarriesErrorAndNoValue()
    {
        var result = new FacadeResult<CurrentModelDto>(
            false, null, ComposerFacade.ComposerUnavailableError, FacadeErrorKind.NotConfigured);

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal("Composer is not available.", result.Error);
        Assert.Equal(FacadeErrorKind.NotConfigured, result.Kind);
    }

    /// <summary>
    /// The page must NOT reintroduce a private current-model DTO — it consumes the shared
    /// <see cref="CurrentModelDto"/>. Reverting to the HttpClient shape fails this test.
    /// </summary>
    [Fact]
    public void ComposerChat_UsesSharedCurrentModelDto()
    {
        Assert.Null(typeof(ComposerChat).GetNestedType(
            "CurrentModelResponse",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(typeof(CurrentModelDto).GetProperty("Model"));
    }

    // ── Source-level seam: the TWO DISTINCT current-model paths ─────────────

    private static string ReadComposerChatSource()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        Assert.NotNull(repoRoot);

        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", "ComposerChat.razor");
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        return File.ReadAllText(razorPath);
    }

    /// <summary>
    /// Extracts the source text of a method by name so assertions target the method body
    /// rather than the whole file.
    /// </summary>
    private static string ExtractMethodSource(string source, string methodSignature)
    {
        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{methodSignature}' not found in Razor source");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"Opening brace for '{methodSignature}' not found");

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(start, i - start + 1);
            }
        }

        Assert.Fail($"Could not find matching closing brace for '{methodSignature}'");
        return string.Empty; // unreachable
    }

    /// <summary>
    /// REMOVAL-PROOF: the current-model read goes through the facade
    /// (<c>ComposerFacade.GetCurrentModelAsync()</c>) — NOT an HTTP GET. The component's own
    /// method delegates to the shared production body, which reads through the facade-binding
    /// host. Reverting <c>ComposerChat.razor</c> to an <c>HttpClient</c>-based call fails this
    /// test, and so does deleting the facade call (the recording-assertion equivalent for a
    /// Razor page that cannot be instantiated without a circuit).
    /// </summary>
    [Fact]
    public void ComposerChat_LoadCurrentModel_CallsTheComposerFacade()
    {
        var source = ReadComposerChatSource();
        var method = ExtractMethodSource(source, "private Task LoadCurrentModelAsync()");

        // The component's own method delegates to the production body, which is where the
        // result/exception paths live.
        Assert.Contains("LoadCurrentModelCoreAsync", method);
        Assert.DoesNotContain("HttpClient", method);
        Assert.DoesNotContain("/api/composer/", method);

        var core = ExtractMethodSource(source, "internal static async Task LoadCurrentModelCoreAsync(ICurrentModelHost host)");
        // The production body reads through the host seam — a revert to HttpClient (or removal
        // of the facade call from the host binding) fails the assertions below.
        Assert.Contains("host.GetCurrentModelAsync()", core);
        Assert.DoesNotContain("HttpClient", core);

        // The host binding is what connects the seam to the facade — removal-proof.
        var host = ExtractMethodSource(source, "private sealed class ComponentCurrentModelHost(ComposerChat owner)");
        Assert.Contains("ComposerFacade.GetCurrentModelAsync()", host);
        Assert.Contains("owner.ComposerFacade", host);
        Assert.DoesNotContain("/api/composer/", host);
    }

    /// <summary>
    /// The TWO DISTINCT current-model result paths are wired exactly as the facade contract
    /// requires. Path 1 — an UNSUCCESSFUL facade result clears <c>_selectedModel</c> AND
    /// logs <c>result.Error</c> (the old exception path). Path 2 — a SUCCESSFUL result with
    /// a null/blank/unknown model clears the selection WITHOUT logging (the normal
    /// unconfigured contract, not an error). The failure-branch assertions are scoped to the
    /// BRACE-BOUNDED failure block, so deleting the clear/log from that branch alone fails
    /// this test — the later silent branch cannot satisfy them.
    /// </summary>
    [Fact]
    public void ComposerChat_LoadCurrentModel_DistinguishesFailureFromNullModelPaths()
    {
        var source = ReadComposerChatSource();
        var method = ExtractMethodSource(source, "internal static async Task LoadCurrentModelCoreAsync(ICurrentModelHost host)");

        // Path 1: unsuccessful result → log the error, clear the selection.
        var failureIndex = method.IndexOf("if (!result.Success)", StringComparison.Ordinal);
        Assert.True(failureIndex >= 0, "Load must branch on an unsuccessful facade result");

        // The failure block is brace-bounded: the assertion cannot be satisfied by the later
        // success-but-null/unknown branch, which also clears the selection.
        var failureBlock = ExtractBraceBlock(method, failureIndex);
        Assert.Contains("host.LogCurrentModelFailure", failureBlock, StringComparison.Ordinal);
        Assert.Contains("result.Error", failureBlock, StringComparison.Ordinal);
        Assert.Contains("SelectedModel = \"\"", failureBlock, StringComparison.Ordinal);

        // The thrown-exception path logs the same way, so the failure log line itself lives in
        // the host binding — removal-proof for the console-log prefix.
        var hostBinding = ExtractMethodSource(source, "private sealed class ComponentCurrentModelHost(ComposerChat owner)");
        Assert.Contains("Failed to load current model", hostBinding, StringComparison.Ordinal);

        // Path 2: successful read with a null/blank/UNKNOWN model → clear WITHOUT logging.
        var elseIndex = method.IndexOf("else", failureIndex, StringComparison.Ordinal);
        Assert.True(elseIndex >= 0, "The success-but-no-valid-model branch must exist");
        var elseBody = method.Substring(elseIndex);
        Assert.Contains("SelectedModel = \"\"", elseBody);
        // The silent path must not contain the failure log line.
        Assert.DoesNotContain("Failed to load current model", elseBody, StringComparison.Ordinal);

        // The successful-with-model branch consults the loaded catalog for the unknown-model rule.
        Assert.Contains("AvailableModels.Contains(model)", method);
    }

    /// <summary>Extracts the brace-bounded block starting at the given index (inclusive of both braces).</summary>
    private static string ExtractBraceBlock(string text, int openBraceIndex)
    {
        var braceStart = text.IndexOf('{', openBraceIndex);
        Assert.True(braceStart >= 0, "No opening brace found");

        var depth = 0;
        for (var i = braceStart; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text.Substring(braceStart, i - braceStart + 1);
            }
        }

        Assert.Fail("Could not find matching closing brace");
        return string.Empty; // unreachable
    }

    // ── Behavioral seam: the PRODUCTION current-model body ──────────────────

    /// <summary>
    /// Recording <see cref="ComposerChat.ICurrentModelHost"/> that drives the PRODUCTION body
    /// (<see cref="ComposerChat.LoadCurrentModelCoreAsync"/>) without a Blazor circuit. The
    /// console-log side effect is replaced by a recorded failure text, and the facade read is
    /// scripted per test — including the thrown-exception path.
    /// </summary>
    private sealed class RecordingCurrentModelHost : ComposerChat.ICurrentModelHost
    {
        /// <inheritdoc />
        public IReadOnlyList<string> AvailableModels { get; set; } = ["claude-sonnet-4", "claude-opus"];

        /// <inheritdoc />
        public string SelectedModel { get; set; } = "stale-model";

        /// <summary>Failure text handed to <see cref="LogCurrentModelFailure"/>, or <c>null</c> if never logged.</summary>
        public string? LoggedFailure { get; private set; }

        /// <summary>Whether <see cref="LogCurrentModelFailure"/> was invoked.</summary>
        public bool FailureLogged => LoggedFailure is not null;

        /// <summary>The result <see cref="GetCurrentModelAsync"/> returns.</summary>
        public FacadeResult<CurrentModelDto>? ReadResult { get; set; }

        /// <summary>When set, <see cref="GetCurrentModelAsync"/> throws it instead of returning.</summary>
        public Exception? ReadException { get; set; }

        /// <inheritdoc />
        public void LogCurrentModelFailure(string error) => LoggedFailure = error;

        /// <inheritdoc />
        public Task<FacadeResult<CurrentModelDto>> GetCurrentModelAsync()
            => ReadException is not null
                ? Task.FromException<FacadeResult<CurrentModelDto>>(ReadException)
                : Task.FromResult(ReadResult!);
    }

    [Fact]
    public async Task LoadCurrentModelCoreAsync_UnsuccessfulResult_LogsAndClearsSelection()
    {
        var host = new RecordingCurrentModelHost
        {
            SelectedModel = "stale-model",
            ReadResult = new FacadeResult<CurrentModelDto>(
                false, null, "Composer is not available.", FacadeErrorKind.NotConfigured),
        };

        await ComposerChat.LoadCurrentModelCoreAsync(host);

        Assert.Equal("Composer is not available.", host.LoggedFailure);
        Assert.Equal("", host.SelectedModel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LoadCurrentModelCoreAsync_SuccessWithNullOrBlankModel_ClearsSilently(string? model)
    {
        var host = new RecordingCurrentModelHost
        {
            SelectedModel = "stale-model",
            ReadResult = new FacadeResult<CurrentModelDto>(
                true, new CurrentModelDto(model), null, FacadeErrorKind.None),
        };

        await ComposerChat.LoadCurrentModelCoreAsync(host);

        // The normal unconfigured contract: cleared WITHOUT any failure log.
        Assert.Equal("", host.SelectedModel);
        Assert.False(host.FailureLogged);
    }

    [Fact]
    public async Task LoadCurrentModelCoreAsync_SuccessWithUnknownModel_ClearsSilently()
    {
        var host = new RecordingCurrentModelHost
        {
            SelectedModel = "stale-model",
            ReadResult = new FacadeResult<CurrentModelDto>(
                true, new CurrentModelDto("not-in-catalog"), null, FacadeErrorKind.None),
        };

        await ComposerChat.LoadCurrentModelCoreAsync(host);

        Assert.Equal("", host.SelectedModel);
        Assert.False(host.FailureLogged);
    }

    [Fact]
    public async Task LoadCurrentModelCoreAsync_SuccessWithKnownModel_SelectsIt()
    {
        var host = new RecordingCurrentModelHost
        {
            SelectedModel = "",
            ReadResult = new FacadeResult<CurrentModelDto>(
                true, new CurrentModelDto("claude-opus"), null, FacadeErrorKind.None),
        };

        await ComposerChat.LoadCurrentModelCoreAsync(host);

        Assert.Equal("claude-opus", host.SelectedModel);
        Assert.False(host.FailureLogged);
    }

    /// <summary>
    /// The facade RETHROWS read failures by design, so the production body must catch a thrown
    /// exception, console-log it and clear the selection — it must NEVER escape and fault the
    /// Blazor circuit (init path) or be misreported as a switch failure (first-connect path).
    /// </summary>
    [Fact]
    public async Task LoadCurrentModelCoreAsync_ThrownReadException_LogsAndClearsSelection_DoesNotThrow()
    {
        var host = new RecordingCurrentModelHost
        {
            SelectedModel = "stale-model",
            ReadException = new InvalidOperationException("stats store exploded"),
        };

        await ComposerChat.LoadCurrentModelCoreAsync(host);

        Assert.Equal("stats store exploded", host.LoggedFailure);
        Assert.Equal("", host.SelectedModel);
    }
}

/// <summary>
/// Recording <see cref="ComposerChat.IModelSwitchHost"/> that drives the PRODUCTION
/// model-selection body (<see cref="ComposerChat.HandleModelChangeCoreAsync"/>) without a
/// Blazor circuit.
/// </summary>
internal sealed class RecordingModelSwitchHost(bool isConnected) : ComposerChat.IModelSwitchHost
{
    /// <inheritdoc />
    public bool IsConnected { get; } = isConnected;

    /// <summary>The model handed to <see cref="SwitchAsync"/>, or <c>null</c> if never called.</summary>
    public string? SwitchedModel { get; private set; }

    /// <summary>The reasoning handed to <see cref="SwitchAsync"/>.</summary>
    public ReasoningEffort? SwitchedReasoning { get; private set; }

    /// <summary>The model recorded via <see cref="SetSelectedModel"/>, or <c>null</c>.</summary>
    public string? SelectedModel { get; private set; }

    /// <summary>How many times <see cref="LoadHistory"/> was invoked.</summary>
    public int HistoryLoadCount { get; private set; }

    /// <summary>How many times <see cref="LoadCurrentModelAsync"/> was invoked.</summary>
    public int CurrentModelLoadCount { get; private set; }

    /// <summary>How many times <see cref="Notify"/> was invoked.</summary>
    public int NotifyCount { get; private set; }

    /// <summary>
    /// The ordered list of method names invoked on this host, in call order. Used to prove
    /// the production sequence is wired (fails if reordered or removed).
    /// </summary>
    public List<string> CallOrder { get; } = [];

    /// <summary>Builds a successful switch result for the given model/reasoning.</summary>
    /// <param name="model">Model reported as applied.</param>
    /// <param name="reasoning">Reasoning effort reported as applied.</param>
    public static FacadeResult<SwitchResultDto> Ok(string model, ReasoningEffort reasoning)
        => new(true, new SwitchResultDto(model, reasoning), null, FacadeErrorKind.None);

    /// <summary>The result <see cref="SwitchAsync"/> returns; defaults to a success.</summary>
    public FacadeResult<SwitchResultDto> SwitchResult { get; set; } = Ok("model-a", ReasoningEffort.Medium);

    /// <summary>When set, <see cref="SwitchAsync"/> throws it instead of returning.</summary>
    public Exception? SwitchException { get; set; }

    /// <inheritdoc />
    public Task<FacadeResult<SwitchResultDto>> SwitchAsync(string model, ReasoningEffort reasoning)
    {
        SwitchedModel = model;
        SwitchedReasoning = reasoning;
        CallOrder.Add("SwitchAsync");
        return SwitchException is not null
            ? Task.FromException<FacadeResult<SwitchResultDto>>(SwitchException)
            : Task.FromResult(SwitchResult);
    }

    /// <inheritdoc />
    public void LoadHistory()
    {
        HistoryLoadCount++;
        CallOrder.Add("LoadHistory");
    }

    /// <inheritdoc />
    public Task LoadCurrentModelAsync()
    {
        CurrentModelLoadCount++;
        CallOrder.Add("LoadCurrentModelAsync");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetSelectedModel(string model)
    {
        SelectedModel = model;
        CallOrder.Add("SetSelectedModel");
    }

    /// <inheritdoc />
    public void Notify()
    {
        NotifyCount++;
        CallOrder.Add("Notify");
    }
}
