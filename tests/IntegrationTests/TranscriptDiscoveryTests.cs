using Xunit;

namespace ClaudeBuddy.Tests
{
    // Finding a session's transcript when the status file does not say where it
    // is.
    //
    // This matters for exactly the session you would most want to look at: one
    // whose status file predates the hook recording transcript_path, which is the
    // orb you click wondering what it had been doing. If the search picks the
    // wrong file the panel opens on somebody else's conversation, and nothing on
    // screen says so.
    //
    // Both entry points now take the home directory as a parameter, so these walk
    // a temp tree rather than the developer's own ~/.claude — which would make the
    // result depend on the machine, and on a CI runner would find nothing at all.
    public class TranscriptDiscoveryTests : IDisposable
    {
        private readonly string _home =
            Path.Combine(Path.GetTempPath(), "cb-discovery-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        }

        // A cwd in the shape this platform actually uses. Claude Code encodes the
        // *cwd*, and the encoder replaces Path.DirectorySeparatorChar — which is
        // a backslash on Windows — so a fixture written with forward slashes
        // encodes to "-/Users-foo-Bar" there and matches nothing. The Windows CI
        // leg caught exactly that: the app is right and the first draft of this
        // file was not.
        private static string Cwd(params string[] parts) =>
            Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts);

        // ...and the project directory name is asked of the encoder rather than
        // written out, for the same reason: one source of truth for the encoding,
        // on either platform.
        private static string Project(params string[] parts) =>
            TranscriptReader.EncodeCwd(Cwd(parts));

        // A transcript inside the encoded project directory Claude Code would
        // have created for `cwd`.
        private string Transcript(string project, string sessionId, DateTime? modified = null)
        {
            var dir = Path.Combine(_home, ".claude", "projects", project);
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, sessionId + ".jsonl");
            File.WriteAllText(path, "{\"type\":\"user\"}\n");

            if (modified is { } when) File.SetLastWriteTimeUtc(path, when);

            return path;
        }

        // --- EncodeCwd ---

        [Fact]
        public void APathBecomesItsDashEncodedProjectName()
        {
            Assert.Equal(
                "-Users-foo-Source-Bar",
                TranscriptReader.EncodeCwd(Cwd("Users", "foo", "Source", "Bar")));
        }

        // A relative path gains the leading dash it would otherwise lack, because
        // Claude Code's own names always start with one.
        [Fact]
        public void ARelativePathGainsTheLeadingSeparator()
        {
            Assert.StartsWith("-", TranscriptReader.EncodeCwd("Users"));
        }

        [Fact]
        public void AnAlreadyEncodedNameIsNotDoublePrefixed()
        {
            Assert.Equal("-already", TranscriptReader.EncodeCwd("-already"));
        }

        // --- ProjectDirMatches: the rule a plain StartsWith gets wrong ---

        [Fact]
        public void TheProjectsOwnDirectoryMatches()
        {
            Assert.True(TranscriptReader.ProjectDirMatches("-Users-foo-Bar", "-Users-foo-Bar"));
        }

        [Fact]
        public void ADirectoryBeneathTheProjectMatches()
        {
            Assert.True(
                TranscriptReader.ProjectDirMatches("-Users-foo-Bar-sub-dir", "-Users-foo-Bar"));
        }

        // The case this rule exists for. A sibling sharing a prefix is a
        // different project, and matching it hands back somebody else's
        // conversation.
        [Theory]
        [InlineData("-Users-foo-Barn")]
        [InlineData("-Users-foo-Bartholomew")]
        public void ASiblingSharingAPrefixDoesNotMatch(string dirName)
        {
            Assert.False(TranscriptReader.ProjectDirMatches(dirName, "-Users-foo-Bar"));
        }

        [Fact]
        public void AnUnrelatedDirectoryDoesNotMatch()
        {
            Assert.False(TranscriptReader.ProjectDirMatches("-somewhere-else", "-Users-foo-Bar"));
        }

        // --- FindTranscriptFor ---

        [Fact]
        public void ASessionsTranscriptIsFoundByItsId()
        {
            var expected = Transcript(Project("Users", "foo", "Bar"), "95eddb0e-99a5");

            Assert.Equal(
                expected, TranscriptReader.FindTranscriptFor("95eddb0e-99a5", _home));
        }

        // Found wherever it is, since the session id is unique and the project
        // directory it landed in is not known in advance.
        [Fact]
        public void ItIsFoundUnderAnyProjectDirectory()
        {
            var expected = Transcript("-somewhere-entirely-else", "abc-123");

            Assert.Equal(expected, TranscriptReader.FindTranscriptFor("abc-123", _home));
        }

        [Fact]
        public void AnUnknownSessionIsNull()
        {
            Transcript(Project("Users", "foo", "Bar"), "abc-123");

            Assert.Null(TranscriptReader.FindTranscriptFor("no-such-session", _home));
        }

        // No projects directory at all — a machine where Claude Code has never
        // run. Null, not an exception: the caller falls back to showing the orb
        // without a transcript.
        [Fact]
        public void AHomeWithNoProjectsDirectoryIsNull()
        {
            Directory.CreateDirectory(_home);

            Assert.Null(TranscriptReader.FindTranscriptFor("abc-123", _home));
        }

        // --- LatestTranscriptForCwd ---

        // The newest transcript wins, because a directory accumulates one file
        // per session and the interesting one is the session that just ran.
        [Fact]
        public void TheMostRecentlyWrittenTranscriptWins()
        {
            var older = Transcript(Project("Users", "foo", "Bar"), "older", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
            var newer = Transcript(Project("Users", "foo", "Bar"), "newer", new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));

            var found = TranscriptReader.LatestTranscriptForCwd(
                Cwd("Users", "foo", "Bar"), _home);

            Assert.Equal(newer, found);
            Assert.NotEqual(older, found);
        }

        // A transcript under a subdirectory of the project counts, which is what
        // the separator half of ProjectDirMatches buys.
        [Fact]
        public void ATranscriptUnderASubdirectoryOfTheProjectCounts()
        {
            var expected = Transcript(Project("Users", "foo", "Bar") + "-worktrees-x", "s1");

            Assert.Equal(
                expected,
                TranscriptReader.LatestTranscriptForCwd(Cwd("Users", "foo", "Bar"), _home));
        }

        // ...and the sibling does not, end to end. This is the same rule as the
        // unit case above, asserted through the walk that uses it, because that
        // is where getting it wrong would actually show up.
        [Fact]
        public void ASiblingProjectsTranscriptIsNotReturned()
        {
            Transcript(Project("Users", "foo", "Barn"), "s1");

            Assert.Null(
                TranscriptReader.LatestTranscriptForCwd(Cwd("Users", "foo", "Bar"), _home));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void NoCwdIsNull(string? cwd)
        {
            Assert.Null(TranscriptReader.LatestTranscriptForCwd(cwd!, _home));
        }

        [Fact]
        public void ACwdWithNoTranscriptsIsNull()
        {
            Transcript(Project("Users", "foo", "Bar"), "s1");

            Assert.Null(TranscriptReader.LatestTranscriptForCwd(
                Cwd("Users", "foo", "Elsewhere"), _home));
        }
    }
}
