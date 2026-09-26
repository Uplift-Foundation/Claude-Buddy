using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia.Input;

namespace ClaudeBuddy
{
    // Registers global hotkeys on macOS via Carbon's RegisterEventHotKey.
    //
    // Deliberately Carbon and not an NSEvent global monitor or a CGEventTap:
    // those two both require the user to grant Accessibility (or, on newer
    // macOS, Input Monitoring) permission before they see a single keystroke,
    // and that grant is exactly the kind of silent, easy-to-miss consent gate
    // this codebase already has scar tissue about (see CLAUDE.md's Local
    // Network section). RegisterEventHotKey is different in kind, not just
    // API surface: it asks the window server to notify *this* process only
    // when *this exact* combination is pressed, rather than handing the
    // process a feed of everyone's keystrokes to filter — so the OS never
    // needs to ask permission for it. It's also not a deprecated-and-broken
    // relic despite HIToolbox's Carbon heritage: this is the same mechanism
    // Rectangle, Alfred and most other menu-bar hotkey apps use today because
    // no AppKit replacement for *global* (not in-app) hotkeys has ever
    // shipped.
    //
    // Excluded from coverage via GlobalHotkeys' class-level attribute, for
    // the reason recorded there: every path through here ends at a real
    // window-server call, which a headless CI runner has no session to make.
    [ExcludeFromCodeCoverage]
    internal sealed class MacOSGlobalHotkeyHook : IGlobalHotkeyHook
    {
        // Carbon modifier bits (Events.h). Distinct from Avalonia's
        // KeyModifiers, which is why this hook translates rather than
        // reusing one enum for both — the two frameworks don't agree on bit
        // positions and never will.
        private const uint CmdKey = 0x0100;
        private const uint ShiftKey = 0x0200;
        private const uint OptionKey = 0x0800;
        private const uint ControlKey = 0x1000;

