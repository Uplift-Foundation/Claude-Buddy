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

    private static string HelpText() =>
        string.Join(
            " ",
            NewWindow().ClaudeCloudRows()[0]
                .GetLogicalDescendants()
                .OfType<TextBlock>()
                .Select(block => block.Text));

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
            var help = HelpText();

            Assert.Contains("Keychain", help);
            Assert.Contains("Always", help);
        }
        finally
        {
            Reset();
        }
    }

    // **And does not promise the prompt goes away for good.**
    //
    // It said so until this was measured otherwise: a secret read that had been
    // succeeding began blocking indefinitely, and in between the credential's
    // stamp had moved because the CLI refreshed its token. What is established
    // is that the read can block and that the credential was rewritten; the link
    // between them is inferred. So the copy says the prompt can come back, and
    // this pins the retraction rather than trusting nobody rewrites it into a
    // promise again — the failure mode being copy that reads as reassuring and
    // is not true, which is the worst kind of settings text.
    [AvaloniaFact]
    public void TheHelpTextDoesNotPromiseTheKeychainPromptIsGoneForGood()
    {
        Reset();
        try
        {
            var help = HelpText();

            Assert.DoesNotContain("stops it asking again", help);
            Assert.DoesNotContain("never ask", help);
            Assert.DoesNotContain("only once", help);

            // ...and does say the other half, so this cannot be satisfied by
            // simply deleting the sentence.
            Assert.Contains("can bring the prompt back", help);
        }
        finally
        {
            Reset();
        }
    }

    // No frequency, either. "Every few hours" would be the same unmeasured claim
    // in a more precise costume, and a number in settings copy reads as
    // something somebody counted.
    [AvaloniaTheory]
    [InlineData("hour")]
    [InlineData("day")]
    [InlineData("week")]
    [InlineData("minute")]
    public void TheHelpTextClaimsNoFrequency(string unit)
    {
        Reset();
        try
        {
            Assert.DoesNotContain(unit, HelpText(), StringComparison.OrdinalIgnoreCase);
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

            // Restart clears the snapshot and moves the arm off whatever it was
            // showing, which is what says the button did something rather than
            // merely existing.
            //
            // Asserting the snapshot and "no longer stale" rather than the exact
            // word "checking…", because that word is a transient this test has no
            // right to observe. Restart sets it synchronously and then starts a
            // poll loop that overwrites it as soon as the credential read answers.
            //
            // It used to assert the exact word and pass — but only because the
            // read did not answer. On a developer's Mac that read is a login
            // Keychain query raising a consent dialog no headless suite can
            // answer, so the arm sat on "checking…" for the full forty-five-second
            // budget and the assertion always won the race. The green was a
            // symptom of the hang, not evidence of the behaviour. Now that a test
            // process refuses the credential store outright
            // (CLAUDE_BUDDY_NO_CREDENTIAL_STORE, see ClaudeCliCredentials), the
            // read returns instantly and the loop moves the state on before this
            // line runs — so the test had to start asserting something that is
            // actually true rather than something that was merely slow.
            Assert.Empty(ClaudeCloudSessions.Snapshot());
            Assert.NotEqual("something stale", ClaudeCloudSessions.StatusText);
        }
        finally
        {
            Reset();
        }
    }
}
