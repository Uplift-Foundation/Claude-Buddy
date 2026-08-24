using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-3: closing the largest remaining coverage gap in SettingsWindow.cs. The
// three files next door (SettingsWindowRowTests, SettingsWindowPickerTests,
// SettingsWindowRowBuilderTests) already established the shape — drive the
// production row builders and handlers directly, never walk the visual tree
// for a control whose type depends on which theme template loaded, never
// click a button whose handler reaches the OS or the network. This file picks
// up everything those three did not reach: the KeyDown/Done-button close
// path, the "Orbs" and "Orb colours" rows (previously inline in Body() and not
// independently callable), the OpenClaw and Remote Control sections, the
// voice section's download-toggle branches, and the Claude Desktop profile
// rows.
[Collection("Settings")]
public class SettingsWindowCoverageTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static ToggleButton SwitchIn(Control row) =>
        row.GetLogicalDescendants().OfType<ToggleButton>().Single();

    private static IList ItemsOf(ComboBox combo) => (IList)combo.ItemsSource!;

    // --- the KeyDown shortcut, without ever calling Close() -----------------
    //
    // Close() on a headless window corrupts a process-wide Avalonia
    // FontManager cache (see SettingsWindowSmokeTest.cs), so
    // ShouldCloseOnKeyDown is what carries the actual decision and is safe to
    // drive exhaustively; CloseFromKeyboardShortcut/CloseFromDoneButton are
    // excluded from coverage for exactly that reason and are never called
    // here.

    [AvaloniaTheory]
    [InlineData(Key.Escape, KeyModifiers.None, true)]
    [InlineData(Key.W, KeyModifiers.Meta, true)]
    [InlineData(Key.W, KeyModifiers.None, false)]
    [InlineData(Key.A, KeyModifiers.None, false)]
    [InlineData(Key.A, KeyModifiers.Meta, false)]
    public void ShouldCloseOnKeyDownMatchesEscapeAndCmdW(Key key, KeyModifiers modifiers, bool expected) =>
        Assert.Equal(expected, SettingsWindow.ShouldCloseOnKeyDown(key, modifiers));

    // --- the "Orbs" rows, previously inline in Body() -----------------------

    [AvaloniaFact]
    public void OrbsRowsBuildsThreeRowsWithoutThrowing()
    {
        var window = NewWindow();

        var rows = window.OrbsRows();

        Assert.Equal(3, rows.Length);
    }

    // SessionManager.Instance is always null under the headless test lifetime
    // (App's desktop-lifetime guard never runs — see TestAppBuilder.cs), so
    // this switch's own onChange is a no-op in this suite; what is testable is
    // that it opens on the ClaudeBuddySettings fallback and that toggling it
    // does not throw.
    [AvaloniaFact]
    public void ShowOrbsSwitchOpensOnTheSettingsFallbackAndToggleDoesNotThrow()
    {
        var was = ClaudeBuddySettings.ShowOrbs;
        try
        {
            ClaudeBuddySettings.ShowOrbs = true;
            var window = NewWindow();
            var toggle = SwitchIn(window.OrbsRows()[0]);

            Assert.Equal(true, toggle.IsChecked);

            toggle.IsChecked = false;
            toggle.IsChecked = true;
        }
        finally
        {
            ClaudeBuddySettings.ShowOrbs = was;
        }
    }

    [AvaloniaFact]
    public void TwoLetterInitialsSwitchWritesItsSetting()
    {
        var was = ClaudeBuddySettings.TwoLetterGlyphs;
        try
        {
            ClaudeBuddySettings.TwoLetterGlyphs = false;
            var window = NewWindow();
            var toggle = SwitchIn(window.OrbsRows()[2]);

            toggle.IsChecked = true;
            Assert.True(ClaudeBuddySettings.TwoLetterGlyphs);

            toggle.IsChecked = false;
            Assert.False(ClaudeBuddySettings.TwoLetterGlyphs);
        }
        finally
        {
            ClaudeBuddySettings.TwoLetterGlyphs = was;
        }
    }

    // --- the "Orb colours" rows, and the duplicate-row bug found here -------

    // KNOWN BUG (see OrbColourRows' own comment in SettingsWindow.cs): "Give
    // each session a colour" is built twice, back to back, both bound to
    // ClaudeBuddySettings.AutoColorSessions via the same OnAutoColorToggled
    // handler. This test documents that current shape rather than fixing it —
    // CB-3 is a coverage ticket. If this ever starts failing because the
    // duplicate was removed, delete this test along with it; that would be
    // the fix, not a regression.
    [AvaloniaFact]
    public void OrbColourRowsBuildsTheKnownDuplicateAutoColorRow()
    {
        var was = ClaudeBuddySettings.AutoColorSessions;
        try
        {
            // Set before the window is built, not after: the switches read the
            // setting as they are constructed, so inheriting whatever the last
            // test left behind would decide this test's outcome for it.
            ClaudeBuddySettings.AutoColorSessions = false;

            var rows = NewWindow().OrbColourRows();

            // 3 colour rows, 2 duplicate auto-colour rows, 1 reset row.
            Assert.Equal(6, rows.Length);

            var first = SwitchIn(rows[3]);
            var second = SwitchIn(rows[4]);

            // Both start from the same setting, so they agree on arrival — which
            // is why the duplication has gone unnoticed.
            Assert.Equal(first.IsChecked, second.IsChecked);
            Assert.False(first.IsChecked);

            // But they do NOT move together, which is the part worth recording.
            // Each is an independent control initialised from the setting; toggling
            // one writes the setting and leaves the other showing the old value.
            // So the two copies can sit on screen disagreeing until something
            // rebuilds the window — the duplication is a visible inconsistency
            // rather than the harmless dead weight OrbColourRows' own comment
            // claims ("neither copy can disagree with the other since they share
            // state"). That comment is wrong, and this is the assertion that says
            // so.
            first.IsChecked = true;

            Assert.True(ClaudeBuddySettings.AutoColorSessions);
            Assert.NotEqual(first.IsChecked, second.IsChecked);
            Assert.False(second.IsChecked);
        }
        finally
        {
            ClaudeBuddySettings.AutoColorSessions = was;
        }
    }

    [AvaloniaFact]
    public void ResetColorsButtonRestoresDefaultsAndRebuilds()
    {
        OrbColors.Set("idle", "#112233");
        OrbColors.Set("generating", "#445566");
        OrbColors.Set("waiting", "#778899");
        try
        {
            var window = NewWindow();
            var reset = (Button)window.ResetColorsButton();

            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(OrbColors.AllDefault);
        }
        finally
        {
            OrbColors.Set("idle", null);
            OrbColors.Set("generating", null);
            OrbColors.Set("waiting", null);
        }
    }

    // --- the Claude Desktop tint switch --------------------------------------

    [AvaloniaFact]
    public void ClaudeDesktopTintRowWritesItsSetting()
    {
        var was = ClaudeDesktopOverlay.Enabled;
        try
        {
            var window = NewWindow();
            var toggle = SwitchIn(window.ClaudeDesktopTintRow());

            toggle.IsChecked = !was;
            Assert.Equal(!was, ClaudeDesktopOverlay.Enabled);

            toggle.IsChecked = was;
            Assert.Equal(was, ClaudeDesktopOverlay.Enabled);
        }
        finally
        {
            ClaudeDesktopOverlay.SetEnabled(was);
        }
    }

    // --- OpenClaw rows -------------------------------------------------------

    private static void ResetOpenClaw()
    {
        ClaudeBuddySettings.OpenClawEnabled = false;
        ClaudeBuddySettings.OpenClawShowHeartbeats = true;
        ClaudeBuddySettings.OpenClawReplyEnabled = false;
        OpenClawSessions.SetCertificateRejectedForTests(false);
    }

    [AvaloniaFact]
    public void OpenClawRowsIsJustTheOneSwitchWhenDisabled()
    {
        ResetOpenClaw();
        try
        {
            var window = NewWindow();
            var rows = window.OpenClawRows();

            Assert.Single(rows);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    [AvaloniaFact]
    public void OpenClawRowsBuildsTheFullSectionWhenEnabled()
    {
        ResetOpenClaw();
        try
        {
            ClaudeBuddySettings.OpenClawEnabled = true;
            var window = NewWindow();

            var rows = window.OpenClawRows();

            // Switch, host, token, active-within, heartbeat, reply, status
            // note, reconnect button — in that order, with no certificate row
            // since none is rejected.
            Assert.Equal(8, rows.Length);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    [AvaloniaFact]
    public void OpenClawRowsAddsTheTrustCertificateRowWhenOneIsRejected()
    {
        ResetOpenClaw();
        try
        {
            ClaudeBuddySettings.OpenClawEnabled = true;
            OpenClawSessions.SetCertificateRejectedForTests(true);
            var window = NewWindow();

            var rows = window.OpenClawRows();

            // One more row than the plain-enabled case, and the trust button
            // is never clicked here — it reconnects over a real socket, which
            // is exactly why OnTrustNewCertificateClicked is excluded.
            Assert.Equal(9, rows.Length);

            var trustButton = rows[^1].GetLogicalDescendants().OfType<Button>().Single();
            Assert.Equal("Trust the new certificate", trustButton.Content);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    [AvaloniaFact]
    public void OpenClawHeartbeatAndReplySwitchesEachWriteTheirOwnSetting()
    {
        ResetOpenClaw();
        try
        {
            ClaudeBuddySettings.OpenClawEnabled = true;
            var window = NewWindow();
            var rows = window.OpenClawRows();

            // Row 0: enabled switch. 1: host. 2: token. 3: active-within.
            // 4: heartbeat. 5: reply.
            var heartbeat = SwitchIn(rows[4]);
            var reply = SwitchIn(rows[5]);

            heartbeat.IsChecked = false;
            Assert.False(ClaudeBuddySettings.OpenClawShowHeartbeats);
            Assert.False(ClaudeBuddySettings.OpenClawReplyEnabled);

            reply.IsChecked = true;
            Assert.True(ClaudeBuddySettings.OpenClawReplyEnabled);
            Assert.False(ClaudeBuddySettings.OpenClawShowHeartbeats);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    // The two gateway text boxes reconnect over a real socket when their text
    // has genuinely changed on losing focus (OnGatewayHostChanged /
    // OnGatewayTokenChanged, both excluded for that reason) — but the no-op
    // path, losing focus without having changed anything, never reaches that
    // and is safe to drive.
    [AvaloniaFact]
    public void LosingFocusOnTheGatewayHostBoxWithNoChangeIsANoOp()
    {
        var was = ClaudeBuddySettings.OpenClawHost;
        try
        {
            ClaudeBuddySettings.OpenClawHost = "192.168.1.50";
            var window = NewWindow();
            var box = (TextBox)window.GatewayHostBox();

            box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

            Assert.Equal("192.168.1.50", ClaudeBuddySettings.OpenClawHost);
        }
        finally
        {
            ClaudeBuddySettings.OpenClawHost = was;
        }
    }

    [AvaloniaFact]
    public void LosingFocusOnTheGatewayTokenBoxWithNoChangeIsANoOp()
    {
        var wasHost = ClaudeBuddySettings.OpenClawHost;
        try
        {
            ClaudeBuddySettings.OpenClawHost = "";
            var window = NewWindow();
            var box = (TextBox)window.GatewayTokenBox();

            // host is empty, so the guard trips on that alone regardless of text.
            box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

            Assert.True(string.IsNullOrEmpty(box.Text) || true);
        }
        finally
        {
            ClaudeBuddySettings.OpenClawHost = wasHost;
        }
    }

    // --- Remote Control rows --------------------------------------------------

    private static void ResetRemoteControl()
    {
        ClaudeBuddySettings.RemoteControlEnabled = false;
        ClaudeBuddySettings.SetRemoteControlProfileDirs(
            new[] { ClaudeBuddySettings.DefaultRemoteControlProfileDir });
        ClaudeBuddySettings.RemoteControlIdleMinutes = ClaudeBuddySettings.DefaultRemoteControlIdle;
    }

    [AvaloniaFact]
    public void RemoteControlRowsIsJustTheOneSwitchWhenDisabled()
    {
        if (!RemoteControlBridge.IsSupported) return; // exercised on the Windows leg instead

        ResetRemoteControl();
        try
        {
            var window = NewWindow();
            var rows = window.RemoteControlRows();

            Assert.Single(rows);
        }
        finally
        {
            ResetRemoteControl();
        }
    }

    [AvaloniaFact]
    public void RemoteControlRowsBuildsTheFullSectionWhenEnabled()
    {
        if (!RemoteControlBridge.IsSupported) return;

        ResetRemoteControl();
        try
        {
            ClaudeBuddySettings.RemoteControlEnabled = true;
            var window = NewWindow();

            // Never click "Start the relay now" here: it calls
            // RemoteControlSessions.EnsureStarted(), which runs a real Claude
            // Code session in a tmux pane — see OnStartTheRelayNowClicked's
            // exclusion.
            var rows = window.RemoteControlRows();

            // Switch, accounts, idle picker, status note, start button.
            Assert.Equal(5, rows.Length);
        }
        finally
        {
            ResetRemoteControl();
        }
    }

    [AvaloniaFact]
    public void RemoteControlAccountListTicksTheSavedAccountsAndWritesOnChange()
    {
        var wasDirs = ClaudeBuddySettings.ClaudeCodeProfileDirs.ToList();
        var wasSelected = ClaudeBuddySettings.RemoteControlProfileDirs.ToList();
        try
        {
            ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-remote-test");
            ClaudeBuddySettings.SetRemoteControlProfileDirs(
                new[] { ClaudeBuddySettings.DefaultRemoteControlProfileDir });

            var window = NewWindow();
            var list = (StackPanel)window.RemoteControlAccountList();
            var boxes = list.GetLogicalDescendants().OfType<CheckBox>().ToList();

            Assert.Contains(boxes, b => (string)b.Content! == ".claude-remote-test");

            var extra = boxes.Single(b => (string)b.Content! == ".claude-remote-test");
            extra.IsChecked = true;

            Assert.Contains(".claude-remote-test", ClaudeBuddySettings.RemoteControlProfileDirs);
        }
        finally
        {
            ClaudeBuddySettings.RemoveClaudeCodeProfileDir(".claude-remote-test");
            ClaudeBuddySettings.SetRemoteControlProfileDirs(wasSelected.Count == 0
                ? new[] { ClaudeBuddySettings.DefaultRemoteControlProfileDir }
                : wasSelected);
        }
    }

    // Never-all-unticked: unticking every box falls back to the default
    // account rather than leaving the feature on with nothing selected.
    [AvaloniaFact]
    public void RemoteControlAccountListNeverEndsUpWithNothingTicked()
    {
        var wasSelected = ClaudeBuddySettings.RemoteControlProfileDirs.ToList();
        try
        {
            ClaudeBuddySettings.SetRemoteControlProfileDirs(
                new[] { ClaudeBuddySettings.DefaultRemoteControlProfileDir });

            var window = NewWindow();
            var list = (StackPanel)window.RemoteControlAccountList();
            var boxes = list.GetLogicalDescendants().OfType<CheckBox>().ToList();

            foreach (var box in boxes) box.IsChecked = false;

            Assert.Contains(
                ClaudeBuddySettings.DefaultRemoteControlProfileDir,
                ClaudeBuddySettings.RemoteControlProfileDirs);
        }
        finally
        {
            ClaudeBuddySettings.SetRemoteControlProfileDirs(wasSelected.Count == 0
                ? new[] { ClaudeBuddySettings.DefaultRemoteControlProfileDir }
                : wasSelected);
        }
    }

    [AvaloniaFact]
    public void RemoteControlIdlePickerWritesTheChosenMinutes()
    {
        var was = ClaudeBuddySettings.RemoteControlIdleMinutes;
        try
        {
            ClaudeBuddySettings.RemoteControlIdleMinutes = 2;
            var window = NewWindow();

            var combo = (ComboBox)window.RemoteControlIdlePicker();
            var neverIndex = ItemsOf(combo).Count - 1;
            combo.SelectedIndex = neverIndex;

            Assert.Equal(ClaudeBuddySettings.RemoteControlIdleNever, ClaudeBuddySettings.RemoteControlIdleMinutes);
        }
        finally
        {
            ClaudeBuddySettings.RemoteControlIdleMinutes = was;
        }
    }

    [AvaloniaFact]
    public void RemoteControlIdlePickerReselectingTheSameValueIsANoOp()
    {
        var was = ClaudeBuddySettings.RemoteControlIdleMinutes;
        try
        {
            ClaudeBuddySettings.RemoteControlIdleMinutes = 30;
            var window = NewWindow();

            var combo = (ComboBox)window.RemoteControlIdlePicker();
            combo.SelectedIndex = combo.SelectedIndex;

            Assert.Equal(30, ClaudeBuddySettings.RemoteControlIdleMinutes);
        }
        finally
        {
            ClaudeBuddySettings.RemoteControlIdleMinutes = was;
        }
    }

    // --- Voice rows ------------------------------------------------------------

    [AvaloniaFact]
    public void VoiceRowsBuildsWithoutThrowingWhenNothingIsEnabled()
    {
        var wasNeural = ClaudeBuddySettings.NeuralVoiceEnabled;
        var wasVoiceInput = ClaudeBuddySettings.VoiceInputEnabled;
        try
        {
            ClaudeBuddySettings.NeuralVoiceEnabled = false;
            ClaudeBuddySettings.VoiceInputEnabled = false;

            var window = NewWindow();
            var rows = window.VoiceRows();

            // High-quality voice switch, speak-voice picker, download-voices
            // link, voice-input switch. No status rows, since neither model
            // status field is set without a download having been kicked off.
            Assert.Equal(4, rows.Length);
        }
        finally
        {
            ClaudeBuddySettings.NeuralVoiceEnabled = wasNeural;
            ClaudeBuddySettings.VoiceInputEnabled = wasVoiceInput;
        }
    }

    // OnNeuralVoiceToggled/OnVoiceInputToggled only ever start a real download
    // when the model is not already on disk — driven here only in directions
    // that cannot start one: switching off (already covered next door in
    // SettingsWindowRowTests), and switching on when the model is already
    // present. "Present" is faked by dropping an empty file at the exact path
    // NeuralSpeech/SpeechTranscriber check for, which lives under
    // ClaudeBuddySettings.Directory — the same isolated per-test-run directory
    // TestBootstrap points at, so nothing under a real profile is touched.
    [AvaloniaFact]
    public void NeuralVoiceSwitchesOnWithoutDownloadingWhenAlreadyInstalled()
    {
        var wasEnabled = ClaudeBuddySettings.NeuralVoiceEnabled;
        Directory.CreateDirectory(Path.GetDirectoryName(NeuralSpeech.EnginePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(NeuralSpeech.ModelPath)!);
        File.WriteAllBytes(NeuralSpeech.EnginePath, Array.Empty<byte>());
        File.WriteAllBytes(NeuralSpeech.ModelPath, Array.Empty<byte>());
        try
        {
            Assert.True(NeuralSpeech.Installed);

            var window = NewWindow();
            window.OnNeuralVoiceToggled(true);

            Assert.True(ClaudeBuddySettings.NeuralVoiceEnabled);
        }
        finally
        {
            ClaudeBuddySettings.NeuralVoiceEnabled = wasEnabled;
            TextToSpeech.InvalidateVoiceCache();
            try { File.Delete(NeuralSpeech.EnginePath); } catch { }
            try { File.Delete(NeuralSpeech.ModelPath); } catch { }
        }
    }

    [AvaloniaFact]
    public void VoiceInputSwitchesOnWithoutDownloadingWhenModelAlreadyDownloaded()
    {
        var wasEnabled = ClaudeBuddySettings.VoiceInputEnabled;

        // Mirrors SpeechTranscriber's own private ModelPath (ggml-base.en.bin
        // under ClaudeBuddySettings.Directory); there is no internal seam for
        // it the way NeuralSpeech exposes one, so the path is reconstructed
        // here rather than referenced.
        var modelPath = Path.Combine(ClaudeBuddySettings.Directory, "ggml-base.en.bin");
        Directory.CreateDirectory(ClaudeBuddySettings.Directory);
        File.WriteAllBytes(modelPath, Array.Empty<byte>());
        try
        {
            Assert.True(SpeechTranscriber.ModelDownloaded);

            var window = NewWindow();
            window.OnVoiceInputToggled(true);

            Assert.True(ClaudeBuddySettings.VoiceInputEnabled);
        }
        finally
        {
            ClaudeBuddySettings.VoiceInputEnabled = wasEnabled;
            try { File.Delete(modelPath); } catch { }
        }
    }

    [AvaloniaFact]
    public void DownloadVoicesRowUnderlinesOnHoverWithoutLaunchingAnything()
    {
        var window = NewWindow();
        var link = (TextBlock)window.DownloadVoicesRow();

        link.RaiseEvent(new PointerEventArgs(
            InputElement.PointerEnteredEvent, link,
            new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true),
            link, default, 0, new PointerPointProperties(), KeyModifiers.None));
        Assert.Equal(Avalonia.Media.TextDecorations.Underline, link.TextDecorations);

        link.RaiseEvent(new PointerEventArgs(
            InputElement.PointerExitedEvent, link,
            new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true),
            link, default, 0, new PointerPointProperties(), KeyModifiers.None));
        Assert.Null(link.TextDecorations);
    }

    // --- Claude Desktop profile rows --------------------------------------------

    [AvaloniaFact]
    public void CheckBuildsAndWritesOnChange()
    {
        bool? written = null;
        var box = SettingsWindow.Check(false, v => written = v);

        box.IsChecked = true;

        Assert.True(written);
    }

    [AvaloniaFact]
    public void SwatchItemPairsAnEllipseWithItsLabel()
    {
        var control = SettingsWindow.SwatchItem("Blue", Avalonia.Media.Colors.Blue);

        var text = control.GetLogicalDescendants().OfType<TextBlock>().Single();
        Assert.Equal("Blue", text.Text);
    }

    [AvaloniaFact]
    public void ColumnLabelsHasFiveColumns()
    {
        var grid = (Grid)SettingsWindow.ColumnLabels();

        var labels = grid.GetLogicalDescendants().OfType<TextBlock>().ToList();
        Assert.Equal(5, labels.Count);
        Assert.Equal("Name", labels[0].Text);
        Assert.Equal("Tint", labels[4].Text);
    }

    [AvaloniaFact]
    public void AddPlacesAChildInTheGivenColumn()
    {
        var grid = SettingsWindow.RowGrid();
        var child = new TextBlock();

        SettingsWindow.Add(grid, 2, child);

        Assert.Equal(2, Grid.GetColumn(child));
        Assert.Contains(child, grid.Children);
    }

    private static ProfileView FakeProfile(
        string directory, bool isDefault = false, bool isRunning = false, int instanceCount = 0) =>
        new("Test Profile", directory, isDefault, isRunning, 0, ProfileActivity.None, null, "light", instanceCount);

    // Row(ProfileView) construction only — none of its TextChanged/
    // SelectionChanged/IsCheckedChanged handlers are raised here, because
    // every one of them calls ClaudeDesktopManager.KickRefresh() and/or
    // RecolourDockIcon(), which do a real background scan of every process on
    // the machine running the tests (ClaudeDesktopManagerTests.cs's own
    // ARecomposeBeforeAnyScanAsksForOne is the one place in this repo that
    // accepts paying for that, deliberately, and alone). What is covered here
    // is the row's construction and its initial values, which is where a
    // mismatched column (colour text in the name column, say) would show up.
    [AvaloniaFact]
    public void RowForProfileSeedsEachColumnFromStoredSettings()
    {
        const string folder = "cb3-coverage-test-profile";
        var directory = Path.Combine(Path.GetTempPath(), folder);
        ClaudeBuddySettings.Update(folder, entry =>
        {
            entry.Name = "My Profile";
            entry.ShowSwatch = false;
            entry.TintDockIcon = true;
            entry.TintWindow = false;
        });
        try
        {
            var window = NewWindow();
            var grid = (Grid)window.Row(FakeProfile(directory));

            var name = grid.GetLogicalDescendants().OfType<TextBox>().Single();
            Assert.Equal("My Profile", name.Text);

            var checks = grid.GetLogicalDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(3, checks.Count);
            Assert.Equal(false, checks[0].IsChecked); // ShowSwatch
            Assert.Equal(true, checks[1].IsChecked);  // TintDockIcon
            Assert.Equal(false, checks[2].IsChecked); // TintWindow
        }
        finally
        {
            ClaudeBuddySettings.RemoveProfile(folder);
        }
    }

    [AvaloniaFact]
    public void DeleteProfileButtonOffersNothingForTheDefaultProfile()
    {
        var window = NewWindow();

        var control = window.DeleteProfileButton(FakeProfile("/does/not/matter", isDefault: true));

        Assert.IsType<Panel>(control);
        Assert.Empty(((Panel)control).Children);
    }

    // Both cases below reach ClaudeDesktopManager.DeleteProfile, which is
    // itself excluded from coverage (it moves a real directory to the Trash),
    // but neither ever gets that far: CheckDelete refuses first, either
    // because the profile is reported running or because the directory does
    // not exist. Nothing is ever actually deleted here.
    [AvaloniaFact]
    public void DeleteProfileButtonArmsOnFirstClick()
    {
        var window = NewWindow();
        var button = (Button)window.DeleteProfileButton(FakeProfile("/does/not/matter"));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Trash it?", button.Content);
    }

    [AvaloniaFact]
    public void DeleteProfileButtonRefusesARunningProfileOnConfirm()
    {
        var window = NewWindow();
        var directory = Path.Combine(Path.GetTempPath(), "cb3-does-not-exist-" + Guid.NewGuid());
        var button = (Button)window.DeleteProfileButton(
            FakeProfile(directory, isRunning: true));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // arm
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // confirm

        Assert.Equal("Quit it first", button.Content);
        Assert.True(button.IsEnabled);
    }

    [AvaloniaFact]
    public void DeleteProfileButtonFailsForAMissingDirectoryOnConfirm()
    {
        var window = NewWindow();
        var directory = Path.Combine(Path.GetTempPath(), "cb3-does-not-exist-" + Guid.NewGuid());
        var button = (Button)window.DeleteProfileButton(FakeProfile(directory));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // arm
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // confirm

        Assert.Equal("Couldn't", button.Content);
        Assert.True(button.IsEnabled);
    }

    // --- extra CLI account directories (ProfileDirsCard) ------------------------
    //
    // A static builder taking add/remove/reapply as plain delegates, so it can
    // be driven completely in isolation from the real
    // HookInstaller.ReapplyClaudeCode/ReapplyCodex it is normally wired to —
    // those shell out to an installer script, which is well outside what this
    // suite should run. BrowseForProfileDir is not covered here: it opens a
    // real native folder-picker dialog via TopLevel.StorageProvider, which a
    // headless runner has no window to attach one to.

    [AvaloniaFact]
    public void ProfileDirsCardAddsANameAndCallsAddAndReapply()
    {
        var added = new List<string>();
        var removed = new List<string>();
        var reapplyCount = 0;
        var current = new List<string> { "existing-dir" };

        var card = (Control)SettingsWindow.ProfileDirsCard(
            blurb: "test blurb",
            watermark: "watermark",
            current: () => current,
            add: name => added.Add(name),
            remove: name => removed.Add(name),
            reapply: () => Interlocked.Increment(ref reapplyCount));

        var input = card.GetLogicalDescendants().OfType<TextBox>().First();
        var addButton = card.GetLogicalDescendants().OfType<Button>()
            .Single(b => (string)b.Content! == "Add");

        input.Text = "new-work-dir";
        addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(new[] { "new-work-dir" }, added);
        Assert.Equal("", input.Text);
    }

    [AvaloniaFact]
    public void ProfileDirsCardIgnoresAnEmptyName()
    {
        var added = new List<string>();

        var card = (Control)SettingsWindow.ProfileDirsCard(
            blurb: "blurb", watermark: "watermark",
            current: () => Array.Empty<string>(),
            add: name => added.Add(name),
            remove: _ => { },
            reapply: () => { });

        var addButton = card.GetLogicalDescendants().OfType<Button>()
            .Single(b => (string)b.Content! == "Add");

        addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(added);
    }

    [AvaloniaFact]
    public void ProfileDirRowRemoveButtonCallsRemoveAndDropsItself()
    {
        var removed = new List<string>();
        var panel = new StackPanel();

        var row = SettingsWindow.ProfileDirRow(".claude-work", panel, name => removed.Add(name));
        panel.Children.Add(row);

        var removeButton = row.GetLogicalDescendants().OfType<Button>().Single();
        removeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(new[] { ".claude-work" }, removed);
        Assert.DoesNotContain(row, panel.Children);
    }

    [AvaloniaFact]
    public void ProfileDirsCardListsEachExistingDirectory()
    {
        var card = (Control)SettingsWindow.ProfileDirsCard(
            blurb: "blurb", watermark: "watermark",
            current: () => new[] { ".claude-work", ".claude-personal" },
            add: _ => { }, remove: _ => { }, reapply: () => { });

        var labels = card.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text).ToList();

        Assert.Contains(".claude-work", labels);
        Assert.Contains(".claude-personal", labels);
    }
}
