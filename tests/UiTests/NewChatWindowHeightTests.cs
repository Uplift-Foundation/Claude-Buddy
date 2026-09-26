using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-207: the New chat dialog must never change height once it is on screen.
//
// On a real Mac, a SizeToContent window whose height changed while it was not
// presenting came back drawing its old-sized surface stretched over the new
// frame — scaled up, Start cut off, the layout no longer under the pixels.
// The fix reserves every slot whose occupant changes after show (see
// NewChatWindow.ReservedSlot), so the content — which is all SizeToContent
// follows — is the same height in every state.
//
// What this suite can and cannot prove. It pins the mechanism: the content's
// measured height is identical across every CLI switch and every status
// message, and each ghost measures exactly like the section it stands in for.
// It cannot prove the presentation fault itself is gone — headless Avalonia
// has no window server, never presents a frame, and cannot stretch a stale
// surface. That half was checked on a real Mac by reading the frame back out
// of CGWindowList and diffing window captures before and after the fix.
//
// Measured rather than shown: showing a NewChatWindow under the headless
// lifetime is what hung the whole suite once (HotkeyActionsTests' header), and
// the content's DesiredSize is exactly the input SizeToContent sizes from.
[Collection("Settings")]
public class NewChatWindowHeightTests : IDisposable
{
    public void Dispose()
    {
        NewChatAvailability.CurrentForTests = null;
        NewChatWindow.OpenClawAvailabilityForTests = null;
        NewChatWindow.KnownAgentsForTests = null;
        NewChatWindow.CurrentStatusesForTests = null;
    }

    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-newchat-height-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    private static NewChatOption Enabled(NewChatCli cli) => new(cli, Enabled: true, Reason: null, Warning: null);

