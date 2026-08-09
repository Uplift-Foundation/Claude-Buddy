using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Avalonia.Threading;

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

        // JsonNode.ToJsonString(options) needs a TypeInfoResolver on the
        // options it's given — it doesn't fall back to
        // JsonSerializerOptions.Default's own resolver just because that one
        // has one. In a normal `dotnet build` this project's own
        // reflection-based JsonSerializerOptions() default silently covers
        // that gap, but SelfContained + PublishSingleFile (this app's actual
        // shipped Windows build) implicitly trims, which turns the missing
        // resolver into a hard InvalidOperationException on every single
        // Save() call. Confirmed by publishing a minimal repro with this
        // project's exact trimming settings and running it for real: it
        // threw with a bare `new JsonSerializerOptions { WriteIndented =
        // true }, and stopped throwing only once TypeInfoResolver was set
        // explicitly like this — the values here are plain bool/string
        // primitives, so reflection over them is always safe regardless of
        // trimming. This is why settings.json silently never got written on
        // a real machine while every local build/run looked fine.
        private static readonly JsonSerializerOptions SaveOptions = new()
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

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

        // How long an orb outlives its session's last hook write, in minutes,
        // with 0 meaning "never prune". Five matches what the app did when this
        // was a hard-coded constant.
        public const int DefaultOrbLifetimeMinutes = 5;
        public const int OrbLifetimeForever = 0;

        public const string DefaultArrangeShape = "heart";
        public const double DefaultArrangeSpacing = 0.85;

        private sealed class Model
        {
            public bool ShowOrbs { get; set; } = true;
            public bool TintActiveWindow { get; set; } = true;
            public int OrbLifetimeMinutes { get; set; } = DefaultOrbLifetimeMinutes;

            // Off by default: one letter is what every existing orb already
            // looks like, and changing that for everyone on upgrade would be
            // a cosmetic surprise nobody asked for.
            public bool TwoLetterGlyphs { get; set; }

            // Off by default: turning this on is what triggers the one-time
            // Whisper model download (a few hundred MB), so it must be an
            // explicit opt-in rather than something a fresh install just has.
            public bool VoiceInputEnabled { get; set; }

            // Which macOS `say` / Windows SAPI voice to use for speaking the
            // latest turn. Null means the default ("Samantha" on macOS).
            public string? SpeakVoice { get; set; }

            // "#RRGGBB", or null for "use the built-in colour". Null rather than
            // a copy of the default so that retuning a shipped colour later still
            // reaches everyone who never touched it — see the properties below.
            public string? IdleColor { get; set; }
            public string? GeneratingColor { get; set; }
            public string? WaitingColor { get; set; }

            public Dictionary<string, ProfileSettings> Profiles { get; init; } =
                new(StringComparer.Ordinal);

            // Keyed by the session's cwd — see SessionManager.PositionKeyFor.
            // Case-insensitive because Windows paths are, and the same repo
            // shouldn't get two entries over a capitalization difference.
            public Dictionary<string, OrbPlacement> OrbPositions { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);

            // Distinct from Profiles above (Claude Desktop, the Electron app):
            // these are Claude Code *CLI* config directory names — e.g.
            // ".claude-work" for a CLAUDE_CONFIG_DIR=~/.claude-work alias
            // managing a second account — that the user has explicitly opted
            // into also wiring Claude Buddy hooks for, beyond the default
            // ~/.claude. install-windows-hooks.ps1 reads this same list (via
            // this file) as its default -ProfileDir/-WslProfileDir value, so
            // configuring it here is also what "configure via the installer"
            // means in practice: a repair/reinstall re-reads whatever's saved
            // here rather than needing its own separate wizard UI for it.
            public List<string> ClaudeCodeProfileDirs { get; init; } = new();

            // Auto-organize: which shape and how much space between orbs.
            public string ArrangeShape { get; set; } = DefaultArrangeShape;
            public double ArrangeSpacing { get; set; } = DefaultArrangeSpacing;
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

        // Minutes an orb sticks around after its session stops reporting;
        // OrbLifetimeForever (0) keeps it until the session's status file goes
        // away. Anything negative would silently mean "prune immediately", which
        // no setting should be able to say, so it reads as forever too.
        public static int OrbLifetimeMinutes
        {
            get
            {
                Load();
                lock (Gate) return _model.OrbLifetimeMinutes < 0
                    ? OrbLifetimeForever
                    : _model.OrbLifetimeMinutes;
            }
            set
            {
                Load();
                lock (Gate) _model.OrbLifetimeMinutes = value < 0 ? OrbLifetimeForever : value;
                Save();
            }
        }

        // Gates both the mic flyout on the orb and the one-time Whisper model
        // download — see VoiceRecorder/SpeechTranscriber. Nothing about speech
        // capture or transcription runs while this is false.
        public static bool VoiceInputEnabled
        {
            get { Load(); lock (Gate) return _model.VoiceInputEnabled; }
            set { Load(); lock (Gate) _model.VoiceInputEnabled = value; Save(); }
        }

        public static string SpeakVoice
        {
            get { Load(); lock (Gate) return _model.SpeakVoice ?? TextToSpeech.DefaultVoice; }
            set { Load(); lock (Gate) _model.SpeakVoice = value; Save(); }
        }

        // One letter (the default) or two initials on every orb's glyph —
        // see OrbWindow.GlyphFor. Purely cosmetic, so there's no lifecycle
        // to guard the way VoiceInputEnabled has; SessionManager.ReapplyGlyphs
        // is what makes an already-open orb notice a flip without waiting
        // for its next hook update.
        public static bool TwoLetterGlyphs
        {
            get { Load(); lock (Gate) return _model.TwoLetterGlyphs; }
            set { Load(); lock (Gate) _model.TwoLetterGlyphs = value; Save(); }
        }

        // ---- auto-organize ----------------------------------------------------

        public static string ArrangeShape
        {
            get { Load(); lock (Gate) return _model.ArrangeShape; }
            set { Load(); lock (Gate) _model.ArrangeShape = value; Save(); }
        }

        public static double ArrangeSpacing
        {
            get { Load(); lock (Gate) return _model.ArrangeSpacing; }
            set { Load(); lock (Gate) _model.ArrangeSpacing = value; Save(); }
        }

        // ---- orb state colours ----------------------------------------------

        // "#RRGGBB", with null meaning the built-in default. OrbColors owns the
        // three defaults and the parsing, which keeps this file free of Avalonia
        // types the way the rest of it is — and means a hand-edited garbage hex
        // costs one colour rather than a Load().
        //
        // Null matters. Storing the *effective* colour instead would bake today's
        // slate blue into every settings.json the first time someone opened the
        // window, and a future retune of the shipped palette would then reach
        // nobody. It's also what the Reset button writes.
        //
        // These three defer their file write — see SaveSoon.
        public static string? IdleColor
        {
            get { Load(); lock (Gate) return _model.IdleColor; }
            set { Load(); lock (Gate) _model.IdleColor = value; SaveSoon(); }
        }

        public static string? GeneratingColor
        {
            get { Load(); lock (Gate) return _model.GeneratingColor; }
            set { Load(); lock (Gate) _model.GeneratingColor = value; SaveSoon(); }
        }

        public static string? WaitingColor
        {
            get { Load(); lock (Gate) return _model.WaitingColor; }
            set { Load(); lock (Gate) _model.WaitingColor = value; SaveSoon(); }
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

        // ---- extra Claude Code (CLI) profile directories ---------------------

        // A copy, so callers can't mutate the store without going through
        // Add/Remove — same guard rail as ProfileSettings.For below.
        public static IReadOnlyList<string> ClaudeCodeProfileDirs
        {
            get { Load(); lock (Gate) return _model.ClaudeCodeProfileDirs.ToList(); }
        }

        public static void AddClaudeCodeProfileDir(string dirName)
        {
            Load();
            lock (Gate)
            {
                if (!_model.ClaudeCodeProfileDirs.Contains(dirName, StringComparer.Ordinal))
                {
                    _model.ClaudeCodeProfileDirs.Add(dirName);
                }
            }
            Save();
        }

        public static void RemoveClaudeCodeProfileDir(string dirName)
        {
            Load();
            lock (Gate) { _model.ClaudeCodeProfileDirs.Remove(dirName); }
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
                        TintActiveWindow = root["tintActiveWindow"]?.GetValue<bool>() ?? true,
                        OrbLifetimeMinutes =
                            root["orbLifetimeMinutes"]?.GetValue<int>() ?? DefaultOrbLifetimeMinutes,
                        VoiceInputEnabled = root["voiceInputEnabled"]?.GetValue<bool>() ?? false,
                        SpeakVoice = root["speakVoice"]?.GetValue<string>(),
                        TwoLetterGlyphs = root["twoLetterGlyphs"]?.GetValue<bool>() ?? false,
                        ArrangeShape = root["arrangeShape"]?.GetValue<string>() ?? DefaultArrangeShape,
                        ArrangeSpacing = root["arrangeSpacing"]?.GetValue<double>() ?? DefaultArrangeSpacing
                    };

                    if (root["orbColors"] is JsonObject orbColors)
                    {
                        model.IdleColor = Text(orbColors["idle"]);
                        model.GeneratingColor = Text(orbColors["generating"]);
                        model.WaitingColor = Text(orbColors["waiting"]);
                    }

                    if (root["claudeCodeProfileDirs"] is JsonArray profileDirs)
                    {
                        foreach (var node in profileDirs)
                        {
                            if (node?.GetValue<string>() is { Length: > 0 } dirName)
                            {
                                model.ClaudeCodeProfileDirs.Add(dirName);
                            }
                        }
                    }

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
                catch (Exception ex)
                {
                    // A corrupt or half-written settings file must never stop the
                    // app starting; defaults are always a valid answer. The next
                    // Save() overwrites it. Logged for the same reason as Save's
                    // catch above — a silent reset to defaults looks identical
                    // to "nothing was ever saved" otherwise.
                    LogFailure("Load", ex);
                    _model = new Model();
                }
            }
        }

        // A JSON value that is a non-empty string, or null.
        //
        // Being defensive per field is not optional here. Load() sits inside one
        // catch that replaces the *entire* model with defaults, so `"idle": 5`
        // reaching GetValue<string>() — which throws on a type mismatch — would
        // cost someone their profile names and every dragged orb position over
        // one typo in a colour. JsonValue.TryGetValue never throws.
        //
        // Deliberately doesn't check that the string is a *colour*: that's
        // OrbColors' job, since it holds the default to fall back to.
        private static string? Text(JsonNode? node) =>
            node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;

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

                    var profileDirs = new JsonArray();
                    foreach (var dirName in _model.ClaudeCodeProfileDirs) profileDirs.Add(dirName);

                    root = new JsonObject
                    {
                        ["version"] = CurrentVersion,
                        ["showOrbs"] = _model.ShowOrbs,
                        ["tintActiveWindow"] = _model.TintActiveWindow,
                        ["orbLifetimeMinutes"] = _model.OrbLifetimeMinutes,
                        ["voiceInputEnabled"] = _model.VoiceInputEnabled,
                        ["speakVoice"] = _model.SpeakVoice,
                        ["twoLetterGlyphs"] = _model.TwoLetterGlyphs,
                        ["arrangeShape"] = _model.ArrangeShape,
                        ["arrangeSpacing"] = _model.ArrangeSpacing,
                        // Grouped rather than three top-level keys: it reads as
                        // one setting in the file the way it reads as one card in
                        // the window. A null entry — which is what a colour left
                        // at its default writes — is the same shape the profile
                        // entries already use for "not set".
                        ["orbColors"] = new JsonObject
                        {
                            ["idle"] = _model.IdleColor,
                            ["generating"] = _model.GeneratingColor,
                            ["waiting"] = _model.WaitingColor
                        },
                        ["claudeCodeProfileDirs"] = profileDirs,
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
                    root.ToJsonString(SaveOptions),
                    new UTF8Encoding(false));
                File.Move(temporary, Path_, overwrite: true);
            }
            catch (Exception ex)
            {
                // Losing a preference is not worth taking the app down for,
                // but silently losing it with zero trace is exactly the "no
                // error, just doesn't work" trap this project's own hook
                // script goes out of its way to avoid elsewhere (see
                // ClaudeBuddyHook.ps1's header comment) — so leave a
                // breadcrumb somewhere known-writable rather than nothing.
                // Added after hitting a real machine where this path
                // silently failed on every single attempt, with no way to
                // tell why short of adding this.
                LogFailure("Save", ex);
            }
        }

        private static void LogFailure(string what, Exception ex)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "claude_buddy");
                System.IO.Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "settings-errors.log"),
                    $"{DateTime.Now:O} {what} failed: {ex}{Environment.NewLine}");
            }
            catch
            {
                // If even this fails, there's nowhere left to report it.
            }
        }

        // ---- deferred write -------------------------------------------------

        // The colour pickers raise ColorChanged on every pointer move across the
        // spectrum rather than once on commit, and ColorPicker's drop down lives
        // inside its own template so there is no "closed" event to commit at
        // instead. An auto-saving setter would therefore rebuild, write and
        // rename settings.json for as long as someone drags — hundreds of temp
        // files and renames to land one preference, each of them rewriting every
        // profile and every orb position too.
        //
        // Only the file write waits. The in-memory model is updated at once and
        // everything reads through that (OrbColors -> the orbs and the tray
        // icon), so the live preview is unaffected.
        //
        // A DispatcherTimer rather than a threadpool one on purpose: Save()
        // writes a temp file and renames it over the target, and two of those in
        // flight end with the second rename failing on a file the first already
        // moved — silently, inside the catch above. Every other Save() in this
        // class happens on the UI thread, so this one does too and the question
        // doesn't arise.
        private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(250);
        private static DispatcherTimer? _deferred;

        private static void SaveSoon()
        {
            try
            {
                if (_deferred is null)
                {
                    _deferred = new DispatcherTimer { Interval = SaveDelay };
                    _deferred.Tick += (_, _) =>
                    {
                        _deferred!.Stop();
                        Save();
                    };
                }

                // Restart rather than let it run out: keep pushing the write
                // further off for as long as changes keep arriving.
                _deferred.Stop();
                _deferred.Start();
            }
            catch
            {
                // No dispatcher. Nothing calls these setters before the app is
                // up, but a lost preference isn't worth a crash — write now.
                Save();
            }
        }

        // A deferred write that never happens is a preference silently lost, so
        // anything that might be the last thing to happen calls this.
        public static void FlushPendingSave()
        {
            if (_deferred is null || !_deferred.IsEnabled) return;

            _deferred.Stop();
            Save();
        }
    }
}
