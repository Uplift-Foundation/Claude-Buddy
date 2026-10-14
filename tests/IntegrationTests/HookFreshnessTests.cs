using System.IO;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // CB-265: an app upgrade refreshes the installed hook script, without ever
    // creating one or touching anything else. Every path here is under a scratch
    // directory; the real ~/.claude and Application Support are never named.
    public class HookFreshnessTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "cb-hookfresh-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                // Undo any read-only directory a case made, so cleanup can run.
                if (!OperatingSystem.IsWindows() && Directory.Exists(_root))
                {
                    foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                    {
                        File.SetUnixFileMode(d,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }
                Directory.Delete(_root, recursive: true);
            }
            catch { /* best effort */ }
        }

        private string Write(string relative, string content)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private string WriteAt(string absolute, string content) =>
            Write(Path.GetRelativePath(_root, absolute), content);

        private IReadOnlyList<string> Candidates(bool onWindows) =>
            HookFreshness.InstalledCandidates(onWindows, Path.Combine(_root, "home"), Path.Combine(_root, "local"));

        private static void LockDirectory(string file) =>
            File.SetUnixFileMode(Path.GetDirectoryName(file)!, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        [Fact]
        public void AStaleInstalledCopyIsOverwrittenWithTheBundledBytes()
        {
            var bundled = Write("app/OrbweaverHook.sh", "new\n");
            var installed = WriteAt(Candidates(false)[0], "old\n");
            var log = new List<string>();

            var done = HookFreshness.Refresh(bundled, Candidates(false), log.Add);

            Assert.Equal("new\n", File.ReadAllText(installed));
            Assert.Equal(new[] { installed }, done.Select(r => r.Path));
            Assert.True(done[0].Overwrote);
            Assert.Contains(log, l => l.Contains("refreshed"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(installed)!, "*.refresh-*"));
        }

        [Fact]
        public void TheLegacyCopyThatSettingsStillInvokeIsRefreshedToo()
        {
            var bundled = Write("app/OrbweaverHook.sh", "new\n");
            var legacy = WriteAt(Candidates(false)[1], "old\n");

            var done = HookFreshness.Refresh(bundled, Candidates(false));

            Assert.Equal("new\n", File.ReadAllText(legacy));
            Assert.Equal(new[] { legacy }, done.Select(r => r.Path));
            // The new-location copy was absent and stays absent.
            Assert.False(File.Exists(Candidates(false)[0]));
        }

        [Fact]
        public void BothCopiesAreRefreshedWhenBothExist()
        {
            var bundled = Write("app/OrbweaverHook.sh", "new\n");
            foreach (var c in Candidates(false)) WriteAt(c, "old\n");

            HookFreshness.Refresh(bundled, Candidates(false));

            Assert.All(Candidates(false), c => Assert.Equal("new\n", File.ReadAllText(c)));
        }

        [Fact]
        public void WindowsLayoutIsRefreshedThroughTheSameCode()
        {
            var bundled = Write("app/OrbweaverHook.ps1", "new ps1");
            var installed = WriteAt(Candidates(true)[0], "old ps1");

            HookFreshness.Refresh(bundled, Candidates(true));

            Assert.Equal("new ps1", File.ReadAllText(installed));
        }

        [Fact]
        public void NothingIsCreatedWhenNoCopyIsInstalled()
        {
            var bundled = Write("app/OrbweaverHook.sh", "new\n");

            var done = HookFreshness.Refresh(bundled, Candidates(false));

            Assert.Empty(done);
            Assert.All(Candidates(false), c => Assert.False(File.Exists(c)));
            Assert.False(Directory.Exists(Path.Combine(_root, "home")));
        }

        [Fact]
        public void AnIdenticalCopyIsNotRewritten()
        {
            var bundled = Write("app/OrbweaverHook.sh", "same\n");
            var installed = WriteAt(Candidates(false)[0], "same\n");
            File.SetLastWriteTimeUtc(installed, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var done = HookFreshness.Refresh(bundled, Candidates(false));

            Assert.Empty(done);
            Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(installed));
        }

        [Fact]
        public void SettingsJsonAndSiblingFilesAreLeftAlone()
        {
            var bundled = Write("app/OrbweaverHook.sh", "new\n");
            WriteAt(Candidates(false)[0], "old\n");
            var settings = Write("home/.claude/settings.json", "{\"hooks\":1}");
            var sibling = Write("home/.claude/orbweaver/other.txt", "keep");

            HookFreshness.Refresh(bundled, Candidates(false));

            Assert.Equal("{\"hooks\":1}", File.ReadAllText(settings));
            Assert.Equal("keep", File.ReadAllText(sibling));
        }

        [Fact]
        public void ANullBundledPathDoesNothing()
        {
            var installed = WriteAt(Candidates(false)[0], "old\n");

            Assert.Empty(HookFreshness.Refresh(null, Candidates(false)));
            Assert.Equal("old\n", File.ReadAllText(installed));
        }

        [Fact]
        public void AnUnreadableBundledScriptIsLoggedAndNeverThrows()
        {
            var installed = WriteAt(Candidates(false)[0], "old\n");
            var log = new List<string>();

            // A directory where the script should be: ReadAllBytes throws.
            var done = HookFreshness.Refresh(_root, Candidates(false), log.Add);

            Assert.Empty(done);
            Assert.Contains(log, l => l.Contains("could not read bundled"));
            Assert.Equal("old\n", File.ReadAllText(installed));
        }

        [Fact]
        public void AnUnreadableBundledScriptWithoutALogStillReturnsQuietly()
        {
            Directory.CreateDirectory(_root);
            Assert.Empty(HookFreshness.Refresh(_root, Candidates(false)));
        }

        [Fact]
        public void AFailedOverwriteIsReportedAndTheOtherCopyStillRefreshes()
        {
            if (OperatingSystem.IsWindows()) return; // read-only directories are a Unix-mode trick
            var bundled = Write("app/OrbweaverHook.sh", "new\n");
            var locked = WriteAt(Candidates(false)[0], "old\n");
            var open = WriteAt(Candidates(false)[1], "old\n");
            LockDirectory(locked);
            var log = new List<string>();

            var done = HookFreshness.Refresh(bundled, Candidates(false), log.Add);

            Assert.False(done.Single(r => r.Path == locked).Overwrote);
            Assert.NotNull(done.Single(r => r.Path == locked).Error);
            Assert.Contains(log, l => l.Contains("could not refresh"));
            Assert.Equal("old\n", File.ReadAllText(locked));
            Assert.Equal("new\n", File.ReadAllText(open));
        }

        [Fact]
        public void ThatFailureWithoutALogStillReturnsQuietly()
        {
            if (OperatingSystem.IsWindows()) return;
            var bundled = Write("app/OrbweaverHook.sh", "new\n");
            var locked = WriteAt(Candidates(false)[0], "old\n");
            LockDirectory(locked);

            Assert.False(HookFreshness.Refresh(bundled, Candidates(false)).Single().Overwrote);
        }

        [Fact]
        public void TheScriptStaysExecutableAfterARefresh()
        {
            if (OperatingSystem.IsWindows()) return;
            var bundled = Write("app/OrbweaverHook.sh", "new\n");
            var installed = WriteAt(Candidates(false)[0], "old\n");
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(installed, mode);

            HookFreshness.Refresh(bundled, Candidates(false));

            Assert.Equal(mode, File.GetUnixFileMode(installed));
        }

        [Fact]
        public void BundledScriptIsFoundInResourcesBesideTheMacOsFolder()
        {
            var expected = Write("App.app/Contents/Resources/OrbweaverHook.sh", "x");
            var baseDir = Path.Combine(_root, "App.app", "Contents", "MacOS");
            Directory.CreateDirectory(baseDir);

            Assert.Equal(Path.GetFullPath(expected), HookFreshness.BundledScript("OrbweaverHook.sh", baseDir));
        }

        [Fact]
        public void BundledScriptIsFoundBesideTheExeOnWindows()
        {
            var expected = Write("app/OrbweaverHook.ps1", "x");

            // Normalised, because Write joins with "/" and the code under test
            // with the platform separator; on Windows those are the same file
            // spelled two ways, which is what the Windows CI leg caught.
            Assert.Equal(
                Path.GetFullPath(expected),
                Path.GetFullPath(HookFreshness.BundledScript("OrbweaverHook.ps1", Path.Combine(_root, "app"))!));
        }

        [Fact]
        public void ASourceBuildNeverFindsTheRepoCopyAndSoNeverRefreshes()
        {
            // The repo-root script is right there, four levels up — and must
            // not be used, or `dotnet run` from any worktree overwrites the
            // user's installed hook with that branch's version.
            Write("repo/OrbweaverHook.sh", "x");
            var baseDir = Path.Combine(_root, "repo", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(baseDir);

            Assert.Null(HookFreshness.BundledScript("OrbweaverHook.sh", baseDir));
        }

        [Fact]
        public void BundledScriptIsNullWhenNothingShipsOne()
        {
            var baseDir = Path.Combine(_root, "empty");
            Directory.CreateDirectory(baseDir);

            Assert.Null(HookFreshness.BundledScript("NoSuchHook-" + Guid.NewGuid().ToString("N") + ".sh", baseDir));
        }
    }
}
