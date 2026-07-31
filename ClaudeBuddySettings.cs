using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeBuddy
{
    // The app's first persistent state.
    //
    // Everything else here is deliberately transient — Claude Code sessions come
    // from status files in the temp directory, and Claude Desktop profiles are
    // whatever directories exist on disk. That stays true: this file only holds
    // *preferences*, never a copy of discovered state. Delete it and the app still
    // works, with derived colours and folder names.
    //
    // Profiles are keyed by folder name rather than by path, so moving the
    // profile root (the CLAUDE_BUDDY_PROFILE_ROOT override) keeps your settings,
    // and renaming a folder deliberately starts fresh.
    internal static class ClaudeBuddySettings
    {
        private const int CurrentVersion = 1;

        private static readonly object Gate = new();
        private static Model _model = new();
        private static bool _loaded;

        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // %APPDATA%\ClaudeBuddy on Windows, ~/Library/Application Support/ClaudeBuddy
        // on macOS. SpecialFolder.ApplicationData resolves to both, so this is one
        // expression rather than a platform branch.
        public static string Directory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClaudeBuddy");

        public static string Path_ => Path.Combine(Directory, "settings.json");

        // ---- shape ----------------------------------------------------------

        // Where a dragged orb sits, in the same coordinate space as
        // Window.Position: physical pixels on the virtual desktop. Storing DIPs
        // instead would survive a scaling change, but there's no coherent
        // desktop-wide DIP space on a mixed-DPI setup, so raw pixels it is —
        // SessionManager clamps a restored point back onto a real screen.
        internal sealed record OrbPlacement(int X, int Y);

        internal sealed class ProfileSettings
        {
            public string? Name { get; set; }          // display name; null = folder name
            public string? Color { get; set; }         // palette name; null = derived from folder
            public bool ShowSwatch { get; set; } = true;
            public bool TintDockIcon { get; set; } = true;
            public bool TintWindow { get; set; } = true;
        }

        private sealed class Model
        {
            public bool ShowOrbs { get; set; } = true;
            public bool TintActiveWindow { get; set; } = true;
            public Dictionary<string, ProfileSettings> Profiles { get; init; } =
                new(StringComparer.Ordinal);

            // Keyed by the session's cwd — see SessionManager.PositionKeyFor.
            // Case-insensitive because Windows paths are, and the same repo
            // shouldn't get two entries over a capitalization difference.
            public Dictionary<string, OrbPlacement> OrbPositions { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        // ---- app-wide -------------------------------------------------------

        public static bool ShowOrbs
        {
            get { Load(); lock (Gate) return _model.ShowOrbs; }
            set { Load(); lock (Gate) _model.ShowOrbs = value; Save(); }
        }

        public static bool TintActiveWindow
        {
            get { Load(); lock (Gate) return _model.TintActiveWindow; }
            set { Load(); lock (Gate) _model.TintActiveWindow = value; Save(); }
        }

        // ---- orb positions --------------------------------------------------

        public static OrbPlacement? OrbPositionFor(string key)
        {
            Load();
            lock (Gate) return _model.OrbPositions.GetValueOrDefault(key);
        }

        public static void SetOrbPosition(string key, int x, int y)
        {
            Load();
            lock (Gate)
            {
                var existing = _model.OrbPositions.GetValueOrDefault(key);
                if (existing is not null && existing.X == x && existing.Y == y) return;
                _model.OrbPositions[key] = new OrbPlacement(x, y);
            }

            Save();
        }

        public static void ClearOrbPosition(string key)
        {
            Load();
            lock (Gate)
            {
                if (!_model.OrbPositions.Remove(key)) return;
            }

            Save();
        }

        // ---- per profile ----------------------------------------------------

        // A copy, so callers can't mutate the store without going through Update.
        public static ProfileSettings For(string folder)
        {
            Load();
            lock (Gate)
            {
                if (!_model.Profiles.TryGetValue(folder, out var found)) return new ProfileSettings();

                return new ProfileSettings
                {
                    Name = found.Name,
                    Color = found.Color,
                    ShowSwatch = found.ShowSwatch,
                    TintDockIcon = found.TintDockIcon,
                    TintWindow = found.TintWindow
                };
            }
        }

        public static void Update(string folder, Action<ProfileSettings> change)
        {
            Load();

            lock (Gate)
            {
                if (!_model.Profiles.TryGetValue(folder, out var entry))
                {
                    entry = new ProfileSettings();
                    _model.Profiles[folder] = entry;
                }

                change(entry);
            }

            Save();
        }

        // ---- storage --------------------------------------------------------

        private static void Load()
        {
            lock (Gate)
            {
                if (_loaded) return;
                _loaded = true;

                try
                {
                    if (!File.Exists(Path_)) return;

                    var root = JsonNode.Parse(File.ReadAllText(Path_)) as JsonObject;
                    if (root is null) return;

                    var model = new Model
                    {
                        ShowOrbs = root["showOrbs"]?.GetValue<bool>() ?? true,
                        TintActiveWindow = root["tintActiveWindow"]?.GetValue<bool>() ?? true
                    };

                    if (root["profiles"] is JsonObject profiles)
                    {
                        foreach (var (folder, node) in profiles)
                        {
                            if (node is not JsonObject entry) continue;

                            model.Profiles[folder] = new ProfileSettings
                            {
                                Name = entry["name"]?.GetValue<string>(),
                                Color = entry["color"]?.GetValue<string>(),
                                ShowSwatch = entry["showSwatch"]?.GetValue<bool>() ?? true,
                                TintDockIcon = entry["tintDockIcon"]?.GetValue<bool>() ?? true,
                                TintWindow = entry["tintWindow"]?.GetValue<bool>() ?? true
                            };
                        }
                    }

                    if (root["orbPositions"] is JsonObject positions)
                    {
                        foreach (var (key, node) in positions)
                        {
                            if (node is not JsonObject entry) continue;

                            var x = entry["x"]?.GetValue<int>();
                            var y = entry["y"]?.GetValue<int>();
                            if (x is null || y is null) continue;

                            model.OrbPositions[key] = new OrbPlacement(x.Value, y.Value);
                        }
                    }

                    _model = model;
                }
                catch
                {
                    // A corrupt or half-written settings file must never stop the
                    // app starting; defaults are always a valid answer. The next
                    // Save() overwrites it.
                    _model = new Model();
                }
            }
        }

        private static void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                JsonObject root;
                lock (Gate)
                {
                    var profiles = new JsonObject();
                    foreach (var (folder, entry) in _model.Profiles)
                    {
                        profiles[folder] = new JsonObject
                        {
                            ["name"] = entry.Name,
                            ["color"] = entry.Color,
                            ["showSwatch"] = entry.ShowSwatch,
                            ["tintDockIcon"] = entry.TintDockIcon,
                            ["tintWindow"] = entry.TintWindow
                        };
                    }

                    var positions = new JsonObject();
                    foreach (var (key, placement) in _model.OrbPositions)
                    {
                        positions[key] = new JsonObject
                        {
                            ["x"] = placement.X,
                            ["y"] = placement.Y
                        };
                    }

                    root = new JsonObject
                    {
                        ["version"] = CurrentVersion,
                        ["showOrbs"] = _model.ShowOrbs,
                        ["tintActiveWindow"] = _model.TintActiveWindow,
                        ["profiles"] = profiles,
                        ["orbPositions"] = positions
                    };
                }

                // Write beside the target and rename over it, so a crash midway
                // can't leave an unparseable settings file. UTF-8 without a BOM:
                // System.Text.Json treats a leading BOM as an invalid start of
                // value, and this file is read back by JsonNode.
                var temporary = Path_ + ".tmp";
                File.WriteAllText(
                    temporary,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                File.Move(temporary, Path_, overwrite: true);
            }
            catch
            {
                // Losing a preference is not worth taking the app down for.
            }
        }
    }
}