    private static NewChatWindow NewWindow()
    {
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })
            ?? throw new MissingMethodException("NewChatWindow", ".ctor(NewChatCli?, string, string)");

        return (NewChatWindow)ctor.Invoke(new object?[] { null, null, null });
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // The window's own fixed width, so text wraps where it does on screen.
    private static double ContentHeight(NewChatWindow window)
    {
        Flush();
        var content = (Control)window.Content!;
        content.InvalidateMeasure();
        content.Measure(new Size(420, double.PositiveInfinity));
        return content.DesiredSize.Height;
    }

    private static void Select(NewChatWindow window, object tag) =>
        window.CliList.Children.OfType<RadioButton>().Single(r => Equals(r.Tag, tag)).IsChecked = true;

    // Everything on: all three local CLIs, an extra account, and a usable
    // OpenClaw — the configuration with the most slots that can change.
    private static NewChatWindow EverythingAvailable()
    {
        FreshSettings();
        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };
        return NewWindow();
    }

    [AvaloniaFact]
    public void TheHeightHoldsAcrossEveryCliSwitch()
    {
        var window = EverythingAvailable();
        var opened = ContentHeight(window);

        // A real measurement, not two zeros agreeing: three CLI rows, the
        // account and folder sections and Start cannot fit in less.
        Assert.True(opened > 200, $"content measured {opened}");

        foreach (var tag in new object[]
                 {
                     NewChatCli.Codex, NewChatCli.ClaudeCode, NewChatCli.Grok, NewChatWindow.OpenClawTag,
                     NewChatCli.ClaudeCode, NewChatWindow.OpenClawTag, NewChatCli.Codex
                 })
        {
            Select(window, tag);
            Assert.Equal(opened, ContentHeight(window));
        }
    }

    [AvaloniaFact]
    public void TheAccountPickerStillHidesForCodexWhileItsSpaceIsKept()
    {
        var window = EverythingAvailable();

        Select(window, NewChatCli.Codex);
        Flush();

        // CB-201's rule is untouched — the picker itself is gone...
        Assert.False(window.AccountSection.IsVisible);
        // ...and only the ghost holds its place.
        Assert.True(window.AccountGhost.IsVisible);
    }

    [AvaloniaFact]
    public void TheHeightHoldsWhateverTheStatusLineSays()
    {
        var window = EverythingAvailable();
        var opened = ContentHeight(window);

        foreach (var text in new[]
                 {
                     "Starting…",
                     "Claude Code started in /Users/someone/Source/a/rather/deep/folder/that/wraps/onto/a/second/line/and/then/a/third/one/and/keeps/going/long/past/that.",
                     "Terminal opened; no orb yet — the CLI's hook may not be installed / trusted.",
                     ""
                 })
        {
            window.StatusLine.Text = text;
            Assert.Equal(opened, ContentHeight(window));
        }
    }

    [AvaloniaFact]
    public void TheStatusLineCarriesItsFullTextAsATooltip()
    {
        var window = EverythingAvailable();

        window.StatusLine.Text = "Couldn't open a terminal for Claude Code.";

        Assert.Equal("Couldn't open a terminal for Claude Code.", ToolTip.GetTip(window.StatusLine));
    }

    [AvaloniaFact]
    public void EachGhostMeasuresExactlyLikeTheSectionItStandsIn()
    {
        var window = EverythingAvailable();

        // Claude Code, at open: the account and folder sections are real, so
        // each can be set against its ghost directly.
        ContentHeight(window);
        Assert.Equal(window.AccountSection.DesiredSize.Height, window.AccountGhost.DesiredSize.Height);
        Assert.Equal(window.FolderSection.DesiredSize.Height, window.FolderGhost.DesiredSize.Height);

        Select(window, NewChatWindow.OpenClawTag);
        ContentHeight(window);
        Assert.Equal(window.AgentSection.DesiredSize.Height, window.AgentGhost.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void AGhostCanBeNeitherClickedNorFocusedNorAnnounced()
    {
        var window = EverythingAvailable();
        Flush();

        foreach (var ghost in new[] { window.AccountGhost, window.FolderGhost, window.AgentGhost })
        {
            Assert.Equal(0, ghost.Opacity);
            Assert.False(ghost.IsHitTestVisible);
            Assert.False(ghost.IsEnabled);
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(ghost));
            // Tab can land on nothing inside it: every control that could take
            // focus is both disabled and unfocusable in its own right.
            var focusables = ghost.GetLogicalDescendants().OfType<TemplatedControl>().ToList();
            Assert.NotEmpty(focusables);
            Assert.All(focusables, c =>
            {
                Assert.False(c.Focusable);
                Assert.False(c.IsEnabled);
            });
        }
    }

    [AvaloniaFact]
    public void NoSpaceIsReservedForAPickerThatCanNeverAppear()
    {
        // No extra accounts: CB-201's "empty list means no picker", and the
        // dialog must read exactly as it did before CB-201 — no gap, no ghost,
        // no StackPanel spacing collected by an empty slot.
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;

        var window = NewWindow();
        Flush();

        Assert.False(window.AccountGhost.IsVisible);
        Assert.False(((Control)window.AccountSection.Parent!).IsVisible);
        Assert.False(window.AgentGhost.IsVisible);
        Assert.True(window.FolderGhost.IsVisible);
    }

    [AvaloniaFact]
    public void AnAccountListWithoutAUsableClaudeCodeReservesNothing()
    {
        FreshSettings();
        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            new NewChatOption(NewChatCli.ClaudeCode, Enabled: false, Reason: "not installed", Warning: null),
            Enabled(NewChatCli.Codex)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;

        var window = NewWindow();
        Flush();

        Assert.False(window.AccountGhost.IsVisible);
    }

    [AvaloniaFact]
    public void OnlyOpenClawUsableReservesNoFolderSpace()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };

        var window = NewWindow();
        Flush();

        Assert.False(window.FolderGhost.IsVisible);
        Assert.True(window.AgentGhost.IsVisible);
        Assert.True(window.OpenClawSelected);
    }
}
