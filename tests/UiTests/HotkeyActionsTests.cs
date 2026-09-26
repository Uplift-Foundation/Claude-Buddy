using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// What a global hotkey does once the OS has delivered it — everything after
// the native hook calls back, which is the whole feature except the key
// press itself. No key is ever pressed here and nothing is synthesized:
// HotkeyActions.Dispatch is called directly, which is exactly what both
// native hooks do on a real press (see GlobalHotkeys.Start).
//
// NewChatWindow.Toggle's two OS-facing halves are replaced with
// PresentForTests/BringForwardForTests, so no window is ever Show()n —
// showing one under the headless lifetime is what hung the whole UiTests
// suite on the collapsible-settings branch — and none is ever Close()d,
// for the font-cache reason NewChatWindowTests' header gives. Every test
// clears the singleton instead, through OpenForTests.
//
// [Collection("Settings")]: the window reads ClaudeBuddySettings while being
// built, and OverrideFor reads the two hotkey settings directly.
[Collection("Settings")]
public class HotkeyActionsTests : IDisposable
{
    private readonly List<NewChatWindow> _presented = new();
    private readonly List<NewChatWindow> _broughtForward = new();
    private readonly List<bool> _onUiThread = new();

    public HotkeyActionsTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-hotkey-actions-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();

