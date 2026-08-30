using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiTests;

// Startup.ClaimUiThread, asserted from the only kind of thread that is allowed
// to own the dispatcher (CB-28).
//
// **What this suite can and cannot say, plainly.** The bug is about a process
// where *nothing* has made a dispatcher yet, and there is no such process here:
// Avalonia's headless lifetime builds one on the test host's main thread before
// the first test runs, and `s_uiThread` is process-wide and cannot be unset. So
// what is asserted here is the postcondition — after the claim, the UI thread
// dispatcher is this thread's, and asking again changes nothing — plus the fact
// that the call is safe when a dispatcher already exists, which is the case on
// every ordinary machine where Main wins the race anyway.
//
// The half no suite in this repository can reach is that claiming *prevents*
// AvaloniaNativePlatform.Initialize from throwing when a pool thread would
// otherwise have got there first. That needs a fresh process with a real
// platform init in it, which is a test that ends the test run. It was measured
// instead, by hand, against Avalonia 12.1.1 — the method and both outcomes are
// in the CB-28 PR body so the next person can repeat it rather than trust it.
public class UiThreadClaimTests
{
    [AvaloniaFact]
    public void Leaves_the_ui_thread_belonging_to_the_thread_that_claimed_it()
    {
        Startup.ClaimUiThread();

        // An AvaloniaFact body runs on the dispatcher thread, so this is the
        // claim's whole postcondition: the thread that asked owns it.
        Assert.True(Dispatcher.UIThread.CheckAccess());
    }

    [AvaloniaFact]
    public void Is_safe_when_a_dispatcher_already_exists()
    {
        // The ordinary-machine case. Main claims it, then Avalonia's own
        // platform init claims the same one a moment later; a claim that threw
        // or replaced anything the second time would break every launch rather
        // than only the unattended ones.
        var before = Dispatcher.UIThread;

        Startup.ClaimUiThread();
        Startup.ClaimUiThread();

        Assert.Same(before, Dispatcher.UIThread);
        Assert.True(Dispatcher.UIThread.CheckAccess());
    }

    [AvaloniaFact]
    public void Leaves_a_dispatcher_that_still_runs_what_is_posted_to_it()
    {
        // The claim's other half, and the reason it is a claim rather than a
        // reset: whatever was queued before it — on a locked machine, the
        // `Dispatcher.UIThread.Post(EnsureTimer)` that hands the relay over —
        // still has to run once the dispatcher pumps.
        Startup.ClaimUiThread();

        var ran = false;
        Dispatcher.UIThread.Post(() => ran = true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(ran);
    }
}
