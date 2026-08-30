using Xunit;

namespace ClaudeBuddy.Tests;

// The order the process starts in (CB-28).
//
// This is a test about a sequence of four calls, which normally would not be
// worth writing down. It is worth writing down here because the bug was the
// sequence: claiming Avalonia's UI thread has to happen before anything that
// can post to the dispatcher from another thread, and on a machine whose screen
// never unlocks *everything* Buddy starts before the UI is up can. Nothing
// about `RemoteControlSessions.ServeOnLaunch()` looks like it competes for a
// static property in Avalonia, which is precisely why moving it back above the
// claim would be an easy and invisible thing to do.
//
// What cannot be asserted here is the claim itself — see
// UiThreadClaimTests for the half that can, and the CB-28 PR body for the
// measurement covering the rest.
public class StartupOrderTests
{
    [Fact]
    public void Claims_the_ui_thread_before_anything_else_starts()
    {
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => order.Add("log"),
            claimUiThread: () => order.Add("claim"),
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => order.Add("wait"),
            startUi: () => order.Add("ui"));

        Assert.Equal(new[] { "log", "claim", "serve", "wait", "ui" }, order);
    }

    [Fact]
    public void Claims_before_the_relay_that_would_otherwise_claim_it_from_the_pool()
    {
        // The pairing that actually matters, stated on its own so that a future
        // reordering fails on a test whose name says why it exists rather than
        // on a list of four strings.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimUiThread: () => order.Add("claim"),
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => { },
            startUi: () => { });

        Assert.Equal("claim", order[0]);
        Assert.Equal("serve", order[1]);
    }

    [Fact]
    public void Waits_for_the_screen_before_starting_the_ui_and_not_after()
    {
        // The CB-24 ordering, still true: the wait exists to keep Avalonia off a
        // locked screen, so a start that ran first would be the original bug
        // back again.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimUiThread: () => { },
            serveOnLaunch: () => { },
            waitForUnlock: () => order.Add("wait"),
            startUi: () => order.Add("ui"));

        Assert.Equal(new[] { "wait", "ui" }, order);
    }

    [Fact]
    public void Installs_crash_logging_before_anything_that_could_crash()
    {
        // The ordering CB-44 is about. Both crashes it was filed for happened
        // in the last step, and left nothing on disk because nothing had
        // subscribed by then — so this is first, ahead of even the UI-thread
        // claim, which is itself a thing that can throw.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => order.Add("log"),
            claimUiThread: () => order.Add("claim"),
            serveOnLaunch: () => { },
            waitForUnlock: () => { },
            startUi: () => order.Add("ui"));

        Assert.Equal("log", order[0]);
    }

    [Fact]
    public void Runs_each_step_exactly_once()
    {
        var counts = new Dictionary<string, int>
        {
            ["log"] = 0, ["claim"] = 0, ["serve"] = 0, ["wait"] = 0, ["ui"] = 0
        };

        Startup.Run(
            installCrashLog: () => counts["log"]++,
            claimUiThread: () => counts["claim"]++,
            serveOnLaunch: () => counts["serve"]++,
            waitForUnlock: () => counts["wait"]++,
            startUi: () => counts["ui"]++);

        Assert.All(counts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void Lets_a_failing_step_stop_the_ones_after_it()
    {
        // Deliberately not guarded, and asserted so that nobody adds a
        // try/catch here thinking it is kind. If ServeOnLaunch throws, the app
        // is in a state nobody has reasoned about; starting a UI on top of it
        // would hide the crash report that is the only evidence of what went
        // wrong. Same behaviour as the straight-line Main this replaced.
        var reached = false;

        Assert.Throws<InvalidOperationException>(() => Startup.Run(
            installCrashLog: () => { },
            claimUiThread: () => { },
            serveOnLaunch: () => throw new InvalidOperationException("relay"),
            waitForUnlock: () => reached = true,
            startUi: () => reached = true));

        Assert.False(reached);
    }
}
