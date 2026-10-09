using Xunit;
using Verdict = ClaudeBuddy.LegacyHookCleanup.Verdict;

namespace ClaudeBuddy.Tests
{
    // CB-255 §1: retiring a legacy hook-script folder, against real temp
    // folders whose `.superseded` markers are back-dated with
    // File.SetLastWriteTimeUtc. LegacyHookCleanupTests in UnitTests has the
    // rule's arms; these are the seams — the marker's real mtime, the real
    // directory listing, and a delete that must never take anything that is
    // not ours.
    public class LegacyHookCleanupFolderTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "cb-legacyhook-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        // A legacy folder the way the installers leave one: the script, and a
        // marker touched `markerAgeDays` ago (null for none).
        private string Folder(string name, double? markerAgeDays, params string[] extras)
        {
            var folder = Path.Combine(_root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "ClaudeBuddyHook.sh"), "#!/bin/sh\n");

            if (markerAgeDays is { } days)
            {
                var marker = Path.Combine(folder, ".superseded");
                File.WriteAllText(marker, "");
                File.SetLastWriteTimeUtc(marker, Now - TimeSpan.FromDays(days));
            }

            foreach (var extra in extras)
                File.WriteAllText(Path.Combine(folder, extra), "mine");

            return folder;
        }

        // --- Retire: one folder ------------------------------------------

        [Fact]
        public void AnOldMarkerOverOnlyOurScriptRemovesTheFolder()
        {
            var folder = Folder("old", markerAgeDays: 15);

            Assert.Equal(Verdict.Retire, LegacyHookCleanup.Retire(folder, Now));
            Assert.False(Directory.Exists(folder));
        }

        // Exactly fourteen days, by the marker's real mtime.
        [Fact]
        public void AMarkerExactlyFourteenDaysOldRetires()
        {
            var folder = Folder("boundary", markerAgeDays: 14);

            Assert.Equal(Verdict.Retire, LegacyHookCleanup.Retire(folder, Now));
            Assert.False(Directory.Exists(folder));
        }

        [Fact]
        public void AYoungMarkerLeavesEverything()
        {
            var folder = Folder("young", markerAgeDays: 3);

            Assert.Equal(Verdict.TooSoon, LegacyHookCleanup.Retire(folder, Now));
            Assert.True(File.Exists(Path.Combine(folder, "ClaudeBuddyHook.sh")));
            Assert.True(File.Exists(Path.Combine(folder, ".superseded")));
        }

        [Fact]
        public void NoMarkerLeavesEverything()
        {
            var folder = Folder("unmarked", markerAgeDays: null);

            Assert.Equal(Verdict.NoMarker, LegacyHookCleanup.Retire(folder, Now));
            Assert.True(File.Exists(Path.Combine(folder, "ClaudeBuddyHook.sh")));
        }

        // A directory called .superseded is not a marker.
        [Fact]
        public void AMarkerThatIsADirectoryIsNoMarker()
        {
            var folder = Folder("dir-marker", markerAgeDays: null);
            Directory.CreateDirectory(Path.Combine(folder, ".superseded"));

            Assert.Equal(Verdict.NoMarker, LegacyHookCleanup.Retire(folder, Now));
            Assert.True(Directory.Exists(folder));
        }

        // The user's own file and the Windows Logs root both refuse, and
        // nothing at all is deleted — not even our own script.
        [Fact]
        public void AnUnrecognisedFileRefusesAndDeletesNothing()
        {
            var folder = Folder("shared", markerAgeDays: 30, "notes.txt");

            Assert.Equal(Verdict.Unrecognised, LegacyHookCleanup.Retire(folder, Now));
            Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
            Assert.True(File.Exists(Path.Combine(folder, "ClaudeBuddyHook.sh")));
            Assert.True(File.Exists(Path.Combine(folder, ".superseded")));
        }

        [Fact]
        public void AnUnmovedLogsFolderRefuses()
        {
            var folder = Folder("ClaudeBuddy", markerAgeDays: 30);
            Directory.CreateDirectory(Path.Combine(folder, "Logs"));
            File.WriteAllText(Path.Combine(folder, "Logs", "crash.log"), "x");

            Assert.Equal(Verdict.Unrecognised, LegacyHookCleanup.Retire(folder, Now));
            Assert.True(File.Exists(Path.Combine(folder, "Logs", "crash.log")));
        }

        [Fact]
        public void AFolderThatIsNotThereIsAbsent()
        {
            Assert.Equal(Verdict.Absent, LegacyHookCleanup.Retire(Path.Combine(_root, "gone"), Now));
        }

        // The no-throw contract: a delete that fails after a Retire verdict is
        // Failed, and the folder is left for the next launch.
        [Fact]
        public void ADeleteThatThrowsIsFailedNotAnException()
        {
            var folder = Folder("stuck", markerAgeDays: 30);

            var verdict = LegacyHookCleanup.Retire(folder, Now, _ => throw new IOException("in use"));

            Assert.Equal(Verdict.Failed, verdict);
            Assert.True(Directory.Exists(folder));
        }

        // --- RemoveRecognised: the delete itself ----------------------------

        // A file that appears between the look and the delete: ours go, the
        // folder delete is non-recursive and so refuses, and the stranger
        // survives. Retire would turn the throw into Failed.
        [Fact]
        public void TheDeleteNeverTakesAFileThatIsNotOurs()
        {
            var folder = Folder("raced", markerAgeDays: 30, "arrived-late.txt");

            Assert.ThrowsAny<IOException>(() => LegacyHookCleanup.RemoveRecognised(folder));

            Assert.True(File.Exists(Path.Combine(folder, "arrived-late.txt")));
            Assert.False(File.Exists(Path.Combine(folder, "ClaudeBuddyHook.sh")));
        }

        // Both scripts and the marker, removed by name.
        [Fact]
        public void BothScriptsAndTheMarkerAreRemoved()
        {
            var folder = Folder("both", markerAgeDays: 30);
            File.WriteAllText(Path.Combine(folder, "ClaudeBuddyHook.ps1"), "");

            LegacyHookCleanup.RemoveRecognised(folder);

            Assert.False(Directory.Exists(folder));
        }

        // --- Run: every folder, under a fake environment -------------------

        // A fake home with all four kinds of legacy folder in different
        // states; Run visits each and only the ripe one goes.
        [Fact]
        public void RunRetiresOnlyTheRipeFolders()
        {
            var home = Path.Combine(_root, "home");
            var local = Path.Combine(_root, "local");
            var ripe = Folder(Path.Combine("home", ".claude", "claude-buddy"), markerAgeDays: 20);
            var young = Folder(Path.Combine("home", ".codex", "claude-buddy"), markerAgeDays: 2);
            var shared = Folder(Path.Combine("local", "ClaudeBuddy"), markerAgeDays: 20, "keep.me");

            var results = LegacyHookCleanup.Run(_ => null, onWindows: true, home, local, Now);

            Assert.Equal(
                new[]
                {
                    (ripe, Verdict.Retire),
                    (young, Verdict.TooSoon),
                    (Path.Combine(home, ".grok", "claude-buddy"), Verdict.Absent),
                    (shared, Verdict.Unrecognised)
                },
                results);
            Assert.False(Directory.Exists(ripe));
            Assert.True(Directory.Exists(young));
            Assert.True(Directory.Exists(shared));
        }

        // The failure arm through Run too, so the log line for it is reached.
        [Fact]
        public void RunReportsAFailedDelete()
        {
            var home = Path.Combine(_root, "home");
            var ripe = Folder(Path.Combine("home", ".grok", "claude-buddy"), markerAgeDays: 20);

            var results = LegacyHookCleanup.Run(
                _ => null, onWindows: false, home, Path.Combine(_root, "local"), Now,
                _ => throw new UnauthorizedAccessException());

            Assert.Contains((ripe, Verdict.Failed), results);
            Assert.True(Directory.Exists(ripe));
        }

        // Under a test override nothing is visited at all — the negative
        // control for the two above, and the contract Startup depends on.
        [Fact]
        public void RunUnderATestOverrideTouchesNothing()
        {
            var home = Path.Combine(_root, "home");
            var ripe = Folder(Path.Combine("home", ".claude", "claude-buddy"), markerAgeDays: 20);

            var results = LegacyHookCleanup.Run(
                n => n == "CLAUDE_BUDDY_SETTINGS_DIR" ? "/sandbox" : null,
                onWindows: false, home, Path.Combine(_root, "local"), Now);

            Assert.Empty(results);
            Assert.True(Directory.Exists(ripe));
        }

        // And the real startup step, under this suite's real overrides.
        [Fact]
        public void TheRealRunIsANoThrowNoOpHere()
        {
            LegacyHookCleanup.Run();
        }
    }
}
