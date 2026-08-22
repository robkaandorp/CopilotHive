using CopilotHive.Components.Pages;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the active event-bus mode configuration UI in <c>ComposerChat.razor</c> and
/// <c>Configuration.razor</c>. Because the project does not use bUnit, these tests use two
/// removal-proof strategies:
/// <list type="bullet">
///   <item>
///     Source-file content assertions that read the actual Razor sources and verify the
///     required markup, state fields, endpoint URLs, request bodies, and status-code handling
///     are present. Deleting or regressing any of these production elements fails the test.
///   </item>
///   <item>
///     Helper-logic tests that exercise the production static helpers directly through
///     <c>InternalsVisibleTo</c> (e.g. <see cref="ComposerChat.NextActiveNotificationsMode"/>).
///   </item>
/// </list>
/// </summary>
public sealed class ActiveModeConfigUiTests
{
    // ── source-file access ───────────────────────────────────────────────────

    private static string ReadRazorSource(string relativePath)
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);

        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", relativePath);
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        return File.ReadAllText(razorPath);
    }

    private static string ReadComposerChatSource() => ReadRazorSource("ComposerChat.razor");
    private static string ReadConfigurationSource() => ReadRazorSource("Configuration.razor");

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
    /// Extracts the "Active Events" checkbox container markup from the Configuration.razor
    /// source — the <c>&lt;div&gt;</c> that follows the "Active Events" label. Assertions
    /// scoped to this block cannot be satisfied by the <c>ActiveEventPresets</c> arrays,
    /// comments, or method declarations elsewhere in the file: deleting or miswiring
    /// a checkbox or preset button removes its markup from this block.
    /// </summary>
    private static string ExtractActiveEventsBlock(string source)
    {
        const string labelMarker = "<label style=\"color:var(--text-muted);font-size:0.9rem\">Active Events</label>";
        var labelIndex = source.IndexOf(labelMarker, StringComparison.Ordinal);
        Assert.True(labelIndex >= 0, "'Active Events' label not found in Configuration.razor");

        var blockStart = source.IndexOf("<div", labelIndex, StringComparison.Ordinal);
        Assert.True(blockStart >= 0, "Active-events container <div> not found after the 'Active Events' label");

        // Nesting scan: "<div" opens, "</div>" closes; "</div>" cannot be mistaken for an
        // opening tag because the '/' follows '<'.
        var depth = 0;
        for (var i = blockStart; i < source.Length - 4; i++)
        {
            if (source[i] == '<' && source[i + 1] == 'd' && source[i + 2] == 'i' && source[i + 3] == 'v')
            {
                depth++;
                i += 3;
            }
            else if (source[i] == '<' && source[i + 1] == '/' && source[i + 2] == 'd'
                     && source[i + 3] == 'i' && source[i + 4] == 'v')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(blockStart, i - blockStart + 6);
                i += 4;
            }
        }

        Assert.Fail("Could not find the matching </div> of the active-events container");
        return string.Empty; // unreachable
    }

    /// <summary>
    /// Extracts the <c>&lt;label&gt;</c> element containing the given checkbox marker, so
    /// assertions can prove the checkbox input AND its visible label live in the same element.
    /// </summary>
    private static string ExtractCheckboxLabel(string block, string checkedMarker)
    {
        var markerIndex = block.IndexOf(checkedMarker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Checkbox marker '{checkedMarker}' not found in the active-events markup block");

        var labelStart = block.LastIndexOf("<label", markerIndex, StringComparison.Ordinal);
        Assert.True(labelStart >= 0, "No <label> opening tag before the checkbox marker");
        var labelEnd = block.IndexOf("</label>", markerIndex, StringComparison.Ordinal);
        Assert.True(labelEnd >= 0, "No </label> closing tag after the checkbox marker");
        return block.Substring(labelStart, labelEnd - labelStart + "</label>".Length);
    }

    /// <summary>
    /// Extracts the <c>&lt;button&gt;</c> element containing the given <c>@onclick</c> index,
    /// so assertions can prove the handler and the visible label live in the same element.
    /// </summary>
    private static string ExtractButtonElement(string block, int onclickIndex)
    {
        var start = block.LastIndexOf("<button", onclickIndex, StringComparison.Ordinal);
        Assert.True(start >= 0, "No <button> opening tag before the @onclick handler");
        var end = block.IndexOf("</button>", onclickIndex, StringComparison.Ordinal);
        Assert.True(end >= 0, "No </button> closing tag after the @onclick handler");
        return block.Substring(start, end - start + "</button>".Length);
    }

    // ── ComposerChat: NextActiveNotificationsMode cycle ──────────────────────

    [Fact]
    public void NextActiveNotificationsMode_Passive_ReturnsActive() =>
        Assert.Equal("active", ComposerChat.NextActiveNotificationsMode("passive"));

    [Fact]
    public void NextActiveNotificationsMode_Active_ReturnsOff() =>
        Assert.Equal("off", ComposerChat.NextActiveNotificationsMode("active"));

    [Fact]
    public void NextActiveNotificationsMode_Off_ReturnsPassive() =>
        Assert.Equal("passive", ComposerChat.NextActiveNotificationsMode("off"));

    /// <summary>
    /// The cycle is only ever computed from an authoritative loaded mode, so an unknown value is
    /// a bug rather than something to silently coerce into a default transition.
    /// </summary>
    [Fact]
    public void NextActiveNotificationsMode_Unknown_Throws() =>
        Assert.Throws<InvalidOperationException>(() => ComposerChat.NextActiveNotificationsMode("bogus"));

    [Fact]
    public void NextActiveNotificationsMode_FullCycle_ReturnsToPassive()
    {
        var mode = "passive";
        mode = ComposerChat.NextActiveNotificationsMode(mode);
        Assert.Equal("active", mode);
        mode = ComposerChat.NextActiveNotificationsMode(mode);
        Assert.Equal("off", mode);
        mode = ComposerChat.NextActiveNotificationsMode(mode);
        Assert.Equal("passive", mode);
    }

    // ── ComposerChat: loaded-mode normalization ─────────────────────────────

    [Theory]
    [InlineData("passive")]
    [InlineData("active")]
    [InlineData("off")]
    public void NormalizeLoadedNotificationsMode_KnownMode_IsKept(string mode) =>
        Assert.Equal(mode, ComposerChat.NormalizeLoadedNotificationsMode(mode));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("Passive")]
    public void NormalizeLoadedNotificationsMode_UnknownMode_IsNotLoaded(string? mode) =>
        Assert.Null(ComposerChat.NormalizeLoadedNotificationsMode(mode));

    // ── ComposerChat: button label ──────────────────────────────────────────

    [Theory]
    [InlineData("passive", "🔕 Passive")]
    [InlineData("active", "🔔 Active")]
    [InlineData("off", "🔕 Off")]
    public void ActiveNotificationsLabel_KnownMode_UsesRequiredText(string mode, string expected) =>
        Assert.Equal(expected, ComposerChat.ActiveNotificationsLabel(mode));

    /// <summary>
    /// An unloaded mode must NOT render as "Passive": that would claim a mode the server never
    /// reported and mislead the user about what the next click would persist.
    /// </summary>
    [Fact]
    public void ActiveNotificationsLabel_NotLoaded_DoesNotClaimPassive()
    {
        var label = ComposerChat.ActiveNotificationsLabel(null);
        Assert.DoesNotContain("Passive", label, StringComparison.Ordinal);
        Assert.DoesNotContain("Active", label, StringComparison.Ordinal);
        Assert.DoesNotContain("Off", label, StringComparison.Ordinal);
    }

    // ── ComposerChat: toggle guard ──────────────────────────────────────────

    [Fact]
    public void CanToggle_LoadedIdleAndNotPending_ReturnsTrue() =>
        Assert.True(ComposerChat.CanToggleActiveNotifications("passive", isStreaming: false, togglePending: false));

    [Fact]
    public void CanToggle_ModeNotLoaded_ReturnsFalse() =>
        Assert.False(ComposerChat.CanToggleActiveNotifications(null, isStreaming: false, togglePending: false));

    [Fact]
    public void CanToggle_Streaming_ReturnsFalse() =>
        Assert.False(ComposerChat.CanToggleActiveNotifications("passive", isStreaming: true, togglePending: false));

    [Fact]
    public void CanToggle_TogglePending_ReturnsFalse() =>
        Assert.False(ComposerChat.CanToggleActiveNotifications("passive", isStreaming: false, togglePending: true));

    // ── ComposerChat: toggle body (production code, executed) ───────────────

    [Fact]
    public async Task Toggle_Success_AdvancesModeAndSetsRestartIndicator()
    {
        var host = new RecordingNotificationToggleHost { Mode = "passive" };

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);

        Assert.Equal("active", host.Mode);
        Assert.True(host.RestartRequired);
        Assert.Null(host.ToggleError);
        Assert.Equal(["active"], host.PatchedModes);
    }

    /// <summary>
    /// Three successive toggles must walk the full cycle, each transition derived from the mode
    /// the previous PATCH actually persisted.
    /// </summary>
    [Fact]
    public async Task Toggle_ThreeTimes_WalksPassiveActiveOffPassive()
    {
        var host = new RecordingNotificationToggleHost { Mode = "passive" };

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);
        Assert.Equal("active", host.Mode);

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);
        Assert.Equal("off", host.Mode);

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);
        Assert.Equal("passive", host.Mode);

        Assert.Equal(["active", "off", "passive"], host.PatchedModes);
    }

    /// <summary>
    /// The transition is computed from the AUTHORITATIVE loaded mode, not a fabricated default:
    /// a page whose stored mode is <c>active</c> must PATCH <c>off</c>, never <c>active</c> again.
    /// </summary>
    [Theory]
    [InlineData("passive", "active")]
    [InlineData("active", "off")]
    [InlineData("off", "passive")]
    public async Task Toggle_UsesAuthoritativeLoadedMode(string loaded, string expectedPatch)
    {
        var host = new RecordingNotificationToggleHost { Mode = loaded };

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);

        Assert.Equal([expectedPatch], host.PatchedModes);
        Assert.Equal(expectedPatch, host.Mode);
    }

    [Fact]
    public async Task Toggle_ModeNotLoaded_SendsNoRequestAndLeavesStateUnchanged()
    {
        var host = new RecordingNotificationToggleHost { Mode = null };

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);

        Assert.Empty(host.PatchedModes);
        Assert.Null(host.Mode);
        Assert.False(host.RestartRequired);
    }

    [Fact]
    public async Task Toggle_WhileStreaming_SendsNoRequest()
    {
        var host = new RecordingNotificationToggleHost { Mode = "passive", IsStreamingValue = true };

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);

        Assert.Empty(host.PatchedModes);
        Assert.Equal("passive", host.Mode);
        Assert.False(host.RestartRequired);
    }

    [Fact]
    public async Task Toggle_Failure_ShowsErrorAndLeavesModeUnchanged()
    {
        var host = new RecordingNotificationToggleHost
        {
            Mode = "passive",
            ResponseStatus = System.Net.HttpStatusCode.BadRequest,
            ResponseBody = "{\"error\":\"nope\"}",
        };

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);

        Assert.Equal("passive", host.Mode);
        Assert.False(host.RestartRequired);
        Assert.NotNull(host.ToggleError);
        Assert.Contains("400", host.ToggleError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Toggle_ThrowingRequest_ShowsErrorAndLeavesModeUnchanged()
    {
        var host = new RecordingNotificationToggleHost
        {
            Mode = "active",
            PatchFailure = new HttpRequestException("network down"),
        };

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);

        Assert.Equal("active", host.Mode);
        Assert.False(host.RestartRequired);
        Assert.NotNull(host.ToggleError);
        Assert.Contains("network down", host.ToggleError!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed toggle must release the in-flight claim so the user can retry — otherwise the
    /// button would stay disabled forever.
    /// </summary>
    [Fact]
    public async Task Toggle_AfterFailure_ClaimIsReleasedAndRetrySucceeds()
    {
        var host = new RecordingNotificationToggleHost
        {
            Mode = "passive",
            ResponseStatus = System.Net.HttpStatusCode.InternalServerError,
        };

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);
        Assert.False(host.TogglePending);
        Assert.Equal("passive", host.Mode);

        host.ResponseStatus = System.Net.HttpStatusCode.OK;
        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);

        Assert.Equal("active", host.Mode);
        Assert.True(host.RestartRequired);
    }

    /// <summary>
    /// The in-flight claim is taken BEFORE the request is issued, so a second click that lands
    /// while the first PATCH is still outstanding observes it.
    /// </summary>
    [Fact]
    public async Task Toggle_ClaimIsTakenBeforeRequestIsIssued()
    {
        var host = new RecordingNotificationToggleHost { Mode = "passive" };

        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);

        Assert.True(host.TogglePendingWhenPatched,
            "The in-flight claim must be set before the PATCH is issued.");
        Assert.False(host.TogglePending, "The claim must be released once the toggle completes.");
    }

    /// <summary>
    /// The overlapping-click regression: the first PATCH is held open while a second toggle is
    /// invoked. Without an in-flight guard both calls would derive <c>active</c> from the same
    /// stale <c>passive</c> and persist <c>active</c> twice instead of completing
    /// passive → active → off. Deterministic — the gate, not a delay, orders the two calls.
    /// </summary>
    [Fact]
    public async Task Toggle_SecondClickWhileFirstInFlight_IsIgnoredAndDoesNotRepeatTransition()
    {
        var host = new RecordingNotificationToggleHost { Mode = "passive", HoldFirstResponse = true };

        // First toggle parks inside the PATCH until we release it.
        var first = ComposerChat.ToggleActiveNotificationsCoreAsync(host);
        await host.PatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Second click lands while the first request is still outstanding.
        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);

        // It must have been dropped: still exactly one request, still the pre-toggle mode.
        Assert.Equal(["active"], host.PatchedModes);
        Assert.Equal("passive", host.Mode);

        host.ReleasePatch();
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Only the FIRST transition was persisted — "active" was never sent twice.
        Assert.Equal(["active"], host.PatchedModes);
        Assert.Equal("active", host.Mode);

        // And the next click continues the cycle instead of repeating it.
        await ComposerChat.ToggleActiveNotificationsCoreAsync(host);
        Assert.Equal(["active", "off"], host.PatchedModes);
        Assert.Equal("off", host.Mode);
    }

    // ── ComposerChat: markup wiring (removal-proof) ─────────────────────────

    [Fact]
    public void ComposerChat_Markup_ContainsToggleButton()
    {
        var source = ReadComposerChatSource();
        Assert.Contains("ToggleActiveNotifications", source);
        Assert.Contains("chat-action-btn", source);
    }

    /// <summary>
    /// The button's disabled expression must consult the full guard, not just streaming —
    /// otherwise an unloaded mode or an in-flight toggle would still be clickable.
    /// </summary>
    [Fact]
    public void ComposerChat_Markup_ToggleButtonUsesFullGuard()
    {
        var source = ReadComposerChatSource();
        Assert.Contains(
            "disabled=\"@(!CanToggleActiveNotifications(_activeNotifMode, Composer.IsStreaming, _notifTogglePending))\"",
            source);
    }

    [Fact]
    public void ComposerChat_Markup_ContainsRestartIndicator()
    {
        var source = ReadComposerChatSource();
        Assert.Contains("_restartRequiredForNotifications", source);
        Assert.Contains("Restart required", source);
    }

    [Fact]
    public void ComposerChat_Markup_ContainsToggleErrorDisplay()
    {
        var source = ReadComposerChatSource();
        Assert.Contains("_notifToggleError", source);
        Assert.Contains("chat-attach-error", source);
    }

    /// <summary>
    /// The config GET is independent of the Composer agent connection, so the load must not sit
    /// inside the <c>if (Composer.IsConnected)</c> block.
    /// </summary>
    [Fact]
    public void ComposerChat_Init_LoadsModeOutsideConnectedGuard()
    {
        var source = ReadComposerChatSource();
        var method = ExtractMethodSource(source, "protected override async Task OnInitializedAsync()");

        var connectedIndex = method.IndexOf("if (Composer.IsConnected)", StringComparison.Ordinal);
        Assert.True(connectedIndex >= 0, "OnInitializedAsync must still guard the agent-dependent loads");

        // The connected block ends at its closing brace; the load must come after it.
        var blockStart = method.IndexOf('{', connectedIndex);
        var depth = 0;
        var blockEnd = -1;
        for (var i = blockStart; i < method.Length; i++)
        {
            if (method[i] == '{') depth++;
            else if (method[i] == '}')
            {
                depth--;
                if (depth == 0) { blockEnd = i; break; }
            }
        }
        Assert.True(blockEnd > 0, "Could not locate the end of the IsConnected block");

        var loadIndex = method.IndexOf("LoadActiveNotificationsModeAsync", StringComparison.Ordinal);
        Assert.True(loadIndex > blockEnd,
            "LoadActiveNotificationsModeAsync must run outside the Composer.IsConnected block");
    }

    [Fact]
    public void ComposerChat_LoadMode_FailureLeavesModeUnloaded()
    {
        var source = ReadComposerChatSource();
        var method = ExtractMethodSource(source, "private async Task LoadActiveNotificationsModeAsync()");
        Assert.Contains("NormalizeLoadedNotificationsMode", method);
        Assert.Contains("/api/config/composer", method);
        // The catch must clear the mode rather than leave a fabricated default in place.
        var catchIndex = method.IndexOf("catch", StringComparison.Ordinal);
        Assert.True(catchIndex >= 0, "Load must handle a failing GET");
        Assert.Contains("_activeNotifMode = null", method.Substring(catchIndex));
    }

    // ── Configuration: event notifications section markup ───────────────────

    [Fact]
    public void Configuration_Markup_ContainsEventNotificationsSection()
    {
        var source = ReadConfigurationSource();
        Assert.Contains("Event Notifications", source);
        Assert.Contains("_composerNotifMode", source);
        Assert.Contains("_composerNotifThrottle", source);
        Assert.Contains("_composerNotifActiveEvents", source);
    }

    [Fact]
    public void Configuration_Markup_ContainsModeDropdownOptions()
    {
        var source = ReadConfigurationSource();
        Assert.Contains("value=\"passive\"", source);
        Assert.Contains("value=\"active\"", source);
        Assert.Contains("value=\"off\"", source);
        Assert.Contains(">Passive<", source);
        Assert.Contains(">Active<", source);
        Assert.Contains(">Off<", source);
    }

    [Fact]
    public void Configuration_Markup_ContainsThrottleInputWithRange()
    {
        var source = ReadConfigurationSource();
        Assert.Contains("min=\"1\"", source);
        Assert.Contains("max=\"300\"", source);
        Assert.Contains("_composerNotifThrottle", source);
    }

    [Fact]
    public void Configuration_Markup_ContainsNineEventCheckboxes()
    {
        var source = ReadConfigurationSource();
        var block = ExtractActiveEventsBlock(source);

        // Each event must have a REAL checkbox input in the active-events markup block:
        // a type="checkbox" input whose @onchange handler calls
        // ToggleComposerNotifEvent("<name>", ...) with the correct snake_case name,
        // plus a visible label. The checked expression binds to the active-events set
        // (not a hardcoded value).
        var events = new (string Name, string Label)[]
        {
            ("goal_completed", "Goal Completed"),
            ("goal_failed", "Goal Failed"),
            ("ci_failed", "CI Failed"),
            ("issue_raised", "Issue Raised"),
            ("package_published", "Package Published"),
            ("ci_succeeded", "CI Succeeded"),
            ("release_completed", "Release Completed"),
            ("goal_dispatched", "Goal Dispatched"),
            ("issue_resolved", "Issue Resolved"),
        };

        foreach (var (name, label) in events)
        {
            var checkedMarker = $"checked=\"@_composerNotifActiveEvents.Contains(\"{name}\")\"";
            var labelElement = ExtractCheckboxLabel(block, checkedMarker);

            Assert.Contains("<input type=\"checkbox\"", labelElement);
            Assert.Contains(
                $"@onchange='(e) => ToggleComposerNotifEvent(\"{name}\", (bool)(e.Value ?? false))'",
                labelElement);
            Assert.Contains(label, labelElement);
        }
    }

    // ── Configuration: active-event presets ─────────────────────────────────

    /// <summary>
    /// The Autopilot preset must select exactly the 9 recognized active events in canonical
    /// snake_case order.
    /// </summary>
    [Fact]
    public void ActiveEventPresets_Autopilot_SelectsExactlyAllNine()
    {
        Assert.Equal(
            ["goal_completed", "goal_failed", "ci_failed", "issue_raised", "package_published",
             "ci_succeeded", "release_completed", "goal_dispatched", "issue_resolved"],
            CopilotHive.Components.Pages.Configuration.ActiveEventPresets.AutopilotEvents);
    }

    /// <summary>
    /// The Normal preset must select exactly the 4 default events.
    /// </summary>
    [Fact]
    public void ActiveEventPresets_Normal_SelectsExactlyTheFourDefaults()
    {
        Assert.Equal(
            ["goal_completed", "goal_failed", "ci_failed", "issue_raised"],
            CopilotHive.Components.Pages.Configuration.ActiveEventPresets.NormalEvents);
    }

    /// <summary>
    /// The preset buttons must exist as REAL <c>&lt;button&gt;</c> elements in the active-events
    /// markup block, each with an <c>@onclick</c> handler that invokes
    /// <c>ApplyActiveEventPreset</c> with the correct preset member. Scoped to the
    /// markup block so comments or the helper declarations alone cannot satisfy the assertions.
    /// </summary>
    [Fact]
    public void Configuration_Markup_PresetButtonsCallHelpers()
    {
        var source = ReadConfigurationSource();
        var block = ExtractActiveEventsBlock(source);

        var autopilotIndex = block.IndexOf(
            "@onclick=\"() => ApplyActiveEventPreset(ActiveEventPresets.AutopilotEvents)\"",
            StringComparison.Ordinal);
        Assert.True(autopilotIndex >= 0,
            "Autopilot button @onclick handler invoking ActiveEventPresets.AutopilotEvents not found in the active-events markup");
        var autopilotButton = ExtractButtonElement(block, autopilotIndex);
        Assert.Contains("<button", autopilotButton);
        Assert.Contains("Autopilot", autopilotButton);

        var normalIndex = block.IndexOf(
            "@onclick=\"() => ApplyActiveEventPreset(ActiveEventPresets.NormalEvents)\"",
            StringComparison.Ordinal);
        Assert.True(normalIndex >= 0,
            "Normal button @onclick handler invoking ActiveEventPresets.NormalEvents not found in the active-events markup");
        var normalButton = ExtractButtonElement(block, normalIndex);
        Assert.Contains("<button", normalButton);
        Assert.Contains("Normal", normalButton);
    }

    /// <summary>
    /// Each new event checkbox must bind its checked state to the active-events set (so an
    /// unselected event renders unchecked), and the not-configured default path must load only
    /// the 4 defaults — leaving the 4 new events unchecked.
    /// </summary>
    [Fact]
    public void Configuration_Markup_NewEventCheckboxesDefaultUnchecked()
    {
        var source = ReadConfigurationSource();
        var block = ExtractActiveEventsBlock(source);

        // Each new checkbox's checked expression must consult the active-events set — never a
        // hardcoded true. Scoped to the actual checkbox label elements.
        foreach (var name in new[] { "ci_succeeded", "release_completed", "goal_dispatched", "issue_resolved" })
        {
            var checkedMarker = $"checked=\"@_composerNotifActiveEvents.Contains(\"{name}\")\"";
            var labelElement = ExtractCheckboxLabel(block, checkedMarker);
            Assert.Contains("<input type=\"checkbox\"", labelElement);
            Assert.DoesNotContain("checked=\"true\"", labelElement);
        }

        // The not-configured default load must populate only the 4 defaults, never the new events.
        var method = ExtractMethodSource(source, "private async Task LoadComposerAsync()");
        var defaultsStart = method.IndexOf("_composerNotifActiveEvents.Add(\"goal_completed\")", StringComparison.Ordinal);
        Assert.True(defaultsStart >= 0, "Default load must seed goal_completed");
        var defaultsBody = method.Substring(defaultsStart);
        Assert.Contains("_composerNotifActiveEvents.Add(\"goal_failed\")", defaultsBody);
        Assert.Contains("_composerNotifActiveEvents.Add(\"ci_failed\")", defaultsBody);
        Assert.Contains("_composerNotifActiveEvents.Add(\"issue_raised\")", defaultsBody);
        Assert.DoesNotContain("_composerNotifActiveEvents.Add(\"ci_succeeded\")", defaultsBody);
        Assert.DoesNotContain("_composerNotifActiveEvents.Add(\"release_completed\")", defaultsBody);
        Assert.DoesNotContain("_composerNotifActiveEvents.Add(\"goal_dispatched\")", defaultsBody);
        Assert.DoesNotContain("_composerNotifActiveEvents.Add(\"issue_resolved\")", defaultsBody);
    }

    [Fact]
    public void Configuration_Markup_AtLeastOneEventRequired()
    {
        var source = ReadConfigurationSource();
        Assert.Contains("At least one active event is required", source);
        Assert.Contains("_composerNotifActiveEvents.Count == 0", source);
    }

    [Fact]
    public void Configuration_Dto_ContainsEventNotificationsNestedRecord()
    {
        var source = ReadConfigurationSource();
        Assert.Contains("record EventNotificationsDto", source);
        Assert.Contains("JsonPropertyName(\"mode\")", source);
        Assert.Contains("JsonPropertyName(\"activeEvents\")", source);
        Assert.Contains("JsonPropertyName(\"throttleSeconds\")", source);
        Assert.Contains("JsonPropertyName(\"eventNotifications\")", source);
    }

    [Fact]
    public void Configuration_LoadComposer_LoadsEffectiveValues()
    {
        var source = ReadConfigurationSource();
        var method = ExtractMethodSource(source, "private async Task LoadComposerAsync()");
        Assert.Contains("EventNotifications?.Mode", method);
        Assert.Contains("EventNotifications?.ThrottleSeconds", method);
        Assert.Contains("EventNotifications?.ActiveEvents", method);
    }

    [Fact]
    public void Configuration_SaveComposer_IncludesEventNotificationFields()
    {
        var source = ReadConfigurationSource();
        var method = ExtractMethodSource(source, "private async Task SaveComposerAsync()");
        Assert.Contains("eventNotificationsMode", method);
        Assert.Contains("eventNotificationsActiveEvents", method);
        Assert.Contains("eventNotificationsThrottleSeconds", method);
        Assert.Contains("PatchAsync", method);
        Assert.Contains("/api/config/composer", method);
    }

    [Fact]
    public void Configuration_SaveComposer_GuardsZeroEvents()
    {
        var source = ReadConfigurationSource();
        var method = ExtractMethodSource(source, "private async Task SaveComposerAsync()");
        Assert.Contains("_composerNotifActiveEvents.Count == 0", method);
        Assert.Contains("At least one active event must be selected", method);
    }

    [Fact]
    public void Configuration_SaveComposer_SuccessReloadsState()
    {
        var source = ReadConfigurationSource();
        var method = ExtractMethodSource(source, "private async Task SaveComposerAsync()");
        Assert.Contains("LoadComposerAsync()", method);
        Assert.Contains("IsSuccessStatusCode", method);
    }

    [Fact]
    public void Configuration_SaveComposer_FailureLeavesStateUnchanged()
    {
        var source = ReadConfigurationSource();
        var method = ExtractMethodSource(source, "private async Task SaveComposerAsync()");
        // The success branch calls LoadComposerAsync; the failure branch only sets the error.
        var successIndex = method.IndexOf("LoadComposerAsync()", StringComparison.Ordinal);
        Assert.True(successIndex >= 0, "Success branch must reload composer state");
        var elseIndex = method.IndexOf("else", successIndex, StringComparison.Ordinal);
        Assert.True(elseIndex >= 0, "Failure branch must exist");
        var elseBody = method.Substring(elseIndex);
        Assert.DoesNotContain("LoadComposerAsync()", elseBody);
        Assert.Contains("_composerSaveError", elseBody);
    }
}

