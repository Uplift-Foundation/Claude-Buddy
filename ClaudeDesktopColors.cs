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

        public static Color For(string folderName, bool isDefault)
        {
            if (isDefault) return DefaultColor;
            return Palette[(int)(Fnv1a(folderName) % (uint)Palette.Length)];
        }

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
