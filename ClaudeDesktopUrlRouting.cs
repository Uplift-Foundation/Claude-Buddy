namespace ClaudeBuddy
{
    // Which profile a `claude://` link belongs to.
    //
    // Claude Desktop claims two URL schemes — `claude:` for deep links and
    // `msauth.com.anthropic.claudefordesktop:` for the Microsoft sign-in
    // callback — and LaunchServices resolves a scheme to a *bundle*, by bundle
    // id. That is fine for one installed app and actively wrong here: every
    // tinted clone this app makes is a byte-identical copy of Claude.app, so
    // every clone carries the same bundle id and claims the same two schemes.
    // Three bundles answering to one id is not a tie macOS can break, and the
    // one it picks — the installed /Applications/Claude.app — is never the
    // bundle a coloured profile runs from.
    //
    // Worse, a LaunchServices-initiated launch carries no CLAUDE_USER_DATA_DIR,
    // because only our own `open --env` ever sets it. So the callback from a
    // sign-in started in *any* profile lands in a fresh instance on the
    // *Default* profile: the profile being signed into never receives its
    // token, the user sees a Default window they didn't ask for, and they try
    // again. It reads as intermittent only because signing in to Default itself
    // works correctly.
    //
    // The fix is to stop letting a bundle id decide. Claude Buddy claims both
    // schemes itself and forwards each URL to one specific instance, addressed
    // by its clone's *path* — which is unique per profile even though the id
    // is not. This file is the part of that with no AppKit in it: given what is
    // running and which instance the user was last in, which profile should
    // receive the link. See ClaudeDesktopUrlRouter for the delivery, and
    // MacOSUrlScheme for the interop.
    internal sealed record UrlRouteCandidate(
        string ProfileDirectory,
        // The bundle this profile runs from: its tinted clone, or the installed
        // /Applications/Claude.app when it has no clone. Unique per profile,
        // which is the whole point — the bundle id is not.
        string BundlePath,
        bool IsDefault,
        bool IsRunning,
        int Pid);

    internal sealed record UrlRoute(
        string ProfileDirectory,
        string BundlePath,
        // Null for Default, exactly as LaunchMac does it: setting the variable
        // suppresses the app's own resolution of its sidecar config directory,
        // so a forwarded link could re-trigger the deployment-mode chooser on
        // an already configured profile.
        string? UserDataDir,
        bool AlreadyRunning,
        int Pid);

    internal static class ClaudeDesktopUrlRouting
    {
        // `lastActivePid` is the most recent Claude Desktop instance to have
        // been frontmost — not necessarily frontmost *now*, because by the time
        // a sign-in callback arrives the browser has had focus for a while.
        // That is the whole reason it is tracked over time rather than read at
        // delivery: "which Claude window was the user in before they were sent
        // to the browser" is the question, and asking it after the round trip
        // gets the browser as the answer.
        //
        // Pass 0 when nothing is known; every rule below still has an answer.
        public static UrlRoute? Choose(IReadOnlyList<UrlRouteCandidate> candidates, int lastActivePid)
        {
            if (candidates.Count == 0) return null;

            var running = candidates.Where(c => c.IsRunning).ToList();

            // 1. The instance the user was last in. A sign-in is started from a
            //    window, so this is the profile that asked for the callback.
            if (lastActivePid > 0)
            {
                var remembered = running.FirstOrDefault(c => c.Pid == lastActivePid);
                if (remembered is not null) return RouteTo(remembered);
            }

            // 2. Only one instance running: there is nothing to be ambiguous
            //    about, and this covers the common single-profile case where no
            //    activation has been observed yet (a link arriving before the
            //    poll has ever seen a frontmost Claude).
            if (running.Count == 1) return RouteTo(running[0]);

            // 3. Several running and no usable hint. Default is the least
            //    surprising of them — it is what macOS would have done on its
            //    own — and falling back to the lowest pid rather than list order
            //    keeps the answer stable across scans, which matters because an
            //    unstable answer here means a link that lands somewhere
            //    different each time for no visible reason.
            if (running.Count > 1)
            {
                var preferred = running.FirstOrDefault(c => c.IsDefault)
                                ?? running.OrderBy(c => c.Pid).First();
                return RouteTo(preferred);
            }

            // 4. Nothing running at all, so the link has to launch something.
            //    Default, which is what the user would have got before this
            //    router existed — the difference is that it now launches
            //    deliberately and by path, rather than by a bundle id that three
            //    bundles answer to.
            var fallback = candidates.FirstOrDefault(c => c.IsDefault)
                           ?? candidates.OrderBy(c => c.ProfileDirectory, StringComparer.Ordinal).First();

            return RouteTo(fallback);
        }

        private static UrlRoute RouteTo(UrlRouteCandidate candidate) =>
            new(candidate.ProfileDirectory,
                candidate.BundlePath,
                candidate.IsDefault ? null : candidate.ProfileDirectory,
                candidate.IsRunning,
                candidate.IsRunning ? candidate.Pid : 0);

        // The schemes Claude Desktop declares in its Info.plist, and therefore
        // the ones a clone declares too. `claude` is deep links; the `msauth.`
        // one is the Microsoft sign-in callback, and is the reason this bug
        // shows up as "I can't log in" rather than only as a stray window.
        public static readonly string[] Schemes =
        {
            "claude",
            "msauth.com.anthropic.claudefordesktop"
        };

        // True for a URL this router is willing to forward. Anything else is
        // left alone rather than guessed at: we became the system handler for
        // these two schemes, so we are answerable for delivering exactly them
        // and nothing else.
        public static bool Handles(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            var colon = url.IndexOf(':');
            if (colon <= 0) return false;

            var scheme = url[..colon];
            return Schemes.Any(s => string.Equals(s, scheme, StringComparison.OrdinalIgnoreCase));
        }
    }
}
