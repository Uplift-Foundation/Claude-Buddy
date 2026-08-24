using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The two pieces of the settings window that only one platform ever shows, tested
// from both.
//
// Each used to be built inline inside its platform gate, which meant it could only
// be reached on one CI leg — so on the other it read as uncovered with no way to
// do anything about it. What they build is a decision about what to tell the user;
// only the decision to show it is about the platform. Split accordingly, and these
// assert the part that is not.
[Collection("Settings")]
public class SettingsPlatformRowTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(ctor);

        return (SettingsWindow)ctor!.Invoke(null);
    }

    // ---- the Done button -------------------------------------------------

    // Windows expects a Done button inside a preferences window; macOS dismisses
    // one with the window's own close button and would read an in-content Done as
    // out of place.
    [AvaloniaFact]
    public void TheDoneButtonIsBuiltTheSameWayOnEitherPlatform()
    {
        var done = NewWindow().DoneButton();

        Assert.Equal("Done", done.Content);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, done.HorizontalAlignment);
        Assert.True(done.MinWidth >= 90);
    }

    // What is NOT asserted here, deliberately: that the button's Click actually
    // does anything. Its handler calls Window.Close(), which on a headless window
    // corrupts a process-wide FontManager cache — the hazard
    // CloseFromDoneButton's own exclusion comment describes — so clicking it is
    // not available. Avalonia offers no way to ask a Button whether a handler is
    // attached either, and the first version of this file tried: an
    // `Assert.True(field is null || …)` that could barely fail. A test that
    // asserts less than its name claims is worse than a missing one, so it is
    // gone and this is the note in its place. A Done button wired to nothing
    // would pass everything here — that gap is real, and it is one line.

    // The window only puts it on screen off macOS. Asserted against the platform
    // rather than a literal, so it says something true on both runners.
    [AvaloniaFact]
    public void TheWindowShowsTheDoneButtonOnlyWhereItBelongs()
    {
        ClaudeBuddySettings.ReloadForTests();
        var window = NewWindow();

        var hasDone = window.GetLogicalDescendants()
            .OfType<Button>()
            .Any(b => b.Content as string == "Done");

        Assert.Equal(!OperatingSystem.IsMacOS(), hasDone);
    }

    // ---- the unsupported note --------------------------------------------

    // Said plainly rather than hidden. A feature that exists in the docs and
    // nowhere in the window reads as a broken install, which is a worse bug
    // report than "not supported yet".
    [AvaloniaFact]
    public void TheUnsupportedNoteExplainsItselfRatherThanJustSayingNo()
    {
        var rows = NewWindow().RemoteControlUnsupportedRows();

        var row = Assert.Single(rows);
        var text = row.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .First(t => !string.IsNullOrWhiteSpace(t));

        // Names the platform, and names the reason — tmux — rather than leaving
        // the user to guess whether it is coming.
        Assert.Contains("macOS-only", text!);
        Assert.Contains("tmux", text!);
    }

    // It wraps, because it is two sentences in a settings row and a single
    // clipped line would lose the half that explains why.
    [AvaloniaFact]
    public void TheUnsupportedNoteWraps()
    {
        var rows = NewWindow().RemoteControlUnsupportedRows();

        var block = rows[0].GetLogicalDescendants().OfType<TextBlock>()
            .First(t => !string.IsNullOrWhiteSpace(t.Text));

        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, block.TextWrapping);
    }

    // And the section shows it exactly where the bridge is unsupported: one row
    // and nothing else, rather than the note plus a set of controls that cannot
    // work.
    [AvaloniaFact]
    public void TheRemoteControlSectionIsJustTheNoteWhereTheBridgeCannotRun()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlEnabled = true;

        var rows = NewWindow().RemoteControlRows();

        if (RemoteControlBridge.IsSupported)
        {
            Assert.True(rows.Length > 1);
        }
        else
        {
            Assert.Single(rows);
        }
    }
}
