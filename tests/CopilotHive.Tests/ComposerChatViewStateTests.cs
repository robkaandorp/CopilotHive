using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CopilotHive.Components.Pages;
using CopilotHive.Dashboard;

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
        host.SwitchResult = new HttpResponseMessage(HttpStatusCode.OK);

        await ComposerChat.HandleModelChangeCoreAsync(host, "model-a", ReasoningEffort.Medium);

        // The switch endpoint was invoked with the selected model + reasoning.
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
        host.SwitchResult = new HttpResponseMessage(HttpStatusCode.OK);

        await ComposerChat.HandleModelChangeCoreAsync(host, "model-a", ReasoningEffort.Medium);

        Assert.Equal(
            ["SwitchAsync", "SetSelectedModel", "LoadHistory", "LoadCurrentModelAsync", "Notify"],
            host.CallOrder);
    }

    [Fact]
    public async Task HandleModelChangeCoreAsync_AlreadyConnected_Success_DoesNotRefreshHistory()
    {
        var host = new RecordingModelSwitchHost(isConnected: true);
        host.SwitchResult = new HttpResponseMessage(HttpStatusCode.OK);

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
        host.SwitchResult = new HttpResponseMessage(HttpStatusCode.OK);

        await ComposerChat.HandleModelChangeCoreAsync(host, "model-b", ReasoningEffort.High);

        Assert.Equal(["SwitchAsync", "SetSelectedModel"], host.CallOrder);
    }

    [Fact]
    public async Task HandleModelChangeCoreAsync_SwitchFails_NoRefreshNoSelection()
    {
        var host = new RecordingModelSwitchHost(isConnected: false);
        // Provide JSON error content so the error-response branch (ReadFromJsonAsync) is
        // fully exercised — the production code reads an ErrorResponse from the body.
        host.SwitchResult = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { error = "model not available" }),
        };

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

    // ── Nullable client DTO deserialization ─────────────────────────────────

    [Fact]
    public void CurrentModelResponse_NullModelJson_DeserializesCleanly()
    {
        // The frozen contract returns {"model":null} — the nullable client DTO must
        // deserialize it without throwing.
        var response = JsonSerializer.Deserialize<ComposerChat.CurrentModelResponse>(
            """{"model":null}""", ReasoningEffortOptions.JsonOptions);

        Assert.NotNull(response);
        Assert.Null(response!.Model);
    }

    [Fact]
    public void CurrentModelResponse_StringModelJson_DeserializesCleanly()
    {
        var response = JsonSerializer.Deserialize<ComposerChat.CurrentModelResponse>(
            """{"model":"claude-opus"}""", ReasoningEffortOptions.JsonOptions);

        Assert.NotNull(response);
        Assert.Equal("claude-opus", response!.Model);
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

    /// <summary>The response <see cref="SwitchAsync"/> returns; defaults to 200 OK.</summary>
    public HttpResponseMessage SwitchResult { get; set; } = new(HttpStatusCode.OK);

    /// <summary>When set, <see cref="SwitchAsync"/> throws it instead of returning.</summary>
    public Exception? SwitchException { get; set; }

    /// <inheritdoc />
    public Task<HttpResponseMessage> SwitchAsync(string model, ReasoningEffort reasoning)
    {
        SwitchedModel = model;
        SwitchedReasoning = reasoning;
        CallOrder.Add("SwitchAsync");
        return SwitchException is not null
            ? Task.FromException<HttpResponseMessage>(SwitchException)
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
