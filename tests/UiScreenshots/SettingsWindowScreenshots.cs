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
}
