using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// What the settings switches actually do.
//
// SettingsWindowSmokeTest next door proves the page builds; this proves the rows
// behave. It goes at the handlers rather than at the controls on purpose, for a
// reason that file's own comment sets up: a row's toggle is a ToggleSwitch whose
// template is *borrowed from Fluent at runtime* and falls back to a CheckBox when
// that fails, so which control a row holds is a property of the theme rather than
// of the setting. Synthesizing a click would be testing the borrow.
//
// The window is constructed through its private constructor, exactly as the smoke
// test does, and never shown and never closed — closing a headless Window here
// corrupts a process-wide Avalonia font cache and takes every later test in the
// assembly with it. That is documented at length next door; this file inherits
// the rule rather than rediscovering it.
//
// Two things are being asserted at once, and both matter. Each handler writes the
// setting its row claims to write — a copy-paste error between two adjacent rows
// is the likeliest bug on a page with twenty of them, and it is invisible on
// screen because both switches still move. And every handler rebuilds the page
// afterwards, so each case also builds the whole visual tree again with the new
// setting in force, which is the only way the rows that only appear when
// something is switched on get built at all.
[Collection("Settings")]
public class SettingsWindowRowTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    // Drives one switch both ways and reads the setting back each time, so a
    // handler that ignored its argument and always wrote `true` fails.
    private static void Toggles(
        Action<SettingsWindow, bool> handler, Func<bool> read)
    {
        var window = NewWindow();

        handler(window, true);
        Assert.True(read(), "switching on did not write the setting");

        handler(window, false);
        Assert.False(read(), "switching off did not write the setting");

        handler(window, true);
        Assert.True(read(), "switching back on did not write the setting");
    }

    [AvaloniaFact]
    public void TheClaudeCodeSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnClaudeCodeEnabledToggled(v), () => ClaudeBuddySettings.ClaudeCodeEnabled);

    [AvaloniaFact]
    public void TheClaudeCodeChatSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnClaudeCodeChatToggled(v), () => ClaudeBuddySettings.ClaudeCodeChatEnabled);

    [AvaloniaFact]
    public void TheClaudeCodeReplySwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnClaudeCodeReplyToggled(v), () => ClaudeBuddySettings.ClaudeCodeReplyEnabled);

    [AvaloniaFact]
    public void TheCodexSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnCodexEnabledToggled(v), () => ClaudeBuddySettings.CodexEnabled);

    [AvaloniaFact]
    public void TheCodexChatSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnCodexChatToggled(v), () => ClaudeBuddySettings.CodexChatEnabled);

    [AvaloniaFact]
    public void TheCodexReplySwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnCodexReplyToggled(v), () => ClaudeBuddySettings.CodexReplyEnabled);

    [AvaloniaFact]
    public void TheHeartbeatSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnOpenClawHeartbeatsToggled(v),
        () => ClaudeBuddySettings.OpenClawShowHeartbeats);

    // The colour switch deliberately does *not* re-wire anything, and that is
    // worth a test of its own rather than only a comment. An earlier version
    // baked a flag into the hook command, so every toggle rewrote Codex's
    // hooks.json and cost the user their hook trust; the hooks now read a marker
    // file the scan reconciles instead. So this handler must stay a plain setting
    // write — if it ever starts shelling out, this test will be the thing that
    // hangs, which is the right place to find out.
    [AvaloniaFact]
    public void TheAutoColourSwitchOnlyWritesItsSetting() => Toggles(
        (w, v) => w.OnAutoColorToggled(v), () => ClaudeBuddySettings.AutoColorSessions);

    // The two download-backed switches, driven only in the direction that cannot
    // start a download: switching off, and switching on when the model is already
    // present. Nothing here fetches 300MB, and the assertion is the same one —
    // the row writes its own setting.
    [AvaloniaFact]
    public void TheNeuralVoiceSwitchWritesItsSettingWhenSwitchedOff()
    {
        var window = NewWindow();

        window.OnNeuralVoiceToggled(false);

        Assert.False(ClaudeBuddySettings.NeuralVoiceEnabled);
    }

    [AvaloniaFact]
    public void TheVoiceInputSwitchWritesItsSettingWhenSwitchedOff()
    {
        var window = NewWindow();

        window.OnVoiceInputToggled(false);

        Assert.False(ClaudeBuddySettings.VoiceInputEnabled);
    }

    // Every switch rebuilds the page, and the page is different depending on what
    // is switched on — whole sections appear only when their CLI is enabled. This
    // walks a realistic sequence rather than one toggle, so the rebuild that
    // happens with each combination in force is the one being exercised.
    [AvaloniaFact]
    public void RebuildingWithEverySectionOnAndOffDoesNotThrow()
    {
        var window = NewWindow();

        foreach (var on in new[] { true, false, true })
        {
            window.OnClaudeCodeEnabledToggled(on);
            window.OnClaudeCodeChatToggled(on);
            window.OnClaudeCodeReplyToggled(on);
            window.OnCodexEnabledToggled(on);
            window.OnCodexChatToggled(on);
            window.OnCodexReplyToggled(on);
            window.OnOpenClawHeartbeatsToggled(on);
            window.OnAutoColorToggled(on);
        }

        // Nothing to assert beyond having got here: a rebuild that threw would
        // have taken the settings window down in the user's face, which is the
        // failure this is watching for.
        Assert.NotNull(window);
    }

    // Rebuild is called by every handler above, and directly by the window when a
    // profile changes underneath it. Called on its own here so a rebuild with no
    // preceding toggle is covered too — that is the path a profile rename takes.
    [AvaloniaFact]
    public void RebuildingOnItsOwnDoesNotThrow()
    {
        var window = NewWindow();

        window.Rebuild();
        window.Rebuild();

        Assert.NotNull(window);
    }

    // The chat and reply switches are independent: someone can reasonably want to
    // read a session and never type into it. Adjacent rows writing one another's
    // setting is the bug this rules out, and it is the one a screenshot cannot
    // show.
    [AvaloniaFact]
    public void ChatAndReplyAreIndependentPerCli()
    {
        var window = NewWindow();

        window.OnClaudeCodeChatToggled(true);
        window.OnClaudeCodeReplyToggled(false);
        window.OnCodexChatToggled(false);
        window.OnCodexReplyToggled(true);

        Assert.True(ClaudeBuddySettings.ClaudeCodeChatEnabled);
        Assert.False(ClaudeBuddySettings.ClaudeCodeReplyEnabled);
        Assert.False(ClaudeBuddySettings.CodexChatEnabled);
        Assert.True(ClaudeBuddySettings.CodexReplyEnabled);
    }

    // ...and across the two CLIs, which have their own pair each rather than one
    // shared "local CLI" setting.
    [AvaloniaFact]
    public void TheTwoClisDoNotShareTheirSwitches()
    {
        var window = NewWindow();

        window.OnClaudeCodeEnabledToggled(true);
        window.OnCodexEnabledToggled(false);

        Assert.True(ClaudeBuddySettings.ClaudeCodeEnabled);
        Assert.False(ClaudeBuddySettings.CodexEnabled);
    }
}
