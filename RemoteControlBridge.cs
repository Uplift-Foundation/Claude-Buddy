using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClaudeBuddy
{
    // The one hidden Claude Code session Buddy runs so it can reach Claude Code
    // sessions on *other* machines.
    //
    // Why this exists at all: Anthropic's Remote Control relay has no
    // third-party API, so Buddy cannot ask it anything. But a Claude Code
    // session that itself has Remote Control on is given peer tools — ListAgents
    // and SendMessage — that reach the rest of the account's sessions, wherever
    // they are running. So Buddy starts a session of its own and talks through
    // it. The user's own account is the relay; there is no server of ours in the
    // path, nothing to host, and nothing for the user to configure beyond which
    // account to use. docs/remote-control-findings.md is the measurement that
    // this works, taken before any of it was built.
    //
    // Four things about the shape here are load-bearing, and all four were
    // learned the hard way in that spike:
    //
    //  * **It runs in tmux, detached.** `tmux new-session -d` needs no attached
    //    client, which is what makes a hidden session possible at all. The
    //    launch line must not be piped: Claude Code correctly decides a piped
    //    stdout is not a TTY and demands --print, so output is read with
    //    capture-pane instead.
    //
    //  * **A private TMPDIR keeps it out of the orb scan.** ClaudeBuddyHook.sh
    //    writes its status file to $TMPDIR/claude_buddy/<id>.txt, so pointing
    //    the bridge at a directory only this class reads means SessionManager
    //    never sees it. Better than synthesising a status file: the hook hands
    //    over the session id, the pane, the socket and the transcript path for
    //    free, and its arrival *is* the readiness signal.
    //
    //  * **Requests are serialized.** This is one interactive session, so it has
    //    one input line and one turn at a time. Two prompts pasted at once
    //    interleave into gibberish, so everything queues behind a semaphore.
    //
    //  * **One tail, demultiplexed.** Replies from other machines arrive
    //    asynchronously on some later turn, with nothing tying them to the send
    //    that caused them except a from-name. So there is a single reader over
    //    the bridge's transcript and it routes by name — not a reader per remote
    //    session, which would mean N watchers over one file racing each other.
    //
    // Not hidden from the *user*, though, only from Buddy's own scan: turning on
    // Remote Control publishes the bridge to the account's RC surface, so it
    // appears on their phone and they can type into it. Buddy must therefore
    // tolerate turns in the transcript it did not cause, which the from-name
    // correlation already does.
    internal sealed class RemoteControlBridge : IDisposable
    {
        // Per account, not fixed.
        //
        // It was one fixed name, which doubled as a machine-wide mutex — fine
        // while one relay could exist. With one relay per account that name
        // becomes a collision: starting the second would kill the first (they
        // adopt-or-replace by name), and the two would fight over one pane
        // forever. Suffixed by profile, it is still a mutex, just per account,
        // which is the granularity that was actually meant.
        private readonly string _tmuxSessionName;

        // The same name as an exact-match target. Used everywhere a session is
        // addressed; the bare name is only for creating it.
        private readonly string _tmuxTarget;

        // The same session as a *pane* target — exact, and resolving to its
        // active pane. Used before the hook has told us a real pane id.
        private readonly string _tmuxPaneTarget;

        private const string TmuxSessionPrefix = "claude-buddy-rc-";

        // The account this relay signs in as, fixed at construction. Not read
        // from settings on demand: a relay's account is baked in when its
        // process starts, so reading it later could report one thing while the
        // running session is another.
        private readonly string _profileDir;

        public RemoteControlBridge(string profileDir)
        {
            _profileDir = string.IsNullOrWhiteSpace(profileDir)
                ? ClaudeBuddySettings.DefaultRemoteControlProfileDir
                : profileDir;

            // tmux session names cannot contain a dot or a colon — it parses
            // them as window/pane separators — and a profile dir starts with one.
            var safe = _profileDir.Replace('.', '-').Replace(':', '-');

            // Test seam, same pattern as CLAUDE_BUDDY_SETTINGS_DIR and
            // CLAUDE_BUDDY_PROFILE_ROOT: without it a live test and the
            // installed app fight over one relay.
            //
            // The per-account name is deliberately a machine-wide mutex — a
            // second Buddy adopting-or-replacing the first is the right answer
            // for a user, who wants one relay and not two bills. But it means a
            // test that starts a relay kills the running app's, and the app then
            // takes its own back, and the two trade it until one of them loses a
            // race. Measured exactly that way: the same live test passed and
            // failed on consecutive runs with the app up.
            //
            // Never set in production, so the mutex is unaffected where it
            // matters.
            var tag = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_RC_BRIDGE_TAG");
            if (!string.IsNullOrWhiteSpace(tag)) safe += "-" + tag.Replace('.', '-').Replace(':', '-');

            _tmuxSessionName = TmuxSessionPrefix + safe;

            // "=" forces an exact match. Without it tmux resolves a target by
            // prefix, and one account's name is a prefix of another's the moment
            // someone has ".claude" and ".claude-board" — which is the common
            // case, not a contrived one. Measured: `kill-session -t
            // claude-buddy-rc--claude` killed `claude-buddy-rc--claude-board`,
            // so starting the second relay silently destroyed the first and the
            // survivor then answered nothing. Every target below is exact.
            _tmuxTarget = "=" + _tmuxSessionName;

            // A pane target needs the trailing colon as well as the "=".
            // Measured: `send-keys -t =name` answers "can't find pane", because
            // for a pane target tmux wants session:window.pane and "=name" alone
            // is not one — while "=name:" resolves to that exact session's
            // active pane, which is what a freshly created session has exactly
            // one of. Same reason AgentTeamViewer's new-window passes
            // "<session>:" rather than the bare name.
            _tmuxPaneTarget = _tmuxTarget + ":";
        }

        public string ProfileDir => _profileDir;

        // Where a relay's scratch lives, and what this one's is called. Exposed
        // so RemoteControlSessions can clear out the ones nothing owns any more —
        // see SweepStaleScratch, and ScratchRoot below for why they pile up.
        public string ScratchName => _tmuxSessionName;

        public static string ScratchRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Caches", "ClaudeBuddy", "rc-bridge");

        // How long to wait for the hook's status file to appear, then for the
        // Remote Control banner. Starting Claude Code cold — plugin sync, MCP,
        // auth — was ~12s when measured, so this is generous on purpose; the
        // cost of being wrong is a feature that looks broken on slow machines.
        private const int ReadyTimeoutMs = 45_000;
        private const int ReadyPollMs = 500;

        // One turn's worth of patience. A remote machine's session may be busy,
        // and the model has to notice the tool result before it answers.
        private const int RequestTimeoutMs = 90_000;

        private readonly SemaphoreSlim _turn = new(1, 1);
        private readonly object _gate = new();

        private string? _tmux;
        private string? _privateTmp;
        private string? _sessionId;
        private string? _transcriptPath;
        private string? _pane;
        private string? _tmuxSocket;

        private long _offset;
        private readonly List<byte> _carry = new();
        private readonly HashSet<string> _seenRows = new(StringComparer.Ordinal);

        // Satisfied by the tail when a row it was waiting for shows up. Only one
        // can be outstanding, because only one request can be in flight.
        private TaskCompletionSource<string>? _awaitingToolResult;
        private Func<string, bool>? _toolResultMatches;

        public bool IsRunning
        {
            get { lock (_gate) return _sessionId is not null; }
        }

        public string? Warning { get; private set; }

        // Why the relay could not start, when the reason is knowable. Null for
        // an ordinary failure — a missing binary, no tmux — where there is
        // nothing useful to add beyond what the caller already knows.
        public string? StartFailure { get; private set; }

        // Every message another session has sent the bridge since it started.
        // Raised on a background thread; callers marshal onto the UI thread
        // themselves, the way OpenClawSessions does.
        public event Action<BridgeProtocol.InboundMessage>? MessageReceived;

        // Remote Control is reached through tmux here, which is macOS/Linux only.
        // Windows has no equivalent in this app today — chat-send is already
        // tmux-only there — so the feature is gated rather than half-working.
        public static bool IsSupported => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // --- starting ---

        public async Task<bool> StartAsync()
        {
            if (!IsSupported) return false;
            if (IsRunning) return true;

            var claude = ClaudeBinary.Path;
            if (claude is null) return false;

            _tmux = ResolveTmux();
            if (_tmux is null) return false;

            // Adopt-or-replace rather than "fail because it exists". A bridge
            // left behind by a crash is indistinguishable from a live one from
            // out here, and its transcript offset is lost either way, so the
            // honest move is to start clean.
            Run(_tmux, 3000, out _, "kill-session", "-t", _tmuxTarget);

            _privateTmp = PreparePrivateTmp();
            if (_privateTmp is null) return false;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configDir = Path.Combine(home, _profileDir);

            if (!Run(_tmux, 5000, out _, "new-session", "-d", "-s", _tmuxSessionName,
                    "-x", "200", "-y", "50", "-c", home))
            {
                return false;
            }

            // Sent as a shell line rather than as new-session's own command
            // argument, so the env assignments are applied by the shell. Every
            // path is single-quoted because a home directory can contain spaces.
            //
            // CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS is set unconditionally. The
            // spike could not establish whether the peer tools genuinely require
            // it — both local profiles already set it — so rather than ship on an
            // unresolved question, Buddy sets it: harmless if redundant,
            // load-bearing if not.
            var line = new StringBuilder()
                .Append("TMPDIR=").Append(Quote(_privateTmp)).Append(' ')
                .Append("CLAUDE_CONFIG_DIR=").Append(Quote(configDir)).Append(' ')
                .Append("CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1 ")
                .Append(Quote(claude))
                .Append(" --remote-control ").Append(Quote(_tmuxSessionName))
                .ToString();

            if (!Run(_tmux, 5000, out _, "send-keys", "-t", _tmuxPaneTarget, line, "Enter"))
            {
                Stop();
                return false;
            }

            var started = await WaitForStatusFileAsync().ConfigureAwait(false);
            if (!started)
            {
                Stop();
                return false;
            }

            // The status file only proves the session came up. Remote Control
            // attaching is a separate event, and a bridge whose RC never
            // attached is running and useless — so it is confirmed separately
            // rather than assumed from a live process.
            await WaitForRemoteControlAsync().ConfigureAwait(false);

            return IsRunning;
        }

        // The hook announces the session here, which is both the readiness
        // signal and where the session id, pane and transcript path come from.
        private async Task<bool> WaitForStatusFileAsync()
        {
            var dir = Path.Combine(_privateTmp!, "claude_buddy");
            var deadline = Environment.TickCount64 + ReadyTimeoutMs;

            while (Environment.TickCount64 < deadline)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        var file = Directory.EnumerateFiles(dir, "*.txt").FirstOrDefault();
                        if (file is not null && Adopt(file)) return true;
                    }
                }
                catch
                {
                    // Mid-write, or the directory appearing under us.
                }

                // An interactive setup screen means nothing will ever arrive:
                // the session is sitting on a question with nobody to answer it.
                // Checked here rather than after the timeout so 45 seconds of
                // waiting turns into an immediate, actionable message.
                var blocked = BridgeProtocol.ReadSetupBlock(CapturePane());
                if (blocked is not null)
                {
                    StartFailure = blocked;
                    return false;
                }

                await Task.Delay(ReadyPollMs).ConfigureAwait(false);
            }

            return false;
        }

        private bool Adopt(string statusFile)
        {
            try
            {
                using var stream = File.Open(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var status = JsonSerializer.Deserialize<SessionStatus>(stream);
                if (status is null || string.IsNullOrWhiteSpace(status.TranscriptPath)) return false;

                lock (_gate)
                {
                    _sessionId = Path.GetFileNameWithoutExtension(statusFile);
                    _transcriptPath = status.TranscriptPath;
                    _pane = status.TmuxPane;
                    _tmuxSocket = status.TmuxSocket;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task WaitForRemoteControlAsync()
        {
            var deadline = Environment.TickCount64 + ReadyTimeoutMs;

            while (Environment.TickCount64 < deadline)
            {
                var health = BridgeProtocol.ReadHealth(CapturePane());
                Warning = health.Warning;
                if (health.RemoteControlActive) return;

                await Task.Delay(ReadyPollMs).ConfigureAwait(false);
            }
        }

        // --- asking it things ---

        // The peers the bridge can see, or null if it could not be asked.
        public async Task<IReadOnlyList<BridgeProtocol.RemoteAgent>?> ListAgentsAsync()
        {
            var raw = await AskAsync(
                BridgeProtocol.ListAgentsPrompt,
                text => text.Contains("Peer sessions", StringComparison.Ordinal)
                        || text.Contains("no peer", StringComparison.OrdinalIgnoreCase))
                .ConfigureAwait(false);

            return raw is null ? null : BridgeProtocol.ParseAgents(raw);
        }

        // Hands a message to a session on another machine. The returned id is the
        // relay's own receipt; the *reply* comes back later through
        // MessageReceived, because there is no turn in which both happen.
        public async Task<string?> SendToAsync(string peerName, string text)
        {
            var raw = await AskAsync(
                BridgeProtocol.SendMessagePrompt(peerName, text),
                t => t.Contains("msg_id", StringComparison.Ordinal))
                .ConfigureAwait(false);

            return raw is null ? null : BridgeProtocol.ParseSentMessageId(raw);
        }

        // Asks a session what colour it is. Fire-and-forget by nature: the
        // answer arrives later as an ordinary inbound message, which
        // RemoteControlSessions recognises by its marker and swallows.
        public async Task<bool> AskColorAsync(string peerName)
        {
            var raw = await AskAsync(
                BridgeProtocol.ColorQueryPrompt(peerName),
                t => t.Contains("msg_id", StringComparison.Ordinal))
                .ConfigureAwait(false);

            return raw is not null;
        }

        // One prompt in, the matching tool result out. Serialized: this is a
        // single interactive session with a single input line, and two prompts
        // pasted at once interleave into nonsense.
        private async Task<string?> AskAsync(string prompt, Func<string, bool> matches)
        {
            if (!IsRunning) return null;

            await _turn.WaitAsync().ConfigureAwait(false);
            try
            {
                var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                lock (_gate)
                {
                    _awaitingToolResult = waiter;
                    _toolResultMatches = matches;
                }

                if (!Paste(prompt)) return null;

                // Drain as fast as the tail can, rather than waiting for the
                // ambient poll: a request is exactly when someone is listening.
                using var cts = new CancellationTokenSource(RequestTimeoutMs);
                var pumping = PumpUntilAsync(waiter.Task, cts.Token);

                var done = await Task.WhenAny(waiter.Task, pumping).ConfigureAwait(false);
                return done == waiter.Task ? waiter.Task.Result : null;
            }
            finally
            {
                lock (_gate)
                {
                    _awaitingToolResult = null;
                    _toolResultMatches = null;
                }

                _turn.Release();
            }
        }

        private async Task PumpUntilAsync(Task settled, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && !settled.IsCompleted)
                {
                    Pump();
                    await Task.Delay(600, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Timed out; AskAsync reports that as a null.
            }
        }

        // --- reading the transcript ---

        // Reads whatever is new and routes it. Safe to call from the ambient
        // poll and from a request at the same time — the offset and carry are
        // only touched under the lock.
        public void Pump()
        {
            string path;
            long from;

            lock (_gate)
            {
                if (_transcriptPath is null) return;
                path = _transcriptPath;
                from = _offset;
            }

            byte[] buffer;
            long to;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // Shorter than where we were means the file was replaced — a
                // /clear starts a new transcript. Carrying the old offset would
                // read from the middle of an unrelated row forever.
                if (fs.Length < from)
                {
                    lock (_gate) { _carry.Clear(); _offset = 0; }
                    from = 0;
                }

                if (fs.Length <= from) return;

                fs.Seek(from, SeekOrigin.Begin);
                buffer = new byte[fs.Length - from];
                fs.ReadExactly(buffer);
                to = fs.Length;
            }
            catch
            {
                // Mid-write, or gone. The next tick tries again.
                return;
            }

            List<string> lines;
            lock (_gate)
            {
                _offset = to;
                lines = TakeWholeLines(buffer);
            }

            foreach (var line in lines) Route(line);
        }

        // Appends to whatever partial line was left over and returns the
        // complete lines that makes, keeping the remainder. Byte-level, because
        // a write landing mid-codepoint would otherwise leave a permanent
        // replacement character in the middle of a message.
        private List<string> TakeWholeLines(byte[] buffer)
        {
            _carry.AddRange(buffer);

            var lines = new List<string>();
            var start = 0;

            for (var i = 0; i < _carry.Count; i++)
            {
                if (_carry[i] != (byte)'\n') continue;

                var count = i - start;
                if (count > 0) lines.Add(Encoding.UTF8.GetString(_carry.GetRange(start, count).ToArray()));
                start = i + 1;
            }

            if (start > 0) _carry.RemoveRange(0, start);
            return lines;
        }

        private void Route(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { return; }

            using (doc)
            {
                var root = doc.RootElement;

                // Rows are re-read after a restart or a rewrite; a message
                // delivered twice would be a duplicate chat bubble.
                if (root.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String)
                {
                    var id = uuid.GetString();
                    if (id is not null)
                    {
                        lock (_gate)
                        {
                            if (!_seenRows.Add(id)) return;
                        }
                    }
                }

                if (!root.TryGetProperty("message", out var message)) return;
                if (!message.TryGetProperty("content", out var content)) return;

                if (content.ValueKind == JsonValueKind.String)
                {
                    Deliver(content.GetString() ?? "");
                    return;
                }

                if (content.ValueKind != JsonValueKind.Array) return;

                foreach (var block in content.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                        continue;

                    switch (type.GetString())
                    {
                        case "tool_result":
                            Deliver(Flatten(block));
                            break;

                        // The bridge narrating a reply rather than the reply row
                        // itself. Worth reading: a paraphrase still carries the
                        // tag when the model quotes it back.
                        case "text":
                            Deliver(block.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "");
                            break;
                    }
                }
            }
        }

        // A tool_result's content is either a string or the usual array of
        // typed blocks. Both shapes appear in one transcript.
        private static string Flatten(JsonElement block)
        {
            if (!block.TryGetProperty("content", out var content)) return "";
            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
            if (content.ValueKind != JsonValueKind.Array) return "";

            var text = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    text.AppendLine(t.GetString());
            }

            return text.ToString();
        }

        // Two unrelated things can be true of one piece of text, so both are
        // checked: it may satisfy the request in flight *and* carry a message
        // from another machine.
        private void Deliver(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            TaskCompletionSource<string>? waiter = null;
            lock (_gate)
            {
                if (_awaitingToolResult is not null && _toolResultMatches?.Invoke(text) == true)
                {
                    waiter = _awaitingToolResult;
                    _awaitingToolResult = null;
                    _toolResultMatches = null;
                }
            }

            waiter?.TrySetResult(text);

            var inbound = BridgeProtocol.ParseInboundMessage(text);
            if (inbound is not null) MessageReceived?.Invoke(inbound.Value);
        }

        // --- stopping ---

        public void Stop()
        {
            var tmux = _tmux;
            if (tmux is not null) Run(tmux, 3000, out _, "kill-session", "-t", _tmuxTarget);

            lock (_gate)
            {
                _sessionId = null;
                _transcriptPath = null;
                _pane = null;
                _offset = 0;
                _carry.Clear();
                _seenRows.Clear();
            }

            // The bridge's own transcript stays where Claude Code put it; only
            // the scratch status directory is ours to remove.
            TryDeletePrivateTmp();
        }

        public void Dispose()
        {
            Stop();
            _turn.Dispose();
        }

        // --- plumbing ---

        // Same three calls TerminalFocuser uses to type into a pane without
        // taking focus: buffer the text, paste it as a bracketed paste so a
        // multi-line prompt arrives as one paste rather than a series of
        // newlines, then send the Return separately.
        private bool Paste(string text)
        {
            var tmux = _tmux;
            string? pane;
            lock (_gate) pane = _pane;

            if (tmux is null || string.IsNullOrEmpty(pane)) return false;

            if (!Run(tmux, 3000, out _, Args("set-buffer", "-b", _tmuxSessionName, "--", text))) return false;
            if (!Run(tmux, 3000, out _, Args("paste-buffer", "-b", _tmuxSessionName, "-t", pane!, "-p", "-d"))) return false;

            return Run(tmux, 3000, out _, Args("send-keys", "-t", pane!, "Enter"));
        }

        private string CapturePane()
        {
            var tmux = _tmux;
            string? pane;
            lock (_gate) pane = _pane;

            // Before the status file lands there is no pane id yet, so the
            // session name stands in — it resolves to its own active pane.
            var target = string.IsNullOrEmpty(pane) ? _tmuxPaneTarget : pane!;

            if (tmux is null) return "";
            return Run(tmux, 3000, out var text, Args("capture-pane", "-p", "-t", target)) ? text : "";
        }

        // -S pins the server the bridge's pane actually lives on. Several can
        // coexist and a pane id is only unique within one.
        private string[] Args(params string[] args)
        {
            string? socket;
            lock (_gate) socket = _tmuxSocket;

            if (string.IsNullOrEmpty(socket)) return args;

            var full = new string[args.Length + 2];
            full[0] = "-S";
            full[1] = socket!;
            args.CopyTo(full, 2);
            return full;
        }

        private string? PreparePrivateTmp()
        {
            try
            {
                // Per account, for the same reason the tmux name is: two relays
                // sharing a status directory would each adopt whichever file
                // landed first, and both would tail one transcript.
                // Keyed off the session name rather than the profile alone, so
                // the test seam above separates status directories too — two
                // relays sharing one would each adopt whichever file landed
                // first and both tail one transcript.
                var root = Path.Combine(ScratchRoot, _tmuxSessionName);

                // Cleared on every start: a status file from a previous bridge
                // would be adopted as this one's, pointing the tail at a
                // transcript that has stopped growing.
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                Directory.CreateDirectory(root);
                return root;
            }
            catch
            {
                return null;
            }
        }

        private void TryDeletePrivateTmp()
        {
            var dir = _privateTmp;
            _privateTmp = null;
            if (dir is null) return;

            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

        private static readonly string[] TmuxCandidates =
        {
            "/opt/homebrew/bin/tmux",
            "/usr/local/bin/tmux",
            "/opt/local/bin/tmux",
            "/usr/bin/tmux"
        };

        private static string? ResolveTmux() => TmuxCandidates.FirstOrDefault(File.Exists);

        private static bool Run(string exe, int timeoutMs, out string stdout, params string[] args)
        {
            stdout = "";
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return false;

                // Both pipes drained concurrently before waiting: a blocking
                // read first would make the timeout unreachable, and leaving
                // stderr undrained can deadlock a chatty child.
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

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
    }
}
