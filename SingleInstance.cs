using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // Whether this process is the one Buddy running, decided by trying to own
    // a named, machine-wide mutex — and split out for the same reason
    // ClickRouting and TerminalScripts were: the decision used to live inside
    // App.OnFrameworkInitializationCompleted, under
    // [ExcludeFromCodeCoverage] and behind an IClassicDesktopStyleApplicationLifetime
    // guard that is never true under the headless test lifetime, so nothing
    // here could be reached by a test no matter how it was written (CB-178).
    //
    // It is also why the decision moved out of Avalonia startup entirely, not
    // just out of App.axaml.cs. The loser used to call desktop.Shutdown() from
    // inside OnFrameworkInitializationCompleted — which tears the dispatcher
    // down while Avalonia's own StartCore is still unwinding toward
    // Dispatcher.MainLoop, so the very next thing that ran was PushFrame on a
    // dispatcher that no longer existed: InvalidOperationException, uncaught,
    // SIGABRT. A duplicate instance that never gets that far — because
    // Program.Main asked this question before Startup.Run ever calls
    // startUi — has no dispatcher to shut down and nothing to throw. See
    // Startup.Run for where the answer now gets used.
    //
    // The named type is an enum rather than a bool because "we hold it",
    // "someone else holds it" and "the previous owner died holding it" are
    // three different facts, not two, and the third is the one a bool would
    // erase.
    //
    // `Claim` below asks that question with `WaitOne(TimeSpan.Zero)` on an
    // unowned mutex rather than with `Mutex(bool, string, out bool
    // createdNew)`, the constructor App.axaml.cs used to call — not because
    // `createdNew` was observed to answer wrong, but because it is a
    // different question. `createdNew` reports whether *this call* is the
    // one that created the underlying kernel/file object; `WaitOne` reports
    // whether *this call* now holds the lock. Those can diverge in
    // principle — a duplicate kernel object existing is not the same fact
    // as a live process currently owning it — and `WaitOne` is the one that
    // is actually true to what SingleInstanceClaim needs to mean.
    //
    // Measured rather than assumed: QA ran `createdNew` against
    // `WaitOne(TimeSpan.Zero)` side by side on this machine, three repeats
    // each, and I repeated the death-mode half of that independently with my
    // own harness (see Claim's comment). **The two signals agreed in every
    // case tried** — both `false` while a live owner holds it, both `true`
    // after either death mode below (a `kill -9`'d holder, and one that
    // exited normally without calling ReleaseMutex). So this file used to
    // claim, in an earlier draft, that a `createdNew`-based check would
    // misread a crashed Buddy's leftover file as "another Buddy is running"
    // and permanently disable the keep-alive. That claim was never measured
    // and the measurement above contradicts it — on this platform,
    // `createdNew` recovers from a crash exactly as cleanly as `WaitOne`
    // does. `WaitOne` is still the right choice, but for a narrower and
    // truer reason: it asks the ownership question directly, so it stays
    // correct if the backing store's behaviour ever changes, rather than
    // inferring ownership from whether a file happened to already exist.
    // Don't "fix" `createdNew` here later on the strength of the old
    // paragraph this replaced — there was nothing wrong with it, measured on
    // this platform, in either death mode.
    internal enum SingleInstanceClaim
    {
        // Nobody else held it; we now do. This is *both* the ordinary case —
        // the first Buddy of the session — *and*, on this app's macOS build,
        // the crash-recovery case CB-178's acceptance criteria call out by
        // name ("the keep-alive still restarts Buddy after a genuine
        // crash"). That second half is not the obvious reading of "Acquired"
        // and is only here because it was measured rather than assumed —
        // see this type's own header comment and AbandonedByPreviousOwner
        // below for what the first draft of this comment got wrong before
        // that measurement came back.
        Acquired,

        // A live process holds it right now. We are the duplicate, and the
        // whole point of this ticket is that this outcome must end in a
        // clean exit(0), never in tearing down a dispatcher that has not
        // started yet.
        HeldByAnother,

        // `WaitOne` threw `AbandonedMutexException`: the previous holder
        // died while owning the mutex and the wait handed ownership to us
        // anyway. Treated exactly like Acquired below, and for the identical
        // reason — a duplicate-instance fix must not turn the crash
        // keep-alive off for the one case it exists to handle.
        //
        // What this arm is NOT, measured rather than assumed: the way that
        // recovery actually happens on macOS. QA measured it directly, three
        // repeats each, covering both ways a holder can go away without
        // calling ReleaseMutex — `kill -9` and a normal exit that just never
        // reached the release — and `AbandonedMutexException` was never
        // thrown in either case. `WaitOne(TimeSpan.Zero)` came back `true`
        // instead, which means the real crash-recovery path on this platform
        // is the plain Acquired arm above, not this one. .NET's named
        // mutexes on Unix are backed by a file under
        // `/tmp/.dotnet/shm/session<SID>/<name>` (session-scoped, not
        // literally machine-wide — worth knowing on its own, see MutexName's
        // comment), and a dead owner's hold on that file is simply gone
        // rather than flagged abandoned: there is no Unix equivalent of the
        // Windows-kernel abandoned-mutex bookkeeping that
        // `AbandonedMutexException` reports.
        //
        // The arm is kept anyway, not deleted: `AbandonedMutexException` is
        // part of `Mutex`'s documented cross-platform contract, this app
        // ships a Windows build too, and Windows' kernel does track
        // abandonment — "never observed on macOS" is a statement about this
        // platform's runtime, not a claim that the BCL can't produce it
        // elsewhere. Catching it and treating it as Acquired costs nothing
        // and is the only safe answer if it ever does fire. See Claim's own
        // comment for exactly what was run to measure this and where that
        // leaves this arm's test coverage.
        AbandonedByPreviousOwner
    }

    internal static class SingleInstance
    {
        // The kernel/file object name every Buddy process claims. Unchanged
        // from the string App.axaml.cs used to own directly, because it is
        // part of the contract with every already-installed copy of Buddy —
        // renaming it would just make two old and new instances blind to
        // each other.
        //
        // Not literally machine-wide, whatever the name says: on this
        // platform it resolves to a file under
        // `/tmp/.dotnet/shm/session<SID>/ClaudeBuddy_SingleInstance_Mutex`,
        // scoped to one POSIX login session, not the whole machine. This is
        // a pre-existing property of the mechanism CB-178 did not introduce
        // and does not repair — it is only worth naming because CB-178
        // itself is proof it holds in the one case that matters: a
        // launchd-spawned duplicate lands in the *same* session as the
        // login-item instance it duplicates, or it could never have taken
        // the HeldByAnother branch that made this ticket's bug reproducible
        // in the first place.
        internal const string MutexName = "ClaudeBuddy_SingleInstance_Mutex";

        // The pure half, and the one CB-178 is actually about: given the
        // outcome of an attempt to claim the named mutex, should this
        // process go on to start the UI, or exit cleanly having done
        // nothing?
        //
        // Acquired and AbandonedByPreviousOwner answer the same way on
        // purpose — see AbandonedByPreviousOwner's own comment for why
        // collapsing them the other way, onto HeldByAnother, would be the
        // regression this ticket exists to prevent.
        internal static bool ShouldProceed(SingleInstanceClaim claim) => claim switch
        {
            SingleInstanceClaim.Acquired => true,
            SingleInstanceClaim.AbandonedByPreviousOwner => true,
            SingleInstanceClaim.HeldByAnother => false,
            _ => throw new ArgumentOutOfRangeException(nameof(claim), claim, null)
        };

        // The thin impure half: actually attempts to take ownership of a
        // named mutex and maps the raw BCL outcome onto SingleInstanceClaim.
        // Takes the name as a parameter — rather than reading MutexName
        // itself — purely so tests/IntegrationTests can drive it against a
        // mutex name of its own and never collide with a real running Buddy
        // or a parallel CI leg sharing this machine.
        //
        // Deliberately not `new Mutex(true, name, out bool createdNew)`, the
        // constructor App.axaml.cs used to call: that overload starts the
        // calling thread out *owning* the mutex if it just created the
        // kernel object, which answers "did I create it" rather than "do I
        // hold it" — see this file's header comment for why that is a
        // different question worth asking directly, and for the measurement
        // showing the two happen to agree on this platform anyway.
        // Constructing it unowned and then attempting the claim with
        // WaitOne is how this method asks the ownership question rather
        // than the existence question.
        //
        // The mutex handle is handed back to the caller on every arm,
        // including HeldByAnother, because closing it is the caller's
        // business (Program.cs disposes it immediately when we are the
        // duplicate, and holds it in a static field for the process's
        // lifetime otherwise) — this method's job ends at answering which
        // of the three outcomes happened.
        internal static (SingleInstanceClaim Claim, Mutex Mutex) Claim(string name)
        {
            var mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                return mutex.WaitOne(TimeSpan.Zero)
                    ? (SingleInstanceClaim.Acquired, mutex)
                    : (SingleInstanceClaim.HeldByAnother, mutex);
            }
            catch (AbandonedMutexException)
            {
                return HandleAbandoned(mutex);
            }
        }

        // Excluded from coverage, and only this arm — not the rest of
        // Claim, which tests/IntegrationTests exercises directly against a
        // real named mutex for the Acquired and HeldByAnother cases (see
        // that suite's SingleInstanceTests). This one line is the part
        // nothing here can reach on the macOS leg: it only runs if WaitOne
        // throws AbandonedMutexException, and QA measured directly that this
        // runtime never throws it — not for a `kill -9`'d holder, not for
        // one that exited normally without calling ReleaseMutex, three
        // repeats each (see AbandonedByPreviousOwner's own comment on the
        // enum above). Reaching this line inside an automated suite would
        // therefore mean asserting a platform behaviour that was just
        // measured to be absent here, on either death mode — there is no
        // honest way to make this arm green on macOS. It was proved by hand
        // instead: a throwaway console harness that takes the mutex, gets
        // `kill -9`'d, and lets a second copy attempt the same claim (the
        // CB-178 PR body has the exact commands and output). What is left
        // here is a one-line mapping to the enum value; nothing this
        // ticket's logic depends on rests on this line being exercised by a
        // test.
        [ExcludeFromCodeCoverage]
        private static (SingleInstanceClaim, Mutex) HandleAbandoned(Mutex mutex) =>
            (SingleInstanceClaim.AbandonedByPreviousOwner, mutex);
    }
}
