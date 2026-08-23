using System.Text;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // One local CLI session — Claude Code or Codex — as something the chat panel
    // can talk to.
    //
    // The thing worth understanding before reading any of this: **there is only
    // one conversation, and this is not a copy of it.** Both CLIs write every
    // session's transcript to a JSONL file and the hook already records where
    // (SessionStatus.TranscriptPath). That file is the conversation. This class
    // tails it, so anything typed in the terminal appears in the panel; and
    // sending goes through tmux into the terminal's own input line, so anything
    // sent from the panel appears in the terminal. Neither surface owns a copy
    // and there is nothing to reconcile — which is why there is no sync code
    // here, only a reader and a writer pointed at the same place.
    //
    // What differs between the two CLIs is small and lives in CliChatFormat: how
    // a line of their transcript maps to a turn, and which settings gate reading
    // and replying. Everything else here is *transcript-shaped-file* machinery
    // rather than Claude Code machinery — the byte offsets, the carry buffer so a
    // write landing mid-codepoint cannot leave a permanent replacement character,
    // the watcher-plus-poll pair because macOS FileSystemWatcher misses JSONL
    // appends, the window sizes measured across six real transcripts — and Codex
    // needs all of it unchanged, including the giant-row case: the largest single
    // row measured in a real rollout is 1,046,104 bytes.
    //
    // Two ways this differs from OpenClawChatSession, both consequences of the
    // transcript being a file rather than an event stream:
    //
    //  * Updates arrive per *block*, not per token. A row is appended when a
    //    thinking pass, a tool call or a paragraph completes, so the panel runs
    //    a few seconds behind the terminal's own streaming. Blocks are still
    //    fine-grained enough to watch a session work, which is most of the
    //    point; nothing here pretends to be faster than it is.
    //  * Nothing is ever mutated in place, so TurnUpdated is raised only when a
    //    message sent from the panel is reconciled against the transcript row it
    //    produced. The contract's "TurnUpdated carries the whole turn" holds
    //    trivially, since the whole turn is all there ever is.
    internal sealed class LocalCliChatSession :
        IRemoteChatSession, IRemoteChatBacklog, IRemoteChatComposer, IRemoteChatPrompts,
        IRemoteChatImages, IDisposable
    {
        // How much of the tail to read when the panel first opens.
        //
        // Sized by measurement, not by taste, because the answer is nothing like
        // what a count of turns suggests. Almost all of a transcript's bytes are
        // tool results and file-history snapshots, none of which is shown, so
        // the conversation is a thin seam through a very large file — and how
        // thin varies hugely with what the session was doing. Across six real
        // transcripts (0.6MB to 33MB), 64KB of tail yielded between **1 and 16**
        // displayable turns; 512KB yielded 14 to 86.
        //
        // So 64KB, which sounds generous for a panel showing a dozen rows, opens
        // some sessions on a single line. Half a megabyte is the point where
        // every transcript measured had more than a screenful, and it parses on
        // a worker thread in well under the time the window takes to appear.
        private const int InitialBytes = 512 * 1024;

        // Larger than the initial read for the same reason: in a tool-heavy
        // transcript a small page can step back through hundreds of kilobytes
        // and surface almost nothing, and doing that four times to fill a screen
        // is four round trips the reader can feel.
        private const int PageBytes = 1024 * 1024;

        // Same reasoning and same number as OpenClawChatSession.Add: high enough
        // that reaching it means a genuinely enormous scrollback rather than an
        // ordinary afternoon.
        private const int KeepTurns = 500;

        private readonly List<ChatTurn> _history = new();

        // Rows already turned into turns. Windows are disjoint by construction —
        // the backlog reads strictly below where the initial read started, and
        // the live tail strictly above where it ended — so this is a guard
        // rather than the mechanism, and cheap enough to keep as one.
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        private SessionStatus _status;

        // Which CLI's transcript this is tailing. Set once from the status the
        // session was created with and never re-resolved: a session does not
        // change CLI, and re-reading it per pump would invite a format swap
        // halfway through a file.
        private readonly CliChatFormat _format;

        private string _transcriptPath = "";

        // Byte offsets into the transcript. _offset is where the live tail
        // resumes; _backlogFrom is the line-aligned start of the oldest window
        // read so far, and reaching zero is what ends paging.
        private long _offset;
        private long _backlogFrom;

        // Bytes of a trailing line the writer hadn't finished when we read. Kept
        // as bytes rather than a string on purpose: a write can land mid
        // codepoint, and decoding half of one produces a replacement character
        // that never heals.
        private readonly List<byte> _carry = new();

        private FileSystemWatcher? _watcher;
        private DispatcherTimer? _poll;
        private DispatcherTimer? _debounce;
        private bool _pumping;
        private bool _started;

        // Set when the opening read has been applied. The live tail must not run
        // before that — see Pump.
        private bool _loaded;

        public LocalCliChatSession(string sessionId, SessionStatus status)
        {
            SessionId = sessionId;
            _status = status;
            _format = CliChatFormat.For(status.Source);
            DisplayName = status.Title ?? "";
        }

        public string SessionId { get; }

        // Settable for the same reason OpenClawChatSession's is: the title can
        // improve after the panel opened, when Claude Code writes an ai-title
        // for a conversation that didn't have one yet.
        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connecting;

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? HistoryReplaced;
        public event Action<int>? HistoryPrepended;
        public event Action? PromptChanged;

        // --- lifecycle ---

        // Called on every scan, so the status this holds is never the one from
        // whenever the panel happened to open. Both things that follow from it
        // change while a panel is up: a transcript path can appear late, and the
        // waiting state is the entire permission-prompt mechanism.
        public void UpdateStatus(SessionStatus status)
        {
            _status = status;
            if (!string.IsNullOrEmpty(status.Title)) DisplayName = status.Title;

            Start();

            var waiting = string.Equals(status.State, "waiting", StringComparison.OrdinalIgnoreCase);

            if (!waiting)
            {
                if (!_waiting) return;

                _waiting = false;
                SetPrompt(null);
                return;
            }

            _waiting = true;

            // Not only on the transition into waiting. Claude Code commonly asks
            // two or three permissions in a row, and the state never leaves
            // "waiting" between them — so keying off the edge showed the first
            // dialog and then sat on a stale panel through every one after it.
            // Prompt going null is the signal that the last one was answered.
            //
            // This does not spin: a refresh always ends with a prompt set, even
            // when the screen could not be read, because "something is waiting
            // and I can't tell you what" is itself an answer.
            if (Prompt is null && !_refreshing) _ = RefreshPromptAsync();
        }

        // Idempotent, and called from both construction-time binding and every
        // status update, because the transcript path is the one field the hook
        // can record later than the rest — a session whose first status file
        // predates its first message has none.
        public void Start()
        {
            if (_started) return;

            var path = _status.TranscriptPath;
            if (string.IsNullOrEmpty(path)) path = TranscriptReader.FindTranscriptFor(SessionId);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            _started = true;
            _transcriptPath = path;

            _ = LoadInitialAsync();
            Watch();
        }

        private void Watch()
        {
            var dir = Path.GetDirectoryName(_transcriptPath);
            var name = Path.GetFileName(_transcriptPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

            try
            {
                _watcher = new FileSystemWatcher(dir, name)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                // Straight onto the UI thread and through the same 150ms
                // debounce SessionManager uses for status files, for the same
                // reason: one logical append can raise several events, and
                // parsing the tail three times to find the same two rows is
                // work on the thread that draws.
                _watcher.Changed += (_, _) => Dispatcher.UIThread.Post(Nudge);
            }
            catch
            {
                // A watcher is an optimisation over the poll below, not a
                // requirement. Losing it costs latency, not correctness.
            }

            // The backstop. FileSystemWatcher on macOS misses writes to a file
            // that is appended to without its metadata changing the way the
            // watcher expects, which is exactly what a JSONL append is.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _poll.Tick += (_, _) => Pump();
            _poll.Start();
        }

        private void Nudge()
        {
            _debounce?.Stop();
            _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _debounce.Tick -= OnDebounce;
            _debounce.Tick += OnDebounce;
            _debounce.Start();
        }

        private void OnDebounce(object? sender, EventArgs e)
        {
            _debounce?.Stop();
            Pump();
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _watcher = null;
            _poll?.Stop();
            _poll = null;
            _debounce?.Stop();
            _debounce = null;
        }

        // --- reading ---

        private async Task LoadInitialAsync()
        {
            var path = _transcriptPath;

            var window = await Task.Run(() =>
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var length = fs.Length;
                    var from = Math.Max(0, length - InitialBytes);
                    var (lines, alignedFrom) = ReadWindow(fs, from, length);
                    return (Turns: MapLines(lines), From: alignedFrom, To: length);
                }
                catch
                {
                    return (Turns: new List<Mapped>(), From: 0L, To: 0L);
                }
            });

            Dispatcher.UIThread.Post(() =>
            {
                _offset = window.To;
                _backlogFrom = window.From;
                _loaded = true;

                _history.Clear();
                _seen.Clear();

                foreach (var m in window.Turns)
                {
                    if (m.Uuid is not null && !_seen.Add(m.Uuid)) continue;
                    _history.Add(m.Turn);
                }

                Trim();
                SetState(RemoteChatState.Connected);
                HistoryReplaced?.Invoke();

                // A prompt may already be up when the panel opens — the session
                // has been sitting on it since before anyone clicked.
                if (string.Equals(_status.State, "waiting", StringComparison.OrdinalIgnoreCase))
                {
                    _waiting = true;
                    _ = RefreshPromptAsync();
                }
            });
        }

        public bool HasMore => _started && _backlogFrom > 0;

        public async Task<bool> LoadOlderAsync(CancellationToken ct)
        {
            if (!HasMore) return false;

            var path = _transcriptPath;
            var to = _backlogFrom;
            var from = Math.Max(0, to - PageBytes);

            var page = await Task.Run(() =>
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var (lines, alignedFrom) = ReadWindow(fs, from, to);
                    return (Turns: MapLines(lines), From: alignedFrom);
                }
                catch
                {
                    return (Turns: new List<Mapped>(), From: to);
                }
            }, ct);

            var added = 0;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _backlogFrom = page.From;

                var older = new List<ChatTurn>();
                foreach (var m in page.Turns)
                {
                    if (m.Uuid is not null && !_seen.Add(m.Uuid)) continue;
                    older.Add(m.Turn);
                }

                if (older.Count == 0) return;

                _history.InsertRange(0, older);
                added = older.Count;
                HistoryPrepended?.Invoke(added);
            });

            // A page that parsed to nothing but moved the offset is not the end
            // — the window can be entirely tool results and bookkeeping. Saying
            // false there would stop paging at the first quiet stretch, so the
            // answer is whether the offset moved, not whether rows came back.
            return added > 0 || page.From < to;
        }

        // The live tail. Everything appended since the last read, decoded as
        // whole lines only.
        private void Pump()
        {
            // Not before the opening read has landed. Watch() starts the poll
            // immediately after kicking off LoadInitialAsync, so without this a
            // tick that beat the initial read's post back to the UI thread would
            // see _offset still at zero and read the entire file — tens of
            // megabytes of it — as though it were new.
            if (!_started || !_loaded || _pumping) return;
            _pumping = true;

            var path = _transcriptPath;
            var from = _offset;

            _ = Task.Run(() =>
            {
                List<Mapped> mapped = new();

                // Only moved once the bytes behind it have actually been mapped.
                // Assigning fs.Length up front and letting the catch below carry
                // it through would skip past whatever the failed read covered
                // and lose those rows for good.
                long to = from;

                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                    // Shorter than where we were reading means the file was
                    // replaced under us — /clear starts a new transcript and
                    // Claude Code can rewrite one wholesale. Starting over is
                    // the only correct answer; carrying the old offset would
                    // read from the middle of an unrelated row forever.
                    if (fs.Length < from)
                    {
                        _carry.Clear();
                        from = 0;
                    }

                    var length = fs.Length;
                    if (length > from)
                    {
                        fs.Seek(from, SeekOrigin.Begin);
                        var buffer = new byte[length - from];
                        fs.ReadExactly(buffer);
                        mapped = MapLines(TakeWholeLines(buffer));
                    }

                    to = length;
                }
                catch
                {
                    // Mid-write, or gone. The poll comes back in two seconds.
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _offset = to;
                    _pumping = false;

                    foreach (var m in mapped)
                    {
                        if (m.Uuid is not null && !_seen.Add(m.Uuid)) continue;
                        Add(m.Turn);
                    }
                });
            });
        }

        // Appends `buffer` to whatever partial line was left over, and returns
        // the complete lines that make. The remainder goes back into the carry.
        private List<string> TakeWholeLines(byte[] buffer)
        {
            _carry.AddRange(buffer);

            var last = _carry.LastIndexOf((byte)'\n');
            if (last < 0) return new List<string>();

            var complete = new byte[last + 1];
            _carry.CopyTo(0, complete, 0, last + 1);
            _carry.RemoveRange(0, last + 1);

            return Split(Encoding.UTF8.GetString(complete));
        }

        // A byte range of the file as whole lines. When `from` is not the start
        // of the file it almost certainly lands mid-row, so the first partial
        // line is dropped and the offset it was dropped to is returned — that
        // aligned offset is where the next page back has to stop, and using the
        // unaligned one would read the same row twice.
        private static (List<string> Lines, long From) ReadWindow(FileStream fs, long from, long to)
        {
            if (to <= from) return (new List<string>(), from);

            fs.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[to - from];
            fs.ReadExactly(buffer);

            var start = 0;
            if (from > 0)
            {
                var nl = Array.IndexOf(buffer, (byte)'\n');

                // A whole window inside one row, which a megabyte-long
                // file-history snapshot manages. Reporting `to` would leave the
                // backlog offset exactly where it was, so every scroll to the
                // top would re-read the same megabyte and never get past it.
                // Reporting `from` steps over the window instead: the row is
                // unparseable from here anyway, and the page before it picks up
                // whatever came earlier.
                if (nl < 0) return (new List<string>(), from);

                start = nl + 1;
            }

            var text = Encoding.UTF8.GetString(buffer, start, buffer.Length - start);
            return (Split(text), from + start);
        }

        private static List<string> Split(string text) =>
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

        // --- mapping rows onto turns ---
        //
        // The parsing itself lives in ChatTranscript or CodexTranscript, both
        // pure and tested. What stays here is only the part that needs this
        // session: which bytes to read, whose format to read them as, and what
        // to do with the turns afterwards.

        private readonly record struct Mapped(string? Uuid, ChatTurn Turn);

        private List<Mapped> MapLines(List<string> lines) =>
            _format.Map(lines).Select(r => new Mapped(r.Uuid, r.Turn)).ToList();

        // --- sending ---

        public string ComposerHint
        {
            get
            {
                if (!TerminalFocuser.CanSendQuietly(_status)) return "No pane to type into";
                return _format.ReplyEnabled() ? "Message…" : "Replying is off";
            }
        }

        // A message sent from the panel, waiting for the transcript row it will
        // produce. Held so the two can be reconciled instead of the same
        // sentence appearing twice a second apart.
        //
        // Two candidate texts rather than one, because an image-bearing send
        // can come back from the transcript in either of two shapes and
        // there is no way to know in advance which this CLI does. _pendingRaw
        // is what was actually typed — caption and path both — which is what
        // comes back verbatim if the CLI never noticed the path was a
        // picture. _pendingCaption is the caption alone, which is what comes
        // back if it did: see ChatTranscript's image handling, confirmed
        // against a real transcript row, for why the two diverge only then.
        // For a plain text send the two are identical, so nothing here
        // changes that path's behaviour.
        private ChatTurn? _pending;
        private string _pendingRaw = "";
        private string _pendingCaption = "";
        private DateTimeOffset _pendingAt;

        public Task SendAsync(string text) => SendCoreAsync(typedText: text, displayText: text, imageBytes: null);

        // The picture is already a file by the time this is called — the
        // panel wrote it there before pasting its path in, the same way a
        // Finder drag-and-drop already puts a path in front of these two
        // CLIs rather than a picture. So the terminal gets the caption with
        // the paths appended as their own words, which is what a drop looks
        // like once it lands in the terminal's own input — but the bubble
        // shown locally gets the caption alone plus a thumbnail read
        // straight back from the same file, since there is no reason to make
        // this app's own echo wait on whether the CLI recognises the path.
        //
        // Only the first picture gets a thumbnail before the real transcript
        // row lands, matching the one-picture-per-turn a received image
        // already has; every path is still typed, so nothing beyond the
        // preview is limited to one.
        public async Task SendWithImagesAsync(string text, IReadOnlyList<string> imagePaths)
        {
            if (imagePaths.Count == 0)
            {
                await SendAsync(text);
                return;
            }

            var caption = text.Trim();
            var typed = imagePaths.Aggregate(caption, (line, path) => line.Length == 0 ? path : line + " " + path);

            byte[]? thumbnail = null;
            try { thumbnail = await File.ReadAllBytesAsync(imagePaths[0]); }
            catch
            {
                // No preview before the real row lands is not a reason to
                // fail the send — the file is still on disk and the
                // terminal still gets its path.
            }

            await SendCoreAsync(typed, caption, thumbnail);
        }

        private async Task SendCoreAsync(string typedText, string displayText, byte[]? imageBytes)
        {
            if (!_format.ReplyEnabled())
            {
                // A System turn rather than an exception, for the reason
                // OpenClawChatSession gives at the same point: the person has
                // just typed a sentence and losing it behind a dialog is a poor
                // answer to "why didn't that send".
                Note("Replying is off. Turn on \"Allow replying to sessions\" in Settings.");
                return;
            }

            if (!TerminalFocuser.CanSendQuietly(_status))
            {
                Note(string.IsNullOrEmpty(_status.TmuxPane)
                    ? "This session isn't in a tmux pane, so there is nowhere to type without "
                    + "bringing its terminal forward. Reply in the terminal instead."
                    : "Couldn't find tmux to type with.");
                return;
            }

            var mine = new ChatTurn
            {
                Role = ChatRole.User,
                Text = displayText,
                IsComplete = true,
                ImageBytes = imageBytes
            };

            // Added *before* being marked pending, not after. Add() runs every
            // turn through Reconcile, so setting _pending first made the user's
            // own message match itself: it was reconciled away on the spot and
            // never reached the history at all. Sending appeared to do nothing.
            Add(mine);

            _pending = mine;
            _pendingRaw = typedText.Trim();
            _pendingCaption = displayText.Trim();
            _pendingAt = DateTimeOffset.Now;

            var sent = await TerminalFocuser.SendTextAndSubmit(_status, typedText);
            if (sent) return;

            _pending = null;
            Note("Couldn't send that to the terminal.");
        }

        // The transcript will produce the message we just sent, because it went
        // through the terminal — that is the whole design. So the row that comes
        // back adopts the turn already on screen rather than adding a second.
        //
        // Matched on text and bounded by time: an identical message sent twice
        // an hour apart must not have the second one swallowed by a stale
        // pending turn that never arrived.
        private bool Reconcile(ChatTurn incoming)
        {
            if (_pending is null) return false;

            if (DateTimeOffset.Now - _pendingAt > TimeSpan.FromMinutes(2))
            {
                _pending = null;
                return false;
            }

            // The pending turn is itself passed through Add on the way in, and
            // must not reconcile against itself. SendAsync orders things so this
            // cannot happen; the check stays because the failure it prevents —
            // a sent message silently never appearing — is invisible.
            if (ReferenceEquals(incoming, _pending)) return false;

            if (incoming.Role != ChatRole.User) return false;

            var incomingText = incoming.Text.Trim();

            // Either the CLI never noticed the path (the row comes back
            // exactly as typed) or it did and swapped it for a real picture
            // plus its own placeholder, which ChatTranscript has already
            // stripped down to the caption alone. Both are "this is the
            // message that was just sent" — see the two fields' own comment.
            if (!string.Equals(incomingText, _pendingRaw, StringComparison.Ordinal)
                && !string.Equals(incomingText, _pendingCaption, StringComparison.Ordinal))
            {
                return false;
            }

            // Keep the transcript's timestamp: it is when the session actually
            // received it, which for a message queued behind a long turn is
            // minutes after it was typed. The settled turn's own ImageBytes,
            // if any, is left alone rather than replaced from incoming — it
            // was already read straight from the file that was pasted, and
            // is the same picture either way this matched.
            var settled = _pending;
            _pending = null;

            settled.Text = incoming.Text;
            TurnUpdated?.Invoke(settled);
            return true;
        }

        public void Cancel()
        {
            // Escape is what interrupts a run in the TUI, and this is the one
            // place the panel can offer it. Gated with everything else that
            // types: stopping someone's work is not something a viewer should be
            // able to do.
            if (!_format.ReplyEnabled()) return;

            _ = TerminalFocuser.SendPaneKey(_status, "Escape");
        }

        // --- permission prompts ---

        private bool _waiting;

        public ChatPrompt? Prompt { get; private set; }

        private bool _refreshing;

        private async Task RefreshPromptAsync()
        {
            _refreshing = true;

            try
            {
                var screen = await TerminalFocuser.CapturePane(_status);

                // Still waiting? The capture runs a process and the answer may
                // have been given in the terminal while it did.
                if (!_waiting) return;

                // A prompt with no options is the honest outcome when the dialog
                // could not be read: something is waiting, we cannot say what,
                // and the panel offers the terminal instead of a guess.
                var parsed = screen is null ? null : ChatTranscript.ParseDialog(screen);
                SetPrompt(parsed ?? new ChatPrompt("Waiting for input", Array.Empty<ChatPromptOption>()));
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void SetPrompt(ChatPrompt? prompt)
        {
            Prompt = prompt;
            Dispatcher.UIThread.Post(() => PromptChanged?.Invoke());
        }

        public async Task AnswerAsync(ChatPromptOption option)
        {
            if (!_format.ReplyEnabled())
            {
                Note("Replying is off, so this can only be answered in the terminal.");
                return;
            }

            // Cleared first. The hook will report the session generating again
            // within a moment, but the buttons should stop being clickable the
            // instant one is clicked rather than staying live for a second
            // answer to the dialog that is already gone.
            SetPrompt(null);

            if (await TerminalFocuser.SendPaneKey(_status, option.Key)) return;

            Note("Couldn't answer that in the terminal.");

            // Put it back. Clearing optimistically is right when the keystroke
            // lands, and leaves the panel silent about a session that is still
            // stopped when it doesn't.
            if (_waiting) await RefreshPromptAsync();
        }

        public void AnswerElsewhere() =>
            TerminalFocuser.Focus(_status, null, SessionId);

        // --- plumbing ---

        private void Note(string text) => Add(new ChatTurn
        {
            Role = ChatRole.System,
            IsComplete = true,
            Text = text
        });

        private void Add(ChatTurn turn)
        {
            if (Reconcile(turn)) return;

            _history.Add(turn);
            Trim();
            TurnAdded?.Invoke(turn);
        }

        private void Trim()
        {
            if (_history.Count > KeepTurns) _history.RemoveRange(0, _history.Count - KeepTurns);
        }

        private void SetState(RemoteChatState state)
        {
            if (State == state) return;

            State = state;
            StateChanged?.Invoke(state);
        }

    }
}
