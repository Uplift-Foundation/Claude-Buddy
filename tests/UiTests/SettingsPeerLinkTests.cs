using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The pairing section of the settings window.
//
// Unlike the relay section beside it, this one is not a switch and a warning —
// it is a switch and a list of machines with an action per row, because the
// thing a person has to *do* here is pair two of them. So what these assert is
// mostly which controls a given state puts on screen: a state that offers no way
// forward is the failure mode, and it is invisible from the code.
//
// In the Settings collection because every one of these reads or writes the
// process-wide settings static — see SettingsCollection.cs.
[Collection("Settings")]
public class SettingsPeerLinkTests : IDisposable
{
    // Put back, because the setter writes settings.json — so ReloadForTests in
    // the *next* test reads the value this one left rather than a default.
    // Three tests elsewhere failed on exactly that before this was added, and
    // each read as a regression in the code they cover rather than as leakage.
    public void Dispose()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.PeerLinkEnabled = false;
    }

    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(ctor);

        return (SettingsWindow)ctor!.Invoke(null);
    }

    private static SettingsWindow WithLink(bool on)
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.PeerLinkEnabled = on;

        return NewWindow();
    }

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        if (root is T self) yield return self;

        var children = root switch
        {
            Panel panel => panel.Children.OfType<Control>(),
            Decorator decorator when decorator.Child is Control only => new[] { only },
            ContentControl holder when holder.Content is Control only => new[] { only },
            _ => Enumerable.Empty<Control>()
        };

        foreach (var child in children)
        foreach (var found in Descendants<T>(child))
            yield return found;
    }

    private static string AllText(Control root) =>
        string.Join(" ", Descendants<TextBlock>(root).Select(t => t.Text));

    // --- the switch ----------------------------------------------------------

    [AvaloniaFact]
    public void SwitchedOffTheSectionIsJustTheSwitch()
    {
        // One row and no list: there is nothing to pair with until the socket
        // is open, and offering an empty list would read as "nothing found"
        // rather than "not looking".
        var rows = WithLink(false).PeerLinkRows();

        Assert.Single(rows);
    }

    [AvaloniaFact]
    public void SwitchedOffThereIsNoLabelForTheTickerToWrite()
    {
        var window = WithLink(false);
        _ = window.PeerLinkRows();

        Assert.Null(window.PeerLinkStatusText);
    }

    [AvaloniaFact]
    public void TheSwitchSaysWhatItCostsWhichIsNothing()
    {
        // The one thing a person cannot discover by looking at this feature is
        // how it differs from the relay directly below it, which does cost.
        var text = AllText(Assert.IsAssignableFrom<Control>(WithLink(false).PeerLinkRows()[0]));

        Assert.Contains("counts against your usage", text);
        Assert.Contains("pair", text, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void TheSectionIsOfferedOnBothPlatforms()
    {
        // Deliberately not derived from OperatingSystem: the relay section is
        // macOS-only because it lives in tmux, and the entire reason this uses
        // SslStream rather than the gateway's hand-rolled TLS is that it is the
        // same on Windows. A platform gate creeping in here would be a
        // regression that only one CI leg could see.
        Assert.NotEmpty(WithLink(false).PeerLinkRows());
        Assert.NotEmpty(WithLink(true).PeerLinkRows());
    }

    // --- switched on ---------------------------------------------------------

    [AvaloniaFact]
    public void SwitchedOnThereIsAStatusLineAndAWayToPair()
    {
        var window = WithLink(true);
        var rows = window.PeerLinkRows();

        Assert.True(rows.Length >= 3);
        Assert.NotNull(window.PeerLinkStatusText);
    }

    [AvaloniaFact]
    public void SwitchedOnWithNoMachinesSaysHowOneWouldAppear()
    {
        // The link is not started in a headless test, so discovery has found
        // nothing and nothing is paired — which is exactly the state a user is
        // in the first time they open this.
        var rows = WithLink(true).PeerLinkRows();
        var text = string.Join(" ", rows.Select(r => AllText(Assert.IsAssignableFrom<Control>(r))));

        Assert.Contains("No other machines yet", text);
    }

    [AvaloniaFact]
    public void TheTickerWritesTheLinkLine()
    {
        var window = WithLink(true);
        _ = window.PeerLinkRows();

        window.OnStatusTick(null, EventArgs.Empty);

        // Not asserting the wording — PeerSessions.StatusText has its own tests
        // for that. What matters here is that the label exists and the tick
        // reaches it, which is the wiring that has been forgotten twice.
        Assert.False(string.IsNullOrWhiteSpace(window.PeerLinkStatusText));
    }

    // --- one machine's row ---------------------------------------------------

    [AvaloniaFact]
    public void AnUnpairedMachineOffersABoxForTheCode()
    {
        var row = WithLink(true).PeerRow(
            new PeerSessions.Listed("mac-mini", Paired: false, Connected: false, Seen: true));

        var control = Assert.IsAssignableFrom<Control>(row);

        Assert.Single(Descendants<TextBox>(control));
        Assert.Contains(Descendants<Button>(control), b => (string?)b.Content == "Pair");
        Assert.Contains("Found — not paired", AllText(control));
    }

    [AvaloniaFact]
    public void APairedMachineOffersAWayOutRatherThanAnotherCodeBox()
    {
        var row = WithLink(true).PeerRow(
            new PeerSessions.Listed("mac-mini", Paired: true, Connected: true, Seen: true));

        var control = Assert.IsAssignableFrom<Control>(row);

        Assert.Empty(Descendants<TextBox>(control));
        Assert.Contains(Descendants<Button>(control), b => (string?)b.Content == "Forget");
        Assert.Contains("Connected", AllText(control));
    }

    [AvaloniaFact]
    public void APairedMachineThatIsAwaySaysWhichProblemItIs()
    {
        var row = WithLink(true).PeerRow(
            new PeerSessions.Listed("mac-mini", Paired: true, Connected: false, Seen: false));

        Assert.Contains(
            "not on this network",
            AllText(Assert.IsAssignableFrom<Control>(row)));
    }

    [AvaloniaFact]
    public void EveryRowNamesItsMachine()
    {
        // The complaint this whole transport exists to answer was a panel that
        // would not say which machine it was talking about.
        var row = WithLink(true).PeerRow(
            new PeerSessions.Listed("mac-mini", Paired: false, Connected: false, Seen: true));

        Assert.Contains("mac-mini", AllText(Assert.IsAssignableFrom<Control>(row)));
    }

    // --- the code button -----------------------------------------------------

    [AvaloniaFact]
    public void TheCodeButtonStartsAsAnInvitation()
    {
        var button = Assert.IsType<Button>(WithLink(true).PairingCodeButton());

        Assert.Equal("Show a code", button.Content);
        Assert.True(button.IsEnabled);
    }

    [AvaloniaFact]
    public void ClickingItAnswersEvenWithNoLinkRunning()
    {
        // Headless, so PeerSessions has no host and OpenForPairing returns null.
        // The button must still say something rather than going blank — a
        // control that empties itself on click reads as a crash.
        var button = Assert.IsType<Button>(WithLink(true).PairingCodeButton());

        button.Command?.Execute(null);
        RaiseClick(button);

        Assert.False(string.IsNullOrWhiteSpace((string?)button.Content));
        Assert.False(button.IsEnabled);
    }

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
