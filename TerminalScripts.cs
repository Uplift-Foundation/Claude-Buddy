namespace ClaudeBuddy
{
    // The text that gets handed to `osascript`, and the small pure decisions
    // around it.
    //
    // Split out of TerminalFocuser for the reason CLAUDE.md gives for
    // OrbArrangement, OrbGlyph and the transcript parsers, and for the reason
    // TeamLinkGeometry was split out of TeamLinks: TerminalFocuser is 600 lines
    // of tmux, ps and osascript subprocesses that a headless runner must not
    // execute, and these few functions were the only part of it that decides
    // anything without touching the OS. Left in there they were untestable by
    // association.
    //
    // They are worth reaching, because this is a file where being wrong does not
    // show up as a wrong label. The output below is a script that selects a
    // window and sends keystrokes into a live terminal session, so a builder
    // that drops a clause does not display something incorrect — it presses
    // something, somewhere else. Two of the loops here exist because exactly
    // that was reported: orbs going to the wrong desktop on the first click and
    // the right one on the second.
    //
    // Nothing here runs a process. Every comment below is the original
    // reasoning, moved with the code it explains.
    internal static class TerminalScripts
    {
        // The tmux argument list, with the socket pinned when the status file
        // recorded one.
        //
        // -S pins the server: several can coexist (plain tmux, tmuxinator, a -L
        // named socket), and the pane id is only unique within one.
        // How old the app's evidence may be before it refuses to type into a
        // pane. Deliberately not the "Keep orbs for" lifetime — see below.
        internal static readonly TimeSpan PaneEvidenceMaxAge = TimeSpan.FromMinutes(30);

        // Whether a status file is recent enough to be believed about which
        // conversation is sitting in the tmux pane it names.
        //
        // This belongs in this file more squarely than anything else in it. The
        // header above says a builder that drops a clause "does not display
        // something incorrect — it presses something, somewhere else", and two
        // of the loops here exist because that was reported. This rule exists
        // because the same thing happened one level up: not a script pressing
        // the wrong key, but a whole sentence typed into a conversation the user
        // had not chosen.
        //
        // A status file is the app's *only* reason to think a session owns a
        // pane, and a pane is not owned for the life of a process. Claude Code
        // mints a new session id on /clear, on resume, and when a conversation
        // migrates between processes, and the hook writes a new file each time
        // without deleting the old one. So a stale file can name a pane that has
        // moved on to something else entirely.
        //
        // Measured, and the reason this exists: a status file last written at
        // 10:28 still named pane %8, whose only claude — alive, started 10:27 —
        // was by 14:08 four hours into a different conversation, one sharing
        // zero message uuids with it. CanSendQuietly was true and *correct*: the
        // pane was real and tmux was real. So the orb typed, the sentence landed
        // in the other conversation, and the orb's own panel went on reading a
        // transcript that had stopped growing at 10:31. It reads as "the orb
        // isn't responding", which is a kinder symptom than what happened,
        // because the text went somewhere.
        //
        // Nothing in the two files linked them — different ids, different pids,
        // and only the stale one claimed a pane — so no amount of grouping could
        // pair them. The staleness of the evidence is the only signal there is.
        //
        // Kept apart from the orb-lifetime setting on purpose. "Keep orbs for:
        // forever" is the user asking to *see* quiet sessions, which is why that
        // orb was on screen and is entirely reasonable. It is not them saying
        // the app may type into a pane on four-hour-old information. Visibility
        // and delivery are different promises, and only one of them is
        // destructive to get wrong.
        //
        // Generous, because a session in use refreshes this constantly: the hook
        // fires on every prompt and every stop, so anything touched in the last
        // half hour has a fresh file. When it does fall the wrong way the cost is
        // one terminal round-trip, and the composer says which and why rather
        // than failing silently — after which the file is fresh and the orb
        // types as normal.
        internal static bool PaneEvidenceIsCurrent(DateTime written, DateTime now, TimeSpan maxAge)
        {
            // No recorded time is not evidence of staleness. It is a status this
            // app built rather than read — ResetSessionToIdle, a gateway
            // stand-in, a test fixture — and refusing those would stop typing
            // for reasons that have nothing to do with panes.
            if (written == default) return true;

            // A file written a moment ago and one written slightly in the future
            // both land here. Clock skew ahead is not evidence of being behind.
            return now - written <= maxAge;
        }

        // Whether a click should jump to the pane this session names.
        //
        // The same evidence a typed message rests on, so it needs the same
        // guard, and leaving it out was an incomplete fix rather than a
        // deliberate omission: the first version of this rule gated typing only,
        // and a double-click went on jumping straight to the pane. Reported as
        // "double-clicking Ev takes me to the Jl pane" — which is the same
        // stale claim doing the same wrong thing through the other door. A jump
        // is less destructive than a typed sentence, because nothing is written,
        // but it is the same lie about which conversation lives where.
        //
        // A session with no tmux pane is not gated at all. It is reached by tty
        // or by terminal program instead, and this rule is about a pane claim
        // specifically — the case that was measured and the case where a pane id
        // outlives the conversation that owned it.
        internal static bool CanJumpToPane(
            string? tmuxPane, DateTime written, DateTime now, TimeSpan maxAge) =>
            string.IsNullOrEmpty(tmuxPane) || PaneEvidenceIsCurrent(written, now, maxAge);

        internal static string[] TmuxArgs(string? socket, params string[] args)
        {
            if (string.IsNullOrEmpty(socket)) return args;

            var full = new string[args.Length + 2];
            full[0] = "-S";
            full[1] = socket;
            args.CopyTo(full, 2);
            return full;
        }

        // The last path segment, used to name a Windows Terminal tab after the
        // session's directory.
        internal static string LeafOf(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "";

            var trimmed = cwd.TrimEnd('\\', '/');
            if (trimmed.Length == 0) return "";

            var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            var leaf = cut < 0 ? trimmed : trimmed[(cut + 1)..];

            // "C:" is a drive, not a directory anyone named.
            return leaf.EndsWith(':') ? "" : leaf;
        }

        // Backslashes first, then quotes. The order matters: escaping quotes
        // first would then have their new backslashes escaped again, doubling
        // them.
        internal static string EscapeForAppleScript(string text) =>
            text.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // property is "id" (a session UUID recorded by the hook) or "tty" (the
        // live tty of an attached tmux client). Both are iTerm2 session
        // properties; a no-match still activates, which is better than nothing.
        //
        // Activate, *wait*, then select. The order and the wait are both
        // load-bearing, and this supersedes an earlier reading of the same
        // behaviour — worth spelling out, because the obvious experiment gives
        // the wrong answer.
        //
        // What macOS actually does: activating an app raises whichever of its
        // windows are on the Space you're looking at, and only follows the app
        // to another Space when it has none here. Ordering a specific window
        // front (`select w`) *does* pull you to its Space — but only if the app
        // is already active. If an activation is still in flight, it lands
        // afterwards and raises the local window instead, undoing the select.
        //
        // That's why the first reading was "activate must come last": tested
        // from a desktop with no terminal window on it, where activation alone
        // switches Spaces and select-then-activate therefore appears to work.
        // From a desktop that *does* have a terminal window, the same script
        // needed two clicks — the first activating, the second selecting with
        // the app already active. Both observations come from the same rule.
        //
        // So: activate, wait for the activation to actually land, then select.
        //
        // Waited for rather than guessed at. This used to be `delay 0.2`, on the
        // reasoning that a fifth of a second is unnoticeable and is the gap a
        // double click was inserting by hand — both true, and it still lost the
        // race often enough to be reported as "clicking an orb doesn't switch
        // desktops any more". Measured on an idle machine, `activate` took 145ms,
        // 167ms and 531ms on three consecutive runs of the same script: the
        // spread is the problem, not the average. Every run slower than the delay
        // selects the window while the activation is still in flight, and then
        // the activation lands and raises the local window instead — the exact
        // failure described above, except intermittent, which is why it reads as
        // "sometimes it works".
        //
        // Polling `frontmost` costs nothing when activation is quick — measured
        // at 141ms end to end, faster than the fixed delay it replaces, because
        // it stops as soon as the app is really there — and keeps waiting when it
        // isn't. The cap is a backstop, not a timeout anyone should reach: if the
        // app never comes forward, selecting a window is going to fail anyway,
        // and hanging the click is worse than trying and missing.
        //
        // `frontmost of application "X"` is answered by the app itself, so this
        // needs no permission beyond the Automation grant the activate already
        // required. Both `delay` and the repeat sit outside the tell block on
        // purpose: inside one they are dispatched to the application, which
        // doesn't understand them, and the whole script fails with "Can't
        // continue delay".
        internal const int ActivationPollTicks = 40;      // x 50ms = 2s ceiling

        internal static string ActivateThenSettle(string app) => $$"""
            tell application "{{app}}" to activate
            repeat {{ActivationPollTicks}} times
                if frontmost of application "{{app}}" then exit repeat
                delay 0.05
            end repeat
            """;

        // How many times to re-assert a selection that has not taken yet.
        //
        // Settling on `frontmost of application` is not quite enough, and the
        // gap is one step past the note above: that property flips true when the
        // app becomes active, which is *before* macOS has finished raising the
        // window activation itself brings forward. A select landing in that gap
        // is overruled a moment later by the app's own most-recently-used
        // window — and when that window is on another desktop, you are taken to
        // the wrong desktop as well as the wrong window.
        //
        // Reported as orbs going somewhere wrong on the first click and right
        // on the second, which is the same rule from the other side: by the
        // second click the app is already frontmost, so activation raises
        // nothing and there is no raise left to overrule the select.
        //
        // Checked against `frontmost of w`, which is the window's own answer and
        // reflects what is actually in front. `id of current window` was tried
        // first and is useless here — it is iTerm's internal notion of current
        // and reads as correct while a different window is on screen, which is
        // exactly the failure being chased.
        //
        // Deliberately *not* `set frontmost of w to true` alongside the select.
        // That was in the first version of this and measured worse than the code
        // it replaced — three failures in six against none — so it is out, and
        // the loop is only a loop.
        internal const int SelectionVerifyTicks = 12;     // x 50ms = 600ms ceiling

        internal static string ITermSelectScript(string property, string value) => $$"""
            {{ActivateThenSettle("iTerm")}}
            tell application "iTerm"
                repeat with w in windows
                    repeat with t in tabs of w
                        repeat with s in sessions of t
                            if {{property}} of s is "{{value}}" then
                                repeat {{SelectionVerifyTicks}} times
                                    select w
                                    select t
                                    select s
                                    if frontmost of w then return
                                    delay 0.05
                                end repeat
                                return
                            end if
                        end repeat
                    end repeat
                end repeat
            end tell
            """;

        // Accepts either form the two paths produce: a bare "ttys004" from the
        // hook, or a "/dev/ttys004" client tty from tmux.
        //
        // Activate, settle, then select — same Spaces rule as ITermSelectScript,
        // where it's explained.
        internal static string TerminalSelectScript(string tty) => $$"""
            {{ActivateThenSettle("Terminal")}}
            tell application "Terminal"
                repeat with w in windows
                    repeat with t in tabs of w
                        if tty of t is "{{(tty.StartsWith("/dev/") ? tty : "/dev/" + tty)}}" then
                            -- Re-asserted until it takes, for the reason
                            -- SelectionVerifyTicks gives. Terminal windows
                            -- answer `frontmost` too, so the check is the same
                            -- question asked of the same kind of object.
                            repeat {{SelectionVerifyTicks}} times
                                set selected of t to true
                                set index of w to 1
                                if frontmost of w then return
                                delay 0.05
                            end repeat
                            return
                        end if
                    end repeat
                end repeat
            end tell
            """;
    }
}
