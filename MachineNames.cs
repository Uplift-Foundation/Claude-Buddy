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

        internal static string Tag()
        {
            string name;
            try { name = Environment.MachineName; }
            catch { name = ""; }

            return Tag(name);
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
