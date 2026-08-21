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

        // OpenClaw's own default. Only worth changing for a gateway that was
        // moved off it.
        public const int DefaultOpenClawPort = 18789;

        // An hour of history by default. Long enough to cover the conversation
        // you were just having, short enough that a gateway holding a year of
        // Discord channels doesn't fill the screen.
        public const int DefaultOpenClawActiveWithin = 60;
        public const int OpenClawActiveWithinAll = 0;

        private static readonly object Gate = new();
        private static Model _model = new();
        private static bool _loaded;

        // Keys found in settings.json that this build knows nothing about, kept
        // verbatim so Save can put them back.
        //
        // Save rebuilds the whole document from the model rather than editing the
        // file in place, which means any key it doesn't write is deleted. That is
        // fine while there is only ever one version of the app — and quietly
        // destructive the moment there isn't. Observed exactly that way: an
        // installed build three commits behind was launched for a couple of
        // minutes, saved once for an unrelated reason, and silently erased
        // speakCommand, neuralVoiceEnabled and neuralVoice from a file it had no
        // idea contained them. The visible symptom was speech coming out in the
        // default system voice with no explanation.
        //
        // It costs a user nothing to be wrong about this and quite a lot to
        // discover it: downgrading, running an old copy once, or testing a build
        // from bin/ beside an installed one all do it. Round-tripping the unknown
        // keys makes an older version leave newer settings alone instead.
        private static readonly Dictionary<string, JsonNode?> _unknownKeys =
            new(StringComparer.Ordinal);

        // Every key Save writes. Kept next to _unknownKeys rather than derived
        // from the JsonObject Save builds, because it has to be consulted during
        // Load, before that object exists. Adding a setting means adding it here
        // too — miss it and the key round-trips through _unknownKeys as well as
        // being written properly, which JsonObject rejects as a duplicate.
        private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
        {
            "version", "showOrbs", "tintActiveWindow", "orbLifetimeMinutes",
            "voiceInputEnabled", "twoLetterGlyphs", "arrangeShape", "arrangeSpacing",
            "speakVoice", "neuralVoiceEnabled", "neuralVoice",
            "speakCommand", "speakCommandArgs",
            "speakVoicesCommand", "speakVoicesCommandArgs", "speakCommandVoice", "speakEngine",
            "orbColors", "claudeCodeProfileDirs", "codexHomes", "profiles", "orbPositions",
            "openclawEnabled", "openclawHost", "openclawPort", "openclawFingerprint",
            "openclawReplyEnabled", "openclawActiveWithinMinutes",
            "codexChatEnabled", "codexReplyEnabled", "autoColorSessions",
            "claudeCodeEnabled", "codexEnabled",
            "clickAction", "doubleClickAction", "tripleClickAction"
        };

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

            // Off by default, same reasoning as VoiceInputEnabled: turning it on
            // is what fetches the neural speech engine and its model (~300MB
            // together), so it has to be something the user just asked for.
            // Windows-only in practice — see NeuralSpeech, and note that macOS's
            // own Enhanced and Premium voices are already better than this.
            public bool NeuralVoiceEnabled { get; set; }

            // Off by default, and for a stronger reason than the voice features
            // above: an app that reaches out to a machine on the network is not
            // something a Claude Code tool should start doing because it was
            // upgraded. Most installs will never point at an OpenClaw gateway,
            // and while this is off nothing here is constructed, no socket is
            // opened, and no key is generated — the only trace of the feature is
            // the settings row that turns it on. Same discipline as
            // VoiceInputEnabled and the mic permission prompt.
            public bool OpenClawEnabled { get; set; }

            // Where the gateway lives. An address rather than a name on purpose:
            // the certificate it serves is self-signed with no subjectAltName,
            // so a hostname buys nothing and pinning does the identity work.
            public string? OpenClawHost { get; set; }

            public int OpenClawPort { get; set; } = DefaultOpenClawPort;

            // The certificate fingerprint this install has agreed to trust,
            // recorded the first time it connects. Empty means "trust whatever
            // is presented next and remember it"; once set, a different
            // certificate is refused rather than silently accepted.
            public string? OpenClawFingerprint { get; set; }

            // Off by default, and separately from OpenClawEnabled on purpose:
            // showing what your agents are doing and being able to make them do
            // things are different powers, and the second one should be asked
            // for. Turning it on widens the scopes this device requests, which
            // the gateway treats as a new pairing to approve.
            public bool OpenClawReplyEnabled { get; set; }

            // The chat panel on a local Claude Code orb. On by default, unlike
            // everything else here that is off: it opens no socket, starts no
            // engine and asks for no permission — it reads a file the hook
            // already tells us about, and only while a panel is up.
            public bool ClaudeCodeChatEnabled { get; set; } = true;

            // Typing into that session from the panel. Off by default, and the
            // same split as OpenClawReplyEnabled for the same reason: watching a
            // session work and being able to drive it are different powers. The
            // second one also covers answering permission prompts and
            // interrupting a run, which are the two places a wrong click costs
            // most.
            public bool ClaudeCodeReplyEnabled { get; set; }

            // Codex's own pair. Separate keys rather than one shared "local CLI"
            // switch, because seeing and controlling are separate powers per CLI
            // as well: someone can reasonably want to read a Codex session and
            // never type into it while doing the opposite for Claude Code.
            public bool CodexChatEnabled { get; set; } = true;

            public bool CodexReplyEnabled { get; set; }

            // Whether the hook gives a Claude Code session a colour of its own
            // when it has none. Off by default: it is the only setting in this
            // file that causes a write to a file the app does not own, even
            // though what it writes is the record /color writes.
            public bool AutoColorSessions { get; set; }

            // Whether a CLI is tracked at all. Both default on, because both
            // are only ever visible if the user wired their hooks — which is
            // itself the opt-in. Off means the app ignores that CLI's status
            // files rather than unwiring anything: the hooks are the user's own
            // config, and a display switch that silently rewrote it would be a
            // surprise, and for Codex would cost them their hook trust as well.
            public bool ClaudeCodeEnabled { get; set; } = true;

            public bool CodexEnabled { get; set; } = true;

            // What clicking an orb does, per number of clicks. One of
            // "terminal", "chat", "speak" or "none" — strings rather than an
            // enum because they go through the same JSON round trip every other
            // setting does, and an unknown value has to degrade rather than
            // throw.
            //
            // The defaults are what the app did before any of this existed: one
            // click goes to the session, and nothing is bound to two or three.
            // That matters beyond taste — see OrbWindow's gesture handling. A
            // second gesture bound to something different forces the first to
            // wait and see whether another click is coming, so leaving these
            // empty is what keeps a single click instant for anyone who never
            // opens this part of the settings.
            public string ClickAction { get; set; } = "terminal";

            public string DoubleClickAction { get; set; } = "none";

            public string TripleClickAction { get; set; } = "none";

            // How far back a gateway session counts as current. Separate from
            // OrbLifetimeMinutes, which decides how long a session lingers after
            // it goes quiet — this decides which of a gateway's many
            // conversations are candidates at all. Zero means all of them.
            public int OpenClawActiveWithinMinutes { get; set; } = DefaultOpenClawActiveWithin;

            // A command of the user's own to speak with, replacing every built-in
            // engine. Null means "use the built-in ones".
            //
            // This is the extension point for any voice or engine this app will
            // never ship: the contract is deliberately the same one TextToSpeech
            // already lives by, because speaking here has always been "a child
            // process is running" and stopping it "kill that process". So the
            // whole interface is: text arrives on stdin as UTF-8, the process
            // exits when it has finished speaking, and being killed means stop.
            // Nothing to implement beyond reading stdin.
            //
            // Whatever is on the other end is the user's business — a Piper
            // wrapper, a voice-conversion chain, a Python script, a cloud API, a
            // batch file. The app makes no assumption about it and reports its
            // failures rather than hiding them. Same posture the project already
            // takes with ClaudeBuddyHook.ps1, which is a user-editable script in
            // %APPDATA% wired into Claude Code by hand.
            public string? SpeakCommand { get; set; }

            // Arguments for it, kept as a list rather than one string so nobody
            // has to guess this app's quoting rules for a path with spaces —
            // they are passed through ArgumentList, which quotes each one
            // correctly by construction.
            public List<string> SpeakCommandArgs { get; init; } = new();

            // Optional companion to SpeakCommand: a command that prints the voice
            // names it can speak with, one per line, so they can be offered in the
            // settings window like any other voice.
            //
            // Needed because the app cannot know what an arbitrary program
            // supports — SpeakCommand is opaque by design. Without this the only
            // honest thing the picker can say is "the command decides", and the
            // chosen voice has to be configured inside the command instead.
            //
            // The selected name reaches SpeakCommand through the CLAUDEBUDDY_VOICE
            // environment variable rather than an appended argument: SpeakCommandArgs
            // belongs to the user, and injecting a positional argument would break
            // any wrapper that takes fixed ones. A command that ignores the variable
            // is unaffected.
            public string? SpeakVoicesCommand { get; set; }

            // Its own arguments, not shared with SpeakCommandArgs. One script
            // usually serves both roles by branching on an argument — ours takes
            // "--list-voices" — and sharing one list would mean handing that same
            // flag to the speaking invocation, which would then list voices
            // instead of talking. Two lists is the difference between "one script,
            // two modes" working and quietly doing the wrong thing.
            public List<string> SpeakVoicesCommandArgs { get; init; } = new();

            // Which of the three engines the selected voice belongs to: "system",
            // "neural" or "custom". The voice itself lives in that engine's own key
            // below, so switching engines and back remembers what was chosen in
            // each.
            //
            // Exists because the engine used to be decided by precedence —
            // configuring a command silently took the neural and system voices out
            // of play, and the settings picker could only ever show one engine's
            // worth of what the machine could do. Making the choice explicit is what
            // lets all three sit in one list.
            public string? SpeakEngine { get; set; }

            // Which of those names is selected. A fourth voice key rather than
            // reusing SpeakVoice or NeuralVoice for the same reason those two are
            // separate: the name spaces have nothing in common, and a value left
            // behind in another engine's key is a name that engine rejects.
            public string? SpeakCommandVoice { get; set; }

            // Which neural voice ("af_heart"). Kept separate from SpeakVoice
            // rather than sharing one key, because the two name spaces have
            // nothing in common: leaving "af_heart" behind in the field SAPI
            // reads would hand SelectVoice a name it throws on, which is exactly
            // the failure the comment at the top of TextToSpeech describes
            // shipping once already. Two keys means either engine's choice
            // survives a round trip through the other.
            public string? NeuralVoice { get; set; }

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

            // The Codex analogue: directory names under $HOME that a second
            // account is run out of via CODEX_HOME. Separate from the list
            // above because they are separate products with separate configs,
            // and someone can easily have extras of one and not the other.
            public List<string> CodexHomes { get; init; } = new();

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

        // Settings-file only, with no row in the settings window on purpose: a
        // free-text command box invites pasting something and hoping, and this
        // belongs next to the hook JSON in the README where the rest of the
        // power-user surface is documented. Easy to surface later if it earns it.
        public static string? SpeakCommand
        {
            get { Load(); lock (Gate) return _model.SpeakCommand; }
            set { Load(); lock (Gate) _model.SpeakCommand = value; Save(); }
        }

        public static List<string> SpeakCommandArgs
        {
            // A copy, not the list itself: callers build a ProcessStartInfo from
            // this off the UI thread while Settings could be writing it.
            get { Load(); lock (Gate) return new List<string>(_model.SpeakCommandArgs); }
        }

        public static string? SpeakVoicesCommand
        {
            get { Load(); lock (Gate) return _model.SpeakVoicesCommand; }
            set { Load(); lock (Gate) _model.SpeakVoicesCommand = value; Save(); }
        }

        public static List<string> SpeakVoicesCommandArgs
        {
            get { Load(); lock (Gate) return new List<string>(_model.SpeakVoicesCommandArgs); }
        }

        // Null when nothing has been chosen, unlike SpeakVoice and NeuralVoice
        // which fall back to a default: there is no sensible default name for a
        // command whose voices this app has never seen, and passing a guess would
        // be worse than passing nothing.
        public static string? SpeakCommandVoice
        {
            get { Load(); lock (Gate) return _model.SpeakCommandVoice; }
            set { Load(); lock (Gate) _model.SpeakCommandVoice = value; Save(); }
        }

        // "system" when unset, because the platform voices are the only ones that
        // always exist — a fresh install has no neural engine downloaded and no
        // command configured.
        public static string SpeakEngine
        {
            get { Load(); lock (Gate) return _model.SpeakEngine ?? "system"; }
            set { Load(); lock (Gate) _model.SpeakEngine = value; Save(); }
        }

        // Turning this on or off takes effect immediately rather than at the
        // next launch: SessionManager asks OpenClawSessions for a snapshot every
        // scan, and that returns nothing at all while this is false.
        public static bool OpenClawEnabled
        {
            get { Load(); lock (Gate) return _model.OpenClawEnabled; }
            set { Load(); lock (Gate) _model.OpenClawEnabled = value; Save(); }
        }

        public static string OpenClawHost
        {
            get { Load(); lock (Gate) return _model.OpenClawHost ?? ""; }
            set { Load(); lock (Gate) _model.OpenClawHost = value; Save(); }
        }

        public static int OpenClawPort
        {
            get { Load(); lock (Gate) return _model.OpenClawPort; }
            set { Load(); lock (Gate) _model.OpenClawPort = value; Save(); }
        }

        // Empty until the first successful connection, which records what it
        // was shown. See OpenClawSocket for why a fingerprint rather than the
        // system trust store.
        public static string OpenClawFingerprint
        {
            get { Load(); lock (Gate) return _model.OpenClawFingerprint ?? ""; }
            set { Load(); lock (Gate) _model.OpenClawFingerprint = value; Save(); }
        }

        public static int OpenClawActiveWithinMinutes
        {
            get { Load(); lock (Gate) return _model.OpenClawActiveWithinMinutes; }
            set { Load(); lock (Gate) _model.OpenClawActiveWithinMinutes = value; Save(); }
        }

        public static bool OpenClawReplyEnabled
        {
            get { Load(); lock (Gate) return _model.OpenClawReplyEnabled; }
            set { Load(); lock (Gate) _model.OpenClawReplyEnabled = value; Save(); }
        }

        public static bool ClaudeCodeChatEnabled
        {
            get { Load(); lock (Gate) return _model.ClaudeCodeChatEnabled; }
            set { Load(); lock (Gate) _model.ClaudeCodeChatEnabled = value; Save(); }
        }

        public static bool ClaudeCodeReplyEnabled
        {
            get { Load(); lock (Gate) return _model.ClaudeCodeReplyEnabled; }
            set { Load(); lock (Gate) _model.ClaudeCodeReplyEnabled = value; Save(); }
        }

        public static bool CodexChatEnabled
        {
            get { Load(); lock (Gate) return _model.CodexChatEnabled; }
            set { Load(); lock (Gate) _model.CodexChatEnabled = value; Save(); }
        }

        public static bool CodexReplyEnabled
        {
            get { Load(); lock (Gate) return _model.CodexReplyEnabled; }
            set { Load(); lock (Gate) _model.CodexReplyEnabled = value; Save(); }
        }

        public static bool AutoColorSessions
        {
            get { Load(); lock (Gate) return _model.AutoColorSessions; }
            set { Load(); lock (Gate) _model.AutoColorSessions = value; Save(); }
        }

        public static bool ClaudeCodeEnabled
        {
            get { Load(); lock (Gate) return _model.ClaudeCodeEnabled; }
            set { Load(); lock (Gate) _model.ClaudeCodeEnabled = value; Save(); }
        }

        public static bool CodexEnabled
        {
            get { Load(); lock (Gate) return _model.CodexEnabled; }
            set { Load(); lock (Gate) _model.CodexEnabled = value; Save(); }
        }

        public static string ClickAction
        {
            get { Load(); lock (Gate) return _model.ClickAction; }
            set { Load(); lock (Gate) _model.ClickAction = value; Save(); }
        }

        public static string DoubleClickAction
        {
            get { Load(); lock (Gate) return _model.DoubleClickAction; }
            set { Load(); lock (Gate) _model.DoubleClickAction = value; Save(); }
        }

        public static string TripleClickAction
        {
            get { Load(); lock (Gate) return _model.TripleClickAction; }
            set { Load(); lock (Gate) _model.TripleClickAction = value; Save(); }
        }

        public static bool NeuralVoiceEnabled
        {
            get { Load(); lock (Gate) return _model.NeuralVoiceEnabled; }
            set { Load(); lock (Gate) _model.NeuralVoiceEnabled = value; Save(); }
        }

        public static string NeuralVoice
        {
            get { Load(); lock (Gate) return _model.NeuralVoice ?? NeuralSpeech.DefaultVoiceName; }
            set { Load(); lock (Gate) _model.NeuralVoice = value; Save(); }
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

        public static IReadOnlyList<string> CodexHomes
        {
            get { Load(); lock (Gate) return _model.CodexHomes.ToList(); }
        }

        public static void AddCodexHome(string dirName)
        {
            Load();
            lock (Gate)
            {
                if (!_model.CodexHomes.Contains(dirName, StringComparer.Ordinal))
                {
                    _model.CodexHomes.Add(dirName);
                }
            }
            Save();
        }

        public static void RemoveCodexHome(string dirName)
        {
            Load();
            lock (Gate) { _model.CodexHomes.Remove(dirName); }
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

        // Forget a profile's name and colour, for when the profile itself is
        // gone. Left behind they would sit waiting for something that no longer
        // exists — and be inherited by the next profile that happened to reuse
        // the folder name, which is not far-fetched: new ones are numbered
        // Claude-Profile-1, -2, and the numbering reuses a gap.
        public static void RemoveProfile(string folder)
        {
            Load();
            lock (Gate)
            {
                if (!_model.Profiles.Remove(folder)) return;
            }
            Save();
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
                        OpenClawEnabled = root["openclawEnabled"]?.GetValue<bool>() ?? false,
                        OpenClawHost = Text(root["openclawHost"]),
                        OpenClawPort = root["openclawPort"]?.GetValue<int>() ?? DefaultOpenClawPort,
                        OpenClawFingerprint = Text(root["openclawFingerprint"]),
                        OpenClawReplyEnabled = root["openclawReplyEnabled"]?.GetValue<bool>() ?? false,
                        OpenClawActiveWithinMinutes =
                            root["openclawActiveWithinMinutes"]?.GetValue<int>() ?? DefaultOpenClawActiveWithin,
                        ClaudeCodeChatEnabled = root["claudeCodeChatEnabled"]?.GetValue<bool>() ?? true,
                        ClaudeCodeReplyEnabled = root["claudeCodeReplyEnabled"]?.GetValue<bool>() ?? false,
                        CodexChatEnabled = root["codexChatEnabled"]?.GetValue<bool>() ?? true,
                        CodexReplyEnabled = root["codexReplyEnabled"]?.GetValue<bool>() ?? false,
                        AutoColorSessions = root["autoColorSessions"]?.GetValue<bool>() ?? false,
                        ClaudeCodeEnabled = root["claudeCodeEnabled"]?.GetValue<bool>() ?? true,
                        CodexEnabled = root["codexEnabled"]?.GetValue<bool>() ?? true,
                        ClickAction = root["clickAction"]?.GetValue<string>() ?? "terminal",
                        DoubleClickAction = root["doubleClickAction"]?.GetValue<string>() ?? "none",
                        TripleClickAction = root["tripleClickAction"]?.GetValue<string>() ?? "none",
                        TwoLetterGlyphs = root["twoLetterGlyphs"]?.GetValue<bool>() ?? false,
                        ArrangeShape = root["arrangeShape"]?.GetValue<string>() ?? DefaultArrangeShape,
                        ArrangeSpacing = root["arrangeSpacing"]?.GetValue<double>() ?? DefaultArrangeSpacing,

                        // speakVoice was declared on the model and written by its
                        // property from the start, but never read here and never
                        // written by Save — so every voice anyone picked was
                        // discarded at exit and silently replaced by the default.
                        // Text(), not GetValue<string>(), for the reason Text's
                        // own comment gives: a type mismatch in one hand-edited
                        // key would otherwise throw and reset every setting in
                        // this file, profiles and orb positions included.
                        SpeakVoice = Text(root["speakVoice"]),
                        NeuralVoiceEnabled = root["neuralVoiceEnabled"]?.GetValue<bool>() ?? false,
                        NeuralVoice = Text(root["neuralVoice"]),
                        SpeakCommand = Text(root["speakCommand"]),
                        SpeakVoicesCommand = Text(root["speakVoicesCommand"]),
                        SpeakCommandVoice = Text(root["speakCommandVoice"]),
                        SpeakEngine = Text(root["speakEngine"])
                    };

                    // Same shape as claudeCodeProfileDirs below: read as an array
                    // and skipped entirely when absent, so an older settings file
                    // simply has no arguments rather than failing to load.
                    if (root["speakCommandArgs"] is JsonArray speakArgs)
                    {
                        foreach (var argument in speakArgs)
                        {
                            var value = Text(argument);
                            if (!string.IsNullOrEmpty(value)) model.SpeakCommandArgs.Add(value);
                        }
                    }

                    if (root["speakVoicesCommandArgs"] is JsonArray voicesArgs)
                    {
                        foreach (var argument in voicesArgs)
                        {
                            var value = Text(argument);
                            if (!string.IsNullOrEmpty(value)) model.SpeakVoicesCommandArgs.Add(value);
                        }
                    }

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

                    if (root["codexHomes"] is JsonArray codexHomes)
                    {
                        foreach (var node in codexHomes)
                        {
                            if (node?.GetValue<string>() is { Length: > 0 } dirName)
                            {
                                model.CodexHomes.Add(dirName);
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

                    // Anything above this line is a key this build understands.
                    // Whatever is left belongs to a different version and is kept
                    // aside for Save to write back untouched — see _unknownKeys
                    // for why, and note that "version" is deliberately treated as
                    // known so it is not carried around twice.
                    _unknownKeys.Clear();
                    foreach (var (key, node) in root)
                    {
                        if (KnownKeys.Contains(key)) continue;

                        // Detached from the document, because the JsonObject this
                        // came from is about to go out of scope and a JsonNode
                        // cannot belong to two parents.
                        _unknownKeys[key] = node?.DeepClone();
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

                    var codexHomeDirs = new JsonArray();
                    foreach (var dirName in _model.CodexHomes) codexHomeDirs.Add(dirName);

                    var speakArgs = new JsonArray();
                    foreach (var argument in _model.SpeakCommandArgs) speakArgs.Add(argument);

                    var voicesArgs = new JsonArray();
                    foreach (var argument in _model.SpeakVoicesCommandArgs) voicesArgs.Add(argument);

                    root = new JsonObject
                    {
                        ["version"] = CurrentVersion,
                        ["showOrbs"] = _model.ShowOrbs,
                        ["tintActiveWindow"] = _model.TintActiveWindow,
                        ["orbLifetimeMinutes"] = _model.OrbLifetimeMinutes,
                        ["voiceInputEnabled"] = _model.VoiceInputEnabled,
                        ["openclawEnabled"] = _model.OpenClawEnabled,
                        ["openclawHost"] = _model.OpenClawHost,
                        ["openclawPort"] = _model.OpenClawPort,
                        ["openclawFingerprint"] = _model.OpenClawFingerprint,
                        ["openclawReplyEnabled"] = _model.OpenClawReplyEnabled,
                        ["openclawActiveWithinMinutes"] = _model.OpenClawActiveWithinMinutes,
                        ["claudeCodeChatEnabled"] = _model.ClaudeCodeChatEnabled,
                        ["claudeCodeReplyEnabled"] = _model.ClaudeCodeReplyEnabled,
                        ["codexChatEnabled"] = _model.CodexChatEnabled,
                        ["codexReplyEnabled"] = _model.CodexReplyEnabled,
                        ["autoColorSessions"] = _model.AutoColorSessions,
                        ["claudeCodeEnabled"] = _model.ClaudeCodeEnabled,
                        ["codexEnabled"] = _model.CodexEnabled,
                        ["clickAction"] = _model.ClickAction,
                        ["doubleClickAction"] = _model.DoubleClickAction,
                        ["tripleClickAction"] = _model.TripleClickAction,
                        ["twoLetterGlyphs"] = _model.TwoLetterGlyphs,
                        ["arrangeShape"] = _model.ArrangeShape,
                        ["arrangeSpacing"] = _model.ArrangeSpacing,
                        // Null when never chosen, like the colours below rather
                        // than a copy of the current default — so changing which
                        // voice ships as the default still reaches everyone who
                        // never picked one.
                        ["speakVoice"] = _model.SpeakVoice,
                        ["neuralVoiceEnabled"] = _model.NeuralVoiceEnabled,
                        ["neuralVoice"] = _model.NeuralVoice,
                        ["speakCommand"] = _model.SpeakCommand,
                        ["speakCommandArgs"] = speakArgs,
                        ["speakVoicesCommand"] = _model.SpeakVoicesCommand,
                        ["speakVoicesCommandArgs"] = voicesArgs,
                        ["speakCommandVoice"] = _model.SpeakCommandVoice,
                        ["speakEngine"] = _model.SpeakEngine,
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
                        ["codexHomes"] = codexHomeDirs,
                        ["profiles"] = profiles,
                        ["orbPositions"] = positions
                    };

                    // Keys this build doesn't understand, put back exactly as they
                    // were found. Without this, saving here *deletes* every
                    // setting a newer version added — see _unknownKeys. Added last
                    // so a key that becomes known later can never be written
                    // twice: the guard below keeps the two sources from colliding
                    // if KnownKeys and this object ever drift apart.
                    foreach (var (key, node) in _unknownKeys)
                    {
                        if (root.ContainsKey(key)) continue;
                        root[key] = node?.DeepClone();
                    }
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
