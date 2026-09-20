using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The speak scope setting reaching the panel that acts on it, and the settings
// row that offers it.
//
// The real utterance is out of reach here on purpose, and that is the whole
// shape of this file. OrbWindowSpeakTests' header sets out why at length:
// TextToSpeech.Speak starts a real speech engine, and an earlier version of
// that suite genuinely started /usr/bin/say on the machine running it. So every
// case below stops at the last observable point before the utterance — the
// state the button is showing, the text the summariser was handed, or the
// cancellation check — and the decision about *what* gets spoken is asserted in
// tests/UnitTests, where it is pure.
//
// [Collection("Settings")] because these set SpeakScope, which is process-wide,
// and TextToSpeech's state is too.
[Collection("Settings")]
public class SpeakScopeUiTests : IDisposable
{
    private readonly List<string> _toClean = new();

    // The id of the most recently built fake session, so a case can ask the
    // shared speak path about the same session the panel is bound to.
    private string _lastFakeId = "";
    private readonly SpeakScope _scopeWas = ClaudeBuddySettings.SpeakScope;

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
        ClaudeBuddySettings.SpeakScope = _scopeWas;
        SpeechSummary.SummarizerForTests = null;
        SpeechRequest.UtteranceForTests = null;
        SpeechRequest.VoiceOptionsForTests = null;
        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
    }

    private FakeChatSession NewFake(IEnumerable<ChatTurn> history)
    {
        var id = "speakscope-" + Guid.NewGuid();
        _toClean.Add(id);
        _lastFakeId = id;
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Fake Session" };
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    // Long enough that summary mode will not short-circuit it. Built from the
    // threshold rather than a literal so it cannot drift away from the rule.
    private static string LongReply() =>
        string.Join(" ", Enumerable.Repeat("the assistant said a great deal", 60))
            .PadRight(SpeechPlan.ShortEnoughChars + 1, '.');

    private ChatPanel OpenWith(string replyText)
    {
        var fake = NewFake(new[] { new ChatTurn { Role = ChatRole.Assistant, Text = replyText } });
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();
        return ChatPanelTestAccess.Instance!;
    }

    // --- the mode reaching the speak path ---

    // Summary mode on a long reply asks the summariser, and asks it about the
    // reply. This is the assertion that the setting is actually wired to the
    // button rather than merely stored.
    [AvaloniaFact]
    public async Task SummaryModeSendsTheReplyToTheSummariser()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;
        var reply = LongReply();

        string? seen = null;
        SpeechSummary.SummarizerForTests = text =>
        {
            seen = text;

            // Returning to Idle here is the user having pressed the button
            // again while the summariser ran, which is exactly the branch that
            // stops this test reaching the real utterance. Asserted in its own
            // case below; here it is what keeps the machine quiet.
            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
            return Task.FromResult<string?>("a summary");
        };

        var panel = OpenWith(reply);
        await SpeechRequest.SpeakSummaryAsync(reply, _lastFakeId);

        Assert.Equal(reply, seen);
    }

    // The hourglass goes up before the round trip, not after. The wait was
    // measured in seconds, and a speaker that simply goes quiet for that long
    // with no indication is the failure this ticket's refinement predicted.
    [AvaloniaFact]
    public async Task TheButtonShowsPreparingWhileTheSummaryIsBeingWritten()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;
        var reply = LongReply();

        TextToSpeech.SpeakState duringRoundTrip = TextToSpeech.SpeakState.Idle;
        SpeechSummary.SummarizerForTests = _ =>
        {
            duringRoundTrip = TextToSpeech.State;
            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
            return Task.FromResult<string?>("a summary");
        };

        var panel = OpenWith(reply);
        await SpeechRequest.SpeakSummaryAsync(reply, _lastFakeId);

        Assert.Equal(TextToSpeech.SpeakState.Preparing, duringRoundTrip);
    }

    // Pressing the button again during the wait means the summary is never
    // spoken when it arrives. Several seconds is long enough that this is an
    // ordinary thing to do, not a race.
    [AvaloniaFact]
    public async Task CancellingDuringTheWaitStopsTheSummaryBeingSpoken()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;
        var reply = LongReply();

        SpeechSummary.SummarizerForTests = _ =>
        {
            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
            return Task.FromResult<string?>("a summary");
        };

        var panel = OpenWith(reply);
        await SpeechRequest.SpeakSummaryAsync(reply, _lastFakeId);

        // Still Idle: had it gone on to speak, the state would have been set by
        // the utterance instead.
        Assert.Equal(TextToSpeech.SpeakState.Idle, TextToSpeech.State);
    }

    // --- no regression for the default ---

    // Full mode never consults the summariser, whatever the reply's length. The
    // branch every existing user takes, and the one that must not acquire a
    // several-second wait it never had.
    [AvaloniaFact]
    public void FullModeNeverAsksTheSummariser()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Full;

        var asked = false;
        SpeechSummary.SummarizerForTests = _ =>
        {
            asked = true;
            return Task.FromResult<string?>("a summary");
        };

        // Already speaking, so the click cancels rather than starting an
        // utterance — the same safe branch ChatPanelInteractionTests uses to
        // reach this button without making a noise.
        var panel = OpenWith(LongReply());
        TextToSpeech.Enter(TextToSpeech.SpeakState.Speaking);
        panel.SpeakButton.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
            panel.SpeakButton, new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, true),
            panel, default, 0, new Avalonia.Input.PointerPointProperties(
                Avalonia.Input.RawInputModifiers.LeftMouseButton,
                Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
            Avalonia.Input.KeyModifiers.None));
        Flush();

        Assert.False(asked, "full mode must not pay for a summary it does not want");
    }

    // --- both buttons, end to end ---------------------------------------------

    // Everything below presses a button a user presses and asserts what would
    // have been spoken, which is the thing nothing did before.
    //
    // CB-165 shipped with each button implementing half the feature, and both
    // halves were covered: the panel's scope handling had cases, the orb's
    // voice resolution had cases, and the two gaps were each in the half the
    // other suite was not looking at. Every case here goes in at the click and
    // comes out at SpeechRequest.UtteranceForTests — the seam that stands in
    // for the one excluded line — so a path that decides wrongly anywhere in
    // between fails here rather than being spoken at a user.
    //
    // Nothing reaches a real speech engine: with that seam set, Say() is never
    // called at all.

    private static readonly TextToSpeech.VoiceOption Sky =
        new(TextToSpeech.SpeakEngine.Neural, "af_sky", "af_sky (Kokoro)");

    private sealed record Utterance(string Text, TextToSpeech.VoiceOption? Voice, double? Rate);

    private Utterance? _spoken;

    // Catches what would have been said, and puts the state machine back where
    // a real utterance would have left it once it finished.
    private void CaptureUtterances()
    {
        _spoken = null;
        SpeechRequest.VoiceOptionsForTests = new[] { Sky };
        SpeechRequest.UtteranceForTests = (text, voice, rate) =>
        {
            _spoken = new Utterance(text, voice, rate);
            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
        };
    }

    private static string GivePersona(string sessionId, string voice, double rate)
    {
        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>
        {
            [sessionId] = new(
                Name: "Jennifer", Voice: voice, Rate: rate,
                AvatarSource: null, AvatarPath: null, Files: Array.Empty<string>()),
        });
        return sessionId;
    }

    // A local session with a transcript, which is what the orb's speak button
    // reads. Written to a temp file and cleaned up by the caller.
    private OrbWindow OrbReading(string reply, out string transcriptPath)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant",
            uuid = "a1",
            timestamp = "2026-09-20T10:00:09Z",
            message = new { role = "assistant", content = new[] { new { type = "text", text = reply } } },
        });

        transcriptPath = Path.Combine(
            Path.GetTempPath(), "cb-speakscope-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcriptPath, line + "\n");

        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "idle",
            Title = "claude-buddy",
            Cwd = "/tmp/does-not-matter",
            TranscriptPath = transcriptPath,
        });

        return orb;
    }

    // The orb button in summary mode. This is the half of CB-165 the user
    // described as "does not summarize like it should": the orb used to speak
    // the transcript verbatim, having never asked SpeechPlan or the setting
    // anything at all.
    [AvaloniaFact]
    public async Task TheOrbsSpeakPathConsultsSpeakScope()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;
        CaptureUtterances();

        string? asked = null;
        SpeechSummary.SummarizerForTests = text =>
        {
            asked = text;
            return Task.FromResult<string?>("It fixed the nested-team case.");
        };

        var orb = OrbReading(LongReply(), out var path);
        try
        {
            // What the orb found, rather than the fixture, because
            // TranscriptReader caps the text it hands back — an assertion
            // against `reply` would be measuring that cap and not this path.
            var found = orb.FindSpeakableText();

            orb.OnSpeakClicked();
            await PumpAsync();

            Assert.Equal(found, asked);
            Assert.Equal("It fixed the nested-team case.", _spoken?.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The other half: the panel used to resolve no voice at all for a local
    // session — every ordinary Claude Code session — so a CLAUDE.md naming one
    // was ignored and the global setting read the reply instead.
    [AvaloniaFact]
    public void ThePanelsSpeakPathResolvesALocalPersonaVoice()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Full;
        CaptureUtterances();

        var panel = OpenWith("Short enough to speak whole.");
        GivePersona(_lastFakeId, "af_sky", 1.2);

        panel.SpeakLatest();
        Flush();

        Assert.Equal("Short enough to speak whole.", _spoken?.Text);
        Assert.Equal(Sky, _spoken?.Voice);
        Assert.Equal(1.2, _spoken?.Rate);
    }

    // The orb's voice was already right, and stays right now that it shares the
    // panel's path. Asserted rather than assumed: this is the half that was
    // working, and a unification that fixed one end by breaking the other would
    // be the same bug with the halves swapped.
    [AvaloniaFact]
    public void TheOrbsSpeakPathStillResolvesALocalPersonaVoice()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Full;
        CaptureUtterances();

        var orb = OrbReading("Fixed the nested-team case.", out var path);
        try
        {
            GivePersona(orb.SessionId, "af_sky", 0.8);

            orb.OnSpeakClicked();
            Flush();

            Assert.Equal("Fixed the nested-team case.", _spoken?.Text);
            Assert.Equal(Sky, _spoken?.Voice);
            Assert.Equal(0.8, _spoken?.Rate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The hourglass, from the orb. The panel's equivalent is asserted above;
    // this is the same assertion from the other button, and it is what the user
    // meant by "both speakers need to look the same" — OrbFlyout.SetSpeakState
    // has drawn an amber hourglass for Preparing since the state existed, and
    // the orb simply never entered it.
    [AvaloniaFact]
    public async Task TheOrbShowsPreparingWhileTheSummaryIsBeingWritten()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;
        CaptureUtterances();

        var duringRoundTrip = TextToSpeech.SpeakState.Idle;
        SpeechSummary.SummarizerForTests = _ =>
        {
            duringRoundTrip = TextToSpeech.State;
            return Task.FromResult<string?>("a summary");
        };

        var orb = OrbReading(LongReply(), out var path);
        try
        {
            orb.OnSpeakClicked();
            await PumpAsync();

            Assert.Equal(TextToSpeech.SpeakState.Preparing, duringRoundTrip);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Pressing the orb's button again during the wait stops it, exactly as the
    // panel's does. Both are the same line now, and this is the case that says
    // so from the end a user touches.
    [AvaloniaFact]
    public async Task CancellingDuringTheWaitStopsTheOrbSpeakingToo()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;
        CaptureUtterances();

        SpeechSummary.SummarizerForTests = _ =>
        {
            // The user pressed it again while the summariser ran.
            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
            return Task.FromResult<string?>("a summary");
        };

        var orb = OrbReading(LongReply(), out var path);
        try
        {
            orb.OnSpeakClicked();
            await PumpAsync();

            Assert.Null(_spoken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A summary that could not be produced says so. Deliberately not the full
    // text: somebody who chose this mode chose it to avoid a long reading, and
    // handing them one because the summariser failed is the opposite of what
    // they asked for. Asserted from both buttons, because that guarantee was
    // previously only reachable through one of them.
    [AvaloniaFact]
    public async Task AFailedSummarySaysSoFromEitherButton()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;
        SpeechSummary.SummarizerForTests = _ => throw new InvalidOperationException("no binary");

        var reply = LongReply();

        CaptureUtterances();
        var orb = OrbReading(reply, out var path);
        try
        {
            orb.OnSpeakClicked();
            await PumpAsync();
            Assert.Equal(SpeechSummary.Unavailable, _spoken?.Text);
        }
        finally
        {
            File.Delete(path);
        }

        CaptureUtterances();
        var panel = OpenWith(reply);
        panel.SpeakLatest();
        await PumpAsync();

        Assert.Equal(SpeechSummary.Unavailable, _spoken?.Text);
    }

    // A panel that has not bound a session yet still has a speak button on it,
    // and pressing it must do nothing rather than throw. Both null arms of the
    // call meet here — no assistant turn to read and no session to resolve a
    // voice for — which is the state every panel is in between construction
    // and its first Bind.
    [AvaloniaFact]
    public void AnUnboundPanelSpeaksNothing()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Full;
        CaptureUtterances();

        new ChatPanel().SpeakLatest();
        Flush();

        Assert.Null(_spoken);
    }

    // The summary leg is started rather than awaited — the button returns while
    // the summariser runs, which is the whole reason the hourglass exists. So a
    // case that presses the button has to let the continuation run before it
    // asserts. Yielding once puts this test behind it; RunJobs then drains
    // whatever the continuation posted back to the UI thread.
    private static async Task PumpAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            Flush();
            await Task.Yield();
        }

        Flush();
    }

    // --- the settings row ---

    // The setting is offered, both modes are named, and the picker starts on
    // whatever is saved. A setting nothing exposes is a setting nobody has.
    [AvaloniaFact]
    public void ThePickerOffersBothModesAndStartsOnTheSavedOne()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Summary;

        var combo = SettingsWindow.SpeakScopePicker();

        var items = Assert.IsAssignableFrom<IEnumerable<string>>(combo.ItemsSource).ToList();
        Assert.Equal(new[] { SettingsWindow.FullLabel, SettingsWindow.SummaryLabel }, items);
        Assert.Equal(1, combo.SelectedIndex);

        ClaudeBuddySettings.SpeakScope = SpeakScope.Full;
        Assert.Equal(0, SettingsWindow.SpeakScopePicker().SelectedIndex);
    }

    // Choosing in the picker writes the setting, both ways.
    [AvaloniaFact]
    public void ChoosingInThePickerRecordsTheMode()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Full;
        var combo = SettingsWindow.SpeakScopePicker();

        combo.SelectedIndex = 1;
        Assert.Equal(SpeakScope.Summary, ClaudeBuddySettings.SpeakScope);

        combo.SelectedIndex = 0;
        Assert.Equal(SpeakScope.Full, ClaudeBuddySettings.SpeakScope);
    }
}
