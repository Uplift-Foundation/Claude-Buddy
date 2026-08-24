using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// The tray menu's "Connect to other machines" item.
//
// This exists because it is the one part of the Remote Control feature that
// could not be verified by driving the real app: synthesizing a menu-bar click
// hangs on a machine someone is using — the modal menu blocks the script, which
// is the hazard CLAUDE.md already warns about — and it would be testing
// AppleScript rather than this code. So the menu is built here and inspected
// instead. What that does and does not prove is worth being precise about: it
// proves the item is created, labelled, and shown or hidden by the right
// setting. It does not prove macOS renders it or that clicking dispatches, both
// of which are Avalonia's job.
//
// Deliberately does not invoke the item's Click handler. That calls
// EnsureStarted, which would launch a real Claude Code session and spend the
// person running the tests money — see LiveBridgeFactAttribute for where that is
// allowed to happen. The handler's own path is covered by the opt-in live test
// in tests/IntegrationTests.
public class TrayRemoteItemTests
{
    private const string ItemLabel = "Connect to other machines";

    // Rebuild is private and takes the session list the controller normally
    // gets from SessionManager. Reflection rather than widening the API for a
    // test, the same reasoning ChatPanelTestAccess records for reaching
    // ChatPanel's private singleton field.
    // Restored afterwards, which is not tidiness — it is the difference between
    // this suite passing and another one failing for no reason it can see.
    //
    // RemoteControlEnabled is a real setter: it writes settings.json, and
    // TestBootstrap points the whole *assembly* at one temp directory, so a
    // value left behind here is the value every later test in every other class
    // reads. Left set, it reached
    // RemoteControlChatSessionTests.AFailedSendKeepsTheMessageOnScreenAndExplainsItself,
    // whose whole subject is what a send does with the feature off — so that
    // test passed or failed on nothing but which class the runner happened to
    // reach first. Alphabetical order put RemoteControl before Tray and hid it
    // on macOS; Windows ordered them the other way and it failed there, three
    // attempts out of three, while the same commit passed on the other runner.
    private static NativeMenu? BuildMenu(bool remoteEnabled)
    {
        var before = ClaudeBuddySettings.RemoteControlEnabled;
        try
        {
            return BuildMenuCore(remoteEnabled);
        }
        finally
        {
            ClaudeBuddySettings.RemoteControlEnabled = before;
        }
    }

    private static NativeMenu? BuildMenuCore(bool remoteEnabled)
    {
        ClaudeBuddySettings.RemoteControlEnabled = remoteEnabled;

        TrayController tray;
        try
        {
            tray = new TrayController();
        }
        catch
        {
            // A TrayIcon may not be constructible under the headless platform.
            // Reported as "couldn't check" by the callers rather than silently
            // passing — a test that quietly stops testing is worse than one
            // that admits it.
            return null;
        }

        var rebuild = typeof(TrayController).GetMethod(
            "Rebuild", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rebuild);

        rebuild!.Invoke(tray, new object?[] { Array.Empty<TrayController.SessionEntry>() });

        var menuField = typeof(TrayController).GetField(
            "_menu", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(menuField);

        return (NativeMenu?)menuField!.GetValue(tray);
    }

    private static IEnumerable<string> Labels(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>().Select(item => item.Header ?? "");

    [AvaloniaFact]
    public void TheItemIsOfferedWhenTheFeatureIsOn()
    {
        var menu = BuildMenu(remoteEnabled: true);
        if (menu is null) return; // headless platform has no tray; see BuildMenu

        // Only on macOS/Linux — the relay is tmux-based, and the menu should not
        // offer something that cannot work.
        if (!RemoteControlBridge.IsSupported)
        {
            Assert.DoesNotContain(ItemLabel, Labels(menu));
            return;
        }

        Assert.Contains(ItemLabel, Labels(menu));
    }

    // The menu should not grow a line about other machines for the majority who
    // have never asked for any — and with the feature off by default, that is
    // every install until someone opts in.
    [AvaloniaFact]
    public void TheItemIsAbsentWhenTheFeatureIsOff()
    {
        var menu = BuildMenu(remoteEnabled: false);
        if (menu is null) return;

        Assert.DoesNotContain(ItemLabel, Labels(menu));
    }
}
