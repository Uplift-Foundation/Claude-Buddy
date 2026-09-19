using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-164's settings section, driven the way SettingsWindowCoverageTests next
// door drives the others: call the production row builder directly, never walk
// the visual tree for a control whose type depends on which theme template
// loaded, and never click a button whose handler reaches the OS or the network.
//
// Both of this section's buttons are safe to click, unlike the gateway's
// Reconnect: ClaudeCloudSessions.Restart clears a snapshot and sets a status
// word, and nothing behind it opens a socket or asks the OS for a credential
// until its poll loop next comes round — which this suite never starts.
[Collection("Settings")]
public class CloudSettingsRowsTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static void Reset()
    {
        ClaudeBuddySettings.ClaudeCloudEnabled = false;
        ClaudeCloudSessions.SetStateForTests("off");
    }

    // Progressive disclosure: off is one row, and there is nothing further to
    // configure because there is nothing further running.
    [AvaloniaFact]
    public void TheSectionIsJustTheOneSwitchWhenDisabled()
    {
        Reset();
        try
        {
            Assert.Single(NewWindow().ClaudeCloudRows());
        }
        finally
        {
            Reset();
        }
    }

    // Switch, status note, retry — and nothing platform-gated, which is the
    // thing the parity rule cares about here. The same three rows are built on
    // either runner.
    [AvaloniaFact]
    public void TheSectionOpensUpWhenEnabled()
    {
        Reset();
        try
        {
            ClaudeBuddySettings.ClaudeCloudEnabled = true;

            Assert.Equal(3, NewWindow().ClaudeCloudRows().Length);
        }
        finally
        {
            Reset();
        }
    }

    // The Keychain prompt is named in the help text, and that sentence is load
    // bearing: it arrives unannounced, it names an item the user has never heard
    // of, and a prompt nobody expected is a prompt people decline — after which
    // the feature never works and nothing on screen says why.
    [AvaloniaFact]
    public void TheHelpTextNamesTheKeychainPromptAndTheAnswerToGiveIt()
    {
        Reset();
        try
        {
            var help = string.Join(
                " ",
                NewWindow().ClaudeCloudRows()[0]
                    .GetLogicalDescendants()
                    .OfType<TextBlock>()
                    .Select(block => block.Text));

            Assert.Contains("Keychain", help);
            Assert.Contains("Always", help);
        }
        finally
        {
            Reset();
        }
    }

    // The switch writes the setting through, so the section the user left open
    // is the section they come back to.
    [AvaloniaFact]
    public void TheSwitchTogglesTheSetting()
    {
        Reset();
        try
        {
            var toggle = NewWindow().ClaudeCloudRows()[0]
                .GetLogicalDescendants().OfType<ToggleButton>().Single();

            toggle.IsChecked = true;

            Assert.True(ClaudeBuddySettings.ClaudeCloudEnabled);
        }
        finally
        {
            Reset();
        }
    }

    // The status line starts at whatever the arm last said, rather than blank —
    // a note row that is empty until the first tick reads as a layout bug.
    [AvaloniaFact]
    public void TheStatusLineStartsAtTheArmsCurrentWord()
    {
        Reset();
        try
        {
            ClaudeBuddySettings.ClaudeCloudEnabled = true;
            ClaudeCloudSessions.SetStateForTests("checking…");

            var window = NewWindow();
            window.ClaudeCloudRows();

            Assert.Equal("checking…", window.ClaudeCloudStatusText);
        }
        finally
        {
            Reset();
        }
    }

    // ...and follows the arm afterwards, which is the whole reason it is a
    // ticked field rather than text baked in when the window was built. The
    // first read here is gated on a Keychain prompt the user answers in a
    // different window, so this line changes at a moment nothing else in the
    // settings window knows about.
    [AvaloniaFact]
    public void TheStatusLineFollowsTheArm()
    {
        Reset();
        try
        {
            ClaudeBuddySettings.ClaudeCloudEnabled = true;
            ClaudeCloudSessions.SetStateForTests("checking…");

            var window = NewWindow();
            window.ClaudeCloudRows();

            ClaudeCloudSessions.SetStateForTests("4 sessions");
            window.OnStatusTick(null, EventArgs.Empty);

            Assert.Equal("4 sessions", window.ClaudeCloudStatusText);
        }
        finally
        {
            Reset();
        }
    }

    // A window whose cloud section was never built has no label to tick, and the
    // shared ticker must survive that rather than throwing on every second.
    [AvaloniaFact]
    public void TheTickerToleratesASectionThatWasNeverBuilt()
    {
        Reset();
        try
        {
            NewWindow().OnStatusTick(null, EventArgs.Empty);
        }
        finally
        {
            Reset();
        }
    }

    // Retry exists because the first failure this feature hits is almost always
    // a dismissed Keychain prompt, and the fix for that is to ask again. Without
    // it the only way back is toggling the switch off and on, which is a thing
    // users discover rather than a thing the window offers.
    [AvaloniaFact]
    public void RetryRestartsTheArm()
    {
        Reset();
        try
        {
            ClaudeBuddySettings.ClaudeCloudEnabled = true;
            ClaudeCloudSessions.SetStateForTests("something stale");

            var rows = NewWindow().ClaudeCloudRows();
            var retry = rows[^1].GetLogicalDescendants().OfType<Button>().Single();

            Assert.Equal("Retry", retry.Content);

            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Restart clears the snapshot and puts the arm back to "checking"
            // when the feature is on, which is what says the button did
            // something rather than merely existing.
            Assert.Equal("checking…", ClaudeCloudSessions.StatusText);
            Assert.Empty(ClaudeCloudSessions.Snapshot());
        }
        finally
        {
            Reset();
        }
    }
}
