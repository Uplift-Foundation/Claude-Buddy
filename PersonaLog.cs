using System.Text;

namespace ClaudeBuddy
{
    // Why a persona picture was not drawn.
    //
    // Every other refusal in PersonaFiles is a *safety* refusal — a rooted
    // path, a symlink out of the directory, a file this process may not read —
    // and those are all shapes the person who wrote the markdown chose. The
    // size cap is not like that. A portrait is exported once from whatever tool
    // took it, at whatever size that tool felt like, and nothing about the file
    // says it is too big; the app simply draws letters instead, exactly as it
    // would for a persona that named no picture at all. The two outcomes are
    // indistinguishable from outside, which is how `.claude/cto.png` sat 57 KB
    // under the old 2 MiB cap in this repository without anybody knowing the
    // margin existed (CB-135).
    //
    // So: one line, saying which file, which reason, and what the cap is. That
    // is the whole feature. It is not telemetry, it reports nothing anywhere,
    // and — like CrashLog, whose directory it shares and whose failure rule it
    // copies — it never throws. A picture failing to draw is a small
    // disappointment; a persona scan that dies trying to write about it would
    // take the orb with it, on the UI thread, every two seconds.
    //
    // Its own file rather than crash.log, because crash.log's own comment says
    // it only ever holds crashes and that a large one means something is wrong.
    // Persona rejections are ordinary, and mixing them in would make that
    // sentence untrue for a reader who has no way to know it changed.
    internal static class PersonaLog
    {
        internal static string Path_ => System.IO.Path.Combine(CrashLog.Directory, "persona.log");

        // A ceiling rather than rotation, the same choice MirrorLog makes: this
        // file holds a handful of lines about a handful of files, and anything
        // approaching this size means the dedupe below has stopped working
        // rather than that the user has 64 KB of broken portraits.
        internal const long MaxBytes = 64 * 1024;

        private static readonly object Gate = new();

        // Said once, not once per scan. The persona scan re-resolves whenever
        // the files it watches move, and *every session in the repository*
        // resolves the same tree — twenty agents in one checkout would write
        // twenty identical lines about one oversized portrait, which is how a
        // diagnostic becomes noise nobody reads. Keyed on the whole message, so
        // the same file rejected for a new reason still says so.
        private static readonly HashSet<string> Said = new(StringComparer.Ordinal);

        internal static void Record(string message) => Write(DateTimeOffset.Now, message);

        // Split from Record so a test can pin the timestamp, and so Format
        // below can be asserted without a filesystem.
        internal static void Write(DateTimeOffset when, string message)
        {
            lock (Gate)
            {
                if (!Said.Add(message)) return;

                try
                {
                    var directory = CrashLog.Directory;
                    System.IO.Directory.CreateDirectory(directory);

                    var file = new FileInfo(Path_);
                    if (file.Exists && file.Length >= MaxBytes) return;

                    File.AppendAllText(Path_, Format(when, message), Encoding.UTF8);
                }
                catch
                {
                    // A full disk, a read-only home, a log directory that is
                    // somebody's file. The picture is already not being drawn;
                    // failing to write that down changes nothing else.
                }
            }
        }

        // One line: when, then what. Short on purpose — the reader of this file
        // is somebody asking "why is there no picture", and the answer has to
        // be the first thing they see.
        internal static string Format(DateTimeOffset when, string message) =>
            when.ToString("yyyy-MM-dd HH:mm:ss zzz") + "  " + message + Environment.NewLine;

        // The dedupe is process-wide and deliberately never expires, which
        // makes it state a test has to be able to clear — the same seam, and
        // for the same reason, as LocalPersonas.SetForTests.
        internal static void ResetForTests()
        {
            lock (Gate) Said.Clear();
        }
    }
}
