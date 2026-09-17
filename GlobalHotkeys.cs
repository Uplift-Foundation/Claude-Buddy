using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // Wires HotkeyRegistry's pure bindings to real actions and a real
    // platform hook. This is the one class allowed to know that
    // ToggleOrbsVisible means TrayController.ToggleOrbsVisible, and the one
    // class that picks which native implementation to start — everything
    // upstream of it (HotkeyRegistry) and downstream of it (the two hooks)
    // stays ignorant of the other.
    //
    // Excluded from coverage as a whole: every path through Start() ends in
    // a real OS-level key registration (Carbon on macOS, RegisterHotKey on
    // Windows), which is exactly the class of thing this repo's CLAUDE.md
    // says can't be exercised from a headless runner — there is no window
    // server session in CI to press a key into. HotkeyRegistryTests covers
    // the part that can be: which combo an action resolves to, and what
    // string round-trips through it.
    [ExcludeFromCodeCoverage]
    internal static class GlobalHotkeys
    {
        // The one entry each HotkeyAction dispatches to. A second action
        // joins this dictionary the same way ToggleOrbsVisible did — no
        // change to either native hook, which only ever see "this id fired".
        private static readonly Dictionary<HotkeyAction, Action> Actions = new()
        {
            [HotkeyAction.ToggleOrbsVisible] = TrayController.ToggleOrbsVisible
        };

        private static IGlobalHotkeyHook? _hook;

        // Called once from App.axaml.cs after the tray and SessionManager are
        // up, so ToggleOrbsVisible has something to toggle by the time a key
        // could possibly be pressed. A no-op on any platform without a real
        // hook yet, rather than throwing — a hotkey nobody can press is a far
        // smaller problem than an app that won't start on a platform this
        // hasn't been ported to.
        public static void Start()
        {
            _hook = CreateHook();
            if (_hook is null) return;

            foreach (var action in Actions.Keys)
            {
                var combo = HotkeyRegistry.Resolve(action, OverrideFor(action));
                _hook.Register(action, combo, () => Invoke(action));
            }
        }

        public static void Stop() => _hook?.Dispose();

        private static string? OverrideFor(HotkeyAction action) => action switch
        {
            HotkeyAction.ToggleOrbsVisible => ClaudeBuddySettings.ToggleOrbsHotkey,
            _ => null
        };

        private static void Invoke(HotkeyAction action)
        {
            if (Actions.TryGetValue(action, out var handler)) handler();
        }

        private static IGlobalHotkeyHook? CreateHook()
        {
            if (OperatingSystem.IsMacOS()) return new MacOSGlobalHotkeyHook();
            if (OperatingSystem.IsWindows()) return new WindowsGlobalHotkeyHook();
            return null;
        }
    }

    // What GlobalHotkeys needs from a platform: register one combo against
    // one callback, and tear everything down. Small on purpose — the two
    // implementations differ completely in how they get a callback to fire
    // (Carbon's application event target vs. a hidden window's WndProc), and
    // this interface is only the sliver both can honestly implement.
    internal interface IGlobalHotkeyHook : IDisposable
    {
        void Register(HotkeyAction action, HotkeyCombo combo, Action callback);
    }
}
