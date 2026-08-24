using Xunit;

namespace ClaudeBuddy.Tests
{
    // Which speech engine version this build asks for, and which one it falls
    // back to.
    //
    // Two small pieces of a file that is otherwise downloads and a side-car
    // process, and both are worth having for the same reason: their failures are
    // silent by construction. Speaking falls back to a system voice that works,
    // so a build that picks the wrong engine drops every neural voice out of the
    // picker with no explanation and no prompt — which is exactly how the bug the
    // fallback exists for was found, by someone noticing their voice was gone.
    public class NeuralSpeechVersionTests
    {
        // --- ResolveVersion: the tag the engine is fetched from ---

        // AssemblyInformationalVersion carries a "+<commit sha>" suffix; the
        // release tag does not, so it is cut. Getting this wrong is a 404 rather
        // than a compile error.
        [Theory]
        [InlineData("0.4.1-beta+9f3c1e88aa", "0.4.1-beta")]
        [InlineData("0.4.1-beta", "0.4.1-beta")]
        [InlineData("1.0.0", "1.0.0")]
        public void TheCommitSuffixIsCutFromTheVersion(string informational, string want)
        {
            Assert.Equal(want, NeuralSpeech.ResolveVersion(informational));
        }

        // No attribute at all, which the running assembly always has — so this
        // arm exists only for a build that somehow lacks one, and could not be
        // reached at all before the value was passed in.
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void AMissingVersionFallsBackToZero(string? informational)
        {
            Assert.Equal("0.0.0", NeuralSpeech.ResolveVersion(informational));
        }

        // A suffix and nothing else is degenerate but must not throw or produce a
        // tag that starts with '+'.
        [Fact]
        public void AVersionThatIsOnlyASuffixBecomesEmptyRatherThanThrowing()
        {
            Assert.Equal("", NeuralSpeech.ResolveVersion("+abc"));
        }

        // --- VersionOrder: which engine on disk is newest ---

        private static List<string> Newest(params string[] names)
        {
            var sorted = names.ToList();
            sorted.Sort((a, b) => NeuralSpeech.VersionOrder.Compare(b, a));   // descending
            return sorted;
        }

        // The bug this comparer exists to prevent, stated as a test. An ordinal
        // sort puts "0.10.0-beta" *below* "0.2.0-beta", so the first time the
        // minor version reaches double digits the app would pick a year-old
        // engine over last month's — dormant until 0.10, and then looking like
        // anything but a sort order.
        [Fact]
        public void DoubleDigitMinorVersionsSortAboveSingleDigitOnes()
        {
            Assert.Equal(
                new[] { "0.10.0-beta", "0.9.0-beta", "0.2.0-beta" },
                Newest("0.2.0-beta", "0.10.0-beta", "0.9.0-beta"));

            // ...and the ordinal sort this replaces would have got it wrong,
            // which is what makes the case worth pinning rather than assuming.
            Assert.True(
                string.CompareOrdinal("0.10.0-beta", "0.2.0-beta") < 0,
                "a string sort really does invert these, so the comparer is load-bearing");
        }

        [Fact]
        public void HigherVersionsComeFirst()
        {
            Assert.Equal(
                new[] { "1.0.0", "0.4.1-beta", "0.4.0-beta", "0.3.0-beta" },
                Newest("0.3.0-beta", "0.4.0-beta", "1.0.0", "0.4.1-beta"));
        }

        [Fact]
        public void APatchVersionOutranksItsBase()
        {
            Assert.True(NeuralSpeech.VersionOrder.Compare("0.4.1-beta", "0.4.0-beta") > 0);
        }

        // A directory name that is not a version at all sorts below everything
        // rather than throwing. Something unparseable in the engine directory is
        // not a reason to lose the fallback entirely — the alternative is silence.
        [Fact]
        public void AnUnparseableNameSortsLast()
        {
            Assert.Equal(
                new[] { "0.3.0-beta", "not-a-version" },
                Newest("not-a-version", "0.3.0-beta"));
        }

        // Same numeric version, different prerelease tag: the order falls back to
        // the string so it is at least stable rather than arbitrary. Asserted as
        // stable rather than as any particular winner, which is all the source
        // claims.
        [Fact]
        public void TwoTagsOnOneVersionOrderStablyRatherThanArbitrarily()
        {
            var first = NeuralSpeech.VersionOrder.Compare("0.4.0-alpha", "0.4.0-beta");
            var again = NeuralSpeech.VersionOrder.Compare("0.4.0-alpha", "0.4.0-beta");

            Assert.Equal(first, again);
            Assert.NotEqual(0, first);

            // ...and the reverse comparison is the mirror image, which is what
            // makes it a valid ordering rather than merely a repeatable answer.
            Assert.Equal(
                -Math.Sign(first),
                Math.Sign(NeuralSpeech.VersionOrder.Compare("0.4.0-beta", "0.4.0-alpha")));
        }

        [Fact]
        public void AVersionEqualsItself()
        {
            Assert.Equal(0, NeuralSpeech.VersionOrder.Compare("0.4.1-beta", "0.4.1-beta"));
        }

        // The two unparseable names both parse to 0.0, so they fall through to the
        // string comparison rather than being reported equal — otherwise a sort
        // would be unstable between them.
        [Fact]
        public void TwoUnparseableNamesStillOrderAgainstEachOther()
        {
            Assert.NotEqual(0, NeuralSpeech.VersionOrder.Compare("junk-a", "junk-b"));
        }
    }
}