/// <summary>
/// Recording <see cref="ComposerChat.INotificationToggleHost"/> that drives the PRODUCTION toggle
/// body (<see cref="ComposerChat.ToggleActiveNotificationsCoreAsync"/>) without a Blazor circuit.
/// It captures every mode actually PATCHed, the in-flight claim observed at request time, and can
/// hold the response open so an overlapping click can be issued deterministically.
/// </summary>
internal sealed class RecordingNotificationToggleHost : ComposerChat.INotificationToggleHost
{
    private readonly List<string> _patchedModes = [];
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public string? Mode { get; set; }

    /// <inheritdoc />
    public bool TogglePending { get; set; }

    /// <inheritdoc />
    public bool RestartRequired { get; set; }

    /// <inheritdoc />
    public string? ToggleError { get; set; }

    /// <summary>Backing value for <see cref="IsStreaming"/>, settable by tests.</summary>
    public bool IsStreamingValue { get; set; }

    /// <inheritdoc />
    public bool IsStreaming => IsStreamingValue;

    /// <summary>Status code the fake PATCH responds with.</summary>
    public System.Net.HttpStatusCode ResponseStatus { get; set; } = System.Net.HttpStatusCode.OK;

    /// <summary>Body the fake PATCH responds with.</summary>
    public string ResponseBody { get; set; } = "{\"saved\":true}";

