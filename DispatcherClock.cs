using System.Reflection;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // Puts Avalonia's UI dispatcher back on the platform's clock, once the
    // platform exists.
    //
    // Startup.ClaimUiThread reads Dispatcher.UIThread before any Avalonia
    // platform is initialised — deliberately, for CB-44 — and that constructs
    // the dispatcher with no platform implementation behind it. The dispatcher
    // binds its clock in its constructor, to a Stopwatch it starts right there,
    // and never re-binds it. When Avalonia.Native (or Win32) comes up later it
    // hands the dispatcher its own implementation, which keeps a Stopwatch of
    // its own, started when *it* was built. Every DispatcherTimer stamps its due
    // time on the dispatcher's clock; the platform arms the OS timer for
    // `dueTime - platformNow` on its own. The two clocks differ by however long
    // startup took between the claim and the platform — the peer link, the
    // gateway and cloud pollers, the screen-lock wait — so every timer in the
    // app fired that much late: measured 108–127 ms on a dev instance and about
    // 215 ms on the build installed on the MacBook, which turned a 40 ms GIF
    // frame into a 255 ms one and a twenty-per-second breath into four.
    //
    // It went unnoticed for a month because the dispatcher also promotes due
    // timers after every job it runs, whatever the OS timer says. The danger
    // ring's endless breathing animation queued render work every frame, and
    // that promoted every timer on time; CB-219 removed the animation to stop
    // it costing a vsync's worth of CPU forever, and the offset was left doing
    // the scheduling on its own. A thread-pool Post every 40 ms put the GIF back
    // to 40 ms on the same build, which is what pinned it on the OS timer.
    //
    // There is no public way to fix it. The clock, the implementation and the
    // interface they share are all private or [PrivateApi], so this reaches
    // them by reflection, once, and says so if they have moved: Avalonia
    // renaming a field turns this into Unavailable rather than an exception,
    // and DispatcherClockTests fails loudly on that, so an upgrade cannot put
    // the lateness back quietly. Upstream would be the better home for the
    // fix — the dispatcher could re-bind its clock when its implementation is
    // replaced — and none of this is needed on a version that does that: Align
    // measures the offset first and does nothing when there is none.
    internal static class DispatcherClock
    {
        // Two stopwatches started back to back still read a millisecond apart
        // now and then, and the ElapsedMilliseconds they are read through
        // truncates. Anything inside that is the same clock.
        internal const long ToleranceMs = 1;

        internal enum Outcome
        {
            // The two clocks already agreed; nothing was touched.
            AlreadyAligned,

            // The dispatcher now reads the platform's clock.
            Aligned,

            // The members this reaches for are not where they were in Avalonia
            // 12.1.1, or the call came from the wrong thread. Nothing was
            // touched, so timers keep whatever lateness they had.
            Unavailable
        }

        internal readonly record struct Result(Outcome Outcome, long OffsetMs, int TimersRestarted);

        // The pure half: given one reading of each clock, how far apart are
        // they, and is that far enough to act on?
        internal static long Offset(long dispatcherNow, long platformNow) => dispatcherNow - platformNow;

        internal static bool NeedsAlignment(long offsetMs) => Math.Abs(offsetMs) > ToleranceMs;

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo? TimeProviderField =
            typeof(Dispatcher).GetField("_timeProvider", Instance);

        private static readonly FieldInfo? ImplField =
            typeof(Dispatcher).GetField("_impl", Instance);

        private static readonly FieldInfo? TimersField =
            typeof(Dispatcher).GetField("_timers", Instance);

        private static readonly PropertyInfo? DispatcherNow =
            typeof(Dispatcher).GetProperty("Now", Instance);

        private static readonly MethodInfo? PlatformNowGetter =
            typeof(IDispatcherImpl).GetProperty("Now")?.GetGetMethod();

        // Whether every member Align needs is where it expects. Separate so the
        // test can say which half broke: this, after an Avalonia upgrade, or the
        // alignment itself.
        internal static bool Supported =>
            TimeProviderField?.FieldType == typeof(Func<long>)
            && ImplField is not null
            && TimersField?.FieldType == typeof(List<DispatcherTimer>)
            && DispatcherNow?.PropertyType == typeof(long)
            && PlatformNowGetter is not null;

        // Make the dispatcher read the platform's clock. Call it on the UI
        // thread once the platform is up — AppBuilder.AfterPlatformServicesSetup
        // is the earliest point — and before much has had a chance to start a
        // timer, though anything that has is handled below.
        internal static Result Align(Dispatcher dispatcher) => Align(dispatcher, Supported);

        // `supported` is a parameter so the test can take the Unavailable arm
        // without an Avalonia that has actually moved the fields.
        internal static Result Align(Dispatcher dispatcher, bool supported)
        {
            if (!supported || !dispatcher.CheckAccess()) return new(Outcome.Unavailable, 0, 0);

            // Never null: the dispatcher builds a managed implementation for
            // itself when it is constructed without one.
            var impl = (IDispatcherImpl)ImplField!.GetValue(dispatcher)!;

            var platformNow = (Func<long>)PlatformNowGetter!.CreateDelegate(typeof(Func<long>), impl);
            var offset = Offset((long)DispatcherNow!.GetValue(dispatcher)!, platformNow());

            if (!NeedsAlignment(offset)) return new(Outcome.AlreadyAligned, offset, 0);

            TimeProviderField!.SetValue(dispatcher, platformNow);

            // A timer already waiting has its due time on the old clock, so left
            // alone it would fire exactly as late as before, once. Restarting
            // stamps it again on the new one. Only running timers are in the
            // list — Stop takes a timer out — and Stop is also why this walks a
            // copy.
            var timers = ((List<DispatcherTimer>)TimersField!.GetValue(dispatcher)!).ToList();

            foreach (var timer in timers)
            {
                timer.Stop();
                timer.Start();
            }

            return new(Outcome.Aligned, offset, timers.Count);
        }
    }
}
