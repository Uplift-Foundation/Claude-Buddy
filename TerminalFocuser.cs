using System.Diagnostics;
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
            if (status.Source != SessionSource.ClaudeCode) return;

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
                if (status.SessionPid <= 0 && !string.IsNullOrEmpty(sessionId))
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
            if (status.Source != SessionSource.ClaudeCode) return Task.CompletedTask;

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
            status is { Source: SessionSource.ClaudeCode }
            && !string.IsNullOrEmpty(status.TmuxPane)
            && ResolveTmuxBinary(status.TmuxBin) is not null;

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
                script = ITermSelectScript("id", status.TermId);
            }
            else
            {
                script = status.TermProgram switch
                {
                    "Apple_Terminal" when !string.IsNullOrEmpty(status.Tty) => TerminalSelectScript(status.Tty),
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
                "iTerm.app" => ITermSelectScript("tty", device),
                "Terminal.app" => TerminalSelectScript(device),
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
                "iTerm.app" => ITermSelectScript("tty", clientTty),
                "Terminal.app" => TerminalSelectScript(clientTty),
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

        private static string[] TmuxArgs(SessionStatus status, params string[] args)
        {
            // -S pins the server: several can coexist (plain tmux, tmuxinator,
            // a -L named socket), and the pane id is only unique within one.
            if (string.IsNullOrEmpty(status.TmuxSocket)) return args;

            var full = new string[args.Length + 2];
            full[0] = "-S";
            full[1] = status.TmuxSocket;
            args.CopyTo(full, 2);
            return full;
        }

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
                    keystroke "{{EscapeForAppleScript(text)}}"
                end tell
                """;

            RunOsaScriptForSendText(script);
        }

        // AppleScript string literals only need their own quote and backslash
        // escaped — unlike the tab-selection scripts elsewhere in this file,
        // this text is never a hook-recorded value (a tty, a UUID); it's
        // whatever the user said, so it can contain anything a string can.
        private static string EscapeForAppleScript(string text) =>
            text.Replace("\\", "\\\\").Replace("\"", "\\\"");

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

            if (status.TermPid <= 0 || string.IsNullOrEmpty(status.Title)) return false;

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
            var target = status.Title;

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
            $procCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
            $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $procCond)
            $tabCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::TabItem)
            $found = @()
            foreach ($win in $windows) {
                foreach ($tab in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) {
                    if ($tab.Current.Name.EndsWith($target, [System.StringComparison]::Ordinal)) {
                        $found += [pscustomobject]@{ Tab = $tab; Window = $win; Hwnd = $win.Current.NativeWindowHandle }
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
        private const int ActivationPollTicks = 40;      // x 50ms = 2s ceiling

        private static string ActivateThenSettle(string app) => $$"""
            tell application "{{app}}" to activate
            repeat {{ActivationPollTicks}} times
                if frontmost of application "{{app}}" then exit repeat
                delay 0.05
            end repeat
            """;

        private static string ITermSelectScript(string property, string value) => $$"""
            {{ActivateThenSettle("iTerm")}}
            tell application "iTerm"
                repeat with w in windows
                    repeat with t in tabs of w
                        repeat with s in sessions of t
                            if {{property}} of s is "{{value}}" then
                                select w
                                select t
                                select s
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
        private static string TerminalSelectScript(string tty) => $$"""
            {{ActivateThenSettle("Terminal")}}
            tell application "Terminal"
                repeat with w in windows
                    repeat with t in tabs of w
                        if tty of t is "{{(tty.StartsWith("/dev/") ? tty : "/dev/" + tty)}}" then
                            set selected of t to true
                            set index of w to 1
                            return
                        end if
                    end repeat
                end repeat
            end tell
            """;
    }
}
