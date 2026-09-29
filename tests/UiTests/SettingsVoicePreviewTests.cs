using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;
using ShapesPath = Avalonia.Controls.Shapes.Path;
using Speak = ClaudeBuddy.TextToSpeech.SpeakState;

namespace ClaudeBuddy.UiTests;

// CB-222: the preview button beside the Speak voice picker. Driven through the
// real window and the real VoicePreview, with only the three points that would
// make a noise or scan the machine — speak, cancel, and resolving the saved
// voice — replaced by seams. TextToSpeech.SilenceForTests is true here as well,
// so a seam left unset still could not reach an engine.
//
// The picker's own scan is the one thing that cannot run for real (`say -v ?`,
// SAPI), so TextToSpeech.SetVoiceOptionsForTests seeds the cache it reads and the
// real DropDownOpened -> FillVoiceList path is driven over that.
[Collection("Settings")]
public class SettingsVoicePreviewTests : IDisposable
{
    private static readonly TextToSpeech.VoiceOption Sapi =
        new(TextToSpeech.SpeakEngine.System, "Microsoft Zira Desktop", "Microsoft Zira Desktop (system)");
    private static readonly TextToSpeech.VoiceOption Kokoro =
        new(TextToSpeech.SpeakEngine.Neural, "af_heart", "af_heart (Kokoro)");
    private static readonly TextToSpeech.VoiceOption Custom =
        new(TextToSpeech.SpeakEngine.Custom, "", "Custom command");

    private readonly List<(TextToSpeech.VoiceOption Voice, bool OnUiThread)> _spoken = new();
    private readonly List<bool> _cancelsOnUiThread = new();
    private readonly List<bool> _resolvesOnUiThread = new();
    private readonly List<SettingsWindow> _windows = new();

    public SettingsVoicePreviewTests()
    {
        ClaudeBuddySettings.ReloadForTests();
        Reset();

        VoicePreview.SpeakForTests = (_, voice) =>
        {
            lock (_spoken) _spoken.Add((voice, Dispatcher.UIThread.CheckAccess()));
            TextToSpeech.Cancel();
            TextToSpeech.Enter(Speak.Speaking);
        };
        VoicePreview.CancelForTests = () =>
        {
            lock (_cancelsOnUiThread) _cancelsOnUiThread.Add(Dispatcher.UIThread.CheckAccess());
        };
        VoicePreview.ResolveSavedForTests = () =>
        {
            lock (_resolvesOnUiThread) _resolvesOnUiThread.Add(Dispatcher.UIThread.CheckAccess());
            return Sapi;
        };
    }

    public void Dispose()
    {
        // Detached, never closed: closing a headless Window can corrupt the
        // process-wide FontManager for every window built afterwards (see
        // SettingsWindowSmokeTest). Emptying it fires the same DetachedFromVisualTree
        // the buttons unsubscribe on.
        foreach (var window in _windows) window.Content = null;
        Reset();
        TextToSpeech.InvalidateVoiceCache();
        ClaudeBuddySettings.ReloadForTests();
    }

    private static void Reset()
    {
        VoicePreview.ResetForTests();
        TextToSpeech.Cancel();
        TextToSpeech.Enter(Speak.Idle);
    }

    private SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(ctor);

