using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Orbweaver
{
    // Moves a verified-ours `Claude Buddy.app` left beside `Orbweaver.app` to
    // the Trash, so a login item pointing at it cannot bring the old build
    // back (CB-255 §5).
    //
    // **Why this has to exist at all.** A DMG user drags Orbweaver.app into
    // /Applications and the old Claude Buddy.app stays where it was. Both carry
    // the same bundle id — it is never renamed, because Automation consent is
    // tied to it — so LaunchServices picks one of the two for `open -b`, the
    // login item the user added years ago launches the old one at the next
    // login, and the user reasonably concludes the upgrade did not take. The
    // single-instance mutex then makes it worse rather than better: whichever
    // build starts first wins, and it may be the old one every time.
    // `build-macos-app.sh --install` removes the old bundle itself; this is the
    // same cleanup for everyone who installs by dragging.
    //
    // **The thing this must never do is touch someone else's app.** A folder
    // called "Claude Buddy.app" is only a name. So the rule moves a bundle only
    // when its Info.plist says it is this app — CFBundleIdentifier is
    // Brand.MacBundleId *and* CFBundleExecutable is the executable every build
    // of Claude Buddy shipped (ClaudeBuddy; the phase-3 rename to Orbweaver
    // changed the new bundle, not that history) — and leaves anything else
    // exactly where it is,
    // including anything whose plist cannot be read or is not the XML form our
    // build script writes. Wrongly leaving our own old bundle costs a duplicate
    // in /Applications; wrongly moving somebody else's costs them their app.
    //
    // The Trash rather than a delete, so a mistake is one drag to undo, and
    // ~/.Trash is not TCC-protected. Assumed rather than measured: that a
    // signed, hardened-runtime app can Directory.Move a bundle the user owns
    // into ~/.Trash — to be confirmed on a real Mac. A move that refuses
    // (another volume, a root-owned bundle a standard user cannot move) is
    // logged and the bundle left in place; nothing retries harder.
    //
    // Runs from Startup.Run's `retireLegacy` step, after both mutex names are
    // held — which is what proves no old build is running out of the bundle
    // while it moves. Does nothing off macOS and nothing while any of
    // DataDirMigration.OverrideVariables is set, which every test suite does.
    // Never throws.
    //
    // **There is no tray notice mechanism in this app** — no balloon, no
    // osascript notification, nothing a startup step can raise. So the notice
    // is NoticeText, handed to the `notice` sink Run takes; the default sink
    // writes it to migration.log beside the crash log, which is where
    // DataDirMigration already says what it moved and where a person asking
    // "where did my app go" will be pointed. Showing it in the tray is a matter
    // of passing a different sink.
    internal static class MacOSLegacyBundle
    {
        // What the old bundle was called, and the executable inside it. Phase 3
        // (CB-256) renamed the executable in the new bundle to Orbweaver, and
        // this is deliberately not Brand.AssemblyName: it is a statement about
        // what *shipped* in Claude Buddy.app, whose CFBundleExecutable is
        // ClaudeBuddy forever. It cannot start matching the new bundle — the
        // rule only ever looks at LegacyBundleName paths — and the phase-2
        // Orbweaver.app, which also ran ClaudeBuddy, lives at the new name
        // and is simply overwritten by the drag, so it is never a candidate.
        // (Equal to Brand.Legacy.Executable, by the same history.)
        internal const string LegacyBundleName = "Claude Buddy.app";
        internal const string LegacyExecutable = "ClaudeBuddy";

        // Once per run that moved anything, not once per bundle moved: two old
        // bundles (/Applications and ~/Applications) are still one thing for
        // the user to do.
        internal const string NoticeText =
            "Claude Buddy.app was moved to the Trash. If it was in your Login Items, " +
            "add Orbweaver instead (System Settings > General > Login Items).";

        internal enum Decision
        {
            // Ours: move it to the Trash.
            Trash,

            // Not provably ours, or not there: do nothing.
            LeaveAlone,
        }

        // ---- the rules ------------------------------------------------------

        // The whole decision, from the two plist values, with no filesystem
        // behind it. Both must match. The bundle id alone is not proof — it is
        // a string anyone can put in a plist — and the executable check means
        // a bundle has to look like ours in two independent places before it
        // is moved. Ordinal, because plist values are case-sensitive and so is
        // the rule.
        internal static Decision Decide(string? plistIdentifier, string? plistExecutable) =>
            plistIdentifier == Brand.MacBundleId && plistExecutable == LegacyExecutable
                ? Decision.Trash
                : Decision.LeaveAlone;

        // CFBundleIdentifier and CFBundleExecutable out of an XML plist's
        // top-level dict. Null for anything else — a binary plist, a torn file,
        // a key whose value is not a <string> — because every one of those
        // reads as "not provably ours", which is the direction this must fail
        // in. Managed rather than `plutil`, which the other plist readers here
        // use, so the rule's input can be tested anywhere.
        internal static (string? Identifier, string? Executable) ReadPlist(string xml)
        {
            XDocument document;
            try
            {
                // The DOCTYPE every plist carries is skipped, never fetched.
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
                using var reader = XmlReader.Create(new StringReader(xml), settings);
                document = XDocument.Load(reader);
            }
            catch (XmlException)
            {
                return (null, null);
            }

            // The top-level dict's own keys only, never a nested dict's
            // (CFBundleURLTypes has dicts in it). A key's value is the element
            // straight after it.
            // (A document that loaded always has a root.)
            var root = document.Root!;
            var dict = root.Name == "plist" ? root.Element("dict") : null;
            if (dict is null) return (null, null);

            return (Value(dict, "CFBundleIdentifier"), Value(dict, "CFBundleExecutable"));
        }

        private static string? Value(XElement dict, string key)
        {
            var named = dict.Elements("key").FirstOrDefault(element => element.Value == key);
            var value = named?.ElementsAfterSelf().FirstOrDefault();
            return value?.Name == "string" ? value.Value : null;
        }

        // Where in the Trash a bundle goes: its own name if that is free, else
        // the name with a timestamp, else that with a counter — so an old bundle
        // trashed last week, or the second of two trashed in this same second,
        // never overwrites anything.
        internal static string TrashDestination(string trash, DateTimeOffset now, Func<string, bool> exists)
        {
            var plain = Path.Combine(trash, LegacyBundleName);
            if (!exists(plain)) return plain;

            var stem = Path.GetFileNameWithoutExtension(LegacyBundleName) + " " + now.ToString("yyyyMMdd-HHmmss");
            var candidate = Path.Combine(trash, stem + ".app");
            for (var n = 2; exists(candidate); n++)
                candidate = Path.Combine(trash, $"{stem} {n}.app");

            return candidate;
        }

        // ---- the executor ---------------------------------------------------

        internal static void Run() =>
            Run(OperatingSystem.IsMacOS(),
                DataDirMigration.OverrideSet(Environment.GetEnvironmentVariable),
                RealCandidates,
                RealTrash);

        // Every effect injectable, so the tests drive it against temp folders
        // with a fake plist on any platform. `candidates` and `trash` are
        // functions so that a no-op run never even resolves the real folders.
        internal static void Run(
            bool onMac,
            bool overrideSet,
            Func<IReadOnlyList<string>> candidates,
            Func<string> trash,
            Func<DateTimeOffset>? now = null,
            Action<string, string>? move = null,
            Action<string>? log = null,
            Action<string>? notice = null)
        {
            if (!onMac || overrideSet) return;

            var lines = new List<string>();
            var moved = false;

            try
            {
                move ??= (from, to) => Directory.Move(from, to);

                foreach (var bundle in candidates())
                    moved |= Retire(bundle, trash, now ?? (() => DateTimeOffset.Now), move, lines.Add);
            }
            catch (Exception error)
            {
                lines.Add($"legacy bundle cleanup stopped: {error.GetType().Name}: {error.Message}");
            }

            if (moved) lines.Add("notice: " + NoticeText);

            try
            {
                var sink = log ?? (line => DataDirMigration.AppendLog(CrashLog.Directory, line));
                foreach (var line in lines) sink(line);
                if (moved) notice?.Invoke(NoticeText);
            }
            catch
            {
                // A diagnostic that can break startup is worse than none.
            }
        }

        // True if this bundle was moved to the Trash.
        private static bool Retire(
            string bundle, Func<string> trash, Func<DateTimeOffset> now, Action<string, string> move, Action<string> log)
        {
            try
            {
                var plist = Path.Combine(bundle, "Contents", "Info.plist");
                if (!File.Exists(plist)) return false;

                var (identifier, executable) = ReadPlist(File.ReadAllText(plist));
                if (Decide(identifier, executable) == Decision.LeaveAlone)
                {
                    log($"left {bundle} alone: its Info.plist is not this app's " +
                        $"(CFBundleIdentifier {identifier ?? "(none)"}, CFBundleExecutable {executable ?? "(none)"})");
                    return false;
                }

                var trashDir = trash();
                Directory.CreateDirectory(trashDir);
                var destination = TrashDestination(trashDir, now(), path => Directory.Exists(path) || File.Exists(path));
                move(bundle, destination);
                log($"moved {bundle} to {destination}");
                return true;
            }
            catch (Exception error)
            {
                log($"could not move {bundle} to the Trash: {error.GetType().Name}: {error.Message}");
                return false;
            }
        }

        // Excluded from coverage: the real folders. Every test drives Run with
        // temp folders, and every suite sets the overrides that keep Run() from
        // resolving these.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<string> RealCandidates() =>
        [
            Path.Combine("/Applications", LegacyBundleName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", LegacyBundleName),
        ];

        [ExcludeFromCodeCoverage]
        private static string RealTrash() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
    }
}
