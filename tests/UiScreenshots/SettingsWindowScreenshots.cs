using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// Matches tests/UiTests/SettingsWindowSmokeTest.cs's one scenario. Same
// private-constructor-via-reflection seam, same reason (Toggle() makes real
// OS calls this project has no business making headless), same "never
// closed" rule (see that test's own comment on the FontManager corruption a
// stray Close() caused once, in this exact suite).
//
// In the Settings collection because the Remote Control scenario below flips
// a process-wide setting before constructing its window — the same reason
// OrbClusterScreenshots is there.
[Collection("Settings")]
public class SettingsWindowScreenshots
{
    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        ScreenshotHelper.Capture(window, "settings-window-constructs-headless.png");
    }

    // The Claude Desktop group, which CB-4 added a row to: the switch that
    // decides whether Claude Buddy claims Claude Desktop's URL schemes.
    //
    // Captured on its own rather than trusting the whole-window shot above,
    // because that one renders at the window's own height and this group sits
    // below the fold of a scrolling settings page — it would not appear at all.
    //
    // The row is macOS-only by design (the collision it works around is caused
    // by the tinted clone bundles, which have no Windows analogue), so the two
    // runners' captures are *expected* to differ here. That is the point of
    // having it on both: a reviewer comparing the rids can see the gate is
    // deliberate rather than a macOS-only implementation that forgot Windows.
    [AvaloniaFact]
    public void ClaudeDesktopGroupShowsTheUrlRoutingRowOnMacOs()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        // Shown and flushed so the settings page is measured and arranged —
        // the stack panels here are not virtualized, so every row is laid out
        // even though most of them are outside the viewport.
        window.Show();
        ScreenshotHelper.Flush();

        // Anchor on the row that exists on both platforms, so the capture is
        // taken from the same place whichever runner it is on.
        var anchor = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == "Tint the active window");

        Assert.NotNull(anchor);

        // Up to the card that holds the whole group rather than the single
        // row, so the new switch is in frame beneath it on macOS.
        var card = anchor!.GetLogicalAncestors().OfType<Control>()
            .FirstOrDefault(control => control.Bounds.Height > 60 && control.Bounds.Width > 200)
            ?? (Control)anchor;

        ScreenshotHelper.CaptureControl(card, "settings-claude-desktop-group.png");
    }

    // The Other machines group with the feature switched on, so the CB-18 row —
    // the switch that starts the relay at launch for a machine that serves its
    // sessions unattended — is in frame. Also below the fold of the whole-window
    // shot, for the same reason as the Claude Desktop group above.
    //
    // macOS-only in a stronger sense than that group: on Windows the section is
    // a single "macOS-only for now" note with no rows at all (the relay runs in
    // tmux), so there is no anchor to capture and the scenario returns rather
    // than asserting one. The Windows leg's coverage of the section is
    // RemoteControlUnsupportedRows' own tests.
    [AvaloniaFact]
    public void RemoteControlGroupShowsTheServeOnLaunchRow()
    {
        if (!RemoteControlBridge.IsSupported) return;

        var wasEnabled = ClaudeBuddySettings.RemoteControlEnabled;
        var wasServe = ClaudeBuddySettings.RemoteControlServeOnLaunch;
        try
        {
            ClaudeBuddySettings.RemoteControlEnabled = true;
            ClaudeBuddySettings.RemoteControlServeOnLaunch = true;

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Avalonia.Controls.Window)ctor.Invoke(null);

            window.Show();
            ScreenshotHelper.Flush();

            var anchor = window.GetLogicalDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(block =>
                    block.Text == "Start the relay when Claude Buddy starts");

            Assert.NotNull(anchor);

            var card = anchor!.GetLogicalAncestors().OfType<Control>()
                .FirstOrDefault(control =>
                    control.Bounds.Height > 60 && control.Bounds.Width > 200)
                ?? (Control)anchor;

            ScreenshotHelper.CaptureControl(card, "settings-remote-control-group.png");
        }
        finally
        {
            ClaudeBuddySettings.RemoteControlServeOnLaunch = wasServe;
            ClaudeBuddySettings.RemoteControlEnabled = wasEnabled;
        }
    }

    // The direct link's card, switched on, so the pairing controls are in frame.
    //
    // **Unlike every other scenario in this file, this one has no platform
    // gate — and that is the thing to look at when comparing the two rids.**
    // The relay card above is macOS-only because it lives in tmux; this is a
    // socket, and the whole reason it uses SslStream rather than the gateway's
    // hand-rolled TLS is that it behaves identically on Windows. So the two
    // captures should show the *same* card. A Windows rid that shows a
    // "macOS-only" note here, or nothing at all, is the regression this exists
    // to make visible, and no unit test can show it.
    [AvaloniaFact]
    public void PeerLinkGroupShowsThePairingControls()
    {
        var wasEnabled = ClaudeBuddySettings.PeerLinkEnabled;
        try
        {
            ClaudeBuddySettings.PeerLinkEnabled = true;

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Avalonia.Controls.Window)ctor.Invoke(null);

            window.Show();
            ScreenshotHelper.Flush();

            var anchor = window.GetLogicalDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(block =>
                    block.Text == "Let another machine pair with this one");

            Assert.NotNull(anchor);

            var card = anchor!.GetLogicalAncestors().OfType<Control>()
                .FirstOrDefault(control =>
                    control.Bounds.Height > 60 && control.Bounds.Width > 200)
                ?? (Control)anchor;

            ScreenshotHelper.CaptureControl(card, "settings-peer-link-group.png");
        }
        finally
        {
            ClaudeBuddySettings.PeerLinkEnabled = wasEnabled;
        }
    }
}
