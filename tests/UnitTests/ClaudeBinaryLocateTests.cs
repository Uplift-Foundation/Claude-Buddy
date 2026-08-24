using Xunit;

namespace ClaudeBuddy.Tests
{
    // Where the `claude` CLI is.
    //
    // Worth testing rather than eyeballing because the file's own comment
    // records a bug that only happened when the app was launched the way users
    // actually launch it: from Finder or as a login item, where the process gets
    // the bare system PATH with none of the places `claude` installs to. The
    // obvious fix — hand the command to the user's shell — looked right and was
    // not, because `zsh -lc` never reads .zshrc, which is where a PATH addition
    // for ~/.local/bin normally lives. So the order of these candidates is the
    // fix, and an order that silently changed would bring the bug back.
    public class ClaudeBinaryLocateTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "cb-claudebinary-" + Guid.NewGuid().ToString("N"));

        // No system installs, unless a case is specifically about them. Every
        // candidate this class checks is otherwise an absolute path, so without
        // this a real `claude` on the developer's machine would answer the test
        // instead of the fixture.
        private static readonly string[] NoSystemInstalls = Array.Empty<string>();

        // The platform's own PATH separator: ':' on Unix, ';' on Windows. Written
        // once here because using a literal ':' is precisely the bug these cases
        // caught in the code under test — a Windows PATH entry contains a colon,
        // so a test that joined with one would be describing a PATH no Windows
        // machine has.
        private static readonly char Sep = System.IO.Path.PathSeparator;

        private string Touch(params string[] parts)
        {
            var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n");
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void TheLocalBinInstallIsFound()
        {
            var expected = Touch("home", ".local", "bin", "claude");

            Assert.Equal(
                expected,
                ClaudeBinary.Locate(Path.Combine(_root, "home"), "", NoSystemInstalls));
        }

        [Fact]
        public void TheClaudeLocalInstallIsFound()
        {
            var expected = Touch("home", ".claude", "local", "claude");

            Assert.Equal(
                expected,
                ClaudeBinary.Locate(Path.Combine(_root, "home"), "", NoSystemInstalls));
        }

        // ~/.local/bin wins over ~/.claude/local. Both are real install
        // locations and a machine can have both, so which one answers is a
        // decision rather than an accident — pinned here so a reordering has to
        // be deliberate.
        [Fact]
        public void LocalBinWinsOverClaudeLocal()
        {
            var first = Touch("home", ".local", "bin", "claude");
            Touch("home", ".claude", "local", "claude");

            Assert.Equal(
                first, ClaudeBinary.Locate(Path.Combine(_root, "home"), "", NoSystemInstalls));
        }

        // A home install wins over a system one. This is the case that matters
        // for the bug: a user who installed into ~/.local/bin should get that
        // copy, not an older one left in /usr/local/bin.
        [Fact]
        public void AHomeInstallWinsOverASystemInstall()
        {
            var home = Touch("home", ".local", "bin", "claude");
            var system = Touch("opt", "claude");

            Assert.Equal(
                home,
                ClaudeBinary.Locate(Path.Combine(_root, "home"), "", new[] { system }));
        }

        [Fact]
        public void ASystemInstallIsFoundWhenThereIsNoHomeOne()
        {
            var system = Touch("opt", "claude");

            Assert.Equal(
                system,
                ClaudeBinary.Locate(Path.Combine(_root, "nothing-here"), "", new[] { system }));
        }

        // PATH is consulted last, for an install none of the known locations
        // anticipate.
        [Fact]
        public void PathIsUsedAsTheLastResort()
        {
            var onPath = Touch("elsewhere", "claude");

            Assert.Equal(
                onPath,
                ClaudeBinary.Locate(
                    Path.Combine(_root, "nothing-here"),
                    Path.Combine(_root, "elsewhere"),
                    NoSystemInstalls));
        }

        [Fact]
        public void PathIsSearchedInOrderAndTheFirstHitWins()
        {
            var second = Touch("b", "claude");
            Touch("c", "claude");

            var search = string.Join(Sep, new[]
            {
                Path.Combine(_root, "a"),      // does not exist at all
                Path.Combine(_root, "b"),
                Path.Combine(_root, "c"),
            });

            Assert.Equal(
                second,
                ClaudeBinary.Locate(Path.Combine(_root, "nothing-here"), search, NoSystemInstalls));
        }

        // The known locations beat PATH, which is the whole design: PATH is what
        // the app could not rely on in the first place.
        [Fact]
        public void AKnownLocationWinsOverPath()
        {
            var known = Touch("home", ".local", "bin", "claude");
            Touch("elsewhere", "claude");

            Assert.Equal(
                known,
                ClaudeBinary.Locate(
                    Path.Combine(_root, "home"),
                    Path.Combine(_root, "elsewhere"),
                    NoSystemInstalls));
        }

        // Null rather than an exception when nothing is found: every caller
        // treats null as "skip this feature", because the app's own work does
        // not depend on the CLI being reachable.
        [Fact]
        public void NothingFoundIsNullRatherThanAThrow()
        {
            Assert.Null(ClaudeBinary.Locate(
                Path.Combine(_root, "nothing-here"),
                Path.Combine(_root, "also-nothing"),
                NoSystemInstalls));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void AnEmptyPathEndsTheSearchRatherThanThrowing(string? search)
        {
            // A null search path falls back to the process's real PATH, so this
            // case only asserts that neither spelling throws — the machine
            // decides the answer, and the point is that it survives either way.
            var got = ClaudeBinary.Locate(
                Path.Combine(_root, "nothing-here"), search, NoSystemInstalls);

            if (search == "") Assert.Null(got);
        }

        // A malformed PATH entry is not worth failing the lookup for, per the
        // catch in the loop. The entry with an illegal character has to be
        // stepped over rather than ending the search, so the good directory
        // after it still answers.
        [Fact]
        public void AMalformedPathEntryIsSteppedOver()
        {
            var good = Touch("good", "claude");
            var search = "\0bad\0entry" + Sep + Path.Combine(_root, "good");

            Assert.Equal(
                good,
                ClaudeBinary.Locate(Path.Combine(_root, "nothing-here"), search, NoSystemInstalls));
        }

        // Empty entries in PATH ("/a::/b") are dropped rather than turning into
        // a lookup in the current directory, which would be a place an attacker
        // can write.
        [Fact]
        public void EmptyPathEntriesAreIgnored()
        {
            var good = Touch("good", "claude");
            var search = $"{Sep}{Sep}" + Path.Combine(_root, "good") + $"{Sep}{Sep}";

            Assert.Equal(
                good,
                ClaudeBinary.Locate(Path.Combine(_root, "nothing-here"), search, NoSystemInstalls));
        }
    }

    // Where a picture from a remote gateway is allowed to land on disk.
    //
    // This is a security boundary, not merely a tidiness one: the name arrives
    // from whatever sent the media, and the sanitised result is used to build a
    // path that gets written to. The source's comment says the point is a name
    // that will "say something useful, minus anything that could point the write
    // somewhere other than here" — so the traversal cases below are the ones
    // that matter.
    public class OpenClawMediaSafeNameTests
    {
        [Fact]
        public void AnOrdinaryNameSurvives()
        {
            Assert.Equal("screenshot.png", OpenClawMedia.SafeName("screenshot.png"));
        }

        // The directory part is stripped before anything else, which is what
        // defeats a traversal: only the final segment can survive.
        [Theory]
        [InlineData("../../etc/passwd", "passwd")]
        [InlineData("/etc/passwd", "passwd")]
        [InlineData("../../../root/.ssh/id_rsa", "id_rsa")]
        [InlineData("dir/sub/image.png", "image.png")]
        public void APathIsReducedToItsLastSegment(string given, string want)
        {
            Assert.Equal(want, OpenClawMedia.SafeName(given));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ANamelessAttachmentGetsADefault(string? given)
        {
            Assert.Equal("image.png", OpenClawMedia.SafeName(given!));
        }

        // A name that is *only* a directory separator reduces to nothing, and
        // the default has to take over rather than leaving an empty filename
        // that would write to the directory itself.
        [Fact]
        public void ANameThatReducesToNothingGetsTheDefault()
        {
            Assert.Equal("image.png", OpenClawMedia.SafeName("some/dir/"));
        }

        [Fact]
        public void CharactersIllegalInAFilenameBecomeDashes()
        {
            var got = OpenClawMedia.SafeName("weird\0name.png");

            Assert.DoesNotContain('\0', got);
            Assert.Equal("weird-name.png", got);
        }

        // Truncated from the *end*, keeping the last 120 characters. The
        // direction is deliberate and worth pinning: a generated name carries
        // its uuid at the end, and truncating from the front would throw away
        // the part that makes it unique — every attachment in one sitting would
        // collide on the same prefix.
        [Fact]
        public void AnOverlongNameKeepsItsTail()
        {
            var name = new string('a', 200) + "-9f3c1e.png";

            var got = OpenClawMedia.SafeName(name);

            Assert.Equal(120, got.Length);
            Assert.EndsWith("-9f3c1e.png", got);
        }

        [Fact]
        public void ANameAtTheLimitIsUnchanged()
        {
            var name = new string('a', 116) + ".png";

            Assert.Equal(name, OpenClawMedia.SafeName(name));
        }
    }
}
