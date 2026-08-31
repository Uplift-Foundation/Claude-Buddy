using System.IO;

namespace ClaudeBuddy
{
    // Which config directory to hand a `claude` this app starts, given the
    // profile the user named — and, for the default profile, the answer is
    // "none, let it decide".
    //
    // **Setting CLAUDE_CONFIG_DIR to the default directory is not the same as
    // not setting it, and that is the whole of CB-42.** Claude Code keeps
    // onboarding and identity per config *context*: with the variable set to
    // `$HOME/.claude` it reads `$HOME/.claude/.claude.json`, and with no
    // variable at all it reads `$HOME/.claude.json`. Those are two different
    // files that routinely disagree, and where they do, a relay launches
    // straight into first-run setup — a theme picker, or `Not logged in ·
    // Please run /login` — and sits there forever, holding a tmux session,
    // never becoming a relay.
    //
    // Measured on the user's MacBook, 29 Aug 2026: `~/.claude.json` had
    // `oauthAccount` and `hasCompletedOnboarding: true`; `~/.claude/.claude.json`
    // had neither key. The default-account relay was still on the theme picker
    // an hour and a half after it started, while the `.claude-board` relay on
    // the same machine worked perfectly — because that context *had* been
    // onboarded. That asymmetry is why this reads as one broken account rather
    // than as a rule about how relays are launched, and it is a first-run cliff:
    // it hits the default account, which is what nearly everyone has.
    //
    // The rule, then: name a context only when the user actually asked for a
    // second one. BackgroundJobs.Read already worked this way and says so in its
    // own comment — the default account is read "with no CLAUDE_CONFIG_DIR of
    // this app's own invention" — so this is the two remaining callers catching
    // up with the one that had it right.
    //
    // **What "none" inherits.** Whatever the environment already says, which for
    // a Buddy launched from Finder or a login item is nothing at all, and so is
    // the context an ordinary `claude` in a terminal would use. A Buddy
    // deliberately launched with CLAUDE_CONFIG_DIR already set keeps running
    // under that account, which is the same choice BackgroundJobs made and is
    // ordinary Unix inheritance; unsetting it would be this app overriding a
    // decision the user made on purpose.
    internal static class ClaudeProfile
    {
        // The config directory to set, or null to set nothing.
        //
        // Compared by resolved path rather than by name, the way
        // BackgroundJobs.ExtraAccountDirs holds the default account out: the
        // settings UI lets someone type ".claude", ".claude/" or an absolute
        // path at their own home, and all three name the same context. Case is
        // ignored for the same reason it is there — Windows paths are, and one
        // account reached under two capitalizations is one account.
        internal static string? ConfigDirFor(string home, string? profileDir)
        {
            if (string.IsNullOrWhiteSpace(profileDir)) return null;

            var wanted = Resolve(home, profileDir.Trim());
            var standard = Resolve(home, ClaudeBuddySettings.DefaultRemoteControlProfileDir);

            return string.Equals(wanted, standard, StringComparison.OrdinalIgnoreCase)
                ? null
                : wanted;
        }

        // Path.Combine already lets an absolute profile dir win, which is what
        // makes an absolute path pointing back at the default directory
        // recognisable rather than a second spelling of it. GetFullPath then
        // flattens the "./.claude" and ".claude/." spellings onto the same
        // answer, and the trailing separator is trimmed because
        // "$HOME/.claude/" and "$HOME/.claude" are one directory.
        //
        // Falls back to the unresolved combination if the path cannot be
        // resolved at all — a name with a NUL in it, say. Something
        // unresolvable is certainly not the default directory, and handing the
        // caller the raw path keeps this from being the layer that decides a
        // nonsense profile is really the default one.
        private static string Resolve(string home, string profileDir)
        {
            var combined = Path.Combine(home, profileDir);

            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
            }
            catch
            {
                return combined;
            }
        }
    }
}
