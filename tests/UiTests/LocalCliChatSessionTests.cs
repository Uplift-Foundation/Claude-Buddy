using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// A local CLI session as the chat panel talks to it: a transcript file on disk,
// tailed.
//
// In UiTests because the loads finish on the Avalonia dispatcher — the read
// happens on a worker and the result is posted back — so a test has to pump the
// loop to see it. That is the only reason this is not a unit test; nothing here
// starts a CLI, and the sending half (which goes through tmux) is not touched.
//
// The thing worth understanding before reading any of it, quoting the source:
// there is only one conversation and this is not a copy of it. The file *is* the
// conversation, so what these tests assert is that the panel reads the same thing
// the terminal is writing — including the parts of that file it must not show.
[Collection("Settings")]
public class LocalCliChatSessionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-localcli-" + Guid.NewGuid().ToString("N"));

    public LocalCliChatSessionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // Real Claude Code row shapes, trimmed of the fields none of this reads.
    private static string User(string uuid, string text) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-08-24T12:00:00Z\","
        + "\"message\":{\"role\":\"user\",\"content\":" + Json(text) + "}}";

    private static string Assistant(string uuid, string text) =>
        "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-08-24T12:00:01Z\","
        + "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":"
        + Json(text) + "}]}}";

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private string Transcript(params string[] rows)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, string.Join('\n', rows) + "\n");
        return path;
    }

    private static LocalCliChatSession Session(string transcriptPath, string state = "idle") =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcriptPath,
            State = state,
            Title = "",
        });

    // The read runs on a worker and posts its result back, so the loop has to be
    // pumped until it lands. Bounded rather than a bare spin: a test that hangs
    // tells you far less than one that fails.
    private static void PumpUntil(Func<bool> done, string what)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), $"timed out waiting for {what}");
    }

    private static LocalCliChatSession Started(string transcriptPath, string state = "idle")
    {
        var session = Session(transcriptPath, state);
        session.Start();
        PumpUntil(() => session.History.Count > 0 || session.State == RemoteChatState.Connected,
            "the initial load");
        return session;
    }

    // --- Start ---

    [AvaloniaFact]
    public void ATranscriptIsReadIntoHistory()
    {
        var session = Started(Transcript(
            User("u1", "fix the arrangement test"),
            Assistant("a1", "Fixed the nested-team case.")));

        Assert.Equal(2, session.History.Count);
        Assert.Equal(ChatRole.User, session.History[0].Role);
        Assert.Equal("fix the arrangement test", session.History[0].Text);
        Assert.Equal(ChatRole.Assistant, session.History[1].Role);
    }

    [AvaloniaFact]
    public void LoadingAnnouncesThatTheWholeTranscriptChanged()
    {
        var session = Session(Transcript(User("u1", "hello")));
        var replaced = 0;
        session.HistoryReplaced += () => replaced++;

        session.Start();
        PumpUntil(() => replaced > 0, "HistoryReplaced");

        Assert.Equal(1, replaced);
    }

    [AvaloniaFact]
    public void ASessionWithATranscriptReportsItselfConnected()
    {
        var session = Started(Transcript(User("u1", "hello")));

        Assert.Equal(RemoteChatState.Connected, session.State);
    }

    // The transcript path is the one field the hook can record later than the
    // rest, so a session whose first status file predates its first message has
    // none — and Start has to be a no-op rather than an error.
    [AvaloniaFact]
    public void ASessionWithNoTranscriptStaysQuiet()
    {
        var session = Session("");

        session.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(session.History);
        Assert.False(session.HasMore);
        Assert.Equal(RemoteChatState.Connecting, session.State);
    }

    [AvaloniaFact]
    public void APathThatDoesNotExistIsAlsoQuiet()
    {
        var session = Session(Path.Combine(_root, "never-written.jsonl"));

        session.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(session.History);
    }

    // Idempotent, because it is called from both construction-time binding and
    // every status update. A second Start must not re-read the file and double
    // the history.
    [AvaloniaFact]
    public void StartingTwiceReadsOnce()
    {
        var path = Transcript(User("u1", "one"), Assistant("a1", "two"));
        var session = Started(path);

        session.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, session.History.Count);
    }

    // Rows the panel must not show. These are in every real transcript and each
    // one has its own reason — a tool result is not conversation, a sidechain is
    // a subagent's own transcript, a system reminder is not something anybody
    // said. The parsing is tested exhaustively elsewhere; what this asserts is
    // that the session hands the file to that parser rather than showing raw
    // rows.
    [AvaloniaFact]
    public void RowsThatAreNotConversationDoNotAppear()
    {
        var session = Started(Transcript(
            User("u1", "fix it"),
            "{\"type\":\"user\",\"uuid\":\"m1\",\"isMeta\":true,\"timestamp\":\"2026-08-24T12:00:00Z\","
                + "\"message\":{\"role\":\"user\",\"content\":\"hook output\"}}",
            "{\"type\":\"user\",\"uuid\":\"s1\",\"isSidechain\":true,\"timestamp\":\"2026-08-24T12:00:00Z\","
                + "\"message\":{\"role\":\"user\",\"content\":\"subagent progress\"}}",
            Assistant("a1", "done")));

        Assert.Equal(2, session.History.Count);
        Assert.DoesNotContain(session.History, t => t.Text.Contains("hook output"));
        Assert.DoesNotContain(session.History, t => t.Text.Contains("subagent progress"));
    }

    // A row repeated in the file — which happens, because the tail window can be
    // re-read — is one turn. The uuid is what makes that possible.
    [AvaloniaFact]
    public void ARepeatedRowBecomesOneTurn()
    {
        var row = User("u1", "said once");
        var session = Started(Transcript(row, row, row));

        Assert.Single(session.History);
    }

    // --- paging backwards ---

    [AvaloniaFact]
    public void AShortTranscriptHasNothingOlderToLoad()
    {
        var session = Started(Transcript(User("u1", "hello")));

        Assert.False(session.HasMore);
    }

    [AvaloniaFact]
    public async Task AskingForOlderTurnsWhenThereAreNoneAnswersFalse()
    {
        var session = Started(Transcript(User("u1", "hello")));

        Assert.False(await session.LoadOlderAsync(CancellationToken.None));
    }

    // A transcript longer than the opening window opens on its tail, and paging
    // back reaches the rest. The rows are padded so the file genuinely exceeds
    // the 512KB the session opens with, because the whole point of the window is
    // that a real transcript is far larger than what it shows.
    [AvaloniaFact]
    public async Task ALongTranscriptOpensOnItsTailAndPagesBack()
    {
        var padding = new string('x', 4000);
        var rows = Enumerable.Range(0, 200)
            .Select(i => User("u" + i, $"message {i} {padding}"))
            .ToArray();

        var session = Started(Transcript(rows));

        var opened = session.History.Count;
        Assert.True(opened > 0, "the tail should hold something");
        Assert.True(opened < rows.Length, $"the whole file should not fit: {opened} of {rows.Length}");
        Assert.True(session.HasMore, "there should be more to page back to");

        var prepended = 0;
        session.HistoryPrepended += n => prepended = n;

        Assert.True(await session.LoadOlderAsync(CancellationToken.None));

        Assert.True(prepended > 0, "paging back should report how many arrived");
        Assert.True(session.History.Count > opened, "paging back should add turns");

        // ...and the oldest turn on screen is older than it was, which is the
        // property a user actually notices.
        Assert.StartsWith("message ", session.History[0].Text);
    }

    // Paging back repeatedly reaches the beginning and then stops claiming there
    // is more — otherwise the panel offers a "load older" that never ends.
    [AvaloniaFact]
    public async Task PagingBackEventuallyReachesTheBeginning()
    {
        var padding = new string('x', 4000);
        var rows = Enumerable.Range(0, 200)
            .Select(i => User("u" + i, $"message {i} {padding}"))
            .ToArray();

        var session = Started(Transcript(rows));

        for (var i = 0; i < 40 && session.HasMore; i++)
        {
            await session.LoadOlderAsync(CancellationToken.None);
        }

        Assert.False(session.HasMore);
        Assert.Contains(session.History, t => t.Text.StartsWith("message 0 ", StringComparison.Ordinal));
    }

    // --- status updates ---

    // The title can improve after the panel opened: Claude Code writes an
    // ai-title for a conversation that did not have one yet, and a panel opened
    // before that would otherwise keep the folder name in its header for as long
    // as the app runs.
    [AvaloniaFact]
    public void ALaterTitleImprovesTheDisplayName()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = path,
            Title = "Fixing the arrangement",
        });

        Assert.Equal("Fixing the arrangement", session.DisplayName);
    }

    // ...and a status that has lost its title does not blank the one already
    // shown. An empty title is "not known yet", not "called nothing".
    [AvaloniaFact]
    public void AStatusWithNoTitleDoesNotBlankTheOneAlreadyKnown()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, Title = "Named",
        });
        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, Title = "",
        });

        Assert.Equal("Named", session.DisplayName);
    }

    // A transcript path arriving late is the case Start is idempotent for: the
    // session was bound before the hook had written one, and the next status
    // update is what gets it reading.
    [AvaloniaFact]
    public void ATranscriptPathArrivingLateStartsTheSession()
    {
        var session = Session("");
        session.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(session.History);

        var path = Transcript(User("u1", "arrived late"));
        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path,
        });

        PumpUntil(() => session.History.Count > 0, "the late load");

        Assert.Single(session.History);
    }

    // --- the composer ---

    // "No pane to type into" wins over the reply setting, and the precedence is
    // the interesting half: a session with nowhere to send has to say *that*
    // rather than "Replying is off", because the two have different answers —
    // one is a setting the user can change and the other is not.
    //
    // The reply-setting branch below it is deliberately not asserted here. It is
    // only reachable once the session can send quietly, which needs a real tmux
    // binary on the machine — so a test of it would pass on a developer's Mac and
    // do nothing on a runner without tmux, which is the same test passing for two
    // different reasons. The setting's own behaviour is covered where it is
    // decided, in the settings suite.
    [AvaloniaFact]
    public void ASessionWithNoPaneSaysSoRatherThanBlamingTheSetting()
    {
        var session = Session(Transcript(User("u1", "hello")));

        ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;
        Assert.Equal("No pane to type into", session.ComposerHint);

        ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;
        Assert.Equal("No pane to type into", session.ComposerHint);
    }

    // Scanned once per session rather than per keystroke, since the commands a
    // running CLI understands do not change while it runs — so asking twice must
    // give the same list rather than re-reading the disk.
    [AvaloniaFact]
    public void SlashCommandsAreScannedOnce()
    {
        var session = Session(Transcript(User("u1", "hello")));

        var first = session.SlashCommands;

        Assert.Same(first, session.SlashCommands);
    }

    [AvaloniaFact]
    public void DisposingASessionThatNeverStartedIsHarmless()
    {
        var session = Session("");

        session.Dispose();

        Assert.Empty(session.History);
    }

    [AvaloniaFact]
    public void DisposingAStartedSessionStopsItCleanly()
    {
        var session = Started(Transcript(User("u1", "hello")));

        session.Dispose();
        Dispatcher.UIThread.RunJobs();

        // The history it already read stays readable; disposing stops the
        // watcher, it does not empty the panel.
        Assert.Single(session.History);
    }

    // --- the permission prompt ---
    //
    // A prompt is the panel offering buttons that send keystrokes into a live
    // session, so a stale one is not a cosmetic problem: it is a button that
    // presses something for a dialog that has already been answered. Finding a
    // prompt means capturing a tmux pane and is excluded; the transitions around
    // one are decided here and are not.

    // Leaving "waiting" clears the prompt. Without this the buttons stay on
    // screen after the dialog is gone.
    [AvaloniaFact]
    public void LeavingTheWaitingStateClearsThePrompt()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        session.SetPrompt(new ChatPrompt("Do you want to proceed?", new[]
        {
            new ChatPromptOption("1", "Yes"),
            new ChatPromptOption("2", "No"),
        }));
        Assert.NotNull(session.Prompt);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle",
        });

        Assert.Null(session.Prompt);
    }

    // ...and says so, because the panel has to take the buttons down rather than
    // wait for something else to happen.
    [AvaloniaFact]
    public void ClearingThePromptIsAnnounced()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        var changes = 0;
        session.PromptChanged += () => changes++;

        session.SetPrompt(new ChatPrompt("Proceed?", Array.Empty<ChatPromptOption>()));
        Dispatcher.UIThread.RunJobs();
        var afterSetting = changes;

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle",
        });
        Dispatcher.UIThread.RunJobs();

        Assert.True(changes > afterSetting, "clearing a prompt has to be announced");
    }

    // A status update that was not waiting and still is not raises nothing. The
    // scan runs a couple of times a second, so an update per tick would be an
    // event per tick for a panel with nothing to change.
    [AvaloniaFact]
    public void StayingNotWaitingRaisesNothing()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        Dispatcher.UIThread.RunJobs();
        var changes = 0;
        session.PromptChanged += () => changes++;

        for (var i = 0; i < 3; i++)
        {
            session.UpdateStatus(new SessionStatus
            {
                Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle",
            });
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, changes);
    }

    // A prompt already on screen survives another "waiting" update. Claude Code
    // commonly asks two or three permissions in a row and the state never leaves
    // "waiting" between them, so an update that cleared or re-read on every tick
    // would flicker the buttons under the pointer.
    [AvaloniaFact]
    public void APromptSurvivesAnotherWaitingUpdate()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        var prompt = new ChatPrompt("Do you want to proceed?", new[]
        {
            new ChatPromptOption("1", "Yes"),
        });
        session.SetPrompt(prompt);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "waiting",
        });

        Assert.Same(prompt, session.Prompt);
    }

    // Answering is refused when replying is off, and refused *out loud* — the
    // panel says so in the transcript rather than a button doing nothing. A send
    // that silently fails is the worst outcome a chat window can produce.
    [AvaloniaFact]
    public async Task AnsweringWithReplyingOffSaysSoInsteadOfDoingNothing()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;

        var before = session.History.Count;
        await session.AnswerAsync(new ChatPromptOption("1", "Yes"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(session.History.Count > before, "a refusal has to be visible");
        Assert.Contains(
            session.History,
            turn => turn.Text.Contains("Replying is off", StringComparison.OrdinalIgnoreCase));
    }
}
