using Avalonia.Media;

namespace ClaudeBuddy
{
    // The three colours that say what a session is *doing*, and the one place
    // that answers "what colour is this state".
    //
    // Three things ask now, which is why this exists at all: the orb's fill and
    // glow (OrbWindow), the menu-bar icon (TrayController, which re-tints its
    // artwork at runtime to match) and the settings window's colour pickers.
    // They used to be static readonly fields on OrbWindow, hand-copied a second
    // time into tools/make-icons.py. Those fields survive here as the
    // *defaults*; what make-icons.py bakes into Assets/tray-*.png is now the
    // default-coloured artwork, which the tray uses directly when a colour has
    // never been changed and as an alpha mask when it has.
    //
    // A projection over ClaudeBuddySettings rather than a cache of it. The
    // settings model is already in memory once Load() has run, so reading
    // through costs a property get, and there is no second copy to fall out of
    // step. Nothing here notifies, either: whoever writes a colour calls
    // SessionManager.ReapplyStateColors, the same shape as the profile rows
    // calling ClaudeDesktopManager.KickRefresh.
    //
    // Deliberately *not* here: the 14 /color accents, which are Claude Code's
    // own palette rather than ours to retune (see OrbWindow.AgentColors), and
    // the "slate" in ClaudeDesktopColors, which shares the idle hex by
    // coincidence of taste but identifies a Claude Desktop profile rather than
    // a state — and is stored as a palette name, not a hex.
    internal static class OrbColors
    {
        public static readonly Color DefaultIdle = Color.Parse("#5B7A94");       // calm slate blue
        public static readonly Color DefaultGenerating = Color.Parse("#8B6FD1"); // violet
        public static readonly Color DefaultWaiting = Color.Parse("#E8983B");    // amber

        public static Color Idle => Resolve(ClaudeBuddySettings.IdleColor, DefaultIdle);

        public static Color Generating =>
            Resolve(ClaudeBuddySettings.GeneratingColor, DefaultGenerating);

        public static Color Waiting => Resolve(ClaudeBuddySettings.WaitingColor, DefaultWaiting);

        // State is a bare string off a hook script, so the default arm carries
        // real weight: "ended" (which deletes the status file and never reaches
        // a visual) and anything unrecognised read as idle rather than throwing.
        // There is still no enum for these; keeping the vocabulary in one file
        // is the closest thing, and it's why OrbWindow.ApplyState asks here
        // instead of switching on state a second time.
        public static Color For(string state) => state switch
        {
            "waiting" => Waiting,
            "generating" => Generating,
            _ => Idle
        };

        public static Color DefaultFor(string state) => state switch
        {
            "waiting" => DefaultWaiting,
            "generating" => DefaultGenerating,
            _ => DefaultIdle
        };

        // Whether this state still looks the way it shipped. TrayController uses
        // it to skip re-tinting entirely, because the baked PNG for a default
        // state already *is* that colour.
        public static bool IsDefault(string state) => For(state).Equals(DefaultFor(state));

        // True when nothing has been chosen at all. Tested against the stored
        // strings rather than the resolved colours, because "never set" is what
        // the Reset button undoes — picking the shipped blue by hand is not the
        // same thing, and shouldn't grey the button out.
        public static bool AllDefault =>
            ClaudeBuddySettings.IdleColor is null
            && ClaudeBuddySettings.GeneratingColor is null
            && ClaudeBuddySettings.WaitingColor is null;

        // The only writer. Keeps the state -> setting mapping beside For() above
        // rather than spreading a third switch through the settings window.
        // A null hex means "back to the built-in colour", which is not the same
        // as writing today's default — see ClaudeBuddySettings.IdleColor.
        public static void Set(string state, string? hex)
        {
            switch (state)
            {
                case "waiting": ClaudeBuddySettings.WaitingColor = hex; break;
                case "generating": ClaudeBuddySettings.GeneratingColor = hex; break;
                default: ClaudeBuddySettings.IdleColor = hex; break;
            }
        }

        // What goes in settings.json: six digits, no alpha. Alpha is not the
        // user's to set here — the glow derives its own 150/95/0 over the chosen
        // RGB (OrbWindow.GlowStops) and the tray icon's alpha channel *is* the
        // shape of its ring — so storing eight digits would only ever be a way
        // to make the orb look like a rendering bug.
        public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        // Anything unparseable is the default, silently. The settings window can
        // only ever write ToHex output, so this fires for a hand-edited file, and
        // a bad colour there should cost you that colour rather than the app.
        // Alpha in a stored value is dropped rather than honoured, for the reason
        // above.
        private static Color Resolve(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;

            return Color.TryParse(hex, out var parsed)
                ? Color.FromRgb(parsed.R, parsed.G, parsed.B)
                : fallback;
        }
    }
}
