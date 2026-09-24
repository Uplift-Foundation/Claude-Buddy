using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-168's "New chat…" dialog.
//
// Toggle() does real OS-facing work (MacOSActivation, Show, Activate) the
// same way SettingsWindow.Toggle does — see SettingsWindowSmokeTest's own
// header — so this suite reaches the private constructor directly via
// reflection instead, and never Close()s the windows it builds (the same
// font-cache corruption SettingsWindowSmokeTest documents).
//
// [Collection("Settings")]: this window reads ClaudeBuddySettings
// (NewChatRecentFolders, NewChatLastCli), which is process-wide.
[Collection("Settings")]
public class NewChatWindowTests : IDisposable
{
    public void Dispose()
    {
        NewChatAvailability.CurrentForTests = null;
        NewChatLauncher.LaunchForTests = null;
        NewChatWindow.CurrentStatusesForTests = null;
        NewChatWindow.ChooseFolderForTests = null;
    }

    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-newchat-window-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    private static NewChatOption Enabled(NewChatCli cli, string? warning = null) =>
        new(cli, Enabled: true, Reason: null, Warning: warning);

    private static NewChatOption Disabled(NewChatCli cli, string reason) =>
        new(cli, Enabled: false, Reason: reason, Warning: null);

    private static NewChatWindow NewWindow(NewChatCli? prefillCli = null, string? prefillCwd = null)
    {
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string) })
            ?? throw new MissingMethodException("NewChatWindow", ".ctor(NewChatCli?, string)");

        return (NewChatWindow)ctor.Invoke(new object?[] { prefillCli, prefillCwd });
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Flush();
    }

    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        FreshSettings();

        var window = NewWindow();

        Assert.NotNull(window);
    }

    [AvaloniaFact]
    public void EveryEnabledCliOffersARow()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Enabled(NewChatCli.Codex),
            Enabled(NewChatCli.Grok)
        };

        var window = NewWindow();

        var radios = window.CliList.Children.OfType<RadioButton>().ToList();
        Assert.Equal(3, radios.Count);
        Assert.All(radios, r => Assert.True(r.IsEnabled));
    }

    [AvaloniaFact]
    public void ADisabledCliShowsItsReasonAndCannotBeSelected()
    {
        FreshSettings();
        const string reason = "Codex not found on PATH or in its usual install locations.";
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Disabled(NewChatCli.Codex, reason),
            Enabled(NewChatCli.Grok)
        };

        var window = NewWindow();

        var codexRow = window.CliList.Children.OfType<RadioButton>()
            .Single(r => (NewChatCli)r.Tag! == NewChatCli.Codex);

        Assert.False(codexRow.IsEnabled);
        Assert.Equal(reason, ToolTip.GetTip(codexRow));
    }

    [AvaloniaFact]
    public void AWarningIsShownForAnEnabledCliMissingItsHook()
    {
        FreshSettings();
        const string warning = "launches, but no orb until hooks are installed — Settings → Hooks";
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode, warning) };

        var window = NewWindow();

        var row = window.CliList.Children.OfType<RadioButton>().Single();
        Assert.Equal(warning, ToolTip.GetTip(row));
    }

    [AvaloniaFact]
    public void ThePrefilledCliIsSelectedOverTheLastChoiceAndTheFirstEnabledRow()
    {
        FreshSettings();
        ClaudeBuddySettings.SetNewChatLastCli("Codex");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Enabled(NewChatCli.Codex),
            Enabled(NewChatCli.Grok)
        };

        var window = NewWindow(prefillCli: NewChatCli.Grok);

        Assert.Equal(NewChatCli.Grok, window.SelectedCli);
    }

    [AvaloniaFact]
    public void WithNoPrefillTheLastChoiceIsSelected()
    {
        FreshSettings();
        ClaudeBuddySettings.SetNewChatLastCli("Codex");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Enabled(NewChatCli.Codex)
        };

        var window = NewWindow();

        Assert.Equal(NewChatCli.Codex, window.SelectedCli);
    }

    [AvaloniaFact]
    public void APrefilledFolderIsAddedToTheComboAndSelected()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };

        var window = NewWindow(prefillCwd: "/repo/prefilled");

        Assert.Equal("/repo/prefilled", window.FolderCombo.SelectedItem);
    }

    [AvaloniaFact]
    public void BrowseGoesThroughTheSeamAndSelectsThePickedFolder()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.ChooseFolderForTests = () => Task.FromResult<string?>("/repo/browsed");

        var window = NewWindow();
        Click(window.BrowseButton);

        Assert.Equal("/repo/browsed", window.FolderCombo.SelectedItem);
    }

    [AvaloniaFact]
    public void ACancelledBrowseLeavesTheSelectionAlone()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.ChooseFolderForTests = () => Task.FromResult<string?>(null);

        var window = NewWindow(prefillCwd: "/repo/prefilled");
        Click(window.BrowseButton);

        Assert.Equal("/repo/prefilled", window.FolderCombo.SelectedItem);
    }

    [AvaloniaFact]
    public void StartWithNoCliChosenSaysSoAndDoesNotLaunch()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        var launched = false;
        NewChatLauncher.LaunchForTests = (_, _) => { launched = true; return new LaunchResult(LaunchOutcome.Launched, "x"); };

        var window = NewWindow();
        Click(window.StartButton);

        Assert.False(launched);
        Assert.Equal("Choose a CLI first.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void StartRendersAFailureMessageFromTheFakeLauncher()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatLauncher.LaunchForTests = (_, _) =>
            new LaunchResult(LaunchOutcome.NotFound, "Claude Code not found on PATH or in its usual install locations.");

        var window = NewWindow();
        Click(window.StartButton);

        Assert.Equal(
            "Claude Code not found on PATH or in its usual install locations.", window.StatusLine.Text);
        Assert.True(window.StartButton.IsEnabled);
    }

    [AvaloniaFact]
    public void ASuccessfulStartSavesTheLastCliAndRecentFolder()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.Codex) };
        NewChatLauncher.LaunchForTests = (_, _) => new LaunchResult(LaunchOutcome.Launched, "Codex started in /repo/one.");

        var window = NewWindow(prefillCli: NewChatCli.Codex, prefillCwd: "/repo/one");
        Click(window.StartButton);

        Assert.Equal("Codex", ClaudeBuddySettings.NewChatLastCli);
        Assert.Contains("/repo/one", ClaudeBuddySettings.NewChatRecentFolders);
    }

    // --- the watch tick, driven directly per LocalCliChatSessionTests' own
    // pattern for a debounce timer: no real ~20s wall-clock wait needed. ---

    [AvaloniaFact]
    public void TheWatchReportsTheOrbOnceAMatchingSessionAppears()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["new-id"] = new() { Cwd = "/repo/one", Source = SessionSource.ClaudeCode }
        };

        var window = NewWindow();
        window.ArmWatchForTests(
            new HashSet<string>(), NewChatCli.ClaudeCode, "/repo/one", DateTime.UtcNow.AddSeconds(20));
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Claude Code is running — its orb should be on screen.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void TheWatchSaysSoOnceTheDeadlinePasses()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();
        window.ArmWatchForTests(
            new HashSet<string>(), NewChatCli.ClaudeCode, "/repo/one", DateTime.UtcNow.AddSeconds(-1));
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal(
            "Terminal opened; no orb yet — the CLI's hook may not be installed / trusted.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void TheWatchKeepsQuietBeforeTheDeadlineWithNoMatch()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();
        window.ArmWatchForTests(
            new HashSet<string>(), NewChatCli.ClaudeCode, "/repo/one", DateTime.UtcNow.AddSeconds(20));
        window.StatusLine.Text = "Starting…";
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Starting…", window.StatusLine.Text);
    }

    // --- ShouldClose: pure, never calls the real Close() ---

    [Fact]
    public void EscapeCloses()
    {
        Assert.True(NewChatWindow.ShouldClose(Key.Escape, KeyModifiers.None));
    }

    [Fact]
    public void CmdWCloses()
    {
        Assert.True(NewChatWindow.ShouldClose(Key.W, KeyModifiers.Meta));
    }

    [Fact]
    public void PlainWDoesNotClose()
    {
        Assert.False(NewChatWindow.ShouldClose(Key.W, KeyModifiers.None));
    }

    [Fact]
    public void AnUnrelatedKeyDoesNotClose()
    {
        Assert.False(NewChatWindow.ShouldClose(Key.A, KeyModifiers.None));
    }
}
