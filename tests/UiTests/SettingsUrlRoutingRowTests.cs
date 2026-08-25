using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// The one visible surface CB-4 adds: the switch that decides whether Claude
// Buddy claims Claude Desktop's URL schemes.
//
// It is worth a test rather than being taken on trust because it is the only
// way a user can undo a system-wide change this app makes to their machine. A
// row that silently failed to render would leave the schemes claimed with
// nothing in the UI to hand them back — which is a worse state than the bug it
// fixes.
//
// Constructed by reflection for the same reason SettingsWindowSmokeTest does
// it: the production entry point is Toggle(), whose body makes real OS calls
// this suite has no business making headless. The window is deliberately never
// Closed — see the long note in that file about the process-wide FontManager
// cache corruption a headless Close() can cause.
public class SettingsUrlRoutingRowTests
{
    private static Window Build()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (Window)ctor.Invoke(null);
    }

    private static bool HasLabel(Window window, string text) =>
        window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Any(block => block.Text == text);

    [AvaloniaFact]
    public void TheRoutingRowIsPresentOnMacOsAndAbsentElsewhere()
    {
        var window = Build();

        // The row is gated on macOS deliberately: the collision it works around
        // is caused by the tinted clone bundles, which are a macOS feature, and
        // LaunchServices has no Windows analogue. Asserting both directions
        // keeps that gate honest on both CI legs rather than only the one the
        // change was written on.
        Assert.Equal(
            OperatingSystem.IsMacOS(),
            HasLabel(window, "Send Claude links to the right profile"));
    }

    [AvaloniaFact]
    public void TheExistingDesktopRowSurvivedTheCardBeingRebuilt()
    {
        // The routing row was added by turning a single-row Card into a list,
        // which is exactly the kind of edit that quietly drops the row that was
        // already there. This is the regression guard for that.
        Assert.True(HasLabel(Build(), "Tint the active window"));
    }
}
