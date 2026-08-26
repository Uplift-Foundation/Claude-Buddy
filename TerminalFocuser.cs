using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeBuddy
{
    // Best-effort "take me to that session's terminal" for a left-click on
    // an orb. Silently does nothing when the status file predates the hook
    // scripts that record terminal info.
    //
    // Precision degrades gracefully. macOS: the exact tmux pane (see
    // FocusTmux), the exact iTerm2 pane (via its session UUID), the exact
    // Terminal.app tab (via tty), otherwise just activate the terminal app;
    // the first click triggers a macOS Automation permission prompt for
    // controlling the terminal — that's expected; approve it once.
    // Windows: for Windows Terminal, the exact tab when its title
    // unambiguously identifies the session (see TrySelectWindowsTerminalTab);
    // otherwise the terminal window whose PID the hook recorded, or any
    // window of the app named by term_program (the WSL case, where the
    // Windows-side parent chain dead-ends in an interop bridge).
    // Excluded from coverage: this is the OS boundary itself. Every method here
    // runs tmux, ps, osascript or PowerShell as a real subprocess, sends
    // synthetic keystrokes through SendInput, or drives UI Automation against a
    // live Windows Terminal window. CLAUDE.md already records why a headless
    // runner must not reach it: a synthesized orb click lands here, and these
    // calls have no OS guard at their own entry point, so on a CI runner they
    // would be real, unpredictable side effects rather than a test.
    //
    // What used to be testable-by-association is now in TerminalScripts, which
    // is not excluded: the AppleScript builders, the tmux socket-pinning rule,
    // the path-leaf rule and the AppleScript escaping. Those are the parts that
    // decide anything.
    [ExcludeFromCodeCoverage]
    internal static class TerminalFocuser
    {
        // teamLead is where the click goes when this session has nowhere of its
        // own to go: an agent-team member runs in a pane of a *detached* tmux
        // server, so there is no window anywhere showing it, and a click on its
        // orb otherwise did nothing at all. The session running the team is the
        // honest answer — it's where that agent's work is being driven from.
        // sessionId is the orb's own id, which SessionStatus doesn't carry —
        // it's the status file's name, not a field inside it. Needed for the
        // background case at the end, where the only way to reach a session is
        // to name it.
        public static void Focus(
            SessionStatus? status,
            SessionStatus? teamLead = null,
            string? sessionId = null)
        {
            if (status is null) return;

            // A gateway session has no terminal anywhere and no local process,
            // so there is nothing to focus. Returning here also keeps it out of
            // the background-session branch below, which reads pid <= 0 as "a
            // local `claude daemon` session" and would open a tmux window trying
            // to attach a session id that exists only on another machine.
            //
            // Widened from Claude Code to any local CLI: a Codex session is in
            // a terminal like any other and everything below finds it the same
            // way, from the tmux pane or the tty the hook recorded. The one
            // exception is that background-session branch, which stays Claude
            // Code's — see the guard on it.
            if (!status.IsLocalCli) return;

            // Resolving a target runs several short-lived processes (tmux
            // queries, ps walks, osascript) and waits on their output; doing
            // that on the UI thread would stall every orb's animation for the
            // duration of the click.
            Task.Run(() =>
            {
                if (FocusCore(status)) return;
                if (teamLead is not null && FocusCore(teamLead)) return;

                // Nothing on screen shows this session. For a background one
                // that is the normal case rather than a failure — it runs under
                // a daemon with no terminal of its own — so open one on it
                // rather than leave the click doing nothing. Gated on having no
                // pid so a real session whose terminal merely couldn't be
                // resolved gets a diagnosis rather than a surprise window.
                //
                // Still Claude Code's alone. This ends in `claude attach`, and
                // there is no Codex equivalent to attach to. The scan already
                // drops a pid-less Codex session before it can have an orb, so
                // nothing should reach here — this is the belt to that braces,
                // because the failure it prevents is a window opening onto
                // someone else's session.
                if (status.Source == SessionSource.ClaudeCode
                    && status.SessionPid <= 0
                    && !string.IsNullOrEmpty(sessionId))
                {
                    var pane = AgentTeamViewer.AttachSession(sessionId, status.Cwd);

                    // It went into tmux, so finish the job the ordinary way:
                    // FocusCore already knows how to select a pane, find the
                    // client showing it and bring that client's window
                    // forward. Only the pane is new — everything after it is
                    // the path every other tmux session takes.
                    if (!string.IsNullOrEmpty(pane))
                    {
                        FocusCore(new SessionStatus
                        {
                            TmuxPane = pane,
                            Cwd = status.Cwd
                        });
                    }
                }
            });
        }

        // Types transcribed speech into the exact terminal/pane a session's
        // orb represents — the voice-dictation mic's send path (see
        // OrbWindow's recording state and SpeechTranscriber). Never presses
        // Enter: the text lands in the prompt line for the user to review,
        // same as if they'd typed it themselves.
        //
        // Deliberately no team-lead fallback, unlike Focus above: if this
        // specific session has no window or pane of its own, there is
        // nowhere safe to type. The team lead's pane belongs to a *different*
        // session, and typing into it would land words in the wrong place
        // rather than nowhere at all — worse than doing nothing.
        //
        // Unlike Focus, this is awaited rather than fire-and-forget. Focus is
        // fired from a mouse click and must never stall the UI thread; this
        // is the tail of an already-async pipeline (record -> transcribe ->
        // inject) with no UI thread waiting on it, so there's nothing lost by
        // waiting out the same settle time the focus step already needs
        // before it's safe to start typing.
        public static Task SendText(SessionStatus? status, string text)
        {
            if (status is null || string.IsNullOrEmpty(text)) return Task.CompletedTask;

            // Nowhere safe to type. Without this the macOS path falls through to
            // SendTextMacKeystroke, which is an unconditional System Events
            // keystroke into whatever happens to be frontmost — so a dictated
            // sentence lands in an editor, a browser, or another session. That
            // is a latent hazard for any pane-less session; a gateway session
            // would make it the normal case.
            if (!status.IsLocalCli) return Task.CompletedTask;

            return Task.Run(async () =>
            {
                // Reuses FocusCore as-is rather than a bespoke synchronous
                // variant: FocusCore's own osascript calls are fire-and-forget
                // (see RunOsaScript), so there's no return value to await
                // here — just the same fixed settle margin the rest of this
                // file already relies on for activation ordering (see
                // ActivateThenSettle), sized a bit larger because this also
                // has to cover FocusCore's own osascript process launch, not
                // just the `tell application to activate` inside it.
                FocusCore(status);
                await Task.Delay(500);

                if (OperatingSystem.IsWindows())
                {
                    SendUnicodeText(text);
                    return;
                }

                if (!OperatingSystem.IsMacOS()) return;

                if (!string.IsNullOrEmpty(status.TmuxPane) && SendTextTmux(status, text)) return;

                SendTextMacKeystroke(text);
            });
        }

        // send-keys writes directly into the pane's input buffer regardless
        // of whether its window is on screen, but FocusCore has already been
        // asked to bring it forward above, so the user sees it land the same
        // way a click would show them the pane.
        //
        // -l is literal: without it tmux tries to interpret the text as key
        // names ("Enter", "C-c", ...) instead of typing it verbatim, which is
        // exactly the gap between "type this" and "run arbitrary keys".
        private static bool SendTextTmux(SessionStatus status, string text)
        {
            var tmux = ResolveTmuxBinary(status.TmuxBin);
            if (tmux is null) return false;

            return TryRun(tmux, out _, TmuxArgs(status, "send-keys", "-t", status.TmuxPane, "-l", text));
        }

        // --- the chat panel's half ---
        //
        // Everything below is tmux-only and deliberately so. The keystroke and
        // SendInput fallbacks above type into whatever is *frontmost*, which
        // means focusing the terminal first; that is a fine trade for dictation,
        // which you started by reaching for the orb anyway, and the wrong one
        // for a chat panel whose entire point is not making you leave what you
        // are doing. A session with no pane gets a read-only panel instead —
        // see ClaudeCodeChatSession.CanType.

        // Whether this session can be typed into without anything coming to the
        // front. The one question the panel asks before enabling its composer.
        public static bool CanSendQuietly(SessionStatus? status) =>
            status is { IsLocalCli: true }
            && !string.IsNullOrEmpty(status.TmuxPane)
            && ResolveTmuxBinary(status.TmuxBin) is not null;

        // How old the app's evidence may be before it refuses to type into a
        // pane. Not the "Keep orbs for" lifetime, deliberately — see below.
        internal static readonly TimeSpan PaneEvidenceMaxAge = TimeSpan.FromMinutes(30);

        // Whether the status file is recent enough to be believed about which
        // conversation is sitting in the pane it names.
        //
        // A status file is the app's *only* reason to think a session owns a
        // tmux pane, and a pane is not owned for the life of a process. Claude
        // Code mints a new session id on /clear, on resume, and when a
        // conversation migrates between processes, and the hook writes a new
        // file each time without deleting the old one. So a stale file can name
        // a pane that has since moved on to a different conversation entirely.
        //
        // Confirmed on a real machine, and the reason this exists: a status file
        // last written at 10:28 still named tmux pane %8, whose live claude was
        // by then four hours into a different conversation. The orb typed —
        // CanSendQuietly was true, the pane and the tmux binary were both real —
        // and the sentence landed in that other conversation, while the orb's
        // own panel went on reading a transcript that had stopped growing. The
        // user reported it as "the orb isn't responding"; what had actually
        // happened is worse than no answer, because the text went somewhere.
        //
        // Nothing in the two status files linked them: different session ids,
        // different pids, and only the stale one claimed the pane. Superseded
        // groups by pid and so could not pair them; the no-terminal rule cannot
        // fire on a file naming a tty, a term program and a pane; and the
        // process-gone rule cannot fire on a pid that is alive and merely busy
        // with something else. So the staleness of the evidence is the only
        // signal there is, and this is it.
        //
        // Kept separate from the orb-lifetime setting on purpose. "Keep orbs
        // for: forever" is the user saying they want to *see* quiet sessions,
        // which is reasonable and is why that orb was still on screen. It is not
        // them saying the app may type into a pane on four-hour-old
        // information. Visibility and delivery are different promises and only
        // one of them is destructive to get wrong.
        //
        // The threshold is generous because a session being used refreshes this
        // constantly: the hook fires on every prompt and every stop, so any
        // conversation touched in the last half hour has a fresh file. The cost
        // of the rule falling the wrong way is one terminal round-trip, and the
        // composer says so rather than failing silently — after which the file
        // is fresh again and the orb types as normal.
        internal static bool PaneEvidenceIsCurrent(DateTime written, DateTime now, TimeSpan maxAge)
        {
            // A file with no recorded time is not evidence of staleness — it is
            // a status this app built rather than read (ResetSessionToIdle, a
            // gateway stand-in, a test fixture), and refusing those would break
            // typing for reasons that have nothing to do with panes.
            if (written == default) return true;

            // Clock skew and a file written a moment ago both land here. A
            // future mtime is not stale.
            return now - written <= maxAge;
        }

        public static bool PaneEvidenceIsCurrent(SessionStatus? status) =>
            status is null
            || PaneEvidenceIsCurrent(status.Written, DateTime.UtcNow, PaneEvidenceMaxAge);

        // Types the text and presses Enter. The Enter is the whole difference
        // from SendText above, and it is why this is reached only from a Send
        // button behind a setting that is off by default: dictation is a typing
        // aid and doesn't get to decide you meant it, but a person clicking Send
        // has said exactly that.
        //
        // No FocusCore. Nothing comes forward, which is the feature.
        public static Task<bool> SendTextAndSubmit(SessionStatus? status, string text)
        {
            if (status is null || string.IsNullOrEmpty(text)) return Task.FromResult(false);
            if (!CanSendQuietly(status)) return Task.FromResult(false);

            return Task.Run(() =>
            {
                var tmux = ResolveTmuxBinary(status.TmuxBin);
                if (tmux is null) return false;

                // Through the paste buffer rather than send-keys -l, for
                // multi-line messages: a literal newline sent as a keystroke is
                // indistinguishable from pressing Return, so Shift+Enter in the
                // panel would submit half a sentence and leave the rest to
                // arrive as a second message. paste-buffer -p wraps it in
                // bracketed-paste markers, which the TUI reads as one paste.
                //
                // -p is safe when the pane's application never asked for
                // bracketed paste: tmux then sends the text unwrapped, which for
                // a single line is what send-keys -l would have done anyway.
                //
                // -- so a message starting with a dash isn't read as a flag.
                if (!TryRun(tmux, out _, TmuxArgs(status, "set-buffer", "-b", PasteBuffer, "--", text)))
                    return false;

                // -d deletes the buffer after pasting, so a half-typed message
                // isn't left sitting in tmux's paste stack for the next
                // middle-click anywhere else on the machine to find.
                if (!TryRun(tmux, out _, TmuxArgs(status,
                        "paste-buffer", "-b", PasteBuffer, "-t", status.TmuxPane, "-p", "-d")))
                    return false;

                return TryRun(tmux, out _, TmuxArgs(status, "send-keys", "-t", status.TmuxPane, "Enter"));
            });
        }

        private const string PasteBuffer = "claude-buddy";

        // A named key — "Enter", "Escape", or a bare digit for a numbered
        // dialog. Not -l: these are key names, which is the one case where
        // letting tmux interpret the argument is the point rather than the
        // hazard. Only ever called with a constant or with a digit this app
        // read off the pane itself, never with anything a person typed.
        public static Task<bool> SendPaneKey(SessionStatus? status, string key)
        {
            if (status is null || string.IsNullOrEmpty(key)) return Task.FromResult(false);
            if (!CanSendQuietly(status)) return Task.FromResult(false);

            return Task.Run(() =>
            {
                var tmux = ResolveTmuxBinary(status.TmuxBin);
                if (tmux is null) return false;

                return TryRun(tmux, out _, TmuxArgs(status, "send-keys", "-t", status.TmuxPane, key));
            });
        }

        // What the pane is showing right now, as text.
        //
        // This is how a permission prompt gets answered from the panel without
        // guessing. The dialog is drawn by the TUI and never reaches the
        // transcript, so the only place its wording exists is the screen —
        // capture-pane is reading the screen, which is exactly as much as is
        // needed and no more. Without -e, so no escape sequences come back.
        public static Task<string?> CapturePane(SessionStatus? status)
        {
            if (status is null || !CanSendQuietly(status)) return Task.FromResult<string?>(null);

            return Task.Run<string?>(() =>
            {
                var tmux = ResolveTmuxBinary(status.TmuxBin);
                if (tmux is null) return null;

                return TryRun(tmux, out var screen, TmuxArgs(status, "capture-pane", "-p", "-t", status.TmuxPane))
                    ? screen
                    : null;
            });
        }

        // Whether anything was actually brought forward. False means the click
        // had no effect at all, which is what the team-lead fallback above is
        // for — and what made two orbs on screen feel broken before it existed.
        private static bool FocusCore(SessionStatus status)
        {
            if (OperatingSystem.IsWindows())
            {
                FocusWindows(status);
                return true;
            }

            if (!OperatingSystem.IsMacOS()) return false;

            // tmux first: when a session is inside tmux, nothing else the hook
            // recorded points at a window you can actually see.
            if (!string.IsNullOrEmpty(status.TmuxPane) && FocusTmux(status)) return true;

            string? script;
            if (!string.IsNullOrEmpty(status.TermId))
            {
                script = TerminalScripts.ITermSelectScript("id", status.TermId);
            }
            else
            {
                script = status.TermProgram switch
                {
                    "Apple_Terminal" when !string.IsNullOrEmpty(status.Tty) => TerminalScripts.TerminalSelectScript(status.Tty),
                    "Apple_Terminal" => "tell application \"Terminal\" to activate",
                    "iTerm.app" => "tell application \"iTerm\" to activate",
                    "vscode" => "tell application \"Visual Studio Code\" to activate",
                    "ghostty" => "tell application \"Ghostty\" to activate",
                    "WezTerm" => "tell application \"WezTerm\" to activate",
                    _ => null
                };
            }

            // Nothing named a terminal program, or tmux couldn't be reached.
            // The tty is the one coordinate the hook always records, and the
            // process tree above it says which app owns it — enough to select
            // the exact iTerm2 session or Terminal.app tab, and failing that to
            // bring the owning app forward. Without this a session whose hook
            // recorded a tty but no TERM_PROGRAM — a background session started
            // by another tool, which is what a team lead often is — had an orb
            // that did nothing when clicked.
            if (script is null) return FocusByTty(status.Tty);

            RunOsaScript(script);
            return true;
        }

        private static bool FocusByTty(string tty)
        {
            if (string.IsNullOrEmpty(tty)) return false;

            var app = ResolveAppBundleForTty(tty);
            if (app is null) return false;

            // iTerm reports the full device path, so compare like with like.
            var device = tty.StartsWith("/dev/") ? tty : "/dev/" + tty;

            var script = Path.GetFileName(app) switch
            {
                "iTerm.app" => TerminalScripts.ITermSelectScript("tty", device),
                "Terminal.app" => TerminalScripts.TerminalSelectScript(device),
                _ => null
            };

            if (script is not null)
            {
                RunOsaScript(script);
                return true;
            }

            ActivateApp(app);
            return true;
        }

        // --- tmux ---
        //
        // Two separate jobs, and skipping either one leaves you looking at the
        // wrong thing:
        //   1. Make the session's pane current *inside* tmux — the attached
        //      client is very likely showing some other window/pane, so
        //      activating its terminal alone would land you somewhere else.
        //   2. Activate the terminal app that hosts a client attached to that
        //      session. Which terminal that is can't be recorded at hook time:
        //      you can detach and reattach a tmux session from a different app
        //      (or from none at all), so it's resolved from the live client's
        //      tty on every click.
        private static bool FocusTmux(SessionStatus status)
        {
            var tmux = ResolveTmuxBinary(status.TmuxBin);
            if (tmux is null) return false;

            var pane = status.TmuxPane;

            // Also serves as the liveness check: a pane id from a server that
            // has since exited (or a pane that's been killed) fails here, and
            // we fall back to the non-tmux heuristics.
            if (!TryRun(tmux, out var sessionName, TmuxArgs(status, "display-message", "-p", "-t", pane, "#{session_name}")))
            {
                return false;
            }
            sessionName = sessionName.Trim();
            if (sessionName.Length == 0) return false;

            TryRun(tmux, out _, TmuxArgs(status, "select-window", "-t", pane));
            TryRun(tmux, out _, TmuxArgs(status, "select-pane", "-t", pane));

            var client = ResolveClient(tmux, status, sessionName);

            // No client attached anywhere: the pane is now selected, so the
            // session is waiting correctly for whenever it's next attached,
            // but there's no window to bring forward. Report that we didn't
            // activate anything so the caller can still try its own heuristics
            // rather than treating the click as handled.
            if (client is null) return false;

            var (clientTty, controlMode) = client.Value;
            var app = ResolveAppBundleForTty(clientTty);

            // iTerm2 and Terminal.app can both select the exact tab the client
            // runs in, which matters when several tmux clients share one app.
            //
            // Except in control mode (iTerm2's native tmux integration,
            // `tmux -CC`), where that tty belongs to the hidden control tab
            // rather than to any window you'd want to look at — iTerm2 mirrors
            // tmux windows as native tabs and follows the select-pane above on
            // its own, so activating the app is both sufficient and correct.
            var script = controlMode ? null : Path.GetFileName(app) switch
            {
                "iTerm.app" => TerminalScripts.ITermSelectScript("tty", clientTty),
                "Terminal.app" => TerminalScripts.TerminalSelectScript(clientTty),
                _ => null
            };

            if (script is not null)
            {
                RunOsaScript(script);
                return true;
            }

            if (app is not null)
            {
                ActivateApp(app);
                return true;
            }

            // Couldn't work out which app owns the client's tty. The pane is
            // selected, but nothing was brought forward — say so, so the
            // caller falls through instead of swallowing the click.
            return false;
        }

        // Works for any terminal without a case per app: `open -a` on a running
        // app just brings it forward.
        private static void ActivateApp(string appBundlePath)
        {
            MacOSWindowExtensions.WaitForOwnActivation();

            try
            {
                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(appBundlePath);
                Process.Start(psi);
            }
            catch { }
        }

        // Kept as a wrapper over TerminalScripts.TmuxArgs so its twelve call
        // sites read the same as before; the socket-pinning rule itself is tested
        // there.
        private static string[] TmuxArgs(SessionStatus status, params string[] args) =>
            TerminalScripts.TmuxArgs(status.TmuxSocket, args);

        // The app can't count on PATH: launched from Finder or Login Items it
        // gets the bare system PATH, with no Homebrew or MacPorts in it. The
        // hook records where tmux actually was, and these are the fallbacks
        // for status files written before it did.
        private static readonly string[] TmuxCandidates =
        {
            "/opt/homebrew/bin/tmux",
            "/usr/local/bin/tmux",
            "/opt/local/bin/tmux",
            "/usr/bin/tmux"
        };

        private static string? ResolveTmuxBinary(string recorded)
        {
            if (!string.IsNullOrEmpty(recorded) && File.Exists(recorded)) return recorded;
            return TmuxCandidates.FirstOrDefault(File.Exists);
        }

        // Prefer a client already looking at the session; otherwise commandeer
        // one — switching some client to it is the only way to get the session
        // on screen at all. Either way, ties break toward the most recently
        // active client: a session can be attached from several terminals at
        // once, and the one you touched last is the one you're sitting at.
        private static (string Tty, bool ControlMode)? ResolveClient(string tmux, SessionStatus status, string sessionName)
        {
            if (!TryRun(tmux, out var listing, TmuxArgs(status, "list-clients", "-F",
                    "#{client_tty}\t#{client_session}\t#{client_activity}\t#{client_control_mode}")))
            {
                return null;
            }

            (string Tty, bool ControlMode)? onSession = null, anyClient = null;
            string? onSessionBest = null, anyClientBest = null;

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                var tty = parts[0].Trim();
                if (tty.Length == 0) continue;

                // client_activity is a unix timestamp; string-compare is fine
                // for equal-width integers and avoids caring about the format.
                var activity = parts.Length > 2 ? parts[2].Trim() : "";
                var candidate = (tty, parts.Length > 3 && parts[3].Trim() == "1");

                if (parts[1].Trim() == sessionName)
                {
                    if (onSession is null || activity.CompareTo(onSessionBest) > 0)
                    {
                        onSession = candidate;
                        onSessionBest = activity;
                    }
                }
                else if (anyClient is null || activity.CompareTo(anyClientBest) > 0)
                {
                    anyClient = candidate;
                    anyClientBest = activity;
                }
            }

            if (onSession is not null) return onSession;
            if (anyClient is null) return null;

            TryRun(tmux, out _, TmuxArgs(status, "switch-client", "-c", anyClient.Value.Tty, "-t", sessionName));
            return anyClient;
        }

        // Walks up from whatever is running on a tty until it hits a process
        // living inside an .app bundle — that's the terminal emulator hosting
        // it. Covers Ghostty, WezTerm, kitty, Alacritty, VS Code and friends
        // without needing a case per app.
        private static string? ResolveAppBundleForTty(string tty)
        {
            var name = tty.StartsWith("/dev/") ? tty[5..] : tty;

            if (!TryRun("/bin/ps", out var listing, "-t", name, "-o", "pid=")) return null;

            var pid = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => int.TryParse(l.Trim(), out var p) ? p : 0)
                .FirstOrDefault(p => p > 0);
            if (pid == 0) return null;

            for (var hop = 0; hop < 12 && pid > 1; hop++)
            {
                if (!TryRun("/bin/ps", out var row, "-o", "ppid=,comm=", "-p", pid.ToString())) return null;

                row = row.Trim();
                var split = row.IndexOf(' ');
                if (split <= 0) return null;

                var command = row[(split + 1)..].Trim();
                var marker = command.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
                if (marker >= 0) return command[..(marker + 4)];

                if (!int.TryParse(row[..split].Trim(), out pid)) return null;
            }

            return null;
        }

        // --- process helpers ---

        private static bool TryRun(string exe, out string stdout, params string[] args) =>
            TryRun(exe, 3000, out stdout, args);

        private static bool TryRun(string exe, int timeoutMs, out string stdout, params string[] args)
        {
            stdout = "";
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,

                    // Not cosmetic, and not implied by redirecting the pipes:
                    // this app is a WinExe, so a console child launched from it
                    // gets a console of its own allocated and *shown* unless
                    // CREATE_NO_WINDOW says otherwise. Measured from a WinExe
                    // parent with this exact ProcessStartInfo: without it the
                    // child owns a visible PseudoConsoleWindow, with it no
                    // window at all and identical stdout and exit code.
                    //
                    // On Windows that window is this file's whole reason for
                    // shelling out gone wrong twice over. It flashes on screen
                    // for the ~400ms the tab-selection helper runs (the "a
                    // terminal pops up and goes away" every orb click and every
                    // dictation produced), and while it exists it holds the
                    // foreground — so the terminal this was supposed to bring
                    // forward loses the race, and dictated text goes wherever
                    // Windows hands focus once the console dies rather than
                    // into the session.
                    //
                    // WslIntegration already sets this on its own launches for
                    // the same reason; this call site simply never did. Ignored
                    // on macOS, where every other TryRun caller lives.
                    CreateNoWindow = true
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return false;

                // Read both pipes concurrently and only then wait. Doing a
                // blocking ReadToEnd() first would make the timeout below
                // unreachable — it returns when the pipe closes, which a wedged
                // child never does — and leaving stderr undrained can deadlock
                // a chatty one once its pipe buffer fills.
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                // A wedged tmux server (or, on Windows, a slow UIA broker)
                // would otherwise hang this click forever.
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    return false;
                }

                stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // Fire-and-forget on purpose — a click must not wait on AppleScript — but
        // *not* blind. Process.Start succeeds whenever osascript merely launches,
        // so the previous version reported success for every outcome, including
        // the one that matters: error -1743, errAEEventNotPermitted, which is what
        // macOS returns when the app's Automation consent is missing or has been
        // invalidated (any change to the app's code identity does that — a
        // re-signed or replaced bundle counts).
        //
        // That failure is otherwise undetectable from the outside. It looks
        // exactly like a click landing on a terminal that is already frontmost,
        // so with a single terminal window it is invisible on the current Space
        // and only shows up as "clicking does nothing" from another one. It cost
        // a long hunt through a focus path that turned out to be correct.
        //
        // So drain stderr on a background task and say so once. Still never
        // throws into the caller: focusing is a convenience, not worth the app.
        private static void RunOsaScript(string? script)
        {
            if (script is null) return;

            // Let our own activation land first, or the terminal this script
            // brings forward is taken back the instant it arrives. See
            // WaitForOwnActivation.
            MacOSWindowExtensions.WaitForOwnActivation();

            try
            {
                var psi = new ProcessStartInfo("/usr/bin/osascript")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                var process = Process.Start(psi);
                if (process is null) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Read before waiting: a full stderr pipe would otherwise
                        // block the child while we block on its exit.
                        var stderr = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0) ReportFocusFailure(stderr);
                    }
                    catch { }
                    finally { process.Dispose(); }
                });
            }
            catch
            {
                // Focusing is a convenience; never let it take the app down.
            }
        }

        // Sends System Events a keystroke command for the frontmost app —
        // correct because SendText's caller (Task.Run above) has already
        // asked FocusCore to bring the right window/tab forward and waited
        // out the settle delay before reaching here.
        //
        // A dedicated run-and-report helper rather than reusing RunOsaScript:
        // that one attributes every failure to the Automation permission
        // (-1743), which is the *wrong* diagnosis here — keystroke injection
        // needs Accessibility permission, a separate TCC grant with its own
        // error text, and telling a user to check the wrong settings pane
        // over a permission failure is worse than not explaining it at all.
        private static void SendTextMacKeystroke(string text)
        {
            var script = $$"""
                tell application "System Events"
                    keystroke "{{TerminalScripts.EscapeForAppleScript(text)}}"
                end tell
                """;

            RunOsaScriptForSendText(script);
        }

        // AppleScript string literals only need their own quote and backslash
        // escaped — unlike the tab-selection scripts elsewhere in this file,
        // this text is never a hook-recorded value (a tty, a UUID); it's
        // whatever the user said, so it can contain anything a string can.


        // Mirrors RunOsaScript's fire-and-forget shape (a click, or here a
        // dictation, must not block on an external process) but reports
        // through ReportSendTextFailure instead — see the comment on
        // SendTextMacKeystroke for why the two can't share one reporter.
        private static void RunOsaScriptForSendText(string script)
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/osascript")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                var process = Process.Start(psi);
                if (process is null) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stderr = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0) ReportSendTextFailure(stderr);
                    }
                    catch { }
                    finally { process.Dispose(); }
                });
            }
            catch
            {
                // Typing the transcription in is a convenience on top of a
                // convenience; never let it take the app down.
            }
        }

        private static int _sendTextFailureReported;

        private static void ReportSendTextFailure(string stderr)
        {
            if (Interlocked.Exchange(ref _sendTextFailureReported, 1) != 0) return;

            var detail = stderr.Trim();

            if (detail.Contains("not allowed to send keystrokes", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("assistive access", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    "Claude Buddy: the mic transcribed your speech, but typing it into the " +
                    "terminal failed — macOS has not granted Accessibility permission (this is " +
                    "separate from the Automation permission clicking an orb already uses).\n" +
                    "  Fix: System Settings > Privacy & Security > Accessibility, and enable the " +
                    "terminal app (or Claude Buddy, if System Events prompts for it there instead).\n" +
                    "  If it was granted before a rebuild, the grant may have been invalidated. Run:\n" +
                    "    tccutil reset Accessibility io.github.wtvamp.claudebuddy\n" +
                    "  then dictate again and approve the prompt.");
                return;
            }

            Console.Error.WriteLine($"Claude Buddy: typing the transcribed text failed: {detail}");
        }

        // Once per app run, not per click: a denied grant fails on every click,
        // and a message per click would bury everything else in the log.
        private static int _focusFailureReported;

        private static void ReportFocusFailure(string stderr)
        {
            if (Interlocked.Exchange(ref _focusFailureReported, 1) != 0) return;

            var detail = stderr.Trim();

            // -1743 is the one worth naming, because the user can actually fix it
            // and the wording macOS uses ("Not authorized to send Apple events")
            // does not say where to go.
            if (detail.Contains("-1743") || detail.Contains("Not authorized to send Apple events"))
            {
                Console.Error.WriteLine(
                    "Claude Buddy: clicking an orb can't focus your terminal — macOS has not " +
                    "granted Automation permission.\n" +
                    "  Fix: System Settings > Privacy & Security > Automation, and enable the " +
                    "terminal under Claude Buddy.\n" +
                    "  If Claude Buddy isn't listed, its permission was invalidated by a rebuild. Run:\n" +
                    "    tccutil reset AppleEvents io.github.wtvamp.claudebuddy\n" +
                    "  then click an orb again and approve the prompt.");
                return;
            }

            Console.Error.WriteLine($"Claude Buddy: focusing the terminal failed: {detail}");
        }

        // --- Windows keystroke injection ---
        //
        // SendInput rather than SendKeys.SendWait: SendKeys reads its string
        // as a small escaping language of its own (parentheses, braces, `+`
        // for shift...), and arbitrary dictated text is not written in that
        // language — every character that happens to collide with it would
        // need escaping, which is worse than not using SendKeys at all.
        //
        // KEYEVENTF_UNICODE sends a raw UTF-16 code unit per event and skips
        // virtual-key mapping entirely, so it doesn't care what's plugged in
        // or which keyboard layout is active — including a surrogate pair,
        // which arrives as two code units and reassembles correctly on the
        // receiving end, the same as typing an emoji normally would.
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        // MOUSEINPUT and HARDWAREINPUT are declared only to size the union
        // below, never sent. Leaving them out is not a harmless simplification:
        // MOUSEINPUT (32 bytes on x64) is the *largest* member, so a union
        // holding KEYBDINPUT alone is 24 bytes instead of 32, INPUT comes out
        // 32 bytes instead of 40, and SendInput — which validates its cbSize
        // against its own sizeof(INPUT) and accepts nothing else — rejects
        // every call with ERROR_INVALID_PARAMETER and inserts no events.
        //
        // That is exactly how this shipped: dictation recorded and transcribed
        // correctly, the terminal even came to the front, and then nothing was
        // typed, silently, because the return value went unchecked too (it is
        // checked now — see SendUnicodeText). Measured directly: cbSize 32
        // returns 0 / GetLastError 87; the same call at 40 types the text.
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint Data;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput
        {
            public uint Msg;
            public ushort ParamL;
            public ushort ParamH;
        }

        // INPUT is a C union of three keyboard/mouse/hardware shapes. All three
        // are declared so the union gets Win32's actual size and layout rather
        // than the size of whichever member this code happens to use.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mi;
            [FieldOffset(0)] public KeyboardInput Ki;
            [FieldOffset(0)] public HardwareInput Hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion U;
        }

        private const uint InputKeyboard = 1;
        private const uint KeyEventFUnicode = 0x0004;
        private const uint KeyEventFKeyUp = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);

        [SupportedOSPlatform("windows")]
        private static void SendUnicodeText(string text)
        {
            if (text.Length == 0) return;

            var inputs = new Input[text.Length * 2];
            for (var i = 0; i < text.Length; i++)
            {
                inputs[i * 2] = KeyEvent(text[i], keyUp: false);
                inputs[i * 2 + 1] = KeyEvent(text[i], keyUp: true);
            }

            // Checked, not fire-and-forget. SendInput has two failure modes
            // that both look identical to the user — nothing gets typed — and
            // neither throws: a cbSize Windows doesn't recognise (the bug the
            // union above exists to prevent, which is worth catching if the
            // layout ever regresses) and UIPI refusing to let this process
            // send input to a more privileged one, which is what an elevated
            // terminal looks like. Reported once per run, like the macOS
            // permission failures — see ReportSendTextFailure.
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent == inputs.Length) return;

            var error = Marshal.GetLastWin32Error();
            ReportSendInputFailure(sent, inputs.Length, error);
        }

        private static int _sendInputFailureReported;

        private static void ReportSendInputFailure(uint sent, int expected, int error)
        {
            if (Interlocked.Exchange(ref _sendInputFailureReported, 1) != 0) return;

            // 5 is ERROR_ACCESS_DENIED, which for SendInput means UIPI: a
            // non-elevated process cannot send input to an elevated window.
            // The user can act on that one, so it's worth naming.
            if (error == 5)
            {
                Console.Error.WriteLine(
                    "Claude Buddy: the mic transcribed your speech, but Windows blocked typing it " +
                    "into the terminal — the terminal is running elevated (as Administrator) and " +
                    "Claude Buddy is not.\n" +
                    "  Fix: run the terminal without elevation, or start Claude Buddy elevated too.");
                return;
            }

            Console.Error.WriteLine(
                $"Claude Buddy: typing the transcribed text failed — SendInput accepted {sent} of " +
                $"{expected} events (GetLastError {error}).");
        }

        private static Input KeyEvent(char ch, bool keyUp) => new()
        {
            Type = InputKeyboard,
            U = new InputUnion
            {
                Ki = new KeyboardInput
                {
                    VirtualKey = 0,
                    ScanCode = ch,
                    Flags = KeyEventFUnicode | (keyUp ? KeyEventFKeyUp : 0),
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

        // --- Windows ---

        // OrbWindow sets ShowActivated="False" (it's a click-to-act overlay,
        // not something that should steal keyboard focus just by existing),
        // so clicking it never makes ClaudeBuddy.exe the foreground process —
        // hence WindowsForegroundWindow's AttachThreadInput dance below.
        private static void FocusWindows(SessionStatus status)
        {
            try
            {
                // Tab-exact beats window-exact: an unambiguous tab is the only
                // thing that identifies *which* session's terminal to show,
                // since every tab of a Windows Terminal window shares one
                // process and one MainWindowHandle.
                //
                // Selecting it is not enough on its own, though, and the
                // earlier reading of this (that selecting also raises the
                // window — docs/windows-wt-tabs-findings.md) was true only of
                // the case it was tested in: switching *away* from some other
                // tab. Selecting the tab that is already current is a no-op, so
                // it raises nothing — and clicking an orb or its mic has just
                // made Claude Buddy the foreground app, so "already on the
                // right tab" left the terminal behind us. Dictation into a
                // session you were already looking at typed into the flyout
                // instead, which is exactly the shape of "it only works if
                // you're on the wrong tab".
                //
                // So raise the window explicitly, and the tab's *own* window
                // rather than MainWindowHandle — with several Windows Terminal
                // windows in one process, that property names an arbitrary one.
                if (status.TermProgram == "WindowsTerminal"
                    && TrySelectWindowsTerminalTab(status, out var tabWindow))
                {
                    WindowsForegroundWindow.BringToFront(tabWindow);
                    return;
                }

                var hwnd = IntPtr.Zero;

                if (status.TermPid > 0)
                {
                    try
                    {
                        hwnd = Process.GetProcessById(status.TermPid).MainWindowHandle;
                    }
                    catch { } // terminal exited; fall through
                }

                if (hwnd == IntPtr.Zero)
                {
                    var processName = status.TermProgram switch
                    {
                        "WindowsTerminal" => "WindowsTerminal",
                        "vscode" => "Code",
                        _ => null
                    };
                    if (processName is null) return;

                    hwnd = Process.GetProcessesByName(processName)
                        .Select(p => p.MainWindowHandle)
                        .FirstOrDefault(h => h != IntPtr.Zero);
                }

                WindowsForegroundWindow.BringToFront(hwnd);
            }
            catch
            {
                // Same convenience-only rule as macOS.
            }
        }

        // The working directory's last segment, which is what a shell puts in a
        // Windows Terminal tab. Trailing separators are trimmed first so
        // "C:\src\fmn\" and "C:\src\fmn" give the same answer; a path that is
        // nothing but a root has no leaf and returns empty, which the caller
        // treats as "don't attempt tab selection".


        // WT puts every window of one launch context in a single process, so
        // Process.MainWindowHandle can't tell tabs apart — but UI Automation
        // enumerates the real TabItem elements of every window that process
        // owns, each with a live Name, and a TabItem's SelectionItemPattern
        // genuinely switches to it (confirmed against a real interactive
        // session; both the window and the tab change in one call — see
        // docs/windows-wt-tabs-findings.md). A titled session's tab Name is
        // "✳ " + the chat title.
        //
        // Deliberately NOT matching on a bare "claude" when status.Title is
        // empty, even though a single such tab would in principle be
        // unambiguous: measured live, a fresh session reads literally
        // "claude" for well under a second before Claude Code sets its own
        // "✳ Claude Code" placeholder title, and that placeholder (not
        // "claude") is what an untitled session sits at indefinitely
        // afterwards. So by the time a human actually clicks an orb, a bare
        // "claude" tab is never that session's own tab — it can only be some
        // other session caught mid-startup — and matching it would pick the
        // wrong window's tab with confidence. See findings doc for the
        // second-by-second trace. status.Title empty means: don't attempt
        // tab selection at all, just fall through to window activation.
        //
        // Shelling out to (Windows) PowerShell rather than adding a
        // System.Windows.Automation package reference keeps this file's
        // approach consistent with the macOS side (osascript) and avoids
        // pulling Windows Desktop framework assemblies into a project that
        // also builds for macOS.
        //
        // Never worse than today: this only returns true when exactly one
        // tab matched and Select() ran. Anything else — zero matches, more
        // than one, PowerShell missing, UIA slow, any exception — returns
        // false and FocusWindows falls through to its existing
        // window-activation path unchanged.
        // On success, tabWindow is the handle of the window that owns the matched
        // tab — the caller needs it to actually bring that window forward, which
        // selecting the tab does not reliably do (see FocusWindows).
        private static bool TrySelectWindowsTerminalTab(SessionStatus status, out IntPtr tabWindow)
        {
            tabWindow = IntPtr.Zero;

            // TermPid is no longer required. It is still the cheap path — one
            // process, its windows only — but a Codex session on Windows
            // routinely has none: Windows Terminal is not in its ancestry
            // (measured: powershell -> pwsh -> codex.exe -> node.exe -> sh.exe,
            // whose own parent has already exited), so the walk has nothing to
            // record. Refusing on that left every Codex orb falling through to
            // window activation, which raises the right *window* and shows
            // whatever tab was already in front — a click that visibly does
            // nothing when both sessions share one window, which is the normal
            // case. 0 means "look at every Windows Terminal", and the
            // one-unambiguous-match rule below is unchanged and is what keeps
            // the wider search honest.
            if (status.TermPid < 0) return false;

            // The title alone, with no glyph prefix — the script matches on the
            // tab name's *ending*. "✳ " + title was the original, and it is
            // wrong for exactly half the time an orb is worth clicking: Claude
            // Code swaps that ✳ for an animated braille spinner while it is
            // actually working, so a generating session's tab reads
            // "⠐ Check Claude Code status" (and "⠂ …", and every other frame)
            // rather than "✳ Check Claude Code status".
            //
            // Observed live with two sessions in one window, which is what made
            // it look intermittent — the idle one's tab matched and its orb
            // worked, the generating one's never matched and its orb didn't.
            // Failing that match doesn't fail safe, either: it falls through to
            // MainWindowHandle activation, and since every tab of a Windows
            // Terminal window shares one process, that raises the window with
            // whatever *other* tab was in front still showing.
            //
            // Matching the tail rather than adding the spinner frames to the
            // list of accepted prefixes is deliberate: the frames are an
            // implementation detail of somebody else's progress animation, and
            // the next status glyph Claude Code invents would break a list
            // again. The one-unambiguous-match rule below is what keeps this
            // honest, and it is unchanged.
            //
            // What a tab is actually *called* differs by CLI, so the string to
            // match on does too.
            //
            // Claude Code renames the tab to the chat title, so the title is
            // the identifying text and the tail match above is about its status
            // glyph. Codex renames nothing: its tab keeps whatever the shell
            // put there, which is the working directory's leaf — measured live,
            // a Codex session titled "what branch is this repo on" sat in a tab
            // named "fmn". Matching the title for Codex could therefore never
            // succeed, so that was a dead click by construction rather than an
            // intermittent one.
            //
            // The leaf is weaker evidence than a chat title, and it is worth
            // being clear about that: it names a directory, not a session. Two
            // Codex sessions in one directory, or a plain shell sitting in it,
            // produce tabs that read the same — which is exactly what the
            // exactly-one-match rule is for; two matches refuse and fall
            // through to window activation rather than guess between them. The
            // case it cannot see is a *single* non-Codex tab in that directory,
            // which would be selected; the cost is landing on a terminal in the
            // right directory rather than the right session, and it is why this
            // is a leaf match and not a substring one.
            var target = status.Source == SessionSource.Codex
                ? TerminalScripts.LeafOf(status.Cwd)
                : status.Title;

            if (string.IsNullOrEmpty(target)) return false;

            // The script has to reach powershell.exe as a *file* (-File), not
            // as -Command text with trailing arguments — verified the hard
            // way: powershell.exe's -Command greedily joins every remaining
            // argument onto the script text and reparses the lot as one
            // command line, so $args never receives them; the title just
            // gets spliced onto the end of the script and fails to parse.
            // -File is the only form where trailing arguments actually
            // arrive as $args.
            string scriptPath;
            try
            {
                scriptPath = Path.Combine(Path.GetTempPath(), $"cb-wt-tab-select-{Guid.NewGuid():N}.ps1");
                File.WriteAllText(scriptPath, SelectTabScript);
            }
            catch
            {
                return false;
            }

            try
            {
                // -NonInteractive: this must never pop a console of its own.
                // Bounded well under the "second or two" budget from
                // docs/windows-wt-tabs.md — a full round trip through a fresh
                // powershell.exe measured ~400ms for a handful of windows/tabs.
                var ok = TryRun("powershell.exe", 1500, out var stdout,
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                    status.TermPid.ToString(), target);

                if (!ok) return false;

                // "SELECTED:<hwnd of the tab's window>". A selection that can't
                // name its window is reported as a miss rather than a success:
                // the caller would have nothing to raise, and falling through to
                // window activation is a better outcome than stopping there.
                const string prefix = "SELECTED:";
                var reply = stdout.Trim();
                if (!reply.StartsWith(prefix, StringComparison.Ordinal)) return false;

                if (!long.TryParse(reply[prefix.Length..], out var handle) || handle == 0) return false;

                tabWindow = new IntPtr(handle);
                return true;
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }

        // $args[0] = target process id, $args[1] = the session title a tab name
        // must end with. Passed as process arguments rather than interpolated
        // into this script text — a session title is arbitrary user text (could
        // contain quotes, `$`, etc.) and splicing it into the script source
        // would be a PowerShell injection risk, not just an escaping
        // nuisance. The comparison is ordinal (case- and byte-exact) on the
        // title itself; only the status glyph ahead of it is allowed to vary.
        //
        // Two things here are about keyboard focus rather than about tabs, and
        // both were paid for. Selecting a tab through UIA puts focus on the tab
        // *header*, not in the terminal — measured with
        // AutomationElement.FocusedElement either side of the call: TermControl
        // before, TabItem/ListViewItem after. When the tab wasn't current that
        // never showed, because switching tabs moves focus into the newly shown
        // pane afterwards; when it was already current, Select() changes no
        // selection and the focus jolt is all that happens. Dictated text then
        // went to a focused tab header, which Windows Terminal takes as the
        // start of an inline rename — "it highlights the tab title and that's
        // it", and only ever on the tab you were already looking at.
        //
        // So: don't Select() a tab that is already selected, and then put focus
        // in the pane explicitly. The SetFocus() is what makes this recover
        // rather than merely stop breaking — a window left focused on its tab
        // header by an earlier run would otherwise stay that way, since nothing
        // else moves focus back. There is exactly one on-screen TermControl (WT
        // only exposes the active tab's) so "the one that isn't offscreen" is
        // unambiguous, and SetFocus on it was confirmed to pull focus back off
        // a tab header.
        private const string SelectTabScript = """
            $targetPid = [int]$args[0]
            $target = $args[1]
            Add-Type -AssemblyName UIAutomationClient
            Add-Type -AssemblyName UIAutomationTypes
            $root = [System.Windows.Automation.AutomationElement]::RootElement
            # A pid of 0 means the hook could not record one (see the caller).
            # Every Windows Terminal is then in scope, which widens what the
            # match has to be unambiguous across but does not weaken the rule
            # itself: still exactly one tab, or nothing happens.
            $targetPids = if ($targetPid -gt 0) { @($targetPid) }
                          else { @(Get-Process WindowsTerminal -ErrorAction SilentlyContinue |
                                   ForEach-Object { $_.Id }) }
            $tabCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::TabItem)
            $found = @()
            foreach ($somePid in $targetPids) {
                $procCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $somePid)
                $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $procCond)
                foreach ($win in $windows) {
                    foreach ($tab in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) {
                        if ($tab.Current.Name.EndsWith($target, [System.StringComparison]::Ordinal)) {
                            $found += [pscustomobject]@{ Tab = $tab; Window = $win; Hwnd = $win.Current.NativeWindowHandle }
                        }
                    }
                }
            }
            if ($found.Count -eq 1) {
                $pattern = $found[0].Tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                if (-not $pattern.Current.IsSelected) {
                    $pattern.Select()
                    Start-Sleep -Milliseconds 120
                }
                $termCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'TermControl')
                foreach ($term in $found[0].Window.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants, $termCond)) {
                    if (-not $term.Current.IsOffscreen) {
                        try { $term.SetFocus() } catch { }
                        break
                    }
                }
                Write-Output "SELECTED:$($found[0].Hwnd)"
            } else {
                Write-Output "NOMATCH:$($found.Count)"
            }
            """;

    }
}
