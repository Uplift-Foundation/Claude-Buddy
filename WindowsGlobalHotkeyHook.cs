using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia.Input;

namespace ClaudeBuddy
{
    // Registers global hotkeys on Windows via user32's RegisterHotKey, which
    // delivers WM_HOTKEY to whatever window registered it. Avalonia doesn't
    // expose a raw HWND/WndProc seam the way WPF's HwndSource does, so this
    // creates one directly with CreateWindowEx — the same "hand-rolled P/Invoke
    // rather than a framework seam that doesn't exist" shape as
    // WindowsForegroundWindow.cs elsewhere in this file set. The window is
    // never shown; it exists purely to have a message queue WM_HOTKEY can be
    // posted to, pumped by the same thread's Win32 message loop Avalonia's own
    // Win32 backend is already running (RegisterHotKey's docs are explicit
    // that GetMessage/DispatchMessage on the *creating* thread is what
    // delivers WM_HOTKEY, and that delivery isn't scoped to whichever
    // framework happens to be pumping — only to which thread created the
    // window and which thread is pumping it).
    //
    // Excluded from coverage via GlobalHotkeys' class-level attribute, for the
    // reason recorded there.
    [ExcludeFromCodeCoverage]
    internal sealed class WindowsGlobalHotkeyHook : IGlobalHotkeyHook
    {
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private const uint ModNoRepeat = 0x4000; // don't refire while the key is held down

        private const uint WmHotkey = 0x0312;
        private const uint WsExToolWindow = 0x00000080;
        private const int GwlExStyle = -20;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassW(ref WndClass lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClass
        {
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        // Standard virtual-key codes for the alphanumeric row (VK_A..VK_Z,
        // VK_0..VK_9 are literally 'A'..'Z' and '0'..'9' on Windows) — the
        // only keys HotkeyRegistry's default and the settings override are
        // expected to name. An unmapped Key fails this one Register() call
        // rather than the whole feature, same as the macOS hook.
        private static readonly Dictionary<Key, uint> VirtualKeyCodes = BuildVirtualKeyCodes();

        private static Dictionary<Key, uint> BuildVirtualKeyCodes()
        {
            var map = new Dictionary<Key, uint>();
            for (var k = Key.A; k <= Key.Z; k++) map[k] = (uint)('A' + (k - Key.A));
            for (var k = Key.D0; k <= Key.D9; k++) map[k] = (uint)('0' + (k - Key.D0));
            return map;
        }

        private readonly Dictionary<int, Action> _callbacksById = new();
        private WndProcDelegate? _wndProc; // kept alive: GC would otherwise collect the delegate user32 still holds a pointer to
        private IntPtr _hwnd;
        private int _nextId;

        public void Register(HotkeyAction action, HotkeyCombo combo, Action callback)
        {
            if (!VirtualKeyCodes.TryGetValue(combo.Key, out var vk)) return;

            EnsureWindow();

            var id = ++_nextId;
            _callbacksById[id] = callback;

            RegisterHotKey(_hwnd, id, ToWinModifiers(combo.Modifiers) | ModNoRepeat, vk);
        }

        private void EnsureWindow()
        {
            if (_hwnd != IntPtr.Zero) return;

            _wndProc = WndProc;
            var className = "ClaudeBuddyGlobalHotkeyWindow";
            var wndClass = new WndClass
            {
                lpfnWndProc = _wndProc,
                hInstance = GetModuleHandleW(null),
                lpszClassName = className
            };
            RegisterClassW(ref wndClass);

            // Never shown (no WS_VISIBLE), and marked a tool window so it
            // never surfaces in Alt-Tab or the taskbar even if that ever
            // changed — this window exists only to own a message queue.
            _hwnd = CreateWindowExW(
                WsExToolWindow, className, "", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmHotkey && _callbacksById.TryGetValue(wParam.ToInt32(), out var callback))
            {
                callback();
                return IntPtr.Zero;
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static uint ToWinModifiers(KeyModifiers modifiers)
        {
            uint result = 0;
            if (modifiers.HasFlag(KeyModifiers.Control)) result |= ModControl;
            if (modifiers.HasFlag(KeyModifiers.Alt)) result |= ModAlt;
            if (modifiers.HasFlag(KeyModifiers.Shift)) result |= ModShift;
            if (modifiers.HasFlag(KeyModifiers.Meta)) result |= ModWin;
            return result;
        }

        public void Dispose()
        {
            if (_hwnd == IntPtr.Zero) return;

            foreach (var id in _callbacksById.Keys) UnregisterHotKey(_hwnd, id);
            _callbacksById.Clear();

            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