        private const uint EventClassKeyboard = 0x6b657962; // 'keyb'
        private const uint EventHotKeyPressed = 5;
        private const uint TypeEventHotKeyId = 0x686b6964; // 'hkid'
        private const uint ParamDirectObject = 0x2d2d2d2d; // '----'
        private const uint NoErr = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct EventHotKeyId
        {
            public uint Signature;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTypeSpec
        {
            public uint EventClass;
            public uint EventKind;
        }

        private delegate int EventHandlerProc(IntPtr inHandlerCallRef, IntPtr inEvent, IntPtr inUserData);

        [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
        private static extern IntPtr GetApplicationEventTarget();

        [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
        private static extern int InstallEventHandler(
            IntPtr inTarget, EventHandlerProc inHandler, int inNumTypes,
            EventTypeSpec[] inList, IntPtr inUserData, out IntPtr outHandlerRef);

        [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
        private static extern int RegisterEventHotKey(
            uint inHotKeyCode, uint inHotKeyModifiers, EventHotKeyId inHotKeyId,
            IntPtr inTarget, uint inOptions, out IntPtr outRef);

        [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
        private static extern int UnregisterEventHotKey(IntPtr inHotKey);

        [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
        private static extern int GetEventParameter(
            IntPtr inEvent, uint inName, uint inDesiredType, IntPtr outActualType,
            int inBufferSize, IntPtr outActualSize, out EventHotKeyId outData);

        // kVK_ANSI_* from Carbon's Events.h — the physical-key codes
        // RegisterEventHotKey wants, which do not match ASCII or Avalonia's
        // Key enum. Only the alphanumeric row is mapped: that covers every
        // sane hotkey letter/digit, and HotkeyRegistry.TryParse already
        // accepts a much wider Key range than either platform hook can act
        // on, so an unmapped key fails registration for that one binding
        // rather than the whole feature — see Register's null check below.
        private static readonly Dictionary<Key, uint> VirtualKeyCodes = new()
        {
            [Key.A] = 0x00, [Key.B] = 0x0B, [Key.C] = 0x08, [Key.D] = 0x02,
            [Key.E] = 0x0E, [Key.F] = 0x03, [Key.G] = 0x05, [Key.H] = 0x04,
            [Key.I] = 0x22, [Key.J] = 0x26, [Key.K] = 0x28, [Key.L] = 0x25,
            [Key.M] = 0x2E, [Key.N] = 0x2D, [Key.O] = 0x1F, [Key.P] = 0x23,
            [Key.Q] = 0x0C, [Key.R] = 0x0F, [Key.S] = 0x01, [Key.T] = 0x11,
            [Key.U] = 0x20, [Key.V] = 0x09, [Key.W] = 0x0D, [Key.X] = 0x07,
            [Key.Y] = 0x10, [Key.Z] = 0x06,
            [Key.D0] = 0x1D, [Key.D1] = 0x12, [Key.D2] = 0x13, [Key.D3] = 0x14,
            [Key.D4] = 0x15, [Key.D5] = 0x17, [Key.D6] = 0x16, [Key.D7] = 0x1A,
            [Key.D8] = 0x1C, [Key.D9] = 0x19,
        };

        // 'CBHK' — a signature this app made up, distinguishing its hotkeys
        // from any other Carbon-registered ones sharing the same event
        // target. Ids are assigned sequentially as actions are registered.
        private const uint Signature = 0x43424849;

        private readonly Dictionary<uint, Action> _callbacksById = new();
        private readonly List<IntPtr> _registrations = new();
        private EventHandlerProc? _handler; // kept alive: GC would otherwise collect the delegate Carbon still holds a pointer to
        private IntPtr _handlerRef;
        private uint _nextId;
        private bool _installed;

        public bool Register(HotkeyAction action, HotkeyCombo combo, Action callback)
        {
            if (!VirtualKeyCodes.TryGetValue(combo.Key, out var keyCode)) return false;

            EnsureHandlerInstalled();

            var id = ++_nextId;
            _callbacksById[id] = callback;

            var hotKeyId = new EventHotKeyId { Signature = Signature, Id = id };
            var carbonModifiers = ToCarbonModifiers(combo.Modifiers);

            var result = RegisterEventHotKey(
                keyCode, carbonModifiers, hotKeyId, GetApplicationEventTarget(), 0, out var hotKeyRef);

            if (result == NoErr && hotKeyRef != IntPtr.Zero)
            {
                _registrations.Add(hotKeyRef);
                return true;
            }

            // Refused — most often eventHotKeyExistsErr, another app already
            // holding the chord. The caller writes that to hotkeys.log.
            _callbacksById.Remove(id);
            return false;
        }

        private void EnsureHandlerInstalled()
        {
            if (_installed) return;
            _installed = true;

            _handler = HandleHotKeyEvent;
            var types = new[] { new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed } };
            InstallEventHandler(GetApplicationEventTarget(), _handler, types.Length, types, IntPtr.Zero, out _handlerRef);
        }

        private int HandleHotKeyEvent(IntPtr inHandlerCallRef, IntPtr inEvent, IntPtr inUserData)
        {
            var result = GetEventParameter(
                inEvent, ParamDirectObject, TypeEventHotKeyId, IntPtr.Zero,
                Marshal.SizeOf<EventHotKeyId>(), IntPtr.Zero, out var hotKeyId);

            if (result == NoErr && hotKeyId.Signature == Signature
                && _callbacksById.TryGetValue(hotKeyId.Id, out var callback))
            {
                callback();
            }

            return (int)NoErr; // consumes the event rather than propagating it further
        }

        private static uint ToCarbonModifiers(KeyModifiers modifiers)
        {
            uint result = 0;
            if (modifiers.HasFlag(KeyModifiers.Control)) result |= ControlKey;
            if (modifiers.HasFlag(KeyModifiers.Alt)) result |= OptionKey;
            if (modifiers.HasFlag(KeyModifiers.Shift)) result |= ShiftKey;
            if (modifiers.HasFlag(KeyModifiers.Meta)) result |= CmdKey;
            return result;
        }

        public void Dispose()
        {
            foreach (var reg in _registrations) UnregisterEventHotKey(reg);
            _registrations.Clear();
            _callbacksById.Clear();
        }
    }
}