    /// <summary>When set, <see cref="PatchModeAsync"/> throws it instead of responding.</summary>
    public Exception? PatchFailure { get; set; }

    /// <summary>
    /// When true, the FIRST PATCH parks until <see cref="ReleasePatch"/> is called. Off by
    /// default so ordinary tests complete synchronously; the overlapping-click test turns it on.
    /// </summary>
    public bool HoldFirstResponse { get; set; }

    /// <summary>Completes as soon as the first PATCH has been entered.</summary>
    public TaskCompletionSource PatchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Every mode handed to the PATCH, in order.</summary>
    public IReadOnlyList<string> PatchedModes => _patchedModes;

    /// <summary>The in-flight claim observed at the moment the first PATCH was issued.</summary>
    public bool TogglePendingWhenPatched { get; private set; }

    /// <summary>How many re-renders the toggle requested.</summary>
    public int NotifyCount { get; private set; }

    /// <summary>Unblocks a parked PATCH.</summary>
    public void ReleasePatch() => _gate.TrySetResult();

    /// <inheritdoc />
    public async Task<HttpResponseMessage> PatchModeAsync(string mode)
    {
        var isFirst = _patchedModes.Count == 0;

        // Captured BEFORE anything else so the test can prove the claim precedes the request.
        if (isFirst)
            TogglePendingWhenPatched = TogglePending;

        _patchedModes.Add(mode);
        PatchEntered.TrySetResult();

        if (PatchFailure is not null)
            throw PatchFailure;

        // Only the first call parks, and only when a test explicitly asked to hold it.
        if (isFirst && HoldFirstResponse)
            await _gate.Task;

        return new HttpResponseMessage(ResponseStatus)
        {
            Content = new StringContent(ResponseBody, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    /// <inheritdoc />
    public void Notify() => NotifyCount++;
}
