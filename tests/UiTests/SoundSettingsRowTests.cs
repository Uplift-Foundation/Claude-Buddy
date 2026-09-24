using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;
// Not `using Avalonia.Controls.Shapes;` — System.IO.Path is already in scope
// via this project's global usings, and Avalonia.Controls.Shapes.Path would
// collide with it on the bare name. SettingsWindow.cs itself sidesteps this
// the same way, with a `Shapes` alias; this file just spells the type out.
using ShapesPath = Avalonia.Controls.Shapes.Path;

namespace ClaudeBuddy.Tests;

// CB-167's settings-window half: the master switch and the two pickers,
// driven the same way SettingsWindowPickerTests drives ClickPicker/
// ShapePicker — through the control each builder hands back, never by
// hunting the visual tree, because what a themed row actually contains
// depends on which template loaded and this suite has no business testing
// that.
//
// ChimePlayer.PlayForTests stands in for the sound card, the same seam
// TurnSoundScanTests uses for the scan side of this feature — every case
// here proves what *would* play without a process or a speaker anywhere
// near the test host. SoundChoices' system-sound entries are read off
// SystemSoundCatalog.DefaultDirectory/DefaultExtensions directly rather
// than a temp directory, since that is exactly what SoundPicker itself
// does in production — the assertions below ask the same catalogue the
// production code asks, so they hold on both this machine and the CI
// runner's regardless of which sounds either one actually has installed.
//
// QA round 2/3 (HIGH): PreviewSound now debounces through a
// DispatcherTimer before handing off to a background Task.Run — see that
// method's own comment for why a real Task.Delay-based first draft of this
// caused genuine test-case-cleanup failures in two unrelated suites. No
// case here ever waits on the real 250ms tick; SettingsWindow.
// FlushPendingPreviewForTests fires it immediately, the same seam
// ClaudeBuddySettings.FlushPendingSave plays for its own debounced write,
// and SettingsDeferredTimerTests is the sibling suite proving that shape.
// What still needs a wait afterward is the Task.Run itself — WaitForPreview
// Async below, adapted from TurnSoundScanTests.WaitForChimeAsync.
[Collection("Settings")]
public class SoundSettingsRowTests : IDisposable
{
    private readonly bool _wasEnabled = ClaudeBuddySettings.TurnSoundsEnabled;
    private readonly string? _wasFinished = ClaudeBuddySettings.TurnFinishedSound;
    private readonly string? _wasAttention = ClaudeBuddySettings.NeedsAttentionSound;
    private readonly object _lock = new();
    private readonly List<string> _played = new();
    private readonly List<string> _tempFiles = new();
    private TaskCompletionSource<bool>? _playSignal;

    public SoundSettingsRowTests()
    {
        ChimePlayer.PlayForTests = path =>
        {
            lock (_lock)
            {
                _played.Add(path);
                _playSignal?.TrySetResult(true);
            }
        };
    }

    public void Dispose()
    {
        // Defensive: flush whatever is still pending before this instance's
        // ChimePlayer.PlayForTests seam is torn down, so a test body that
        // forgot to flush can never leave a live DispatcherTimer armed for
        // some later, unrelated test to trip over.
        SettingsWindow.FlushPendingPreviewForTests();
        ChimePlayer.PlayForTests = null;
        SettingsWindow.ChooseSoundFileForTests = null;
        ClaudeBuddySettings.TurnSoundsEnabled = _wasEnabled;
        ClaudeBuddySettings.TurnFinishedSound = _wasFinished;
        ClaudeBuddySettings.NeedsAttentionSound = _wasAttention;
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static List<string> SystemSounds() => SystemSoundCatalog.List(
        SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);

    // A real file on disk — SystemSoundCatalog.Resolve checks File.Exists
    // for an absolute path, so a path that doesn't actually exist resolves
    // to null and PreviewSound never reaches ChimePlayer.Play at all. QA
    // round 2 found exactly that: the original choose-file test pointed at
    // a made-up path, which meant "preview once" was never actually
    // exercised by it, only "wrote the setting" was.
    private string NewRealTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cb-sound-picker-" + Guid.NewGuid() + ".wav");
        File.WriteAllBytes(path, Array.Empty<byte>());
        _tempFiles.Add(path);
        return path;
    }

