using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// The speak button's three branches (cancel an ongoing read, read a gateway
// agent's transcript over the wire, read a local session's own transcript
// file) and the transcript lookup that backs the local one.
//
// OnSpeakClicked, SpeakRemoteAsync and FindSpeakableText are internal for the
// same reason the rest of this file's siblings are: EnsureFlyoutShown wires
// them to the flyout's own speak button, which this suite does not click
// (OrbFlyoutTests.SpeakButton reaching TextToSpeech is one thing; reaching it
// through a *lambda registered on this orb* is the thing worth pinning here).
//
// TextToSpeech.Speak and TextToSpeech.Enter's callers below never actually
// reach the OS speech engine: Speak itself is [ExcludeFromCodeCoverage]
// (spawns a real process), and every case here either returns before calling
// it, or reaches it only through SpeakRemoteAsync's Dispatcher.UIThread.Post
// (never pumped afterwards here, so the posted call sits queued and
// unexecuted for the rest of the test, the same way an unfired
// DispatcherTimer would).
//
// OnSpeakClicked's *local* branch has no such boundary — it calls
// TextToSpeech.Speak(text, ...) straight from the UI thread with nothing to
// intercept it, unlike the gateway branch's dispatcher hop. An earlier
// version of this file called OnSpeakClicked() end-to-end for a local
// session with a real transcript to test, and it genuinely started
// /usr/bin/say on the machine running the suite. Every local-branch test
// below exercises FindSpeakableText directly instead, and OnSpeakClicked
// itself is only ever driven with a status that FindSpeakableText answers
// null for, keeping the local Speak() call itself out of reach — see the
// per-test comments below for exactly where.
[Collection("Settings")]
public class OrbWindowSpeakTests
{
    private static string WriteTranscript(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "cb-uitests-orbspeak-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    // --- cancelling an ongoing read -----------------------------------------

    // TextToSpeech.Enter is internal (InternalsVisibleTo reaches it), which is
    // what lets this drive the "already speaking" branch without ever calling
    // the excluded Speak() that would normally get it there.
    [AvaloniaFact]
    public void ClickingWhileSpeakingCancelsRatherThanStartingASecondRead()
    {
        TextToSpeech.Enter(TextToSpeech.SpeakState.Speaking);
        try
        {
            var orb = new OrbWindow(Guid.NewGuid().ToString());

            orb.OnSpeakClicked();

            // Cancel() finds no real process (nothing ever called the
            // excluded Speak()), so it just returns the state to Idle.
            Assert.Equal(TextToSpeech.SpeakState.Idle, TextToSpeech.State);
        }
        finally
        {
            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
        }
    }

    [AvaloniaFact]
    public void ClickingWhilePreparingAlsoCancelsRatherThanStarting()
    {
        TextToSpeech.Enter(TextToSpeech.SpeakState.Preparing);
        try
        {
            var orb = new OrbWindow(Guid.NewGuid().ToString());

            orb.OnSpeakClicked();

            Assert.Equal(TextToSpeech.SpeakState.Idle, TextToSpeech.State);
        }
        finally
        {
            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
        }
    }

    // --- the gateway branch --------------------------------------------------

    [AvaloniaFact]
    public void ClickingAGatewayOrbFiresTheRemoteSpeakPathWithoutThrowing()
    {
        var wasEnabled = ClaudeBuddySettings.OpenClawEnabled;
        try
        {
            // Disabled explicitly rather than assumed: this means
            // LastAssistantTextAsync's callee, ChatFor, returns null
            // immediately and SpeakRemoteAsync's fire-and-forget task
            // completes without ever posting to the dispatcher — the
            // "nothing to say" half of that method.
            ClaudeBuddySettings.OpenClawEnabled = false;

            var orb = new OrbWindow(Guid.NewGuid().ToString());
            orb.UpdateFrom(new SessionStatus { Source = SessionSource.OpenClaw, State = "idle", Title = "Zara" });

            orb.OnSpeakClicked();
        }
        finally
        {
            ClaudeBuddySettings.OpenClawEnabled = wasEnabled;
        }
    }

    // SpeakRemoteAsync driven directly and awaited, covering the rest of it:
    // a real (in-memory) history entry found synchronously, so
    // LastAssistantTextAsync returns without its own 20x100ms poll loop, and
    // the Dispatcher.UIThread.Post(...) line actually runs — scheduling, not
    // executing, the excluded Speak() call.
    [AvaloniaFact]
    public async System.Threading.Tasks.Task SpeakRemoteAsyncFindsARealHistoryEntryAndSchedulesTheRead()
    {
        var wasEnabled = ClaudeBuddySettings.OpenClawEnabled;
        var agent = "zara" + Guid.NewGuid().ToString("N")[..8];
        var sessionId = $"openclaw:agent:{agent}:discord:channel:1";
        try
        {
            ClaudeBuddySettings.OpenClawEnabled = true;

            var chat = (OpenClawChatSession)OpenClawSessions.ChatFor(sessionId, "Zara")!;
            chat.SetHistory(new[]
            {
                (ChatRole.Assistant, "hello from the agent", (string?)null, "",
                 DateTimeOffset.UtcNow, (string?)null, (string?)null),
            });

            var orb = new OrbWindow(sessionId);
            orb.UpdateFrom(new SessionStatus { Source = SessionSource.OpenClaw, State = "idle", Title = "Zara" });

            await orb.SpeakRemoteAsync();
        }
        finally
        {
            ClaudeBuddySettings.OpenClawEnabled = wasEnabled;
        }
    }

    // --- the local branch, via FindSpeakableText ----------------------------

    // A gateway session never reaches FindSpeakableText's transcript lookup at
    // all (its own !IsLocalCli guard), so this is the one place that fires
    // from a Claude Code status instead — safe because it never asks
    // TerminalFocuser or the pointer pipeline for anything, only
    // TranscriptReader against a file this test wrote itself.
    [AvaloniaFact]
    public void ASessionWithNoTranscriptAnywhereSpeaksNothing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "idle",
            Title = "claude-buddy",
            Cwd = "",
            TranscriptPath = "",
        });

        orb.OnSpeakClicked();
    }

    [AvaloniaFact]
    public void ALocalSessionWithATranscriptPathSpeaksItsLastAssistantTurn()
    {
        const string AssistantSaid =
            """{"type":"assistant","uuid":"a1","timestamp":"2026-08-16T10:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the nested-team case."}]}}""";
        var path = WriteTranscript(AssistantSaid + "\n");

        try
        {
            var orb = new OrbWindow(Guid.NewGuid().ToString());
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.ClaudeCode,
                State = "idle",
                Title = "claude-buddy",
                Cwd = "/tmp/does-not-matter",
                TranscriptPath = path,
            });

            // Deliberately not calling OnSpeakClicked() here. Its local
            // branch calls TextToSpeech.Speak(text, ...) directly rather than
            // via Dispatcher.UIThread.Post the way the gateway branch's
            // SpeakRemoteAsync does — there is no async boundary between
            // "found some text" and "spawn /usr/bin/say (or the Windows
            // equivalent) with it" for a local session. An earlier version
            // of this test called it here, and it genuinely started the
            // real system speech engine on the machine running the suite
            // (confirmed the hard way: this test alone added ~650ms and
            // spoke the fixture's own sentence out loud on macOS). FindSpeakableText
            // is exercised directly instead, which is the whole of what
            // OnSpeakClicked's local branch decides before handing off to
            // the (already excluded, unavoidably OS-touching) Speak() call.
            Assert.Equal(
                "Fixed the nested-team case.",
                orb.FindSpeakableText());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A Codex session reads through LatestCodexAgentText instead, which has no
    // cwd fallback (see FindSpeakableText's own comment on why sharing the
    // Claude Code path would be wrong).
    [AvaloniaFact]
    public void ACodexSessionReadsThroughItsOwnRolloutParser()
    {
        const string CxAgentFinal =
            """{"timestamp":"2026-08-19T17:03:05.443Z","ordinal":310,"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"msg_000c73","content":[{"type":"Text","text":"Converted the workspace from Claude to Codex."}],"phase":"final_answer"}}}""";
        var path = WriteTranscript(CxAgentFinal + "\n");

        try
        {
            var orb = new OrbWindow(Guid.NewGuid().ToString());
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.Codex,
                Cli = "codex",
                State = "idle",
                Title = "codex-session",
                Cwd = "/tmp/does-not-matter",
                TranscriptPath = path,
            });

            Assert.Equal(
                "Converted the workspace from Claude to Codex.",
                orb.FindSpeakableText());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // No explicit transcript path, but a cwd that cannot possibly match
    // anything under the real machine's actual ~/.claude/projects (the guid
    // makes that a practical certainty) — the honest way to exercise the
    // "own transcript missing, fall back to a cwd search" branch without
    // pointing TranscriptReader.LatestTranscriptForCwd at a fake home
    // directory, which OrbWindow's own call site has no seam for (it calls
    // the single-argument overload). LatestTranscriptForCwd is read-only and
    // side-effect-free, so scanning the real directory tree and finding
    // nothing is a safe, deterministic result rather than a risky one.
    [AvaloniaFact]
    public void ASessionWithNoTranscriptFallsBackToACwdSearchAndFindsNothing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "idle",
            Title = "claude-buddy",
            Cwd = "/tmp/cb-uitests-nonexistent-cwd-" + Guid.NewGuid(),
            TranscriptPath = "",
        });

        Assert.Null(orb.FindSpeakableText());
    }
}
