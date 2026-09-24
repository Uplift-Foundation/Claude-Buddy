namespace ClaudeBuddy
{
    // What sounds this machine already has, and turning a setting string back
    // into a file ChimePlayer can open.
    //
    // No sound ships with this app. That was the point of the decision, not
    // an oversight: bundling audio raises a licensing question the settings
    // this feature reads never has to answer, and every platform this app
    // runs on already ships a drawer of short, unobtrusive sounds nobody has
    // to be asked to approve. List and Resolve both take the directory and
    // the extensions to look in as parameters rather than reading a platform
    // switch internally, which is what lets a test point either of them at a
    // handful of files in a temp directory instead of trusting whatever is
    // actually installed on the machine running the suite.
    internal static class SystemSoundCatalog
    {
        private const string OffSetting = "off";

        // Where the platform keeps its own short system sounds, and what
        // counts as one. macOS ships them as .aiff; Windows as .wav. A user's
        // own "Choose file..." pick can be a wider set (wav/aiff/mp3/m4a on
        // macOS, wav only on Windows, since the Windows player is
        // Media.SoundPlayer and that is what it can open) — that check lives
        // in the file picker, not here, because an absolute path is trusted
        // on its own existence rather than re-validated against this list.
        internal static string DefaultDirectory =>
            OperatingSystem.IsWindows() ? @"C:\Windows\Media" : "/System/Library/Sounds";

        internal static string[] DefaultExtensions =>
            OperatingSystem.IsWindows() ? new[] { ".wav" } : new[] { ".aiff" };

        // The two defaults from the plan's decisions section. Named rather
        // than resolved here, because "the name of the default" and "whether
        // that name resolves to a real file on this machine" are different
        // questions — Resolve below answers the second one, and a machine
        // where the name is missing gets silence rather than an exception,
        // exactly as an explicit "off" would.
        internal static string DefaultFinishedSoundName =>
            OperatingSystem.IsWindows() ? "Windows Notify Messaging" : "Glass";

        internal static string DefaultAttentionSoundName =>
            OperatingSystem.IsWindows() ? "Windows Notify System Generic" : "Ping";

        // Every sound file directly inside `directory` whose extension is one
        // of `extensions`, named the way a setting refers to it: no
        // extension, no directory. What the settings picker and the orb's
        // Sound submenu both show comes straight from this list, so a name
        // that appears there is always one Resolve can turn back into a
        // path.
        internal static List<string> List(string directory, string[] extensions)
        {
            var names = new List<string>();

            // A missing directory is not an error here — it is what "this
            // platform's sound drawer isn't where we expect" or "this is a
            // test pointed at a directory it never created" both look like,
            // and both mean the same thing: nothing to list.
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return names;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var extension = Path.GetExtension(file);
                var matches = false;
                foreach (var candidate in extensions)
                {
                    if (!string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)) continue;
                    matches = true;
                    break;
                }

                if (matches) names.Add(Path.GetFileNameWithoutExtension(file));
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        // A setting to a playable path, or null when there isn't one.
        //
        // Three shapes of input, three answers: null/empty/"off" is a
        // deliberate absence and always null back; an absolute path is the
        // user's own file and is trusted as-is, checked only for actually
        // being there; anything else is a bare name looked up in `directory`
        // against each of `extensions` in turn. A name that used to resolve
        // and no longer does — a system sound removed, a chosen file deleted
        // — comes back null exactly the same as an explicit "off", which is
        // the plan's rule that a missing default is silence rather than an
        // error nobody asked for.
        internal static string? Resolve(string? setting, string directory, string[] extensions)
        {
            if (string.IsNullOrWhiteSpace(setting)) return null;
            if (string.Equals(setting, OffSetting, StringComparison.OrdinalIgnoreCase)) return null;

            if (Path.IsPathRooted(setting))
            {
                return File.Exists(setting) ? setting : null;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, setting + extension);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }
    }
}
