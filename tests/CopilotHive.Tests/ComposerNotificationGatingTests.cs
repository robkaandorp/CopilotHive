using CopilotHive.Security;
using CopilotHive.Services;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE NOTIFICATION GATING CONTRACT: the event-notification mode is a GLOBAL (installation-wide)
/// setting, so a NON-ADMIN must be offered neither the editable control nor a personal-sounding
/// label — only a clear "this is global, an administrator changes it" notice.
/// <para>
/// The repository does not use bUnit, so the established strategy (see
/// <see cref="ActiveModeConfigUiTests"/>) is used: the REAL Razor markup is read and the admin and
/// non-admin branches are extracted and asserted SEPARATELY, so an assertion can never be
/// satisfied by text in the wrong branch.
/// </para>
/// <para>
/// The behavioural half of the contract — that the gate really follows CURRENT stored state — is
/// covered by the <c>ComponentActingUserTests</c> class; this class pins the RENDERING half.
/// </para>
/// </summary>
public sealed class ComposerNotificationGatingTests
{
    // ── source-file access ───────────────────────────────────────────────────

    private static string ReadRazorSource(string relativePath)
    {
        var repoRoot = TestRepoRoot.Find();
        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", relativePath);
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        return File.ReadAllText(razorPath).ReplaceLineEndings("\n");
    }

    private static string ReadComposerChatSource() => ReadRazorSource("ComposerChat.razor");

    private static string ReadConfigurationSource() => ReadRazorSource("Configuration.razor");

