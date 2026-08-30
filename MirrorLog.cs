using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ClaudeBuddy
{
    // What the mirror actually did with a frame, written down.
    //
    // **This exists because six separate faults in this subsystem have produced
    // the same sentence on screen.** "The other machine didn't answer in time"
    // has meant, at different times: a deadline shorter than one relay turn
    // (CB-54), a queue the waiting itself built (CB-55), a poll starving the
    // fetch (CB-56), an idle timer retiring the relay under an open panel
    // (CB-57), a relay that stopped with nothing to restart it (CB-60), and a
    // far machine receiving well-formed frames and answering none of them.
    //
    // Every one of those was diagnosed by reading relay transcripts and
    // inferring, because nothing on the serving side ever recorded a decision.
    // That is a slow way to find a fault and a very easy way to be confident and
    // wrong: this session alone killed five plausible theories that way, each of
    // which would have shipped as a fix.
    //
    // Deliberately cheap and deliberately dull. One line per decision, no
    // levels, no categories, no formatting — a frame arrived, this is what
    // happened to it. The value is entirely in there being a record at all.
    internal static class MirrorLog
    {
        // Off unless asked for, in one of two ways.
        //
        // The env var is for a terminal. The marker file is for a machine where
        // there is no terminal to set one in: the headless mini runs Buddy from
        // a launchd agent, and adding an environment variable to a plist to
        // diagnose a fault is more ceremony than the fault deserves. Touch the
        // file, restart, read the log.
        private static readonly Lazy<string?> Path = new(Resolve);

        private static readonly object Gate = new();

        // A ceiling rather than rotation. This is a diagnostic that somebody
        // turns on, looks at, and turns off; a megabyte is far more than that
        // needs and rotation is machinery this does not earn.
        private const long MaxBytes = 1024 * 1024;

        internal static bool On => Path.Value is not null;

        [ExcludeFromCodeCoverage]
        private static string? Resolve()
        {
            try
            {
                var dir = ClaudeBuddySettings.Directory;
                if (string.IsNullOrEmpty(dir)) return null;

                var asked = !string.IsNullOrWhiteSpace(
                                Environment.GetEnvironmentVariable("CLAUDE_BUDDY_MIRROR_LOG"))
                            || File.Exists(System.IO.Path.Combine(dir, "mirror-log"));

                if (!asked) return null;

                return System.IO.Path.Combine(dir, "mirror-log.txt");
            }
            catch
            {
                return null;
            }
        }

        // The line, given the parts. Pure so the shape can be asserted without
        // touching a disk — and the shape is the whole contract, since the only
        // consumer is a person reading it.
        internal static string Line(DateTime whenUtc, string what, string detail) =>
            $"{whenUtc:HH:mm:ss.fff} {what} {detail}".TrimEnd();

        [ExcludeFromCodeCoverage]
        internal static void Say(string what, string detail = "")
        {
            var path = Path.Value;
            if (path is null) return;

            try
            {
                lock (Gate)
                {
                    if (new FileInfo(path) is { Exists: true, Length: > MaxBytes }) return;

                    File.AppendAllText(
                        path, Line(DateTime.UtcNow, what, detail) + "\n", Encoding.UTF8);
                }
            }
            catch
            {
                // A diagnostic that can break the thing it is diagnosing is
                // worse than no diagnostic.
            }
        }
    }
}