    // Adapted from TurnSoundScanTests.WaitForChimeAsync: checks for an
    // already-landed play first (the seam can fire before this is even
    // called), otherwise arms a signal and waits up to five seconds —
    // generous against ChimePlayer's own five-second cap plus scheduling
    // slack, and still fails a genuine regression fast rather than hanging
    // CI. Callers flush the debounce timer first (FlushPendingPreviewForTests)
    // — this only ever waits for the Task.Run PlayPendingPreview hands off
    // to actually call the seam, never for the 250ms debounce itself.
    private async Task WaitForPreviewAsync()
    {
        TaskCompletionSource<bool> signal;
        lock (_lock)
        {
            if (_played.Count > 0) return;
            signal = _playSignal = new TaskCompletionSource<bool>();
        }

        var winner = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == signal.Task, "Timed out waiting for a preview to play in the background.");
    }

    // --- SoundChoices, the list both the settings pickers and the orb's
    // Sound submenu build from ---

    [AvaloniaFact]
    public void FinishedChoicesLeadWithDefaultOffAndTheSummaryThenTheSystemSoundsThenChooseFile()
    {
        var choices = SettingsWindow.SoundChoices(null, "Glass", includeSummary: true);

        Assert.Equal($"Default (Glass)", choices[0].Label);
        Assert.Null(choices[0].Value);
        Assert.Equal(("Off", "off"), choices[1]);
        Assert.Equal((SettingsWindow.VibeSummaryLabel, "summary"), choices[2]);

        var systemSounds = SystemSounds();
        for (var i = 0; i < systemSounds.Count; i++)
        {
            Assert.Equal((systemSounds[i], systemSounds[i]), choices[3 + i]);
        }

        var last = choices[^1];
        Assert.Equal("Choose file…", last.Label);
        Assert.Equal(SettingsWindow.ChooseFileValue, last.Value);
    }

    // needsAttentionSound has no vibe-summary value — the settings comment on
    // it says so, and this is that rule reaching the list a user actually
    // sees.
    [AvaloniaFact]
    public void AttentionChoicesOmitTheSummary()
    {
        var choices = SettingsWindow.SoundChoices(null, "Ping", includeSummary: false);

        Assert.DoesNotContain(choices, c => c.Value == "summary");
        Assert.DoesNotContain(choices, c => c.Label == SettingsWindow.VibeSummaryLabel);
    }

    // A value this list would not otherwise offer — a hand-edited settings
    // file, or a sound removed since it was chosen — is shown under its own
    // name rather than silently dropped, the courtesy ClickPicker's own
    // unknown-value entry extends.
    [AvaloniaFact]
    public void AnUnknownSystemNameIsOfferedUnderItsOwnName()
    {
        var choices = SettingsWindow.SoundChoices("NotARealSound", "Glass", includeSummary: true);

        Assert.Contains(("NotARealSound", "NotARealSound"), choices);
    }

    // Same for an absolute path — a file the user chose in an earlier run —
    // except it is shown by its file name rather than the whole path, the
    // same reason SoundChoices' own comment gives.
    [AvaloniaFact]
    public void AnUnknownAbsolutePathIsOfferedUnderItsFileName()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\Users\me\Music\ding.wav" : "/Users/me/ding.wav";

        var choices = SettingsWindow.SoundChoices(path, "Glass", includeSummary: true);

        Assert.Contains((Path.GetFileName(path), path), choices);
    }

    // --- the pickers start on the saved value ---

    [AvaloniaFact]
    public void TheFinishedPickerStartsOnDefaultWhenNothingIsSaved()
    {
        ClaudeBuddySettings.TurnFinishedSound = null;

        var combo = SettingsWindow.TurnFinishedSoundPicker();

        Assert.Equal(0, combo.SelectedIndex);
    }

    [AvaloniaFact]
    public void TheFinishedPickerStartsOnOffWhenThatIsSaved()
    {
        ClaudeBuddySettings.TurnFinishedSound = "off";

        var combo = SettingsWindow.TurnFinishedSoundPicker();

        Assert.Equal(1, combo.SelectedIndex);
    }

    [AvaloniaFact]
    public void TheFinishedPickerStartsOnVibeSummaryWhenThatIsSaved()
    {
        ClaudeBuddySettings.TurnFinishedSound = "summary";

        var combo = SettingsWindow.TurnFinishedSoundPicker();

        var items = Assert.IsAssignableFrom<IEnumerable<string>>(combo.ItemsSource).ToList();
        Assert.Equal(SettingsWindow.VibeSummaryLabel, items[combo.SelectedIndex]);
    }

    [AvaloniaFact]
    public void TheAttentionPickerStartsOnASavedSystemSound()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        ClaudeBuddySettings.NeedsAttentionSound = systemSounds[0];

        var combo = SettingsWindow.NeedsAttentionSoundPicker();

        var items = Assert.IsAssignableFrom<IEnumerable<string>>(combo.ItemsSource).ToList();
        Assert.Equal(systemSounds[0], items[combo.SelectedIndex]);
    }

    // --- choosing writes the setting, and previews once ---

    [AvaloniaFact]
    public void ChoosingOffWritesOffAndPlaysNothing()
    {
        ClaudeBuddySettings.TurnFinishedSound = null;
        var combo = SettingsWindow.TurnFinishedSoundPicker();

        combo.SelectedIndex = 1; // "Off"

        Assert.Equal("off", ClaudeBuddySettings.TurnFinishedSound);
        SettingsWindow.FlushPendingPreviewForTests(); // a safe no-op: nothing armed the timer
        Assert.Empty(_played);
    }

    [AvaloniaFact]
    public void ChoosingVibeSummaryWritesSummaryAndPlaysNothing()
    {
        ClaudeBuddySettings.TurnFinishedSound = null;
        var combo = SettingsWindow.TurnFinishedSoundPicker();

        combo.SelectedIndex = 2; // "Vibe summary"

        Assert.Equal("summary", ClaudeBuddySettings.TurnFinishedSound);
        SettingsWindow.FlushPendingPreviewForTests();
        Assert.Empty(_played);
    }

    [AvaloniaFact]
    public async Task ChoosingBackToDefaultWritesNull()
    {
        ClaudeBuddySettings.TurnFinishedSound = "off";
        var combo = SettingsWindow.TurnFinishedSoundPicker();
        Assert.Equal(1, combo.SelectedIndex);

        combo.SelectedIndex = 0; // "Default (Glass)"

        Assert.Null(ClaudeBuddySettings.TurnFinishedSound);

        SettingsWindow.FlushPendingPreviewForTests();
        if (SystemSoundCatalog.Resolve(
                SystemSoundCatalog.DefaultFinishedSoundName, SystemSoundCatalog.DefaultDirectory,
                SystemSoundCatalog.DefaultExtensions) is not null)
        {
            await WaitForPreviewAsync();
        }
    }

    [AvaloniaFact]
    public async Task ChoosingASystemSoundWritesItsNameAndPreviewsItOnce()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        ClaudeBuddySettings.NeedsAttentionSound = null;
        var combo = SettingsWindow.NeedsAttentionSoundPicker();

        var items = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        combo.SelectedIndex = items.IndexOf(systemSounds[0]);

        Assert.Equal(systemSounds[0], ClaudeBuddySettings.NeedsAttentionSound);

        SettingsWindow.FlushPendingPreviewForTests();
        await WaitForPreviewAsync();

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    // --- Choose file… ---

    [AvaloniaFact]
    public async Task ChoosingChooseFileWritesThePickedPathSelectsItByNameAndPreviewsItOnce()
    {
        var picked = NewRealTempFile();
        SettingsWindow.ChooseSoundFileForTests = () => Task.FromResult<string?>(picked);

        ClaudeBuddySettings.TurnFinishedSound = null;
        var combo = SettingsWindow.TurnFinishedSoundPicker();

        var items = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        combo.SelectedIndex = items.IndexOf("Choose file…");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(picked, ClaudeBuddySettings.TurnFinishedSound);

        var reselected = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        Assert.Equal(Path.GetFileName(picked), reselected[combo.SelectedIndex]);

        // The point QA round 2 raised: a made-up path resolves to null and
        // PreviewSound never reaches ChimePlayer.Play, so this is a real
        // file precisely so this assertion means something.
        SettingsWindow.FlushPendingPreviewForTests();
        await WaitForPreviewAsync();
        Assert.Equal(new[] { picked }, _played);
    }

    [AvaloniaFact]
    public void CancellingChooseFileLeavesTheSettingAndSelectionAlone()
    {
        ClaudeBuddySettings.TurnFinishedSound = "off";
        SettingsWindow.ChooseSoundFileForTests = () => Task.FromResult<string?>(null);

        var combo = SettingsWindow.TurnFinishedSoundPicker();
        var offIndex = combo.SelectedIndex;

        var items = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        combo.SelectedIndex = items.IndexOf("Choose file…");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("off", ClaudeBuddySettings.TurnFinishedSound);
        Assert.Equal(offIndex, combo.SelectedIndex);
        SettingsWindow.FlushPendingPreviewForTests();
        Assert.Empty(_played);
    }

    // --- ChooseSoundFile's own branch (not SoundPicker's) ---

    // With no test seam set — the ordinary case for every case above except
    // the two that set one — ChooseSoundFile falls through to
    // PickSoundFileAsync, which is [ExcludeFromCodeCoverage] because it
    // opens a real OS dialog. That exclusion covers PickSoundFileAsync's own
    // body, not the ternary in ChooseSoundFile that decides to call it, so
    // this is the case that reaches it: a Control with no TopLevel (never
    // attached to a shown window) makes TopLevel.GetTopLevel return null,
    // which PickSoundFileAsync's own first line already handles by
    // returning null — no dialog opens, nothing hangs.
    [AvaloniaFact]
    public async Task ChooseSoundFileFallsThroughToTheRealPickerWhenNoSeamIsSet()
    {
        Assert.Null(SettingsWindow.ChooseSoundFileForTests);

        var unattached = new ComboBox();
        var result = await SettingsWindow.ChooseSoundFile(unattached);

        Assert.Null(result);
    }

    // --- overlapping previews (QA round 3) ---
    //
    // "Moving previews off the UI thread must still give exactly one
    // preview per choice. Fast keyboard arrowing must never stack
    // overlapping previews: each new preview stops the last one."
    // PreviewSound now debounces through a DispatcherTimer (see its own
    // comment on why not a stop-the-last-one call, which ChimePlayer cannot
    // do yet): every call restarts the same timer, so only the last of a
    // fast burst is still pending once anything actually flushes or the
    // real tick fires — the earlier ones are never played at all, not
    // played and then silenced.

    [AvaloniaFact]
    public async Task PreviewSoundCancelsAnEarlierStillPendingPreview()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count < 2) return; // need two distinct choices to arrow between

        SettingsWindow.PreviewSound(systemSounds[0], systemSounds[0]);
        SettingsWindow.PreviewSound(systemSounds[1], systemSounds[1]);

        // One flush, because there is only ever one pending preview to
        // flush — the first call's request was overwritten, not queued
        // alongside the second's, so there is nothing left over for a
        // second flush or a wait to find.
        SettingsWindow.FlushPendingPreviewForTests();
        await WaitForPreviewAsync();

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[1], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    // The same guard, reached the way a user actually reaches it — arrowing
    // through the real picker rather than calling PreviewSound directly.
    [AvaloniaFact]
    public async Task RapidArrowingThroughThePickerPreviewsOnlyTheSoundItSettlesOn()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count < 2) return; // need two distinct choices to arrow between

        ClaudeBuddySettings.NeedsAttentionSound = null;
        var combo = SettingsWindow.NeedsAttentionSoundPicker();
        var items = ((IEnumerable<string>)combo.ItemsSource!).ToList();

        // Three selections in immediate succession — a burst of arrow-key
        // steps, none of which waits for the last one's debounce.
        combo.SelectedIndex = items.IndexOf(systemSounds[0]);
        combo.SelectedIndex = items.IndexOf(systemSounds[1]);
        combo.SelectedIndex = items.IndexOf(systemSounds[0]);

        Assert.Equal(systemSounds[0], ClaudeBuddySettings.NeedsAttentionSound);

        SettingsWindow.FlushPendingPreviewForTests();
        await WaitForPreviewAsync();

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    // The other half of "exactly one preview per choice": settling on ONE
    // choice, with no burst at all, still plays it exactly once — the
    // debounce is not a rate limiter that could also eat a deliberate,
    // solitary selection.
    [AvaloniaFact]
    public async Task ASingleDeliberateSelectionStillPreviewsExactlyOnce()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        SettingsWindow.PreviewSound(systemSounds[0], systemSounds[0]);

        SettingsWindow.FlushPendingPreviewForTests();
        await WaitForPreviewAsync();

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    // FlushPendingPreviewForTests' own guard: calling it with nothing armed
    // — the common case, since most cases above call it defensively even
    // when nothing was going to play — must stay a no-op rather than throw
    // on a null timer or play something stale. Both of the guard's shapes
    // of "nothing armed": a timer that has never been created at all
    // (reset first — see ResetPreviewDebounceForTests' own comment on why
    // that reset has to be forced rather than merely arranged), and one
    // that exists but is not currently running, reached immediately
    // afterward the ordinary way.
    [AvaloniaFact]
    public void FlushingWithNothingPendingIsASafeNoOp()
    {
        SettingsWindow.ResetPreviewDebounceForTests();
        SettingsWindow.FlushPendingPreviewForTests();
        Assert.Empty(_played);

        SettingsWindow.FlushPendingPreviewForTests();
        Assert.Empty(_played);

        // A second consecutive reset: this time _previewTimer is guaranteed
        // already null (the first reset just set it), covering
        // ResetPreviewDebounceForTests' own `_previewTimer?.Stop()` null arm
        // — the first call above could land on either arm depending on
        // whether an earlier test in this process had already created one.
        SettingsWindow.ResetPreviewDebounceForTests();
        Assert.Empty(_played);
    }

    // --- the preview button ---

    [AvaloniaFact]
    public async Task ThePreviewButtonReplaysWhateverIsCurrentlySelected()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        var window = NewWindow();
        var button = (Button)window.PreviewButton(() => systemSounds[0], "Glass");

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        SettingsWindow.FlushPendingPreviewForTests();
        await WaitForPreviewAsync();

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    [AvaloniaFact]
    public void PreviewSoundIsSilentForOffAndSummary()
    {
        SettingsWindow.PreviewSound("off", "Glass");
        SettingsWindow.PreviewSound("summary", "Glass");

        SettingsWindow.FlushPendingPreviewForTests();
        Assert.Empty(_played);
    }

    // The other silent path: a real (non-off, non-summary) setting that
    // SystemSoundCatalog.Resolve simply can't turn into a file — a system
    // sound removed since it was chosen, or a hand-edited settings value.
    // Distinct from the off/summary case above: this returns from
    // PreviewSound's *second* early return, after Resolve has already run,
    // rather than skipping Resolve entirely.
    [AvaloniaFact]
    public void PreviewSoundIsSilentForAnUnresolvableSetting()
    {
        SettingsWindow.PreviewSound("NotARealSoundName", "Glass");

        SettingsWindow.FlushPendingPreviewForTests();
        Assert.Empty(_played);
    }

    // PlayGlyph's own branch: a drawn triangle rather than the "▶" text
    // glyph it replaces (see that method's own comment on CB-173), coloured
    // off IsDark the same way CardBackground/Hairline a few hundred lines up
    // already are. Two windows rather than one with its theme flipped
    // partway through: ActualThemeVariantChanged triggers a full Rebuild()
    // in the constructor, which would tear down whichever button this test
    // built first — a fresh window per theme sidesteps that entirely rather
    // than fighting it.
    [AvaloniaTheory]
    [InlineData(false, "#FF000000")]
    [InlineData(true, "#FFFFFFFF")]
    public void ThePreviewGlyphIsColouredForTheWindowsTheme(bool dark, string expectedHex)
    {
        var window = NewWindow();
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();

        var button = (Button)window.PreviewButton(() => null, "Glass");
        var glyph = Assert.IsType<ShapesPath>(button.Content);
        var fill = Assert.IsType<SolidColorBrush>(glyph.Fill);

        Assert.Equal(Color.Parse(expectedHex), fill.Color);
    }

    [AvaloniaFact]
    public async Task PreviewSoundResolvesTheDefaultWhenGivenNull()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        SettingsWindow.PreviewSound(null, systemSounds[0]);
        SettingsWindow.FlushPendingPreviewForTests();
        await WaitForPreviewAsync();

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    // QA round 2 (HIGH): PreviewSound used to call ChimePlayer.Play
    // synchronously, straight from SelectionChanged and the ▶ click's
    // handler — both on the Avalonia UI thread — so arrowing through the
    // picker froze the whole settings window for about 2.4s per step, the
    // length of a short system chime. Measured the same way
    // TurnSoundScanTests.AChimeIsNeverPlayedOnTheUiThread measures the scan
    // side of the identical bug: the seam fires exactly where the real
    // ChimePlayer.Play would run, so the thread it fires on is the thread a
    // real process wait would have blocked. Flushed rather than waited on a
    // real tick, for the reason every other case in this file is.
    [AvaloniaFact]
    public async Task APreviewNeverPlaysOnTheUiThread()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        var onUiThread = new List<bool>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = _ =>
        {
            onUiThread.Add(Dispatcher.UIThread.CheckAccess());
            signal.TrySetResult(true);
        };

        SettingsWindow.PreviewSound(systemSounds[0], systemSounds[0]);
        SettingsWindow.FlushPendingPreviewForTests();

        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        var single = Assert.Single(onUiThread);
        Assert.False(single, "PreviewSound ran ChimePlayer.Play on the UI thread; the real call blocks it for up to 5 s");
    }

    // --- the master switch and the row list ---

    // Driven at the handler, not the control, for the reason
    // SettingsWindowRowTests' own header gives: a row's toggle is a
    // ToggleSwitch whose template is borrowed from Fluent at runtime and
    // falls back to a CheckBox when that fails, so which control a row holds
    // is a property of the theme rather than of the setting. Synthesizing a
    // click would be testing the borrow.
    [AvaloniaFact]
    public void TheMasterSwitchWritesTurnSoundsEnabled()
    {
        var window = NewWindow();

        window.OnTurnSoundsToggled(true);
        Assert.True(ClaudeBuddySettings.TurnSoundsEnabled);

        window.OnTurnSoundsToggled(false);
        Assert.False(ClaudeBuddySettings.TurnSoundsEnabled);
    }

    [AvaloniaFact]
    public void SoundRowsReturnsExactlyThreeRows()
    {
        var window = NewWindow();

        Assert.Equal(3, window.SoundRows().Length);
    }

    // SoundRows() wires each row's preview button to a getter closing over
    // the *live* setting (`() => ClaudeBuddySettings.TurnFinishedSound`,
    // not a snapshot taken when the row was built) — unlike
    // ThePreviewButtonReplaysWhateverIsCurrentlySelected above, which drives
    // PreviewButton directly with its own throwaway getter and so never
    // actually calls either of SoundRows' two real ones. Found by
    // tools/coverage.sh: both closures were built (SoundRows() runs them as
    // arguments) but never invoked anywhere, which line coverage cannot
    // distinguish from "covered" without a case that clicks the real button.
    [AvaloniaFact]
    public async Task TheFinishedRowsPreviewButtonReadsTheLiveFinishedSetting()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        ClaudeBuddySettings.TurnFinishedSound = systemSounds[0];
        var window = NewWindow();
        var rows = window.SoundRows();

        var button = rows[1].GetLogicalDescendants().OfType<Button>().Single();
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        SettingsWindow.FlushPendingPreviewForTests();
        await WaitForPreviewAsync();

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    [AvaloniaFact]
    public async Task TheAttentionRowsPreviewButtonReadsTheLiveAttentionSetting()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        ClaudeBuddySettings.NeedsAttentionSound = systemSounds[0];
        var window = NewWindow();
        var rows = window.SoundRows();

        var button = rows[2].GetLogicalDescendants().OfType<Button>().Single();
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        SettingsWindow.FlushPendingPreviewForTests();
        await WaitForPreviewAsync();

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }
}