    /// <summary>
    /// Extracts the body of the Razor <c>@if (...)</c> block whose condition contains
    /// <paramref name="condition"/> — from the opening brace to its matching close brace.
    /// <para>
    /// The scan counts braces, so a nested Razor <c>@if</c> inside the block cannot end the
    /// extraction early: the non-admin branch of the Configuration page contains no nested block,
    /// but the admin branch does (the "no active events" hint), and both must be extracted whole.
    /// </para>
    /// </summary>
    private static string ExtractIfBlock(string source, string condition)
    {
        var conditionIndex = source.IndexOf(condition, StringComparison.Ordinal);
        Assert.True(conditionIndex >= 0, $"The condition '{condition}' was not found in the source.");

        var braceStart = source.IndexOf('{', conditionIndex);
        Assert.True(braceStart >= 0, $"The opening brace of '{condition}' was not found.");

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(braceStart + 1, i - braceStart - 1);
            }
        }

        Assert.Fail($"Could not find the matching '}}' for '{condition}'.");
        return string.Empty; // unreachable
    }

    // ── Configuration.razor ──────────────────────────────────────────────────

    /// <summary>
    /// THE ADMIN BRANCH OF THE CONFIGURATION PAGE IS UNCHANGED IN BEHAVIOUR: an admin still gets
    /// the full "Event Notifications" editor — the mode select, the active-event checkboxes and the
    /// preset buttons — and the Save button is enabled exactly as before (its <c>disabled</c>
    /// expression reduces to the pre-slice <c>_composerSaving</c> for an admin).
    /// </summary>
    [Fact]
    public void TheConfigurationAdminBranch_StillRendersTheFullNotificationEditor()
    {
        var source = ReadConfigurationSource();
        var adminBlock = ExtractIfBlock(source, "_composerAdminAllowed");

        // The editor itself is present for an admin.
        Assert.Contains("Event Notifications", adminBlock, StringComparison.Ordinal);
        Assert.Contains("Global / system-wide setting", adminBlock, StringComparison.Ordinal);
        Assert.Contains("<select @bind=\"_composerNotifMode\"", adminBlock, StringComparison.Ordinal);
        Assert.Contains("Active Events", adminBlock, StringComparison.Ordinal);
        Assert.Contains("ApplyActiveEventPreset(ActiveEventPresets.AutopilotEvents)", adminBlock, StringComparison.Ordinal);
        Assert.Contains("ApplyActiveEventPreset(ActiveEventPresets.NormalEvents)", adminBlock, StringComparison.Ordinal);

        // ...and the non-admin prose is NOT in the admin branch.
        Assert.DoesNotContain("Only an administrator can change", adminBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrator permission required", adminBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE NON-ADMIN BRANCH OF THE CONFIGURATION PAGE IS HIDDEN AND LABELLED: it renders NO editable
    /// control (no mode select, no active-event checkboxes, no preset buttons) — only the global
    /// label and the "ask an administrator" notice.
    /// </summary>
    [Fact]
    public void TheConfigurationNonAdminBranch_HidesTheControls_AndLabelsThemGlobal()
    {
        var source = ReadConfigurationSource();

        // The else-branch is the one immediately AFTER the admin block.
        var adminBlockStart = source.IndexOf("@if (_composerAdminAllowed)", StringComparison.Ordinal);
        Assert.True(adminBlockStart >= 0, "The admin gate was not found in Configuration.razor.");

        var elseIndex = source.IndexOf("else", adminBlockStart, StringComparison.Ordinal);
        Assert.True(elseIndex >= 0, "The non-admin else-branch was not found in Configuration.razor.");

        var nonAdminBlock = ExtractIfBlock(source[elseIndex..], "else");

        // The global label is present.
        Assert.Contains("Global / system-wide setting", nonAdminBlock, StringComparison.Ordinal);
        Assert.Contains("applies to the whole installation, not to your personal session",
            nonAdminBlock, StringComparison.Ordinal);

        // The admin-only notice is present.
        Assert.Contains("Only an administrator can change", nonAdminBlock, StringComparison.Ordinal);

        // ...and NO editable control is offered.
        Assert.DoesNotContain("<select", nonAdminBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyActiveEventPreset", nonAdminBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"checkbox\"", nonAdminBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONFIGURATION PAGE'S SAVE BUTTON IS DISABLED FOR A NON-ADMIN: the whole Composer section
    /// (max steps AND the global notification mode) is a global write, so a non-admin is not
    /// offered a Save that could only be refused — and the reason is stated next to the button.
    /// </summary>
    [Fact]
    public void TheConfigurationSaveButton_IsDisabledForANonAdmin_AndExplainsWhy()
    {
        var source = ReadConfigurationSource();

        Assert.Contains(
            "disabled=\"@(_composerSaving || !_composerAdminAllowed)\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Administrator permission required to change global Composer settings.",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONFIGURATION ADMIN GATE IS DERIVED FROM THE CIRCUIT IDENTITY: the page asks the scoped
    /// accessor for <see cref="HiveCapability.GlobalAdmin"/> through the ONE helper, and the answer
    /// feeds the rendering gate. Deleting the question (or hardcoding the flag to true) fails here.
    /// </summary>
    [Fact]
    public void TheConfigurationAdminGate_IsDerivedFromTheCircuitIdentity()
    {
        var source = ReadConfigurationSource();

        Assert.Contains("ComponentActingUser.IsAllowedAsync(", source, StringComparison.Ordinal);
        Assert.Contains("HiveCapability.GlobalAdmin", source, StringComparison.Ordinal);
        Assert.Contains("CircuitPrincipal", source, StringComparison.Ordinal);

        // The gate is assigned from that question — not hardcoded.
        Assert.Contains("_composerAdminAllowed = await ComponentActingUser.IsAllowedAsync(", source,
            StringComparison.Ordinal);
    }

    // ── ComposerChat.razor ───────────────────────────────────────────────────

    /// <summary>
    /// THE ADMIN BRANCH OF THE COMPOSER-CHAT TOGGLE IS UNCHANGED: an admin still gets the real
    /// toggle BUTTON (with its own disabled expression, exactly as before).
    /// </summary>
    [Fact]
    public void TheComposerChatAdminBranch_StillRendersTheRealToggleButton()
    {
        var source = ReadComposerChatSource();
        var adminBlock = ExtractIfBlock(source, "_notifToggleAdminAllowed");

        Assert.Contains("<button @onclick=\"ToggleActiveNotifications\"", adminBlock, StringComparison.Ordinal);
        Assert.Contains(
            "disabled=\"@(!CanToggleActiveNotifications(_activeNotifMode, Composer.IsStreaming, _notifTogglePending))\"",
            adminBlock,
            StringComparison.Ordinal);
        Assert.Contains("@ActiveNotificationsButtonText", adminBlock, StringComparison.Ordinal);

        // The non-admin prose is NOT in the admin branch.
        Assert.DoesNotContain("global setting", adminBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE NON-ADMIN BRANCH OF THE COMPOSER-CHAT TOGGLE IS HIDDEN AND LABELLED: no button at all —
    /// only a hint that names the setting as GLOBAL and says an administrator changes it. The
    /// label must not read as a personal preference.
    /// </summary>
    [Fact]
    public void TheComposerChatNonAdminBranch_HidesTheButton_AndLabelsItGlobal()
    {
        var source = ReadComposerChatSource();

        var adminBlockStart = source.IndexOf("@if (_notifToggleAdminAllowed)", StringComparison.Ordinal);
        Assert.True(adminBlockStart >= 0, "The notification gate was not found in ComposerChat.razor.");

        var elseIndex = source.IndexOf("else", adminBlockStart, StringComparison.Ordinal);
        Assert.True(elseIndex >= 0, "The non-admin else-branch was not found in ComposerChat.razor.");

        var nonAdminBlock = ExtractIfBlock(source[elseIndex..], "else");

        // The global label is present.
        Assert.Contains("Notifications (global setting)", nonAdminBlock, StringComparison.Ordinal);
        Assert.Contains("Global / system-wide setting", nonAdminBlock, StringComparison.Ordinal);
        Assert.Contains("only an administrator can change it", nonAdminBlock, StringComparison.Ordinal);

        // ...and NO button is offered.
        Assert.DoesNotContain("<button", nonAdminBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleActiveNotifications", nonAdminBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE COMPOSER-CHAT GATE IS DERIVED FROM THE CIRCUIT IDENTITY: the page asks the scoped
    /// accessor for <see cref="HiveCapability.GlobalAdmin"/> through the ONE helper.
    /// </summary>
    [Fact]
    public void TheComposerChatAdminGate_IsDerivedFromTheCircuitIdentity()
    {
        var source = ReadComposerChatSource();

        Assert.Contains("ComponentActingUser.IsAllowedAsync(", source, StringComparison.Ordinal);
        Assert.Contains("HiveCapability.GlobalAdmin", source, StringComparison.Ordinal);
        Assert.Contains("CircuitPrincipal", source, StringComparison.Ordinal);

        // The gate is assigned from that question — not hardcoded.
        Assert.Contains("_notifToggleAdminAllowed = await ComponentActingUser.IsAllowedAsync(", source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A FAILURE TO ANSWER UNDER-RENDERS: the gate's question is wrapped so that an exception
    /// leaves the flag FALSE (the control stays hidden). A page that let an exception leave the flag
    /// at its default might render an editable control for an unverified caller.
    /// </summary>
    [Fact]
    public void AFailureToVerifyTheGate_LeavesTheControlHidden()
    {
        var composerChat = ReadComposerChatSource();
        Assert.Contains("_notifToggleAdminAllowed = false;", composerChat, StringComparison.Ordinal);

        var configuration = ReadConfigurationSource();
        Assert.Contains("_composerAdminAllowed = false;", configuration, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE GATE IS RENDERING-ONLY: the facade still refuses the write independently, so a stale
    /// flag can only UNDER-render, never over-grant. The non-admin path therefore still reaches
    /// the facade — the gate does not replace the authorization.
    /// </summary>
    [Fact]
    public void TheGateIsRenderingOnly_TheFacadeStillRefusesTheWrite()
    {
        var source = ReadComposerChatSource();

        // The notification write still goes through the facade with the circuit identity, so the
        // facade re-resolves and refuses a non-admin regardless of the rendering gate.
        Assert.Contains("owner.ConfigFacade.SaveComposerAsync(", source, StringComparison.Ordinal);
        Assert.Contains("await owner.ActingUserAsync(),", source, StringComparison.Ordinal);

        var configuration = ReadConfigurationSource();
        Assert.Contains("ConfigFacade.SaveComposerAsync(", configuration, StringComparison.Ordinal);
        Assert.Contains(
            "await ComponentActingUser.AcquireAsync(AuthenticationState, CircuitPrincipal)",
            configuration,
            StringComparison.Ordinal);
    }
}