        var window = (SettingsWindow)ctor!.Invoke(null);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        _windows.Add(window);
        return window;
    }

    // The Speak voice row's control panel: the picker and its preview button.
    private static (StackPanel Panel, ComboBox Combo, Button Button) VoiceRow(SettingsWindow window)
    {
        var label = window.GetLogicalDescendants().OfType<TextBlock>()
            .First(t => t.Text == "Speak voice");
        var panel = label.GetLogicalParent()!.GetLogicalChildren().OfType<StackPanel>().Single();
        return (panel, panel.Children.OfType<ComboBox>().Single(), panel.Children.OfType<Button>().Single());
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    // Everything the click queued, then the UI-thread posts the worker made.
    private static async Task Settle()
    {
        await VoicePreview.Drained();
        Dispatcher.UIThread.RunJobs();
    }

    // Opens the real dropdown handler. Raised through the event's own delegate
    // rather than by opening a popup, which a headless window does not do
    // reliably.
    private static void OpenDropDown(ComboBox combo)
    {
        var field = typeof(ComboBox).GetField("DropDownOpened",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var handler = (EventHandler?)field!.GetValue(combo);
        Assert.NotNull(handler);
        handler!(combo, EventArgs.Empty);
    }

    // What Close() would raise, without closing: see Dispose. The handler under
    // test is the one the constructor subscribes, so this reaches it exactly.
    private static void RaiseClosed(Window window)
    {
        FieldInfo? field = null;
        for (var t = window.GetType(); t is not null && field is null; t = t.BaseType)
        {
            field = t.GetField("Closed", BindingFlags.NonPublic | BindingFlags.Instance);
        }
        Assert.NotNull(field);
        ((EventHandler?)field!.GetValue(window))?.Invoke(window, EventArgs.Empty);
    }

    private static string TipOf(Button button) => Assert.IsType<string>(ToolTip.GetTip(button));

    private static Color? FillOf(Button button) =>
        (button.Background as ISolidColorBrush)?.Color;

    private static void AssertLooksIdle(Button button)
    {
        Assert.Equal("Preview voice", TipOf(button));
        // The theme's own button fill, not one of ours.
        Assert.NotEqual(Color.Parse("#E04A90D9"), FillOf(button));
        Assert.NotEqual(Color.Parse("#E0B8860B"), FillOf(button));
        var glyph = Assert.IsType<ShapesPath>(button.Content);
        Assert.Equal(7, glyph.Width);
        Assert.Equal(8, glyph.Height);
    }

    // ---- the look, per state -------------------------------------------------

    [AvaloniaFact]
    public void TheLookMatchesTheFlyoutsOwnColoursForEachState()
    {
        Assert.Equal(("Preview voice", (IBrush?)null), SettingsWindow.VoicePreviewLook(Speak.Idle));

        var (playTip, playFill) = SettingsWindow.VoicePreviewLook(Speak.Speaking);
        Assert.Equal("Stop", playTip);
        Assert.Same(OrbFlyout.SpeakActiveFill, playFill);

        var (prepTip, prepFill) = SettingsWindow.VoicePreviewLook(Speak.Preparing);
        Assert.Equal("Preparing…", prepTip);
        Assert.Same(OrbFlyout.SpeakPreparingFill, prepFill);
    }

    // ---- layout ---------------------------------------------------------------

    [AvaloniaFact]
    public void TheVoiceRowHoldsThePickerAndADrawnGlyphButtonLikeTheSoundRows()
    {
        var (panel, combo, button) = VoiceRow(NewWindow());

        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, panel.Orientation);
        Assert.Equal(6, panel.Spacing);
        Assert.Same(combo, panel.Children[0]);
        Assert.Same(button, panel.Children[1]);

        // Drawn geometry, never a text glyph (CB-173).
        AssertLooksIdle(button);
        Assert.True(button.IsEnabled);
    }

    [AvaloniaFact]
    public void TheVoiceRowsHelpTextMentionsThePreviewButton()
    {
        var window = NewWindow();

        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.Text is not null && t.Text.Contains("persona sets its own voice", StringComparison.Ordinal));
    }

    // ---- clicking -------------------------------------------------------------

    // AC 3, 4, 5: with the picker still on its placeholder the saved selection is
    // resolved, off the UI thread, and the button answers before any engine runs.
    [AvaloniaFact]
    public async Task ClickAnswersImmediatelyAndNothingBlockingRunsOnTheUiThread()
    {
        using var hold = new ManualResetEventSlim();
        var resolveStarted = new TaskCompletionSource();
        VoicePreview.ResolveSavedForTests = () =>
        {
            lock (_resolvesOnUiThread) _resolvesOnUiThread.Add(Dispatcher.UIThread.CheckAccess());
            resolveStarted.TrySetResult();
            hold.Wait(TimeSpan.FromSeconds(10));
            return Sapi;
        };

        var (_, _, button) = VoiceRow(NewWindow());

        Click(button);

        // Returned from the handler with nothing spoken, and already amber.
        Assert.Empty(_spoken);
        Assert.Equal("Preparing…", TipOf(button));
        Assert.Equal(Color.Parse("#E0B8860B"), FillOf(button));
        Assert.IsType<ShapesPath>(button.Content);

        await resolveStarted.Task;
        hold.Set();
        await Settle();

        var (voice, spokeOnUi) = Assert.Single(_spoken);
        Assert.Same(Sapi, voice);
        Assert.False(spokeOnUi);
        Assert.Equal(new[] { false }, _resolvesOnUiThread);   // resolved off the UI thread too
    }

    [AvaloniaFact]
    public async Task ThePlayingStateIsBlueWithAStopSquareAndGoesBackToIdle()
    {
        var (_, _, button) = VoiceRow(NewWindow());

        Click(button);
        await Settle();

        Assert.Equal("Stop", TipOf(button));
        Assert.Equal(Color.Parse("#E04A90D9"), FillOf(button));
        var stop = Assert.IsType<ShapesPath>(button.Content);
        Assert.Equal(7, stop.Width);
        Assert.Equal(7, stop.Height);

        TextToSpeech.Enter(Speak.Preparing);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Preparing…", TipOf(button));
        var hourglass = Assert.IsType<ShapesPath>(button.Content);
        Assert.Equal(9, hourglass.Height);

        TextToSpeech.Enter(Speak.Idle);   // finished by itself
        Dispatcher.UIThread.RunJobs();
        AssertLooksIdle(button);
    }

    [AvaloniaFact]
    public async Task ClickingWhilePlayingStopsThePreviewOffTheUiThread()
    {
        var (_, _, button) = VoiceRow(NewWindow());
        Click(button);
        await Settle();

        Click(button);

        AssertLooksIdle(button);   // at once, before the kill has run
        await Settle();
        Assert.Equal(new[] { false }, _cancelsOnUiThread);
        Assert.Single(_spoken);
    }

    // AC 7: the second click stops the first, so the engine is never started twice.
    [AvaloniaFact]
    public async Task RapidClicksNeverStartTheEngineMoreThanOnce()
    {
        var (_, _, button) = VoiceRow(NewWindow());

        Click(button);
        Click(button);
        Click(button);
        Click(button);
        await Settle();

        // start, stop, start, stop: whatever ran, the last click stopped it.
        Assert.True(_spoken.Count <= 2);
        Assert.True(_cancelsOnUiThread.Count <= _spoken.Count);
        AssertLooksIdle(button);
    }

    // ---- the picker ------------------------------------------------------------

    // AC 2: with the list opened, the preview speaks what the picker shows, on
    // that voice's own engine.
    [AvaloniaFact]
    public async Task ThePreviewSpeaksTheVoiceThePickerShows()
    {
        TextToSpeech.SetVoiceOptionsForTests(new List<TextToSpeech.VoiceOption> { Sapi, Kokoro, Custom });
        var (_, combo, button) = VoiceRow(NewWindow());
        OpenDropDown(combo);

        combo.SelectedIndex = 1;
        Click(button);
        await Settle();

        var (voice, _) = Assert.Single(_spoken);
        Assert.Same(Kokoro, voice);
        Assert.Equal(TextToSpeech.SpeakEngine.Neural, voice.Engine);
        Assert.Empty(_resolvesOnUiThread);   // nothing to resolve: the picker named it
    }

    // AC 12: the scan replacing the placeholder is not a choice; a real choice is.
    [AvaloniaFact]
    public async Task OnlyARealSelectionStopsALivePreviewNotTheScanArriving()
    {
        TextToSpeech.SetVoiceOptionsForTests(new List<TextToSpeech.VoiceOption> { Sapi, Kokoro, Custom });
        var (_, combo, button) = VoiceRow(NewWindow());

        Click(button);   // placeholder still showing: previews the saved voice
        await Settle();
        Assert.Equal("Stop", TipOf(button));

        OpenDropDown(combo);   // the scan arrives and sets the selection
        await Settle();
        Assert.Empty(_cancelsOnUiThread);
        Assert.Equal("Stop", TipOf(button));

        combo.SelectedIndex = combo.SelectedIndex == 2 ? 0 : 2;   // the user chooses
        AssertLooksIdle(button);
        await Settle();

        Assert.Single(_cancelsOnUiThread);
        Assert.Single(_spoken);   // choosing never plays anything
    }

    // AC 16
    [AvaloniaFact]
    public void NoVoicesFoundDisablesThePreviewButtonToo()
    {
        TextToSpeech.SetVoiceOptionsForTests(new List<TextToSpeech.VoiceOption>());
        var (_, combo, button) = VoiceRow(NewWindow());
        Assert.True(button.IsEnabled);

        OpenDropDown(combo);

        Assert.False(combo.IsEnabled);
        Assert.False(button.IsEnabled);

        combo.IsEnabled = true;
        Assert.True(button.IsEnabled);
    }

    // ---- window life -------------------------------------------------------------

    // AC 14
    [AvaloniaFact]
    public async Task ARebuildDoesNotOrphanALivePreview()
    {
        var window = NewWindow();
        var (_, _, before) = VoiceRow(window);
        Click(before);
        await Settle();

        window.Rebuild();
        Dispatcher.UIThread.RunJobs();

        var (_, _, after) = VoiceRow(window);
        Assert.NotSame(before, after);
        Assert.Equal("Stop", TipOf(after));    // shows the current state on arrival

        // The old button is detached and no longer listens.
        TextToSpeech.Enter(Speak.Idle);
        Dispatcher.UIThread.RunJobs();
        AssertLooksIdle(after);
        Assert.Equal("Stop", TipOf(before));
    }

    [AvaloniaFact]
    public async Task TheRebuiltButtonCanStillStopThePreview()
    {
        var window = NewWindow();
        Click(VoiceRow(window).Button);
        await Settle();

        window.Rebuild();
        Dispatcher.UIThread.RunJobs();
        Click(VoiceRow(window).Button);
        await Settle();

        Assert.Single(_cancelsOnUiThread);
    }

    // AC 13
    [AvaloniaFact]
    public async Task ClosingTheWindowStopsALivePreview()
    {
        var window = NewWindow();
        Click(VoiceRow(window).Button);
        await Settle();

        RaiseClosed(window);
        await Settle();

        Assert.Single(_cancelsOnUiThread);
        Assert.Equal(Speak.Idle, VoicePreview.Look);
    }

    // AC 11
    [AvaloniaFact]
    public async Task ClosingTheWindowLeavesAReadAloudItDidNotStartAlone()
    {
        var window = NewWindow();
        Click(VoiceRow(window).Button);
        await Settle();

        // The read-aloud takes the speaker.
        TextToSpeech.Cancel();
        TextToSpeech.Enter(Speak.Speaking);

        RaiseClosed(window);
        await Settle();

        Assert.Empty(_cancelsOnUiThread);
        Assert.Equal(Speak.Speaking, TextToSpeech.State);
    }

    // AC 9: it does not mirror the read-aloud's state.
    [AvaloniaFact]
    public async Task AReadAloudStartingWhileAPreviewPlaysReturnsTheButtonToIdle()
    {
        var (_, _, button) = VoiceRow(NewWindow());
        Click(button);
        await Settle();

        TextToSpeech.Cancel();
        TextToSpeech.Enter(Speak.Speaking);
        Dispatcher.UIThread.RunJobs();

        AssertLooksIdle(button);
    }

    // AC 10: clicking with a read-aloud playing starts a preview rather than
    // reading as a stop.
    [AvaloniaFact]
    public async Task ClickingWhileAReadAloudPlaysStartsAPreview()
    {
        TextToSpeech.Enter(Speak.Speaking);   // someone else's speech
        var (_, _, button) = VoiceRow(NewWindow());

        Click(button);
        await Settle();

        Assert.Single(_spoken);
        Assert.Empty(_cancelsOnUiThread);
    }

    // AC 17: the flyouts already show the global speech state, and a preview is
    // speech. Kept on purpose, and pinned here so it stays deliberate.
    [AvaloniaFact]
    public async Task TheOrbFlyoutShowsAPreviewAsOrdinarySpeech()
    {
        var (_, _, button) = VoiceRow(NewWindow());
        Click(button);
        await Settle();

        var flyout = new OrbFlyout();
        flyout.SetSpeakState(TextToSpeech.State);

        Assert.Equal(Speak.Speaking, TextToSpeech.State);
        var fill = (ISolidColorBrush)flyout.FindControl<Avalonia.Controls.Shapes.Ellipse>("SpeakFill")!.Fill!;
        Assert.Equal(Color.Parse("#E04A90D9"), fill.Color);
    }
}
