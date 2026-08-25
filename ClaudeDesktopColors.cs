using System.Diagnostics.CodeAnalysis;
using Avalonia.Media;

namespace ClaudeBuddy
{
    // One stable colour per Claude Desktop profile, shared by every surface that
    // shows it: the tray swatch, the tinted Dock icon of its cloned bundle, and
    // the window overlay. They have to agree or the colour stops meaning
    // anything.
    //
    // Derived from the folder name rather than stored, which keeps the feature's
    // "profiles are whatever is on disk" property — no config file, and renaming
    // a folder simply re-rolls its colour.
    internal static class ClaudeDesktopColors
    {
        // Deliberately a copy of OrbWindow.AgentColors' values rather than a
        // reference to them: this feature stays deletable in one revert, and a
        // shared palette would mean editing the session-monitoring side. Keep
        // the two in sync by hand if the orb palette changes.
        private static readonly Color[] Palette =
        {
            Color.Parse("#00AF5F"), // green
            Color.Parse("#5F87D7"), // blue
            Color.Parse("#D787AF"), // magenta
            Color.Parse("#00AFAF"), // teal
            Color.Parse("#D7875F"), // orange
            Color.Parse("#875FD7"), // purple
            Color.Parse("#D7AF5F"), // yellow
            Color.Parse("#D75F5F")  // red
        };

        // The original profile keeps the app's own idle slate, so "Default" is
        // recognisable at a glance and never collides with a created profile.
        private static readonly Color DefaultColor = Color.Parse("#5B7A94");

        // Names are what settings.json stores — a palette name survives a
        // palette retune, where a raw hex would silently drift out of the set.
        private static readonly (string Name, Color Color)[] Named =
        {
            ("green", Color.Parse("#00AF5F")),
            ("blue", Color.Parse("#5F87D7")),
            ("magenta", Color.Parse("#D787AF")),
            ("teal", Color.Parse("#00AFAF")),
            ("orange", Color.Parse("#D7875F")),
            ("purple", Color.Parse("#875FD7")),
            ("yellow", Color.Parse("#D7AF5F")),
            ("red", Color.Parse("#D75F5F")),
            ("slate", Color.Parse("#5B7A94"))
        };

        public static IReadOnlyList<string> Names { get; } =
            Named.Select(entry => entry.Name).ToArray();

        public static Color ByName(string colourName)
        {
            foreach (var (name, color) in Named)
            {
                if (string.Equals(name, colourName, StringComparison.OrdinalIgnoreCase)) return color;
            }

            return DefaultColor;
        }

        public static Color For(string folderName, bool isDefault)
        {
            // An explicit choice in settings beats both the derived colour and the
            // Default profile's reserved slate.
            var chosen = ClaudeBuddySettings.For(folderName).Color;
            if (chosen is { Length: > 0 })
            {
                foreach (var (name, color) in Named)
                {
                    if (string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase)) return color;
                }
            }

            if (isDefault) return DefaultColor;
            return Palette[(int)(Fnv1a(folderName) % (uint)Palette.Length)];
        }

        // The palette name currently in effect, for showing a selection in the UI.
        //
        // Excluded from coverage for its last line only, which cannot run: every
        // colour For() can return is in Named, so the loop always finds one. The
        // three sets involved are Palette, DefaultColor and an explicit choice
        // matched out of Named itself, and EveryColourAProfileCanGetHasAName
        // asserts the first two — so the fallback is proved dead rather than
        // assumed to be. Kept because "no name for this colour" is a shape the
        // compiler insists on having an answer for, and returning the app's own
        // idle slate is a better one than throwing at a settings window.
        [ExcludeFromCodeCoverage]
        public static string NameFor(string folderName, bool isDefault)
        {
            var target = For(folderName, isDefault);
            foreach (var (name, color) in Named)
            {
                if (color == target) return name;
            }

            return "slate";
        }

        // The invariant NameFor's fallback rests on, asserted rather than
        // assumed — see EveryColourAProfileCanGetHasAName.
        internal static IReadOnlyList<Color> EveryColourAProfileCanGet =>
            Palette.Append(DefaultColor).ToArray();

        internal static IReadOnlyList<Color> NamedColours =>
            Named.Select(entry => entry.Color).ToArray();

        public static string HexFor(string folderName, bool isDefault)
        {
            var c = For(folderName, isDefault);
            return $"{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        // FNV-1a, not string.GetHashCode: .NET randomises string hashing per
        // process, which would give a profile a different colour on every launch.
        private static uint Fnv1a(string value)
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return hash;
        }
    }
}
