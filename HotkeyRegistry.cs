using Avalonia.Input;

namespace ClaudeBuddy
{
    // What a global hotkey does, once pressed. CB-155 asks for one — hide/show
    // orbs — "plus room for a couple more" without saying which, so this is an
    // enum with one member rather than a hardcoded shortcut wired straight into
    // App.axaml.cs: the next hotkey CB-155's follow-up asks for is a new case
    // here, a Default() arm, an entry in GlobalHotkeys.Actions, and nothing
    // else — no new registry, no new settings-parsing code, no new platform
    // plumbing.
    public enum HotkeyAction
    {
        ToggleOrbsVisible
    }

    // One key combination: the modifiers held down plus the key that completes
    // them. A record rather than a tuple so ToString()/Equals() are free and
    // the two platform hooks can compare "what's registered" against "what
    // settings now says" without hand-rolling equality.
    public readonly record struct HotkeyCombo(KeyModifiers Modifiers, Key Key)
    {
        public override string ToString() => HotkeyRegistry.Format(this);
    }

    // Parses, formats and resolves hotkey bindings — pure string/enum logic
    // with no window, no settings read and no OS call anywhere in it, on the
    // same principle as OrbArrangement and OrbGlyph: the *rule* for which key
    // combo an action maps to is cheap to get wrong and cheap to test, so it
    // lives somewhere a unit test can reach it without constructing anything.
    //
    // GlobalHotkeys (the platform-facing half) is what actually reads
    // ClaudeBuddySettings and calls into a native hook; this class never
    // touches either.
    public static class HotkeyRegistry
    {
        // macOS reserves plain Cmd+H to hide the frontmost app, and Cmd+Shift+H
        // is Finder's "move to trash"-adjacent muscle memory for a lot of
        // people, so this leans on Ctrl+Alt rather than Cmd/Option alone.
        // Windows has no standing claim on Ctrl+Alt+H, and testing showed no
        // conflict with the handful of default Windows/Explorer bindings that
        // use H (Ctrl+Shift+H in some browsers is "History", not the same
        // chord). One default across both platforms is simpler to document and
        // simpler for a settings.json override to reason about than a
        // per-platform pair would be, and nothing here rules out moving to
        // per-platform defaults later if a real conflict turns up.
        private static readonly HotkeyCombo ToggleOrbsDefault =
            new(KeyModifiers.Control | KeyModifiers.Alt, Key.H);

        public static HotkeyCombo Default(HotkeyAction action) => action switch
        {
            HotkeyAction.ToggleOrbsVisible => ToggleOrbsDefault,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        // The binding actually in effect: whatever settings.json names for
        // this action, or the built-in default if it names nothing or names
        // something that doesn't parse. A bad string in a hand-edited
        // settings.json is a reason to fall back, not a reason to leave the
        // hotkey unregistered — the toggle other than the file is still worth
        // having.
        public static HotkeyCombo Resolve(HotkeyAction action, string? overrideSpec)
        {
            if (!string.IsNullOrWhiteSpace(overrideSpec) && TryParse(overrideSpec, out var combo))
            {
                return combo;
            }

            return Default(action);
        }

        // "Ctrl+Alt+H", "Cmd+Shift+P", "Control+Alt+H" — case-insensitive,
        // any order, "+"-joined, the last token the key and everything before
        // it a modifier. Cmd/Command/Win/Super/Meta all mean the same
        // KeyModifiers.Meta bit, since which physical key that is differs by
        // platform and the settings.json author shouldn't have to know which
        // OS they're writing for.
        public static bool TryParse(string spec, out HotkeyCombo combo)
        {
            combo = default;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false; // a bare key with no modifier isn't a global hotkey

            var modifiers = KeyModifiers.None;
            Key? key = null;

            foreach (var part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= KeyModifiers.Control;
                        break;
                    case "alt":
                    case "option":
                        modifiers |= KeyModifiers.Alt;
                        break;
                    case "shift":
                        modifiers |= KeyModifiers.Shift;
                        break;
                    case "cmd":
                    case "command":
                    case "win":
                    case "windows":
                    case "super":
                    case "meta":
                        modifiers |= KeyModifiers.Meta;
                        break;
                    default:
                        // Only one non-modifier token is allowed; a second one
                        // ("Ctrl+H+P") is a malformed spec, not "pick the last".
                        if (key is not null) return false;
                        if (!Enum.TryParse<Key>(part, ignoreCase: true, out var parsedKey)) return false;
                        key = parsedKey;
                        break;
                }
            }

            if (key is null || modifiers == KeyModifiers.None) return false;

            combo = new HotkeyCombo(modifiers, key.Value);
            return true;
        }

        // The inverse of TryParse, and the form a settings.json author can
        // paste straight back in — Format(Resolve(a, s)) round-trips for any
        // spec TryParse accepted, which HotkeyRegistryTests checks directly.
        public static string Format(HotkeyCombo combo)
        {
            var pieces = new List<string>(4);
            if (combo.Modifiers.HasFlag(KeyModifiers.Control)) pieces.Add("Ctrl");
            if (combo.Modifiers.HasFlag(KeyModifiers.Alt)) pieces.Add("Alt");
            if (combo.Modifiers.HasFlag(KeyModifiers.Shift)) pieces.Add("Shift");
            if (combo.Modifiers.HasFlag(KeyModifiers.Meta)) pieces.Add("Cmd");
            pieces.Add(combo.Key.ToString());
            return string.Join("+", pieces);
        }
    }
}
