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
        // taken from the same place whichever runner it is on. Climb from
        // there to the group carrying the "Claude Desktop" heading rather
        // than trusting how big the ancestor measures — a 480x151 capture of
        // this same group, missing the heading and the footer note, once
        // passed a bounds-only search on develop without failing anything.
        CaptureGroup(window, "Tint the active window", "Claude Desktop",
            "settings-claude-desktop-group.png");
    }

    // The speech group, so the mode picker CB-165 added is in frame.
    //
    // Its own capture for the same reason the Claude Desktop group has one:
    // the voice rows sit below the fold of a scrolling settings page and would
    // not appear in the whole-window shot at all.
    //
    // Unlike that one, this row has no platform gate — the picker is two names
    // in a combo box and nothing behind it is OS-specific — so the two rids
    // should show the same card. A Windows rid missing the row is the
    // regression worth seeing here, and it is the failure mode the repo's
    // parity rule exists for.
    [AvaloniaFact]
    public void SpeechGroupShowsTheSpeakScopePicker()
    {
        var wasScope = ClaudeBuddySettings.SpeakScope;
        try
        {
            // Captured on the non-default mode deliberately: it shows the
            // picker holding a saved choice rather than its initial state, so
            // the capture would change if the round trip stopped working.
            ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Avalonia.Controls.Window)ctor.Invoke(null);

            window.Show();
            ScreenshotHelper.Flush();

            // The group's own heading is "Voice", not "Speech" — the
            // scenario name refers to the feature, the search has to use the
            // string actually on screen. This is the scenario that shipped
            // the worst of the two silent crops on develop: 452x96 instead
            // of the full 480x441, missing the heading and three of the four
            // rows this comment claims to capture, and still comfortably
            // past a 60x200 bounds floor. Only reading the pixels found it,
            // which is why the group is now also asserted to contain its own
            // heading rather than merely measured.
            CaptureGroup(window, "Speaks", "Voice", "settings-speak-scope.png");
        }
        finally
        {
            ClaudeBuddySettings.SpeakScope = wasScope;
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

            // Used to be found by searching for "No other machines yet"
            // rather than the group's own heading, because a bounds-based
            // search — first ancestor over 60 by 200 — landed on the whole
            // card on macOS and on one row on Windows, making the two rids
            // look like a platform gate when there isn't one. The heading
            // search below is immune to that: "Other machines" sits at the
            // top of the same SettingsSection on both platforms.
            CaptureGroup(window, "Let another machine pair with this one",
                "Other machines", "settings-peer-link-group.png");
        }
        finally
        {
            ClaudeBuddySettings.PeerLinkEnabled = wasEnabled;
        }
    }

    // The Codex group, now with the usage-orb row. Same reason as Grok: the
    // whole-window shot is the window's own height, and this section sits
    // below Claude Code.
    [AvaloniaFact]
    public void CodexGroupShowsTheUsageRow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        window.Show();
        ScreenshotHelper.Flush();

        CaptureGroup(window, "Show Codex sessions", "Codex", "settings-codex-group.png");
    }

    // The Grok Build group. Same reason the Claude Desktop group is captured
    // on its own: the whole-window shot is the window's own height, and this
    // section sits below Codex. A reviewer comparing rids should see the new
    // CLI on both, not infer it from a cropped page.
    [AvaloniaFact]
    public void GrokBuildGroupShowsTheCliRows()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        window.Show();
        ScreenshotHelper.Flush();

        CaptureGroup(window, "Show Grok Build sessions", "Grok Build",
            "settings-grok-build-group.png");
    }

    // CB-96's row, which only exists once Grok's usage orbs are already on —
    // the default capture above never shows it, and adding a UiTests scenario
    // for the toggle does not add its screenshot the way this repo's own rule
    // says it should not.
    [AvaloniaFact]
    public void GrokBuildGroupShowsTheAutoRefreshRowOnceUsageOrbsAreOn()
    {
        ClaudeBuddySettings.GrokAccountUsageEnabled = true;

        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        window.Show();
        ScreenshotHelper.Flush();

        CaptureGroup(window, "Keep Grok usage fresh automatically", "Grok Build",
            "settings-grok-auto-refresh.png");

        ClaudeBuddySettings.GrokAccountUsageEnabled = false;
    }

    // The one ancestor-climb every control-scoped scenario above shares,
    // pulled out because that is exactly where the copies used to drift:
    // two of them climbed by measuring the ancestor (first one over 60 by
    // 200), four climbed by content, and nobody noticed the two measuring
    // ancestors were silently wrong until the baseline comparison below
    // caught it. One helper means there is only one place left to drift.
    //
    // Finds the row named by `anchorText`, climbs to the nearest ancestor
    // whose descendants include a TextBlock reading `headingText`, and
    // asserts three separate things before capturing rather than one:
    // that a group was found at all, that it measures large enough to be
    // worth a screenshot, and — the assertion that actually has teeth —
    // that the control being captured still contains the heading it was
    // found by. That last check looks redundant against the search
    // predicate immediately above it, and today it is: but it is a
    // separate, independent statement that survives a future edit to the
    // search (back to a bounds guess, say) in a way a check folded into
    // the predicate would not. `settings-speak-scope.png` was 452x96 on
    // develop — comfortably past 60x200 in both dimensions, showing only
    // one row of five with no heading in frame at all — which is exactly
    // the shape of defect no size check catches and this one does.
    private static void CaptureGroup(
        Avalonia.Controls.Window window, string anchorText, string headingText, string fileName)
    {
        var anchor = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == anchorText);

        Assert.NotNull(anchor);

        var group = anchor!.GetLogicalAncestors().OfType<Control>()
            .FirstOrDefault(control => control.GetLogicalDescendants()
                .OfType<TextBlock>()
                .Any(block => block.Text == headingText));

        Assert.NotNull(group);
        AssertWorthCapturing(group!);
        AssertContainsHeading(group!, headingText);

        ScreenshotHelper.CaptureControl(group!, fileName);
    }

    // A 1x1 and a "plausible but cropped" are different failures, so this
    // stays alongside AssertContainsHeading rather than being replaced by
    // it — a control could pass the heading check and still have collapsed
    // to a sliver if the heading itself sits in a thin strip above content
    // that failed to lay out.
    private static void AssertWorthCapturing(Control control)
    {
        Assert.True(control.Bounds.Height > 60 && control.Bounds.Width > 200,
            $"capture target measured {control.Bounds.Width}x{control.Bounds.Height}");
    }

    // The structural check: a control can measure comfortably past
    // AssertWorthCapturing's floor and still be the wrong control, missing
    // the very heading a reviewer expects the capture to show — that is
    // what settings-claude-desktop-group.png (480x151, heading and footer
    // both cropped) and settings-speak-scope.png (452x96, four of five
    // rows missing) both did on develop, silently, through a green suite.
    private static void AssertContainsHeading(Control control, string headingText)
    {
        var containsHeading = control.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Any(block => block.Text == headingText);

        Assert.True(containsHeading,
            $"capture target does not contain a heading reading \"{headingText}\"");
    }
}
