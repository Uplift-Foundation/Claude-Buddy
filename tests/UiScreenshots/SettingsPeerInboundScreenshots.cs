using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-267's Settings surfaces, rendered through real Skia: the one-time card
// with its switch, and two config roots side by side with one of them mid
// confirmation. The roots are an in-memory world — nothing here reads or writes
// a real ~/.claude.
[Collection("Settings")]
public class SettingsPeerInboundScreenshots : IDisposable
{
    private readonly Func<IReadOnlyList<string>> _roots = SettingsWindow.InboundRoots;
    private readonly Func<string, InboundSetting> _state = SettingsWindow.InboundState;
    private readonly Func<string, bool, PeerInboundWrite> _apply = SettingsWindow.InboundApply;
    private readonly Func<string, string> _display = SettingsWindow.InboundDisplayPath;

    private readonly Dictionary<string, InboundSetting> _world = new();

    public SettingsPeerInboundScreenshots()
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.ClaudeCodeChatEnabled = true;
        OrbweaverSettings.ClaudeCodeReplyEnabled = true;
        OrbweaverSettings.PeerInboundNoticeSeen = false;

        SettingsWindow.InboundRoots = () => _world.Keys.ToList();
        SettingsWindow.InboundState = root => _world[root];
        SettingsWindow.InboundDisplayPath = path => path.Replace("/Users/example", "~");
        SettingsWindow.InboundApply = (_, _) => new PeerInboundWrite(PeerInboundWriteKind.Unchanged, null);
    }

    public void Dispose()
    {
        SettingsWindow.InboundRoots = _roots;
        SettingsWindow.InboundState = _state;
        SettingsWindow.InboundApply = _apply;
        SettingsWindow.InboundDisplayPath = _display;

        OrbweaverSettings.ClaudeCodeReplyEnabled = false;
        OrbweaverSettings.PeerInboundNoticeSeen = false;
    }

    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    // The group is captured as its own control, not the window: it sits below
    // the fold of a scrolling page, the same reason the Claude Desktop capture
    // next door climbs to it.
    private static Control Section(SettingsWindow window) => window.Sections["claude-code"];

    [AvaloniaFact]
    public void FirstRunCardAtTheTopOfTheClaudeCodeSection()
    {
        _world["/Users/example/.claude"] = InboundSetting.NotAccepting;
        var window = NewWindow();
        window.Show();
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureControl(Section(window), "settings-peer-inbound-first-run-card.png");
    }

    [AvaloniaFact]
    public void TwoRootRowsWithOneAskingForConfirmation()
    {
        OrbweaverSettings.PeerInboundNoticeSeen = true;
        _world["/Users/example/.claude"] = InboundSetting.Accepting;
        _world["/Users/example/.claude-board"] = InboundSetting.NotAccepting;
        var window = NewWindow();
        window.OnInboundToggled("/Users/example/.claude-board", true);
        window.Show();
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureControl(Section(window), "settings-peer-inbound-two-roots.png");
    }
}
