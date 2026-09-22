using Xunit;

namespace ClaudeBuddy.Tests;

// The order the process starts in (CB-28, extended by CB-178).
//
// This is a test about a sequence of calls, which normally would not be
// worth writing down. It is worth writing down here because the bug was the
// sequence: claiming Avalonia's UI thread has to happen before anything that
// can post to the dispatcher from another thread, and on a machine whose screen
// never unlocks *everything* Buddy starts before the UI is up can. Nothing
// about `RemoteControlSessions.ServeOnLaunch()` looks like it competes for a
// static property in Avalonia, which is precisely why moving it back above the
// claim would be an easy and invisible thing to do.
//
// CB-178 added `claimSingleInstance` ahead of all of that, as a step that can
// answer "stop" and have `Run` return without touching anything after it —
// see the short-circuit tests near the bottom of this file, and
// Startup.Run's own comment for why a duplicate instance must never reach
// `claimUiThread` at all now.
//
// What cannot be asserted here is either claim itself — see
// UiThreadClaimTests for the UI-thread half, SingleInstanceTests for the
// single-instance decision, and tests/IntegrationTests' SingleInstanceTests
// for the real named mutex — and the CB-28 PR body for the measurement
// covering the rest.
public class StartupOrderTests
{
    [Fact]
    public void Claims_the_ui_thread_before_anything_else_starts()
    {
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => order.Add("log"),
            claimSingleInstance: () => { order.Add("single"); return true; },
            claimUiThread: () => order.Add("claim"),
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => order.Add("wait"),
            startUi: () => order.Add("ui"));

        Assert.Equal(new[] { "log", "single", "claim", "serve", "wait", "ui" }, order);
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
            claimSingleInstance: () => true,
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
            claimSingleInstance: () => true,
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
        // subscribed by then — so this is first, ahead of even the single-
        // instance claim and the UI-thread claim, both of which can throw.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => order.Add("log"),
            claimSingleInstance: () => { order.Add("single"); return true; },
            claimUiThread: () => order.Add("claim"),
            serveOnLaunch: () => { },
            waitForUnlock: () => { },
            startUi: () => order.Add("ui"));

        Assert.Equal("log", order[0]);
    }

    [Fact]
    public void Claims_single_instance_before_the_ui_thread_and_everything_after_it()
    {
        // CB-178's ordering. Placed straight after installCrashLog and ahead
        // of every other step, so a duplicate instance — which this test
        // arranges by having the claim answer "someone else holds it" — never
        // reaches claimUiThread, serveOnLaunch, waitForUnlock or startUi. See
        // the short-circuit tests below for that half; this one is only about
        // where the check sits when it says yes.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => { order.Add("single"); return true; },
            claimUiThread: () => order.Add("claim"),
            serveOnLaunch: () => { },
            waitForUnlock: () => { },
            startUi: () => { });

        Assert.Equal("single", order[0]);
        Assert.Equal("claim", order[1]);
    }

    [Fact]
    public void Runs_each_step_exactly_once()
    {
        var counts = new Dictionary<string, int>
        {
            ["log"] = 0, ["single"] = 0, ["claim"] = 0, ["serve"] = 0, ["wait"] = 0, ["ui"] = 0
        };

        Startup.Run(
            installCrashLog: () => counts["log"]++,
            claimSingleInstance: () => { counts["single"]++; return true; },
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
            claimSingleInstance: () => true,
            claimUiThread: () => { },
            serveOnLaunch: () => throw new InvalidOperationException("relay"),
            waitForUnlock: () => reached = true,
            startUi: () => reached = true));

        Assert.False(reached);
    }

    // The CB-178 short-circuit itself: everything from claimUiThread onward
    // is a delegate specifically so this can be asserted without a real
    // dispatcher, a real relay or a real screen-lock wait. A duplicate
    // instance used to reach all of this and then some — see
    // App.axaml.cs's history — which is the bug.
    [Fact]
    public void Runs_nothing_after_the_single_instance_claim_when_it_says_stop()
    {
        var reached = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => false,
            claimUiThread: () => reached.Add("claim"),
            serveOnLaunch: () => reached.Add("serve"),
            waitForUnlock: () => reached.Add("wait"),
            startUi: () => reached.Add("ui"));

        Assert.Empty(reached);
    }

    [Fact]
    public void Still_installs_crash_logging_even_when_the_claim_says_stop()
    {
        // The one step that must run regardless: an unexpected throw out of
        // the claim itself is exactly the kind of failure CrashLog exists to
        // catch, so it has to be installed before the claim is even
        // attempted — independent of what the claim answers.
        var logged = false;

        Startup.Run(
            installCrashLog: () => logged = true,
            claimSingleInstance: () => false,
            claimUiThread: () => { },
            serveOnLaunch: () => { },
            waitForUnlock: () => { },
            startUi: () => { });

        Assert.True(logged);
    }

    [Fact]
    public void Runs_everything_in_order_when_the_claim_says_proceed()
    {
        // The complement of the two tests above, so "stop" and "proceed" are
        // both pinned down rather than one being inferred from the other.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => order.Add("log"),
            claimSingleInstance: () => { order.Add("single"); return true; },
            claimUiThread: () => order.Add("claim"),
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => order.Add("wait"),
            startUi: () => order.Add("ui"));

        Assert.Equal(new[] { "log", "single", "claim", "serve", "wait", "ui" }, order);
    }
}