        // No real CLI probe and no real session scan behind the window.
        NewChatAvailability.CurrentForTests = () => new[]
        {
            new NewChatOption(NewChatCli.ClaudeCode, Enabled: true, Reason: null, Warning: null)
        };
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        NewChatWindow.OpenForTests = null;
        NewChatWindow.PresentForTests = w =>
        {
            _onUiThread.Add(Dispatcher.UIThread.CheckAccess());
            _presented.Add(w);
        };
        NewChatWindow.BringForwardForTests = w =>
        {
            _onUiThread.Add(Dispatcher.UIThread.CheckAccess());
            _broughtForward.Add(w);
        };
    }

    public void Dispose()
    {
        NewChatWindow.OpenForTests = null;
        NewChatWindow.PresentForTests = null;
        NewChatWindow.BringForwardForTests = null;
        NewChatWindow.CurrentStatusesForTests = null;
        NewChatAvailability.CurrentForTests = null;
    }

    [AvaloniaFact]
    public void OpenNewChat_WithNoWindowOpen_OpensOne()
    {
        HotkeyActions.Dispatch(HotkeyAction.OpenNewChat);

        // Posted, not run inside the call — the native callback returns
        // before any window work happens.
        Assert.Null(NewChatWindow.OpenForTests);
        Assert.Empty(_presented);

        Dispatcher.UIThread.RunJobs();

        var opened = Assert.Single(_presented);
        Assert.Same(opened, NewChatWindow.OpenForTests);
        Assert.Empty(_broughtForward);
    }

    [AvaloniaFact]
    public void OpenNewChat_WithAWindowOpen_FocusesItAndNeverOpensASecond()
    {
        HotkeyActions.Dispatch(HotkeyAction.OpenNewChat);
        Dispatcher.UIThread.RunJobs();
        var first = Assert.Single(_presented);

        // Pressed twice more: still the one window, brought forward each
        // time, and never closed — a toggle that closed on the second press
        // would leave OpenForTests null here.
        HotkeyActions.Dispatch(HotkeyAction.OpenNewChat);
        HotkeyActions.Dispatch(HotkeyAction.OpenNewChat);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_presented);
        Assert.Equal(new[] { first, first }, _broughtForward);
        Assert.Same(first, NewChatWindow.OpenForTests);
    }

    [AvaloniaFact]
    public void OpenNewChat_AfterTheWindowCloses_OpensAFreshOne()
    {
        HotkeyActions.Dispatch(HotkeyAction.OpenNewChat);
        Dispatcher.UIThread.RunJobs();
        var first = Assert.Single(_presented);

        // What the Closed handler does to the singleton, called directly:
        // OnClosed itself only fires on a real Close(), which NewChatWindowTests'
        // header says a headless run must not make, and all it adds to
        // Forget() is the macOS activation-policy call.
        NewChatWindow.Forget();
        Assert.Null(NewChatWindow.OpenForTests);

        HotkeyActions.Dispatch(HotkeyAction.OpenNewChat);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, _presented.Count);
        Assert.NotSame(first, _presented[1]);
        Assert.Same(_presented[1], NewChatWindow.OpenForTests);
        Assert.Empty(_broughtForward);
    }

    [AvaloniaFact]
    public void OpenNewChat_WithTheWindowMinimised_RestoresItBeforeBringingItForward()
    {
        HotkeyActions.Dispatch(HotkeyAction.OpenNewChat);
        Dispatcher.UIThread.RunJobs();
        var window = NewChatWindow.OpenForTests!;
        window.WindowState = WindowState.Minimized;

        WindowState? stateWhenBroughtForward = null;
        NewChatWindow.BringForwardForTests = w => stateWhenBroughtForward = w.WindowState;

        HotkeyActions.Dispatch(HotkeyAction.OpenNewChat);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WindowState.Normal, stateWhenBroughtForward);
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [AvaloniaFact]
    public void OpenNewChat_FromABackgroundThread_RunsOnTheUiThread()
    {
        // The case Post exists for: a hook that ever calls back off the UI
        // thread. Dispatch itself runs on a pool thread here; the window work
        // must not.
        Task.Run(() => HotkeyActions.Dispatch(HotkeyAction.OpenNewChat)).Wait();
        Assert.Empty(_presented);

        Dispatcher.UIThread.RunJobs();

        Assert.Single(_presented);
        Assert.Equal(new[] { true }, _onUiThread);
    }

    [AvaloniaFact]
    public void TheTrayItemAndTheHotkeyShareOneWindow()
    {
        // The tray's "New chat…" handler and the hotkey are the same method,
        // so whichever is used first, the other focuses rather than opening.
        TrayController.OpenNewChat();
        HotkeyActions.Dispatch(HotkeyAction.OpenNewChat);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_presented);
        Assert.Single(_broughtForward);
    }

    [AvaloniaFact]
    public void ToggleOrbsVisible_StillDispatchesWithNoSessionManager()
    {
        // The existing action through the new dispatch path. SessionManager
        // is null under the headless lifetime, so the toggle is a no-op —
        // what this proves is that routing it through Post neither throws
        // nor opens New chat by mistake.
        HotkeyActions.Dispatch(HotkeyAction.ToggleOrbsVisible);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(_presented);
        Assert.Null(NewChatWindow.OpenForTests);
    }

    [AvaloniaFact]
    public void HandlerFor_MapsEachActionToItsTrayMethod()
    {
        Assert.Equal(nameof(TrayController.ToggleOrbsVisible),
            HotkeyActions.HandlerFor(HotkeyAction.ToggleOrbsVisible).Method.Name);
        Assert.Equal(nameof(TrayController.OpenNewChat),
            HotkeyActions.HandlerFor(HotkeyAction.OpenNewChat).Method.Name);
    }

    [AvaloniaFact]
    public void OverrideFor_ReadsEachActionsOwnSetting()
    {
        Assert.Null(HotkeyActions.OverrideFor(HotkeyAction.OpenNewChat));

        ClaudeBuddySettings.ToggleOrbsHotkey = "Ctrl+Shift+H";
        ClaudeBuddySettings.NewChatHotkey = "Ctrl+Shift+N";

        Assert.Equal("Ctrl+Shift+H", HotkeyActions.OverrideFor(HotkeyAction.ToggleOrbsVisible));
        Assert.Equal("Ctrl+Shift+N", HotkeyActions.OverrideFor(HotkeyAction.OpenNewChat));

        // What GlobalHotkeys.Start registers, end to end short of the OS.
        Assert.Equal("Ctrl+Shift+N", HotkeyRegistry.Format(
            HotkeyRegistry.Resolve(HotkeyAction.OpenNewChat, HotkeyActions.OverrideFor(HotkeyAction.OpenNewChat))));
    }

    [AvaloniaFact]
    public void AnUnknownActionThrowsRatherThanDoingNothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HotkeyActions.HandlerFor((HotkeyAction)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => HotkeyActions.OverrideFor((HotkeyAction)999));
    }

    // CB-196 AC6 through the live settings: what GlobalHotkeys.Start will
    // register, and the one hotkeys.log line a lost collision leaves.
    [AvaloniaFact]
    public void Plan_NewChatOnTheTogglesChord_FallsBackAndLogsOneLine()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "cb-hotkey-plan-log-" + Guid.NewGuid());
        using var scope = CrashLog.ScopeForTests(logDir);
        HotkeyLog.ResetForTests();
        try
        {
            ClaudeBuddySettings.NewChatHotkey = "Alt+Ctrl+H";

            // Twice, as a relaunch-free re-plan would: still one line.
            HotkeyActions.Plan();
            var plan = HotkeyActions.Plan();

            Assert.Equal("Ctrl+Alt+H", HotkeyRegistry.Format(
                plan.Single(b => b.Action == HotkeyAction.ToggleOrbsVisible).Combo!.Value));
            Assert.Equal("Ctrl+Alt+N", HotkeyRegistry.Format(
                plan.Single(b => b.Action == HotkeyAction.OpenNewChat).Combo!.Value));
            var line = Assert.Single(File.ReadAllLines(HotkeyLog.Path_));
            Assert.Contains("OpenNewChat: Ctrl+Alt+H is already ToggleOrbsVisible's hotkey", line);
        }
        finally
        {
            HotkeyLog.ResetForTests();
        }
    }

    [AvaloniaFact]
    public void Plan_WithNoCollision_WritesNoLog()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "cb-hotkey-plan-log-" + Guid.NewGuid());
        using var scope = CrashLog.ScopeForTests(logDir);
        HotkeyLog.ResetForTests();

        var plan = HotkeyActions.Plan();

        Assert.All(plan, b => Assert.NotNull(b.Combo));
        Assert.False(File.Exists(HotkeyLog.Path_));
    }

    // RegisterAll against a fake hook: what reaches the OS, and the
    // hotkeys.log line for each registration the OS refused.
    private sealed class FakeHook : IGlobalHotkeyHook
    {
        private readonly Func<HotkeyAction, bool> _accepts;
        private readonly bool _throws;

        public FakeHook(Func<HotkeyAction, bool>? accepts = null, bool throws = false)
        {
            _accepts = accepts ?? (_ => true);
            _throws = throws;
        }

        public List<(HotkeyAction Action, HotkeyCombo Combo, Action Callback)> Calls { get; } = new();

        public bool Register(HotkeyAction action, HotkeyCombo combo, Action callback)
        {
            Calls.Add((action, combo, callback));
            if (_throws) throw new InvalidOperationException("a hook that breaks its contract");
            return _accepts(action);
        }

        public void Dispose() { }
    }

    private static string[] LogLines() =>
        File.Exists(HotkeyLog.Path_) ? File.ReadAllLines(HotkeyLog.Path_) : Array.Empty<string>();

    private static IDisposable FreshLog()
    {
        var scope = CrashLog.ScopeForTests(Path.Combine(Path.GetTempPath(), "cb-hotkey-register-log-" + Guid.NewGuid()));
        HotkeyLog.ResetForTests();
        return scope;
    }

    [AvaloniaFact]
    public void RegisterAll_AllAccepted_RegistersBothDefaultsAndLogsNothing()
    {
        using var log = FreshLog();
        var hook = new FakeHook();

        HotkeyActions.RegisterAll(hook);

        Assert.Equal(new[] { "Ctrl+Alt+H", "Ctrl+Alt+N" },
            hook.Calls.Select(c => HotkeyRegistry.Format(c.Combo)).ToArray());
        Assert.Empty(LogLines());
    }

    [AvaloniaFact]
    public void RegisterAll_TheCallbackItHandsTheHookDispatchesItsAction()
    {
        using var log = FreshLog();
        var hook = new FakeHook();
        HotkeyActions.RegisterAll(hook);

        // What a real press does: the hook invokes the callback it was given.
        hook.Calls.Single(c => c.Action == HotkeyAction.OpenNewChat).Callback();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_presented);
    }

    [AvaloniaFact]
    public void RegisterAll_NewChatRefused_LogsOneLineNamingItAndTheToggleStillRegisters()
    {
        using var log = FreshLog();
        var hook = new FakeHook(accepts: a => a != HotkeyAction.OpenNewChat);

        HotkeyActions.RegisterAll(hook);

        var line = Assert.Single(LogLines());
        Assert.Contains("OpenNewChat", line);
        Assert.Contains("Ctrl+Alt+N", line);
        Assert.Contains(hook.Calls, c => c.Action == HotkeyAction.ToggleOrbsVisible
                                         && HotkeyRegistry.Format(c.Combo) == "Ctrl+Alt+H");
    }

    [AvaloniaFact]
    public void RegisterAll_BothRefused_LogsOneLineEach_AndASecondStartAddsNone()
    {
        using var log = FreshLog();

        HotkeyActions.RegisterAll(new FakeHook(accepts: _ => false));
        HotkeyActions.RegisterAll(new FakeHook(accepts: _ => false));

        var lines = LogLines();
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.Contains("ToggleOrbsVisible") && l.Contains("Ctrl+Alt+H"));
        Assert.Contains(lines, l => l.Contains("OpenNewChat") && l.Contains("Ctrl+Alt+N"));
    }

    [AvaloniaFact]
    public void RegisterAll_AHookThatThrows_IsLoggedAsRefusedAndTheLoopCarriesOn()
    {
        using var log = FreshLog();
        var hook = new FakeHook(throws: true);

        HotkeyActions.RegisterAll(hook);   // no exception escapes

        Assert.Equal(2, hook.Calls.Count);
        Assert.Equal(2, LogLines().Length);
    }

    [AvaloniaFact]
    public void RegisterAll_ACollisionNoteAndARefusal_WriteExactlyTwoLines_AndTheUnplannedActionIsNeverRegistered()
    {
        using var log = FreshLog();
        ClaudeBuddySettings.ToggleOrbsHotkey = "Ctrl+Alt+N";
        var hook = new FakeHook(accepts: _ => false);

        HotkeyActions.RegisterAll(hook);

        Assert.DoesNotContain(hook.Calls, c => c.Action == HotkeyAction.OpenNewChat);
        var lines = LogLines();
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.Contains("OpenNewChat: no hotkey registered"));
        Assert.Contains(lines, l => l.Contains(HotkeyActions.RefusedNote(HotkeyAction.ToggleOrbsVisible,
            HotkeyRegistry.Default(HotkeyAction.OpenNewChat))));
    }

    [AvaloniaFact]
    public void RegisterAll_WithNoHook_DoesNothing()
    {
        using var log = FreshLog();

        HotkeyActions.RegisterAll(null);

        Assert.Empty(LogLines());
    }
}
