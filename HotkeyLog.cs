using System.Text;

namespace ClaudeBuddy
{
    // Why a global hotkey is not on the chord its setting names — the Notes
    // HotkeyRegistry.Plan writes when two actions resolve to one chord.
    //
    // A hotkey that loses a collision looks, from outside, exactly like one
    // whose override was ignored or one the OS never delivered: the key does
    // nothing, or does the default thing. One line here is the only place
    // that says which. Its own file beside crash.log and persona.log rather
    // than in either, because each of those states what it holds, and this
    // is neither a crash nor a persona. Same rules as PersonaLog, for the
    // same reasons: never throws, says each thing once, and has a ceiling.
    internal static class HotkeyLog
    {
        internal static string Path_ => System.IO.Path.Combine(CrashLog.Directory, "hotkeys.log");

        internal const long MaxBytes = 64 * 1024;

        private static readonly object Gate = new();
        private static readonly HashSet<string> Said = new(StringComparer.Ordinal);

        internal static void Record(string message) => Write(DateTimeOffset.Now, message);

        internal static void Write(DateTimeOffset when, string message)
        {
            lock (Gate)
            {
                if (!Said.Add(message)) return;

                try
                {
                    System.IO.Directory.CreateDirectory(CrashLog.Directory);

                    var file = new FileInfo(Path_);
                    if (file.Exists && file.Length >= MaxBytes) return;

                    File.AppendAllText(Path_, PersonaLog.Format(when, message), Encoding.UTF8);
                }
                catch
                {
                    // The hotkey is already on its fallback or unregistered;
                    // failing to write that down changes nothing else.
                }
            }
        }

        internal static void ResetForTests()
        {
            lock (Gate) Said.Clear();
        }
    }
}
