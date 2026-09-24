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
        NewChatWindow.OpenClawAvailabilityForTests = null;
        NewChatWindow.KnownAgentsForTests = null;
        NewChatWindow.StartOpenClawConversationForTests = null;
        NewChatWindow.OrbForTests = null;
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

    private static NewChatWindow NewWindow(
        NewChatCli? prefillCli = null, string? prefillCwd = null, string? prefillAgentId = null)
    {
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })
            ?? throw new MissingMethodException("NewChatWindow", ".ctor(NewChatCli?, string, string)");

        return (NewChatWindow)ctor.Invoke(new object?[] { prefillCli, prefillCwd, prefillAgentId });
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

        // Local rows only — the CLI list always carries a fourth, OpenClaw
        // row alongside these three; OpenClawRowTests below cover it.
        var radios = window.CliList.Children.OfType<RadioButton>()
            .Where(r => r.Tag is NewChatCli).ToList();
        Assert.Equal(3, radios.Count);
        Assert.All(radios, r => Assert.True(r.IsEnabled));

        // No reason to state for a plain enabled row — the text line is
        // absent entirely, not present-and-empty.
        Assert.All(radios, r => Assert.Null(window.ReasonTextFor(r.Tag!)));
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
            .Single(r => r.Tag is NewChatCli cli && cli == NewChatCli.Codex);

        Assert.False(codexRow.IsEnabled);
        Assert.Equal(reason, ToolTip.GetTip(codexRow));

        // Stated on screen, not only in a tooltip nobody can see in a
        // screenshot or without hovering — team-lead's own review flag.
        var reasonText = window.ReasonTextFor(NewChatCli.Codex);
        Assert.NotNull(reasonText);
        Assert.Equal(reason, reasonText!.Text);
        Assert.True(reasonText.IsVisible);
    }

    [AvaloniaFact]
    public void AWarningIsShownForAnEnabledCliMissingItsHook()
    {
        FreshSettings();
        const string warning = "launches, but no orb until hooks are installed — Settings → Hooks";
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode, warning) };

        var window = NewWindow();

        var row = window.CliList.Children.OfType<RadioButton>().Single(r => r.Tag is NewChatCli);
        Assert.Equal(warning, ToolTip.GetTip(row));

        var reasonText = window.ReasonTextFor(NewChatCli.ClaudeCode);
        Assert.NotNull(reasonText);
        Assert.Equal(warning, reasonText!.Text);
        Assert.True(reasonText.IsVisible);
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

    // --- OpenClaw row ---

    private static FakeChatSession OpenClawFake(string sessionId = "openclaw:abc123") =>
        new(null) { SessionId = sessionId, DisplayName = "Fake OpenClaw" };

    [AvaloniaFact]
    public void TheOpenClawRowIsDisabledWithItsReasonWhenNotReady()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.ReplyDisabled;

        var window = NewWindow();

        var row = window.CliList.Children.OfType<RadioButton>().Single(r => (string)r.Content! == "OpenClaw");
        Assert.False(row.IsEnabled);
        Assert.Equal("Turn on \"Allow replying to agents\" in Settings.", ToolTip.GetTip(row));

        var reasonText = window.ReasonTextFor(NewChatWindow.OpenClawTag);
        Assert.NotNull(reasonText);
        Assert.Equal("Turn on \"Allow replying to agents\" in Settings.", reasonText!.Text);
        Assert.True(reasonText.IsVisible);
    }

    [AvaloniaFact]
    public void TheOpenClawRowIsEnabledWhenReady()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;

        var window = NewWindow();

        var row = window.CliList.Children.OfType<RadioButton>().Single(r => (string)r.Content! == "OpenClaw");
        Assert.True(row.IsEnabled);
        Assert.Null(window.ReasonTextFor(NewChatWindow.OpenClawTag));
    }

    // With no local CLI usable at all, OpenClaw (if ready) is the fallback
    // selection — the dialog never opens with nothing chosen when it has at
    // least one usable option.
    [AvaloniaFact]
    public void OpenClawIsSelectedWhenNoLocalCliIsUsable()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => Array.Empty<(string, string)>();

        var window = NewWindow();

        Assert.True(window.OpenClawSelected);
        Assert.Null(window.SelectedCli);
    }

    // A local CLI, when usable, still wins over OpenClaw by default — the
    // fourth row is a fallback, not a preference.
    [AvaloniaFact]
    public void ALocalCliIsPreferredOverOpenClawWhenBothAreUsable()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;

        var window = NewWindow();

        Assert.False(window.OpenClawSelected);
        Assert.Equal(NewChatCli.ClaudeCode, window.SelectedCli);
    }

    [AvaloniaFact]
    public void SelectingOpenClawShowsTheAgentSectionAndHidesTheFolderSection()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };

        var window = NewWindow();
        Assert.True(window.FolderSection.IsVisible);
        Assert.False(window.AgentSection.IsVisible);

        var openClawRow = window.CliList.Children.OfType<RadioButton>().Single(r => (string)r.Content! == "OpenClaw");
        openClawRow.IsChecked = true;

        Assert.False(window.FolderSection.IsVisible);
        Assert.True(window.AgentSection.IsVisible);
        Assert.True(window.OpenClawSelected);
    }

    [AvaloniaFact]
    public void TheAgentComboIsPopulatedFromKnownAgentsSortedAsGiven()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis"), ("id-2", "Bram") };

        var window = NewWindow();

        var items = window.AgentCombo.ItemsSource!.Cast<NewChatWindow.AgentItem>().ToList();
        Assert.Equal(new[] { "Alexis", "Bram" }, items.Select(i => i.Name));
        Assert.Equal("Alexis", ((NewChatWindow.AgentItem)window.AgentCombo.SelectedItem!).Name);
    }

    [AvaloniaFact]
    public void StartOpenClawWithNoAgentChosenSaysSoAndDoesNotCallTheSeam()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => Array.Empty<(string, string)>();
        var called = false;
        NewChatWindow.StartOpenClawConversationForTests = (_, _) =>
        {
            called = true;
            return Task.FromResult<(IRemoteChatSession?, string?)>((null, null));
        };

        var window = NewWindow();
        Click(window.StartButton);

        Assert.False(called);
        Assert.Equal("Choose an agent first.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void StartOpenClawRendersAFailureMessageFromTheFakeSeam()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };
        NewChatWindow.StartOpenClawConversationForTests = (_, _) =>
            Task.FromResult<(IRemoteChatSession?, string?)>((null, "not connected to the gateway"));

        var window = NewWindow();
        Click(window.StartButton);

        Assert.Equal("not connected to the gateway", window.StatusLine.Text);
        Assert.True(window.StartButton.IsEnabled);
    }

    [AvaloniaFact]
    public void ASuccessfulOpenClawStartShowsTheAgentNameAndArmsTheWatch()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };
        var fake = OpenClawFake();
        NewChatWindow.StartOpenClawConversationForTests = (agentId, _) =>
        {
            Assert.Equal("id-1", agentId);
            return Task.FromResult<(IRemoteChatSession?, string?)>((fake, null));
        };

        var window = NewWindow();
        Click(window.StartButton);

        Assert.Equal("Conversation started with Alexis. Waiting for its orb…", window.StatusLine.Text);
    }

    // --- the OpenClaw watch tick, driven directly ---

    [AvaloniaFact]
    public void TheOpenClawWatchOpensAChatPanelOnceTheScanBuildsTheOrb()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        var fake = OpenClawFake("openclaw:watch-match");
        var orb = new OrbWindow(fake.SessionId);
        NewChatWindow.OrbForTests = id => id == fake.SessionId ? orb : null;

        var window = NewWindow();
        window.ArmOpenClawWatchForTests(fake, DateTime.UtcNow.AddSeconds(20));
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Its orb should be on screen.", window.StatusLine.Text);
        Assert.True(ChatPanel.IsOpenFor(fake.SessionId));

        ChatPanel.CloseFor(fake.SessionId);
    }

    [AvaloniaFact]
    public void TheOpenClawWatchSaysSoOnceTheDeadlinePassesWithNoOrb()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OrbForTests = _ => null;
        var fake = OpenClawFake();

        var window = NewWindow();
        window.ArmOpenClawWatchForTests(fake, DateTime.UtcNow.AddSeconds(-1));
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Conversation created; no orb yet — check your gateway connection.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void TheOpenClawWatchKeepsQuietBeforeTheDeadlineWithNoOrb()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OrbForTests = _ => null;
        var fake = OpenClawFake();

        var window = NewWindow();
        window.ArmOpenClawWatchForTests(fake, DateTime.UtcNow.AddSeconds(20));
        window.StatusLine.Text = "Starting…";
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Starting…", window.StatusLine.Text);
    }
}
