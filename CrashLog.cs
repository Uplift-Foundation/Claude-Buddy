using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // What the process leaves behind when it dies of an unhandled exception.
    //
    // Until now: nothing. There was no AppDomain.UnhandledException handler, no
    // TaskScheduler.UnobservedTaskException handler, no dispatcher handler and
    // no log file anywhere — `~/Library/Application Support/ClaudeBuddy/` held
    // one settings.json and that was the whole record of the app's existence.
    //
    // **What that costs is measured, not hypothetical.** Buddy aborted twice on
    // the headless mini on 28 Aug 2026, both times exactly two hours after
    // launch. The only artifact was a pair of `.ips` reports whose managed
    // frames are unsymbolicated JIT addresses, so the exception type and message
    // were unrecoverable from them: the stack read `IL_Throw ->
    // DispatchManagedException -> PROCAbort` and then a column of question
    // marks. Identifying it as Avalonia refusing to start on a thread that did
    // not own its dispatcher (CB-28) took two crash reports, a read of Avalonia
    // 12.1.1's source, and a purpose-built probe replaying the failure against
    // the app assembly. One line in a file would have named it in seconds.
    //
    // So the bar here is deliberately low: name the exception, say which of the
    // three paths caught it, timestamp it, and get it onto disk before the
    // runtime tears the process down. It is not telemetry, it is not a logging
    // framework, and it never reports anything anywhere — it writes a file on
    // the user's own machine that the user can read, delete, or ignore.
    //
    // **A crash logger that throws is worse than none**, because it turns a
    // diagnosable crash into a crash inside the diagnosis. Every public entry
    // point here swallows everything: a full disk, a read-only home, a
    // directory somebody deleted mid-write. The worst outcome allowed is that
    // nothing is written, which is exactly where the app was before.
    internal static class CrashLog
    {
        // ~/Library/Logs/ClaudeBuddy on macOS, %LOCALAPPDATA%\ClaudeBuddy\Logs on
        // Windows — each platform's own answer to "where do logs go", rather than
        // a directory of this app's invention next to settings.json.
        //
        // The env override is the same test seam as CLAUDE_BUDDY_SETTINGS_DIR and
        // exists for the same reason: without it a test that exercises this
        // writes into the developer's real log directory, and the one thing a
        // crash log must not do is fill up with test noise.
        internal static string Directory =>
            Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR") is { Length: > 0 } scratch
                ? scratch
                : DefaultDirectory;

        // Excluded from coverage: reads the real user profile. Every test runs
        // with CLAUDE_BUDDY_LOG_DIR pointed at a scratch directory, which is the
        // point of the seam above.
        [ExcludeFromCodeCoverage]
        private static string DefaultDirectory =>
            OperatingSystem.IsWindows()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeBuddy",
                    "Logs")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Logs",
                    "ClaudeBuddy");

        internal static string Path_ => Path.Combine(Directory, "crash.log");

        // The previous file, kept so that the crash *before* the one being
        // investigated is still readable. Two is the right number here: the
        // interesting comparison is nearly always "this crash versus the last
        // one", and anything more is a retention policy nobody asked for.
        internal static string PreviousPath => Path.Combine(Directory, "crash.log.1");

        // Roll over at a quarter of a megabyte. Small on purpose: this file only
        // ever holds crashes, so a large one means either a crash loop — where
        // the newest entries are the ones worth having — or something writing to
        // it that should not be.
        internal const long MaxBytes = 256 * 1024;

        // One entry, as text. Pure, so what an entry actually says can be
        // asserted without a filesystem — and it is worth asserting, because the
        // whole value of this file is that a person reading it in six months
        // learns something. The order is deliberate: when, where, what, then the
        // stack, so the first line alone is usually enough.
        internal static string Format(DateTimeOffset when, string source, Exception? error)
        {
            var text = new StringBuilder();

            text.Append("=== ").Append(when.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append("  ").Append(source)
                .Append("  pid ").Append(Environment.ProcessId)
                .AppendLine();

            if (error is null)
            {
                // The runtime can hand a handler a non-Exception object — the
                // ExceptionObject on AppDomain.UnhandledException is typed
                // `object` for exactly that reason. Recording "something went
                // wrong and it was not an Exception" is still more than the
                // nothing this replaces.
                text.AppendLine("    (no exception object was provided)");
                return text.ToString();
            }

            text.Append("    ").Append(error.GetType().FullName)
                .Append(": ").AppendLine(error.Message);

            // Inner exceptions first, because the outer one is usually the
            // wrapper and the inner one is the answer — TargetInvocationException
            // over the real fault is the shape this app hits most.
            var inner = error.InnerException;
            var depth = 0;

            while (inner is not null && depth < 5)
            {
                text.Append("    ---> ").Append(inner.GetType().FullName)
                    .Append(": ").AppendLine(inner.Message);

                inner = inner.InnerException;
                depth++;
            }

            if (!string.IsNullOrWhiteSpace(error.StackTrace))
            {
                text.AppendLine(error.StackTrace!.TrimEnd());
            }

            return text.ToString();
        }

        // Write one entry, and never fail doing it.
        internal static void Record(string source, Exception? error) =>
            Append(Format(DateTimeOffset.Now, source, error));

        // One writer at a time, within this process.
        //
        // Not belt-and-braces: the three handlers are genuinely concurrent — an
        // AppDomain throw on one thread, a dispatcher throw on the UI thread and
        // a faulted task being collected by the finalizer are three different
        // threads reaching this at once — and a rotation racing an append is a
        // lost entry. Measured while writing the test for it: twenty-four
        // threads recording at once produced fewer than twenty-four entries
        // before this lock existed, and the missing ones were silent.
        private static readonly object Gate = new();

        // The whole of the file handling, kept in one place so the swallowing is
        // in one place too.
        internal static void Append(string entry)
        {
            lock (Gate)
            {
                // Three goes, because the plausible failures here are transient
                // and the entry is not repeatable: an antivirus scanner holding
                // the file open on Windows, or a second process mid-rotation.
                // A sleep would be wrong — the process may be seconds from being
                // torn down — so the retries are immediate.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(Directory);
                        Rotate();

                        // Append rather than rewrite, and FileShare.ReadWrite
                        // because the user may well have the file open in
                        // Console.app while the thing they are chasing happens
                        // again.
                        using var stream = new FileStream(
                            Path_, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var writer = new StreamWriter(stream, Encoding.UTF8);

                        writer.Write(entry);
                        writer.Flush();
                        stream.Flush(flushToDisk: true);
                        return;
                    }
                    catch
                    {
                        // A full disk, a read-only home, a directory deleted
                        // under us. The process is usually already dying when
                        // this runs; making it die of something else instead
                        // would be a poor trade.
                    }
                }
            }
        }

        // Move the current file aside once it is big enough, replacing whatever
        // was there before it.
        private static void Rotate()
        {
            try
            {
                var current = new FileInfo(Path_);
                if (!current.Exists || current.Length < MaxBytes) return;

                File.Move(Path_, PreviousPath, overwrite: true);
            }
            catch
            {
                // Losing the rotation is survivable; losing the entry that
                // prompted it is not, so this never stops the write.
            }
        }

        // Subscribe to the three ways an exception escapes.
        //
        // They catch genuinely different failures, which is why it is three and
        // not one:
        //
        //   AppDomain.UnhandledException  — a throw on any thread that nothing
        //     caught. This is the one that fires for the crash that started all
        //     of this, because Avalonia's platform init throws on the main
        //     thread before any dispatcher is pumping.
        //   TaskScheduler.UnobservedTaskException — a faulted Task nobody
        //     awaited. It does not kill the process on modern .NET, which makes
        //     it the *only* record such a failure leaves; this app is full of
        //     fire-and-forget `_ = SomethingAsync()` calls by design.
        //   Dispatcher.UIThread.UnhandledException — a throw inside a UI
        //     callback. Avalonia catches these itself, so neither handler above
        //     ever sees them.
        //
        // Idempotent, because being called twice must not double every entry —
        // and Main is not the only plausible caller of something like this.
        //
        // Excluded from coverage: what it does is subscribe process-wide runtime
        // events, and a test that subscribed them would then be recording every
        // other test's exceptions for the rest of the run. What each handler
        // *writes* is Record and Format, which are covered directly.
        [ExcludeFromCodeCoverage]
        internal static void Install()
        {
            if (_installed) return;
            _installed = true;

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Record("AppDomain.UnhandledException", e.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Record("TaskScheduler.UnobservedTaskException", e.Exception);

                // Deliberately not marked observed. Marking it would change the
                // app's behaviour to suit its logging, and the default is the
                // one the runtime documents.
            };

            // Touching Dispatcher.UIThread claims it for this thread — see
            // Startup.ClaimUiThread, which is why this runs from Main and only
            // from Main. Subscribing here is not what claims it (the claim is
            // its own step, first, so that reading this line does not become
            // load-bearing), but it would be enough on its own, and that is
            // worth knowing before anyone moves this call.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
                Record("Dispatcher.UnhandledException", e.Exception);
        }

        private static bool _installed;
    }
}
