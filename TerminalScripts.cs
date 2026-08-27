using System.Collections.Generic;

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
        internal static string[] TmuxArgs(string? socket, params string[] args)
        {
            if (string.IsNullOrEmpty(socket)) return args;

            var full = new string[args.Length + 2];
            full[0] = "-S";
            full[1] = socket;
            args.CopyTo(full, 2);
            return full;
        }

        // One argument, safe to hand to `sh -c`.
        //
        // Single-quoted, with any embedded quote closed and reopened the shell
        // way, so a directory with a space or an apostrophe still arrives as one
        // word. AgentTeamViewer has carried two inline copies of this rule since
        // `claude attach` was wired up; this is the same rule stated where it can
        // be tested, and the builder below is the first caller.
        internal static string ShellQuote(string value) =>
            "'" + value.Replace("'", "'\\''") + "'";

        // The shell script that puts a terminal onto an existing tmux server.
        //
        // For a session whose pane is alive in a server nothing is attached to —
        // an agent-team member in a detached `claude-swarm-<pid>` socket, which
        // is the shape the user reported as an orb that "does nothing". A plain
        // `attach` is all that is needed: the caller has already run
        // select-window and select-pane against that pane, so the client lands on
        // the right teammate rather than on whatever the session last had
        // current.
        //
        // A script file rather than AppleScript's `do script`, for the reason
        // AgentTeamViewer.AttachSession records: `do script` is Terminal.app's
        // own vocabulary, while `open -a <app> <executable file>` is understood
        // by every terminal this app names, so one path covers all of them.
        //
        // An absolute tmux path, never a bare `tmux` resolved by a login shell:
        // `zsh -lc` skips .zshrc, which is where a PATH addition for Homebrew
        // normally lives, so a bare name silently fails whenever the app was
        // launched from Finder. See ClaudeBinary, and the identical note in
        // AgentTeamViewer.
        //
        // The `cd` is for after the attach ends rather than for the attach
        // itself: detach or exit and the window drops to a shell, and the useful
        // place to land is the directory whose orb was clicked. Skipped when no
        // cwd was recorded, because `cd ''` fails and would take the attach with
        // it — the one thing this script exists to do.
        internal static string TmuxAttachScript(string tmuxBinary, string? socket, string? cwd)
        {
            // Built with a loop rather than a LINQ chain, which is not a style
            // preference: everything else in this file is plain string building,
            // and a deferred Select/Prepend puts a compiler-generated state
            // machine on the line, which reads in a coverage report as a branch
            // nothing took while the line itself is plainly executed.
            var parts = new List<string> { ShellQuote(tmuxBinary) };
            foreach (var arg in TmuxArgs(socket, "attach")) parts.Add(ShellQuote(arg));

            var attach = string.Join(" ", parts);

            var script = "#!/bin/sh\n";
            if (!string.IsNullOrEmpty(cwd)) script += "cd " + ShellQuote(cwd) + " || exit 1\n";

            return script + "exec " + attach + "\n";
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
