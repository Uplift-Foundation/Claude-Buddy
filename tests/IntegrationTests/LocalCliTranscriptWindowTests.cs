using System.Text;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // Which bytes of a transcript the chat panel reads, and how a partial one is
    // carried across reads.
    //
    // In IntegrationTests because two of the three want a real file: the whole
    // question is what happens when a byte range lands in the middle of a row,
    // and a stream over a byte array would be testing a different function.
    //
    // Constructing a LocalCliChatSession costs nothing — no watcher, no timer, no
    // CLI — as long as Start() is not called, so the carry buffer is reachable
    // here without a dispatcher.
    //
    // Both rules below were bought with measurements the source records. The
    // largest single row in a real Codex rollout is 1,046,104 bytes, which is
    // what makes "a whole window inside one row" a case rather than a curiosity;
    // and a write landing mid-codepoint is what the carry exists to stop turning
    // into a permanent replacement character.
    public class LocalCliTranscriptWindowTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "cb-transcript-" + Guid.NewGuid().ToString("N"));

        public LocalCliTranscriptWindowTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private static LocalCliChatSession Session() =>
            new("s1", new SessionStatus { Source = SessionSource.ClaudeCode });

        private FileStream Write(string text)
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
            return File.OpenRead(path);
        }

        // --- Split ---

        [Fact]
        public void RowsAreSplitOnNewlinesAndBlanksAreDropped()
        {
            Assert.Equal(new[] { "a", "b", "c" }, LocalCliChatSession.Split("a\nb\n\n\nc\n"));
        }

        [Fact]
        public void NoRowsIsAnEmptyListRatherThanOneBlankRow()
        {
            Assert.Empty(LocalCliChatSession.Split(""));
            Assert.Empty(LocalCliChatSession.Split("\n\n"));
        }

        // --- TakeWholeLines: the carry buffer ---

        [Fact]
        public void OnlyCompleteRowsAreReturned()
        {
            var session = Session();

            var lines = session.TakeWholeLines(Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2"));

            Assert.Equal(new[] { "{\"a\":1}" }, lines);
        }

        // The partial row is finished by the next read rather than lost, which is
        // the whole point: the CLI appends whenever it likes, and a read can land
        // anywhere.
        [Fact]
        public void APartialRowIsCompletedByTheNextRead()
        {
            var session = Session();

            Assert.Empty(session.TakeWholeLines(Encoding.UTF8.GetBytes("{\"a\":")));
            Assert.Empty(session.TakeWholeLines(Encoding.UTF8.GetBytes("1")));

            Assert.Equal(new[] { "{\"a\":1}" }, session.TakeWholeLines(Encoding.UTF8.GetBytes("}\n")));
        }

        // The case the carry exists for. A write that lands between the two bytes
        // of a multi-byte character must not decode to a replacement character —
        // and it would be *permanent*, because the row is only decoded once.
        [Fact]
        public void AWriteSplittingACharacterDoesNotCorruptIt()
        {
            var session = Session();
            var bytes = Encoding.UTF8.GetBytes("{\"text\":\"café — ☕\"}\n");

            // Split in the middle of the em dash, which is three bytes in UTF-8.
            var cut = Array.IndexOf(bytes, (byte)0xE2) + 1;
            Assert.True(cut > 0, "fixture should contain a multi-byte character");

            Assert.Empty(session.TakeWholeLines(bytes[..cut]));
            var lines = session.TakeWholeLines(bytes[cut..]);

            Assert.Equal(new[] { "{\"text\":\"café — ☕\"}" }, lines);
            Assert.DoesNotContain('�', lines[0]);
        }

        // A four-byte character (an emoji is a surrogate pair on the .NET side)
        // split at every one of its interior boundaries.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void AFourByteCharacterSurvivesASplitAtAnyBoundary(int into)
        {
            var session = Session();
            var bytes = Encoding.UTF8.GetBytes("{\"t\":\"\U0001F680\"}\n");
            var cut = Array.IndexOf(bytes, (byte)0xF0) + into;

            Assert.Empty(session.TakeWholeLines(bytes[..cut]));

            Assert.Equal(new[] { "{\"t\":\"\U0001F680\"}" }, session.TakeWholeLines(bytes[cut..]));
        }

        [Fact]
        public void SeveralRowsInOneReadAllComeBack()
        {
            var session = Session();

            var lines = session.TakeWholeLines(Encoding.UTF8.GetBytes("a\nb\nc\n"));

            Assert.Equal(new[] { "a", "b", "c" }, lines);
        }

        // Everything up to the *last* newline is taken, so a read holding two
        // complete rows and the start of a third yields two and carries one.
        [Fact]
        public void TheTailAfterTheLastNewlineIsCarried()
        {
            var session = Session();

            Assert.Equal(new[] { "a", "b" }, session.TakeWholeLines(Encoding.UTF8.GetBytes("a\nb\nc")));
            Assert.Equal(new[] { "c" }, session.TakeWholeLines(Encoding.UTF8.GetBytes("\n")));
        }

        // --- ReadWindow: paging backwards through a transcript ---

        [Fact]
        public void AWindowFromTheStartOfTheFileKeepsItsFirstRow()
        {
            using var fs = Write("one\ntwo\nthree\n");

            var (lines, from) = LocalCliChatSession.ReadWindow(fs, 0, fs.Length);

            Assert.Equal(new[] { "one", "two", "three" }, lines);
            Assert.Equal(0, from);
        }

        // A window that does not start at the beginning almost certainly lands
        // mid-row, so the partial first row is dropped — and the offset it was
        // dropped *to* is what comes back, because the next page has to stop
        // there. Returning the unaligned offset would read that row twice.
        [Fact]
        public void APartialFirstRowIsDroppedAndTheAlignedOffsetReturned()
        {
            using var fs = Write("one\ntwo\nthree\n");

            // Start inside "two".
            var (lines, from) = LocalCliChatSession.ReadWindow(fs, 5, fs.Length);

            Assert.Equal(new[] { "three" }, lines);
            Assert.Equal(8, from);   // just past the newline that ended "two"
        }

        // A whole window inside one row, which a megabyte-long file-history
        // snapshot manages. Reporting `to` would leave the backlog offset exactly
        // where it was, so scrolling to the top would re-read the same megabyte
        // forever; reporting `from` steps over the window instead.
        [Fact]
        public void AWindowEntirelyInsideOneRowStepsOverItself()
        {
            using var fs = Write("short\n" + new string('x', 4096) + "\ntail\n");

            var (lines, from) = LocalCliChatSession.ReadWindow(fs, 100, 200);

            Assert.Empty(lines);
            Assert.Equal(100, from);
        }

        [Fact]
        public void AnEmptyOrBackwardsWindowReadsNothing()
        {
            using var fs = Write("one\ntwo\n");

            Assert.Empty(LocalCliChatSession.ReadWindow(fs, 4, 4).Lines);
            Assert.Empty(LocalCliChatSession.ReadWindow(fs, 6, 2).Lines);

            // ...and the offset comes back unchanged, so a caller paging
            // backwards does not lose its place.
            Assert.Equal(4, LocalCliChatSession.ReadWindow(fs, 4, 4).From);
            Assert.Equal(6, LocalCliChatSession.ReadWindow(fs, 6, 2).From);
        }

        // Paging backwards twice must not show a row twice, which is what the
        // returned offset is for. This walks the file in windows the way
        // LoadOlderAsync does and asserts every row appears exactly once.
        [Fact]
        public void PagingBackwardsCoversEveryRowExactlyOnce()
        {
            var rows = Enumerable.Range(0, 40).Select(i => $"{{\"n\":{i}}}").ToArray();
            using var fs = Write(string.Join('\n', rows) + "\n");

            var seen = new List<string>();
            var to = fs.Length;

            while (to > 0)
            {
                var from = Math.Max(0, to - 37);   // a deliberately unaligned page size
                var (lines, aligned) = LocalCliChatSession.ReadWindow(fs, from, to);

                seen.InsertRange(0, lines);

                // The next page ends where this one really began.
                if (aligned >= to) break;
                to = aligned;
            }

            Assert.Equal(rows, seen);
        }
    }
}
