using Xunit;

namespace ClaudeBuddy.Tests
{
    // Which copy of an installer script the app runs.
    //
    // In IntegrationTests rather than UnitTests because the answer is decided by
    // what is on disk — the point is the layouts, and a fake filesystem would be
    // testing a different function.
    //
    // The order is the part worth asserting, and the reason is in the source's own
    // comment: installed wins, because a stale clone next to an installed app
    // should not be what runs. That failure is silent — both scripts exist, both
    // appear to work, and the wrong one wires up an older hook with an older set
    // of flags. It has happened before in this repo: the colour setting shipped
    // broken for Codex because one installer ran and the other kept an older hook
    // copy without the flag.
    public class HookInstallerResolveTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "cb-hookinstaller-" + Guid.NewGuid().ToString("N"));

        private const string Script = "install-macos-hooks.sh";

        private string Touch(params string[] parts)
        {
            var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/bash\n");
            return path;
        }

        private string Dir(params string[] parts)
        {
            var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        // The installed layout: inside the .app the scripts sit in
        // Contents/Resources, and AppContext.BaseDirectory is Contents/MacOS, so
        // Resources is its sibling.
        [Fact]
        public void TheBundleResourcesCopyIsFound()
        {
            var expected = Touch("Contents", "Resources", Script);
            var baseDir = Dir("Contents", "MacOS");

            Assert.Equal(expected, HookInstaller.Resolve(Script, baseDir));
        }

        // Returned fully qualified, not with the ".." still in it. The path is
        // handed to /bin/bash, and a relative segment resolves against the
        // working directory of whatever launched the app rather than against the
        // bundle.
        [Fact]
        public void TheBundlePathIsReturnedFullyQualified()
        {
            Touch("Contents", "Resources", Script);
            var baseDir = Dir("Contents", "MacOS");

            var resolved = HookInstaller.Resolve(Script, baseDir);

            Assert.DoesNotContain("..", resolved);
            Assert.True(Path.IsPathFullyQualified(resolved!));
        }

        // The Inno layout on Windows: {app}\tools\ alongside the binary.
        [Fact]
        public void TheToolsDirectoryBesideTheBinaryIsFound()
        {
            var expected = Touch("app", "tools", Script);
            var baseDir = Dir("app");

            Assert.Equal(expected, HookInstaller.Resolve(Script, baseDir));
        }

        // A source build has no bundle, so the walk up finds the repo's tools/.
        [Fact]
        public void ASourceBuildWalksUpToTheReposTools()
        {
            var expected = Touch("repo", "tools", Script);
            var baseDir = Dir("repo", "bin", "Debug", "net10.0", "osx-arm64");

            Assert.Equal(expected, HookInstaller.Resolve(Script, baseDir));
        }

        // The decision this test exists for. Both copies are present — an
        // installed app with a checkout underneath it — and the installed one has
        // to win.
        [Fact]
        public void TheInstalledCopyBeatsAClonesCopy()
        {
            var installed = Touch("Contents", "Resources", Script);
            Touch("Contents", "MacOS", "tools", Script);

            var resolved = HookInstaller.Resolve(Script, Dir("Contents", "MacOS"));

            Assert.Equal(installed, resolved);
        }

        // ...and the same again one rung down: a tools/ beside the binary beats
        // one found by walking up.
        [Fact]
        public void ToolsBesideTheBinaryBeatsToolsFurtherUp()
        {
            var beside = Touch("repo", "bin", "tools", Script);
            Touch("repo", "tools", Script);

            Assert.Equal(beside, HookInstaller.Resolve(Script, Dir("repo", "bin")));
        }

        // Null rather than a guess when the script is nowhere. Every caller
        // treats null as "do nothing", which is the same outcome as the wiring
        // not having been added — better than invoking bash on a path that does
        // not exist.
        [Fact]
        public void AMissingScriptIsNull()
        {
            Assert.Null(HookInstaller.Resolve(Script, Dir("empty")));
        }

        // The walk up is bounded at six levels. A deeper build output than that
        // finds nothing rather than climbing to the filesystem root and picking
        // up an unrelated tools/ directory belonging to somebody else.
        [Fact]
        public void TheWalkUpIsBounded()
        {
            Touch("repo", "tools", Script);
            var deep = Dir("repo", "a", "b", "c", "d", "e", "f", "g");

            Assert.Null(HookInstaller.Resolve(Script, deep));
        }

        // Each installer is resolved by name, so asking for one does not find
        // another — the Codex and Claude Code scripts live side by side and wire
        // different CLIs.
        [Fact]
        public void OnlyTheScriptAskedForIsFound()
        {
            Touch("repo", "tools", "install-codex-hooks.sh");
            var baseDir = Dir("repo", "bin");

            Assert.Null(HookInstaller.Resolve("install-macos-hooks.sh", baseDir));
            Assert.NotNull(HookInstaller.Resolve("install-codex-hooks.sh", baseDir));
        }
    }

    // The short form of a session id the daemon uses.
    //
    // The only thing in AgentTeamViewer that decides anything without asking the
    // OS — everything else in that file runs ps, lsof, tmux or osascript.
    public class AgentTeamViewerJobIdTests
    {
        [Fact]
        public void TheJobIdIsTheFirstSegmentOfTheSessionUuid()
        {
            Assert.Equal(
                "95eddb0e", AgentTeamViewer.JobIdOf("95eddb0e-99a5-4e5a-ba63-d9775a21b81f"));
        }

        [Theory]
        [InlineData("nodashes", "nodashes")]
        [InlineData("", "")]
        [InlineData("-leading", "-leading")]
        public void AnIdThatIsNotAUuidDegradesToItself(string given, string want)
        {
            Assert.Equal(want, AgentTeamViewer.JobIdOf(given));
        }

        // The two copies of this rule — here and in BackgroundJobs — have to
        // agree, because one decides which pane to reuse and the other decides
        // whether the job is still alive. A session that resolved to two
        // different job ids would be adopted into a pane and then have its orb
        // hidden.
        [Fact]
        public void ItAgreesWithTheBackgroundJobsCopy()
        {
            foreach (var id in new[]
                     {
                         "95eddb0e-99a5-4e5a-ba63-d9775a21b81f", "nodashes", "", "-leading", "a-b-c",
                     })
            {
                Assert.Equal(BackgroundJobs.JobIdOf(id), AgentTeamViewer.JobIdOf(id));
            }
        }
    }
}
