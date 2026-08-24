using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The static half of ChatPanel: the calls the rest of the app makes to a panel it
// does not hold a reference to.
//
// ChatPanel is a singleton — its own comment explains why, two panels would fight
// over being the key window — so OpenFor, HideFor, RepositionFor, SetSpeakState,
// SetRecording, AppendToInput and RefreshIdentityFor are all static and all gate
// on which orb the one live panel is currently bound to. That gate is the whole
// substance of them, and getting it wrong means an orb's mic light, or somebody's
// dictation, landing on a different session's panel.
//
// Conventions inherited from ChatPanelTests next door rather than reinvented:
// orbs are never closed (closing one corrupts a process-wide font resource shared
// with every other headless window), and each test unbinds the panel afterwards,
// which is the one thing that does need tearing down between cases.
public class ChatPanelStaticApiTests : IDisposable
{
    private readonly List<string> _toClean = new();

    private FakeChatSession NewFake(string? sessionId = null)
    {
        var id = sessionId ?? "fake-" + Guid.NewGuid();
        _toClean.Add(id);

        return new FakeChatSession(null) { SessionId = id, DisplayName = "Fake Session" };
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ChatPanel Open(OrbWindow orb, FakeChatSession fake)
    {
        ChatPanel.OpenFor(orb, fake);
        Flush();

        return ChatPanelTestAccess.Instance!;
    }

    // --- IsOpenFor: which session the one panel is showing ---

    [AvaloniaFact]
    public void APanelIsOpenForTheSessionItWasBoundTo()
    {
        var fake = NewFake();
        Open(NewOrb(), fake);

        Assert.True(ChatPanel.IsOpenFor(fake.SessionId));
    }

    // The question is per session, not "is any panel open". A caller asking about
    // its own orb must not be told yes because somebody else's is showing.
    [AvaloniaFact]
    public void APanelIsNotOpenForADifferentSession()
    {
        var shown = NewFake();
        var other = NewFake();
        Open(NewOrb(), shown);

        Assert.False(ChatPanel.IsOpenFor(other.SessionId));
    }

    // Binding a second session moves the one panel rather than opening another,
    // which is the singleton's whole point.
    [AvaloniaFact]
    public void OpeningASecondSessionMovesTheOnePanel()
    {
        var first = NewFake();
        var second = NewFake();

        Open(NewOrb(), first);
        Open(NewOrb(), second);

        Assert.True(ChatPanel.IsOpenFor(second.SessionId));
        Assert.False(ChatPanel.IsOpenFor(first.SessionId));
    }

    // --- HideFor: only the session that asked ---

    [AvaloniaFact]
    public void HidingBySessionIdClosesThatPanel()
    {
        var fake = NewFake();
        Open(NewOrb(), fake);

        ChatPanel.HideFor(fake.SessionId);
        Flush();

        Assert.False(ChatPanel.IsOpenFor(fake.SessionId));
    }

    // An orb going away must not take somebody else's panel with it. This is the
    // gate that stops a closing orb hiding the panel a user is reading.
    [AvaloniaFact]
    public void HidingAnotherSessionLeavesThePanelAlone()
    {
        var shown = NewFake();
        var other = NewFake();
        Open(NewOrb(), shown);

        ChatPanel.HideFor(other.SessionId);
        Flush();

        Assert.True(ChatPanel.IsOpenFor(shown.SessionId));
    }

    [AvaloniaFact]
    public void HidingWhenNothingIsOpenIsHarmless()
    {
        var fake = NewFake();

        ChatPanel.HideFor(fake.SessionId);

        Assert.False(ChatPanel.IsOpenFor(fake.SessionId));
    }

    // --- AppendToInput: where dictation lands ---

    // Dictation lands in the box rather than being sent, the same rule
    // TerminalFocuser.SendText follows: transcription is a typing aid and does not
    // get to decide that you meant it.
    [AvaloniaFact]
    public void DictationLandsInTheInputBox()
    {
        var fake = NewFake();
        var panel = Open(NewOrb(), fake);
        panel.Input.Text = "";

        ChatPanel.AppendToInput("fix the arrangement test");
        Flush();

        Assert.Equal("fix the arrangement test", panel.Input.Text);
    }

    // A second phrase is appended with one space, not concatenated and not
    // replacing what is there — somebody dictating two sentences gets both.
    [AvaloniaFact]
    public void ASecondPhraseIsAppendedWithOneSpace()
    {
        var fake = NewFake();
        var panel = Open(NewOrb(), fake);
        panel.Input.Text = "";

        ChatPanel.AppendToInput("first sentence.");
        ChatPanel.AppendToInput("second sentence.");
        Flush();

        Assert.Equal("first sentence. second sentence.", panel.Input.Text);
    }

    // Trailing whitespace already in the box is absorbed rather than doubled, so
    // a box the user left with a space does not end up with two.
    [AvaloniaFact]
    public void ExistingTrailingSpaceIsNotDoubled()
    {
        var fake = NewFake();
        var panel = Open(NewOrb(), fake);
        panel.Input.Text = "typed already   ";

        ChatPanel.AppendToInput("dictated");
        Flush();

        Assert.Equal("typed already dictated", panel.Input.Text);
    }

    // The caret goes to the end, so carrying on typing continues the sentence
    // rather than inserting in front of it.
    [AvaloniaFact]
    public void TheCaretEndsUpAfterTheDictatedText()
    {
        var fake = NewFake();
        var panel = Open(NewOrb(), fake);
        panel.Input.Text = "";

        ChatPanel.AppendToInput("dictated words");
        Flush();

        Assert.Equal(panel.Input.Text!.Length, panel.Input.CaretIndex);
    }

    // Nothing open means nowhere to put it, and dropping it is better than
    // creating a panel the user did not ask for.
    [AvaloniaFact]
    public void DictationWithNoPanelOpenIsDropped()
    {
        var fake = NewFake();
        var panel = Open(NewOrb(), fake);
        ChatPanel.HideFor(fake.SessionId);
        Flush();

        panel.Input.Text = "before";
        ChatPanel.AppendToInput("should not arrive");

        Assert.Equal("before", panel.Input.Text);
    }

    // --- SetRecording: the mic light ---

    // Brushes are compared by colour, not by instance. A brush does not implement
    // value equality, so two SolidColorBrushes of the same colour are unequal —
    // which produced the delightful "Expected #e0202024, Actual #e0202024" on the
    // first run of this file.
    private static Avalonia.Media.Color ColourOf(Avalonia.Media.IBrush? brush) =>
        ((Avalonia.Media.ISolidColorBrush)brush!).Color;

    // Bound to the orb, not to the panel: the mic is on an orb's flyout, and a
    // different orb's recording must not light this panel's button.
    [AvaloniaFact]
    public void TheMicLightFollowsTheOrbThePanelIsBoundTo()
    {
        var fake = NewFake();
        var orb = NewOrb();
        var panel = Open(orb, fake);

        ChatPanel.SetRecording(orb, false);
        Flush();
        var idle = ColourOf(panel.MicFill.Fill);

        ChatPanel.SetRecording(orb, true);
        Flush();
        var recording = ColourOf(panel.MicFill.Fill);

        ChatPanel.SetRecording(orb, false);
        Flush();

        Assert.NotEqual(idle, recording);
        Assert.Equal(idle, ColourOf(panel.MicFill.Fill));
    }

    [AvaloniaFact]
    public void AnotherOrbsRecordingDoesNotLightThisPanel()
    {
        var fake = NewFake();
        var panel = Open(NewOrb(), fake);
        var before = ColourOf(panel.MicFill.Fill);

        ChatPanel.SetRecording(NewOrb(), true);
        Flush();

        Assert.Equal(before, ColourOf(panel.MicFill.Fill));
    }

    // --- SetSpeakState: speech is global ---

    // Speech is global rather than per-orb, so the panel is told from one place
    // and does not check which orb is speaking — there is only ever one voice.
    [AvaloniaFact]
    public void TheSpeakButtonShowsEachState()
    {
        var fake = NewFake();
        var panel = Open(NewOrb(), fake);

        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Idle);
        Flush();
        var idle = ColourOf(panel.SpeakFill.Fill);

        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Preparing);
        Flush();
        var preparing = ColourOf(panel.SpeakFill.Fill);

        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Speaking);
        Flush();
        var speaking = ColourOf(panel.SpeakFill.Fill);

        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Idle);
        Flush();

        // Preparing and Speaking are distinguishable from rest and from each
        // other — the button is how you know a long reply is being fetched rather
        // than the click having missed.
        Assert.NotEqual(idle, preparing);
        Assert.NotEqual(idle, speaking);
        Assert.NotEqual(preparing, speaking);
        Assert.Equal(idle, ColourOf(panel.SpeakFill.Fill));
    }

    [AvaloniaFact]
    public void SpeakStateWithNoPanelOpenIsHarmless()
    {
        var fake = NewFake();
        Open(NewOrb(), fake);
        ChatPanel.HideFor(fake.SessionId);
        Flush();

        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Speaking);
        ChatPanel.SetSpeakState(TextToSpeech.SpeakState.Idle);
    }

    // --- RepositionFor: following its own orb ---

    // Gated on the orb, because the panel follows the orb it is bound to. An
    // arrangement animation moves every orb, and each one asks — so a panel that
    // repositioned for any orb would chase whichever moved last.
    [AvaloniaFact]
    public void ThePanelRepositionsOnlyForItsOwnOrb()
    {
        var fake = NewFake();
        var orb = NewOrb();
        var panel = Open(orb, fake);

        panel.Position = new PixelPoint(500, 500);
        Flush();

        ChatPanel.RepositionFor(NewOrb());
        Flush();
        Assert.Equal(new PixelPoint(500, 500), panel.Position);

        ChatPanel.RepositionFor(orb);
        Flush();
        Assert.NotEqual(new PixelPoint(500, 500), panel.Position);
    }

    [AvaloniaFact]
    public void RepositioningWithNothingOpenIsHarmless()
    {
        ChatPanel.RepositionFor(NewOrb());
    }

    // --- RefreshIdentityFor ---

    // Called when the gateway's agent list arrives after a panel opened, which is
    // the window where a header would otherwise keep showing a raw agent id. Safe
    // to call for an orb that is not the bound one, and safe with nothing open.
    [AvaloniaFact]
    public void RefreshingIdentityIsSafeWhicheverOrbAsks()
    {
        var fake = NewFake();
        var orb = NewOrb();
        Open(orb, fake);

        ChatPanel.RefreshIdentityFor(orb);
        ChatPanel.RefreshIdentityFor(NewOrb());
        Flush();

        Assert.True(ChatPanel.IsOpenFor(fake.SessionId));
    }
}
