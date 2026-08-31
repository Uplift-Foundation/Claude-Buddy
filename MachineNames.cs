using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // What this machine calls itself on a wire, and how to recognise a relay
    // that outlived the code that started it.
    //
    // **Both of these used to live in RemoteControlBridge, and both outlive
    // it.** The tag is what a peer announcement carries, which has nothing to do
    // with relays and only lived there because the relay was the first thing
    // that needed a short, safe machine name. The relay-name test is the
    // opposite case: the relay is gone, and precisely because it is gone this
    // has to stay — see LooksLikeALeftoverRelay.
    internal static class MachineNames
    {
        // The prefix Claude Buddy used to give a relay's tmux session and scratch
        // directory. Kept as a literal now that nothing constructs one.
        private const string RelayPrefix = "claude-buddy-rc-";

        // What this machine is called, as its owner would recognise it.
        //
        // **Environment.MachineName is the wrong source on macOS, and it took a
        // user seeing the answer to notice.** It returns `gethostname()`, which
        // is a *network* name and picks up whatever the DHCP search domain
        // supplies. On a real Mac mini here it produced `avatar.internal` →
        // "avatar", a word its owner had never chosen and did not recognise;
        // macOS's own name for the same machine is "Warren's Mac mini".
        //
        // That matters more than a label, because this name is the key a
        // pairing is stored under. A machine that names itself after a DNS
        // artefact is a machine somebody has to squint at in a list of peers
        // before deciding whether to trust it.
        //
        // LocalHostName first: it is the name macOS publishes on the network,
        // it is already restricted to letters, digits and hyphens, and it
        // tracks ComputerName automatically. ComputerName next, which is the
        // free-text one from Settings and needs the sanitising below. Then
        // gethostname, which is what this used to be and is still better than
        // nothing.
        [ExcludeFromCodeCoverage]
        internal static string Tag() => Tag(Preferred(Ask, () =>
        {
            try { return Environment.MachineName; }
            catch { return ""; }
        }));

        // Which of the candidates to use. Pure so the order is a rule with a
        // test rather than a chain of nulls inside a P/Invoke.
        internal static string Preferred(Func<string, string?> ask, Func<string> fallback)
        {
            if (!OperatingSystem.IsMacOS()) return fallback();

            foreach (var key in new[] { "LocalHostName", "ComputerName" })
            {
                var answer = ask(key);
                if (!string.IsNullOrWhiteSpace(answer)) return answer!;
            }

            return fallback();
        }

        // Asks macOS what it calls this machine.
        //
        // Excluded from coverage: a subprocess whose answer differs per machine.
        // Cached because the answer does not change while the app runs and this
        // is on the path of every announcement.
        private static readonly Dictionary<string, string?> Asked = new();

        [ExcludeFromCodeCoverage]
        private static string? Ask(string key)
        {
            lock (Asked)
            {
                if (Asked.TryGetValue(key, out var cached)) return cached;
            }

            string? answer = null;

            try
            {
                using var scutil = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("/usr/sbin/scutil")
                    {
                        ArgumentList = { "--get", key },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    });

                if (scutil is not null)
                {
                    answer = scutil.StandardOutput.ReadToEnd().Trim();

                    // Bounded, because this runs during startup and a wedged
                    // scutil must not be the thing that stops Buddy launching.
                    if (!scutil.WaitForExit(2000)) answer = null;
                }
            }
            catch
            {
                // Any failure falls through to the next candidate, which ends at
                // the behaviour this replaced.
            }

            lock (Asked) Asked[key] = answer;

            return answer;
        }

        // The one name this machine goes by: announced by discovery, offered in
        // the greeting, and written into the certificate's subject.
        //
        // **One, because three were already disagreeing.** Discovery announced
        // Tag() while the greeting sent Environment.MachineName, and on this
        // machine those happen to differ only by case — which is why nothing
        // had noticed. On a Mac whose LocalHostName is not its gethostname,
        // they differ by more than case, and a peer discovered under one name
        // and greeting under another is a peer that pairs and is then never
        // dialled again.
        //
        // Case and hyphens kept, unlike Tag: this is a name a person reads in a
        // list of machines and matches against what macOS shows them in
        // Sharing settings. Thirty-two characters because it is not a tmux
        // target any more and nothing else here is length-bound.
        [ExcludeFromCodeCoverage]
        internal static string Mine() => Clean(Preferred(Ask, () =>
        {
            try { return Environment.MachineName; }
            catch { return ""; }
        }));

        // Made safe for a wire without being made ugly.
        //
        // Pure, and separate from Tag because they want different things: a tag
        // was a tmux session name and had to be short and lower-case, while this
        // is shown to a person and stored as a key.
        internal static string Clean(string? name)
        {
            name ??= "";

            // ".local" is Bonjour's, not the user's, and carries no information.
            if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                name = name[..^".local".Length];

            var safe = new string(name
                .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                .ToArray())
                .Trim('-');

            if (safe.Length > 32) safe = safe[..32];

            // Never empty: an empty name would make two machines that both
            // failed to report one collide with each other.
            return safe.Length == 0 ? "machine" : safe;
        }

        // Split from the call to Environment.MachineName so every branch below
        // can be tested: a headless runner has exactly one machine name, and the
        // interesting cases are the ones it does not have.
        internal static string Tag(string? name)
        {
            name ??= "";

            // ".local" is Bonjour's, not the user's: every Mac's hostname ends
            // in it, so it carries no information and costs six of the twenty
            // characters there are. Dropped first, before the length cap, or a
            // perfectly ordinary "Warrens-MacBook-Pro.local" truncates to
            // "warrens-macbook-prol".
            if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                name = name[..^".local".Length];

            var safe = new string(name
                .Where(c => char.IsLetterOrDigit(c) || c == '-')
                .ToArray())
                .Trim('-')
                .ToLowerInvariant();

            if (safe.Length > 20) safe = safe[..20];

            // Never empty: an empty tag would put a trailing dash on the name
            // and, worse, would make two machines that both failed to report a
            // name collide again.
            return safe.Length == 0 ? "machine" : safe;
        }

        // --- leftovers ---------------------------------------------------------------

        // Whether a working directory belongs to a relay this app used to start.
        //
        // **This survives the relay's deletion, and it survives *because* of
        // it.** Removing the code that starts a relay does not remove the relays
        // already running on somebody's machine: a user upgrading has one live
        // per account this second, in a tmux pane, holding a real Claude Code
        // session. Nothing will ever start another, and nothing stops those from
        // running for days.
        //
        // Without this filter each of them becomes an orb the moment the upgrade
        // lands — a session the user never started, named after an internal
        // mechanism, which is a worse first impression of the new version than
        // any bug in it. They vanish on their own once those panes are closed.
        //
        // Delete this when a release has been out long enough that no relay
        // from before it is plausibly still running, and not before.
        internal static bool LooksLikeALeftoverRelay(string? cwd) =>
            !string.IsNullOrEmpty(cwd) && IsRelayName(TerminalScripts.LeafOf(cwd));

        internal static bool IsRelayName(string? name) =>
            !string.IsNullOrEmpty(name)
            && name.StartsWith(RelayPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
