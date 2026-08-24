namespace ClaudeBuddy
{
    // Where the `claude` CLI is, for the parts of this app that shell out to it.
    //
    // This app can't count on PATH. Launched from Finder or as a login item it
    // gets the bare system PATH, which has none of the places `claude` installs
    // to. The obvious workaround — hand the command to the user's own shell and
    // let it resolve — looks right and isn't: `zsh -lc` reads .zshenv, .zprofile
    // and .zlogin, but *not* .zshrc, because -c means non-interactive and .zshrc
    // is the interactive file. A PATH addition for ~/.local/bin normally lives
    // in .zshrc, so the lookup failed with "command not found" precisely when
    // launched the way users actually launch this.
    //
    // So resolve the binary the same way TerminalFocuser resolves tmux: check
    // the known install locations directly. PATH is still consulted last, for
    // an install none of these anticipate.
    internal static class ClaudeBinary
    {
        private static readonly object Gate = new();
        private static bool _looked;
        private static string? _path;

        // Null when nothing was found, which every caller treats as "skip this
        // feature" rather than an error — the app's own work doesn't depend on
        // the CLI being reachable.
        public static string? Path
        {
            get
            {
                lock (Gate)
                {
                    if (_looked) return _path;
                    _looked = true;
                    _path = Locate();
                    return _path;
                }
            }
        }

        // The install locations that don't depend on who is logged in. Named
        // rather than inlined below so a test can substitute its own — see the
        // comment on Locate.
        private static readonly string[] SystemInstalls =
        {
            "/opt/homebrew/bin/claude",
            "/usr/local/bin/claude",
            "/usr/bin/claude"
        };

        // Every input is a parameter with the real one as its default, so a test
        // can hand it a temp directory, a PATH and a candidate list of its own
        // rather than depending on the machine it runs on.
        //
        // All three, not just the home directory, and that is the point rather
        // than over-engineering: the system locations are absolute, so a test
        // that only controlled `home` would still find a real `claude` on the
        // developer's Mac and find nothing on a CI runner — the same test
        // passing for two different reasons, which CLAUDE.md calls out as the
        // way to block a build with a test that only works where it was
        // written. Setting HOME for the whole process was the other option, and
        // every other test in the assembly would have been sharing it.
        internal static string? Locate(
            string? home = null, string? searchPath = null, string[]? systemInstalls = null)
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string[] candidates =
            [
                System.IO.Path.Combine(home, ".local", "bin", "claude"),
                System.IO.Path.Combine(home, ".claude", "local", "claude"),
                .. systemInstalls ?? SystemInstalls
            ];

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            // Last resort: whatever PATH this process did inherit. Worth trying
            // because a session started from a terminal has a real one, and it
            // costs nothing when it doesn't.
            var path = searchPath ?? Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;

            foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = System.IO.Path.Combine(dir, "claude");
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // A malformed PATH entry is not worth failing the lookup for.
                }
            }

            return null;
        }
    }
}
