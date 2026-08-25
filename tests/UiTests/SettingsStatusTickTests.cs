using System;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The two live status lines in the settings window, and the one timer that feeds
// them both.
//
// These exist because what they describe changes while you are looking at it: a
// relay takes a few seconds to start and stops itself when idle, and a gateway
// connects, gets refused, or waits to be approved. A line that was only true when
// the window opened would be worse than no line, because it would look current.
//
// The tick is driven directly. StartStatusTicker is excluded — it starts a real
// one-second Avalonia timer — and waiting on that timer is exactly the mistake
// this branch has now fixed five times.
[Collection("Settings")]
public class SettingsStatusTickTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(ctor);

        return (SettingsWindow)ctor!.Invoke(null);
    }

    // Both labels are created only PAST their section's enabled-check, so the
    // feature has to be switched on before the rows are built, not after.
    private static SettingsWindow WithGateway()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawEnabled = true;

        var window = NewWindow();
        _ = window.OpenClawRows();
        return window;
    }

    private static SettingsWindow WithRelay()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlEnabled = true;
        RemoteControlSessions.ClearRelaysForTests();

        var window = NewWindow();
        _ = window.RemoteControlRows();
        return window;
    }

    // The relay section is macOS-only — the bridge is tmux-based, so on Windows
    // RemoteControlRows returns before its label exists and there is nothing for
    // the tick to write. Derived from the platform rather than skipped, so these
    // assert something true on both CI legs.
    private static bool RelayRowsExist => OperatingSystem.IsMacOS();

    // Each label is null unless its own section has been built, which is the
    // point of the single timer: one ticker serving at most two labels rather
    // than a timer each.
    [AvaloniaFact]
    public void TickingWithNeitherSectionBuiltDoesNothing()
    {
        // Both switched OFF first, explicitly. The constructor calls Rebuild(),
        // so a section left enabled by an earlier test builds its label before
        // this test gets a look in — which is how this assertion failed the first
        // time I ran it, and is the same order-dependence this branch has spent
        // five commits removing.
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawEnabled = false;
        ClaudeBuddySettings.RemoteControlEnabled = false;

        var window = NewWindow();

        window.OnStatusTick(null, EventArgs.Empty);

        Assert.Null(window.OpenClawStatusText);
        Assert.Null(window.RemoteControlStatusText);
    }

    [AvaloniaFact]
    public void TheGatewayLineIsFilledOnceItsSectionExists()
    {
        var window = WithGateway();

        window.OnStatusTick(null, EventArgs.Empty);

        Assert.Equal(OpenClawSessions.StatusText, window.OpenClawStatusText);
        Assert.False(string.IsNullOrWhiteSpace(window.OpenClawStatusText));
    }

    [AvaloniaFact]
    public void TheRelayLineIsFilledOnceItsSectionExists()
    {
        var window = WithRelay();

        window.OnStatusTick(null, EventArgs.Empty);

        if (RelayRowsExist)
            Assert.Equal(RemoteControlSessions.StatusText, window.RemoteControlStatusText);
        else
            Assert.Null(window.RemoteControlStatusText);
    }

    // The relay line has to follow the relay, which is the whole reason it ticks
    // rather than being written once when the window opens.
    [AvaloniaFact]
    public void TheRelayLineFollowsTheRelayChangingUnderIt()
    {
        if (!RelayRowsExist) return;

        var window = WithRelay();

        window.OnStatusTick(null, EventArgs.Empty);
        var off = window.RemoteControlStatusText;

        try
        {
            RemoteControlSessions.SetRelayForTests("work@example.com", "3 sessions");
            window.OnStatusTick(null, EventArgs.Empty);

            Assert.NotEqual(off, window.RemoteControlStatusText);
            Assert.Contains("3 sessions", window.RemoteControlStatusText!);
        }
        finally
        {
            RemoteControlSessions.ClearRelaysForTests();
        }
    }

    // Ticking again with nothing changed leaves the same text. The guard is an
    // `if (label.Text != value)` rather than an unconditional assignment, which
    // matters at one tick a second for as long as the window is open: assigning
    // Text invalidates layout even when the string is identical.
    [AvaloniaFact]
    public void TickingRepeatedlyWithNothingChangedIsStable()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawEnabled = true;
        ClaudeBuddySettings.RemoteControlEnabled = true;
        RemoteControlSessions.ClearRelaysForTests();

        var window = NewWindow();
        _ = window.OpenClawRows();
        _ = window.RemoteControlRows();

        window.OnStatusTick(null, EventArgs.Empty);
        var gateway = window.OpenClawStatusText;
        var relay = window.RemoteControlStatusText;

        for (var i = 0; i < 5; i++) window.OnStatusTick(null, EventArgs.Empty);

        Assert.Equal(gateway, window.OpenClawStatusText);
        Assert.Equal(relay, window.RemoteControlStatusText);
    }

    // Both sections built, both lines fed by the one tick.
    [AvaloniaFact]
    public void OneTickServesBothLines()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawEnabled = true;
        ClaudeBuddySettings.RemoteControlEnabled = true;
        RemoteControlSessions.ClearRelaysForTests();

        var window = NewWindow();
        _ = window.OpenClawRows();
        _ = window.RemoteControlRows();

        window.OnStatusTick(null, EventArgs.Empty);

        Assert.NotNull(window.OpenClawStatusText);
        Assert.Equal(RelayRowsExist, window.RemoteControlStatusText is not null);
    }

    // The sender and args are ignored — it is wired as a DispatcherTimer.Tick
    // handler but reads only the fields, which is what makes driving it directly
    // legitimate rather than a trick.
    [AvaloniaFact]
    public void TheTickIgnoresItsSenderAndArgs()
    {
        var window = WithGateway();

        window.OnStatusTick(null, EventArgs.Empty);
        var first = window.OpenClawStatusText;

        window.OnStatusTick(window, new EventArgs());

        Assert.Equal(first, window.OpenClawStatusText);
    }
}
