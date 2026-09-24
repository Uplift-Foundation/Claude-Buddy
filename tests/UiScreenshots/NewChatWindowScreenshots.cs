using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-168's "New chat…" dialog, captured through real Skia the same way every
// other window in this project is — see SettingsWindowScreenshots' own
// header for why this suite exists at all (ScreenshotHelper.Capture never
// runs through the null renderer tests/UiTests uses).
//
// Same reflection-constructor seam as NewChatWindowTests, and for the same
// reason: NewChatWindow.Toggle() makes real OS calls this project has no
// business making headless, so every scenario here reaches the private
// constructor directly and is never closed (SettingsWindowSmokeTest's own
// comment on the FontManager corruption a stray Close() caused once).
//
// [Collection("Settings")]: the window reads ClaudeBuddySettings
// (NewChatRecentFolders, NewChatLastCli, the OpenClaw settings the fourth
// row's availability depends on), which is process-wide — see
// SettingsCollection.cs.
[Collection("Settings")]
public class NewChatWindowScreenshots
{
    private static NewChatWindow NewWindow(
        NewChatCli? prefillCli = null, string? prefillCwd = null, string? prefillAgentId = null)
    {
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })
            ?? throw new MissingMethodException("NewChatWindow", ".ctor(NewChatCli?, string, string)");

        return (NewChatWindow)ctor.Invoke(new object?[] { prefillCli, prefillCwd, prefillAgentId });
    }

    private static NewChatOption Enabled(NewChatCli cli, string? warning = null) =>
        new(cli, Enabled: true, Reason: null, Warning: warning);

    private static NewChatOption Disabled(NewChatCli cli, string reason) =>
        new(cli, Enabled: false, Reason: reason, Warning: null);

    // The default state: every local CLI usable, OpenClaw present but
    // disabled (no gateway configured is the state a fresh install actually
    // has), Claude Code preselected as the first enabled row.
    [AvaloniaFact]
    public void DefaultState()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Enabled(NewChatCli.Codex),
            Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();

        ScreenshotHelper.Capture(window, "new-chat-window-default.png");

        ClearSeams();
    }

    // One local CLI disabled with its reason — Codex not found — beside the
    // other two still usable.
    [AvaloniaFact]
    public void OneCliDisabled()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Disabled(NewChatCli.Codex, "Codex not found on PATH or in its usual install locations."),
            Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();

        ScreenshotHelper.Capture(window, "new-chat-window-cli-disabled.png");

        ClearSeams();
    }

    // The inline failure a launch attempt renders — the status line is the
    // whole surface a failed Start has, so this is what "it didn't work"
    // looks like to whoever is looking at the dialog when it happens.
    [AvaloniaFact]
    public void LaunchError()
    {
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatLauncher.LaunchForTests = (_, _) => new LaunchResult(
            LaunchOutcome.NotFound, "Claude Code not found on PATH or in its usual install locations.");

        var window = NewWindow();
        window.Show();
        ScreenshotHelper.Flush();

        window.StartButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-launch-error.png");

        ClearSeams();
    }

    // The OpenClaw row selected, Ready, with agents loaded — the agent
    // section replacing the folder section is CB-168's own decision record
    // ("OpenClaw selected: an agent picker replaces the folder field"), and
    // this is the only scenario that shows it actually happening.
    [AvaloniaFact]
    public void OpenClawRowEnabledWithAgents()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[]
        {
            ("agent-1", "Alexis"), ("agent-2", "Amber"), ("agent-3", "Hope")
        };
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();

        var openClawRow = window.CliList.Children.OfType<RadioButton>()
            .Single(r => (string)r.Content! == "OpenClaw");
        window.Show();
        ScreenshotHelper.Flush();
        openClawRow.IsChecked = true;
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-openclaw-enabled.png");

        ClearSeams();
    }

    // OpenClaw disabled — no gateway configured, the state a fresh install
    // actually has. The row itself (greyed, unselectable) is what this
    // captures; the reason text is a tooltip, which only a real hover shows
    // and which NewChatWindowTests already asserts directly
    // (TheOpenClawRowIsDisabledWithItsReasonWhenNotReady) — a static capture
    // can't demonstrate a hover state any more usefully than describing it.
    [AvaloniaFact]
    public void OpenClawRowDisabledWithReason()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.ReplyDisabled;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();

        ScreenshotHelper.Capture(window, "new-chat-window-openclaw-disabled.png");

        ClearSeams();
    }

    private static void ClearSeams()
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
}
