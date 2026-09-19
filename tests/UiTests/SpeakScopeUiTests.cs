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
    private readonly SpeakScope _scopeWas = ClaudeBuddySettings.SpeakScope;

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
        ClaudeBuddySettings.SpeakScope = _scopeWas;
        SpeechSummary.SummarizerForTests = null;
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
    }

    private FakeChatSession NewFake(IEnumerable<ChatTurn> history)
    {
        var id = "speakscope-" + Guid.NewGuid();
        _toClean.Add(id);
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
        await panel.SpeakSummaryAsync(reply);

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
        await panel.SpeakSummaryAsync(reply);

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
        await panel.SpeakSummaryAsync(reply);

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
