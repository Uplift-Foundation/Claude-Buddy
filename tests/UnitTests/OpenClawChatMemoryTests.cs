using Xunit;

using Residency = ClaudeBuddy.OpenClawChatMemory.ChatResidency;

namespace ClaudeBuddy.Tests
{
    // OpenClawChatMemory: which conversations get let go of, and which only
    // give their pictures up — CB-92.
    //
    // A case per outcome rather than a smoke test, because both mistakes are
    // silent in opposite directions. Releasing too little is the bug this
    // ticket is: nothing on screen changes, the process just keeps every
    // decoded picture it has ever seen. Releasing too much is worse and just as
    // quiet — a conversation somebody is reading losing its pictures mid-scroll
    // looks like the gateway failing to serve them, not like a cache being
    // swept.
    public class OpenClawChatMemoryTests
    {
        private static readonly DateTime Now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

        private static Residency Idle(string key, TimeSpan ago, long bytes = 0)
            => new(key, PanelOpen: false, IdleSince: Now - ago, ImageBytes: bytes);

        private static Residency Open(string key, long bytes = 0)
            => new(key, PanelOpen: true, IdleSince: Now, ImageBytes: bytes);

        private static OpenClawChatMemory.Plan Decide(
            IReadOnlyList<Residency> chats, long budget = long.MaxValue)
            => OpenClawChatMemory.Decide(chats, Now, budget, Grace);

        // --- what a transcript weighs ---

        [Fact]
        public void AnEmptyTranscriptWeighsNothing()
            => Assert.Equal(0, OpenClawChatMemory.ResidentBytes(Array.Empty<ChatTurn>()));

        [Fact]
        public void OnlyDecodedPicturesAreCounted()
        {
            var turns = new List<ChatTurn>
            {
                new() { Text = "a wall of text, and not a byte of picture in it" },

                // A url is a picture this process has not fetched, so it costs
                // nothing here however large the file behind it is.
                new() { ImageUrl = "https://example.invalid/enormous.png" },

                new() { ImageBytes = new byte[1000] },
                new() { ImageBytes = new byte[24] },

                // An empty array is not a picture; counting it would be
                // harmless and asserting it keeps the rule "length, not
                // presence" honest.
                new() { ImageBytes = Array.Empty<byte>() }
            };

            Assert.Equal(1024, OpenClawChatMemory.ResidentBytes(turns));
        }

        // --- nothing to do ---

        [Fact]
        public void NothingResidentIsNothingToDo()
        {
            var plan = Decide(Array.Empty<Residency>());

            Assert.Empty(plan.Evict);
            Assert.Empty(plan.Release);
        }

        [Fact]
        public void AConversationSomebodyIsLookingAtIsNeverTouched()
        {
            // Old enough to evict twice over and heavy enough to blow any
            // budget — and open, which beats both.
            var plan = Decide(new[] { Open("watched", bytes: 500_000_000) }, budget: 0);

            Assert.Empty(plan.Evict);
            Assert.Empty(plan.Release);
        }

        [Fact]
        public void AnOpenPanelDoesNotCountTowardsTheBudgetEither()
        {
            // The open one alone is over budget. If it were counted, the idle
            // one would be released to pay for it — which would mean opening a
            // big conversation quietly strips the pictures out of a small one
            // somebody closed a second ago.
            var plan = Decide(
                new[] { Open("watched", bytes: 100), Idle("closed", TimeSpan.FromSeconds(1), bytes: 10) },
                budget: 50);

            Assert.Empty(plan.Evict);
            Assert.Empty(plan.Release);
        }

        [Fact]
        public void AConversationClosedWithinTheGraceIsKeptWhole()
        {
            var plan = Decide(new[] { Idle("recent", Grace - TimeSpan.FromSeconds(1), bytes: 10) });

            Assert.Empty(plan.Evict);
            Assert.Empty(plan.Release);
        }

        // --- the grace bound ---

        [Fact]
        public void AConversationIdleForTheWholeGraceIsEvicted()
        {
            var plan = Decide(new[] { Idle("stale", Grace) });

            Assert.Equal(new[] { "stale" }, plan.Evict);
            Assert.Empty(plan.Release);
        }

        [Fact]
        public void AnEvictedConversationIsNotAlsoListedForRelease()
        {
            // Evicting already gives the pictures back, so naming it twice
            // would have the caller release a transcript it had just dropped.
            var plan = Decide(new[] { Idle("stale", Grace * 2, bytes: 1_000_000) }, budget: 0);

            Assert.Equal(new[] { "stale" }, plan.Evict);
            Assert.Empty(plan.Release);
        }

        [Fact]
        public void EveryConversationPastTheGraceGoes()
        {
            var plan = Decide(new[]
            {
                Idle("oldest", Grace * 3),
                Idle("newer", Grace + TimeSpan.FromSeconds(1)),
                Idle("young", TimeSpan.FromSeconds(5))
            });

            // Oldest first, which is what makes the answer worth asserting
            // rather than sorting before comparing.
            Assert.Equal(new[] { "oldest", "newer" }, plan.Evict);
        }

        // --- the byte bound ---

        [Fact]
        public void PicturesGoWhenTheClosedConversationsTogetherExceedTheBudget()
        {
            var plan = Decide(
                new[]
                {
                    Idle("a", TimeSpan.FromSeconds(30), bytes: 60),
                    Idle("b", TimeSpan.FromSeconds(10), bytes: 60)
                },
                budget: 100);

            Assert.Empty(plan.Evict);

            // One is enough to get back under, and it is the one closed
            // longest ago — the other is the likelier of the two to be
            // reopened next.
            Assert.Equal(new[] { "a" }, plan.Release);
        }

        [Fact]
        public void ReleasingStopsAsSoonAsItIsBackUnderBudget()
        {
            var plan = Decide(
                new[]
                {
                    Idle("a", TimeSpan.FromSeconds(30), bytes: 100),
                    Idle("b", TimeSpan.FromSeconds(20), bytes: 100),
                    Idle("c", TimeSpan.FromSeconds(10), bytes: 100)
                },
                budget: 150);

            // Two of the three, not all of them: 300 resident is over 150 after
            // the first release and under it after the second, so the youngest
            // keeps its pictures.
            Assert.Equal(new[] { "a", "b" }, plan.Release);
        }

        [Fact]
        public void EnoughGoToGetUnderTheBudget()
        {
            var plan = Decide(
                new[]
                {
                    Idle("a", TimeSpan.FromSeconds(30), bytes: 100),
                    Idle("b", TimeSpan.FromSeconds(20), bytes: 100),
                    Idle("c", TimeSpan.FromSeconds(10), bytes: 100)
                },
                budget: 50);

            Assert.Equal(new[] { "a", "b", "c" }, plan.Release);
        }

        [Fact]
        public void ExactlyAtTheBudgetIsNotOverIt()
        {
            var plan = Decide(new[] { Idle("a", TimeSpan.FromSeconds(30), bytes: 100) }, budget: 100);

            Assert.Empty(plan.Release);
        }

        [Fact]
        public void AConversationWithNoPicturesIsNeverReleased()
        {
            // It cannot pay anything towards the budget, and naming it would
            // make the plan claim a saving that is not there — which is the
            // same "a count is not a cost" mistake the rejected turn-count fix
            // would have made.
            var plan = Decide(
                new[]
                {
                    Idle("text-only", TimeSpan.FromSeconds(30)),
                    Idle("pictures", TimeSpan.FromSeconds(10), bytes: 400)
                },
                budget: 100);

            Assert.Equal(new[] { "pictures" }, plan.Release);
        }

        [Fact]
        public void APlanIsStableWhenTwoConversationsWentIdleTogether()
        {
            // Same instant, so the timestamp cannot order them. Without the
            // tie-break the answer would depend on dictionary order, and a test
            // asserting which one was released would pass or fail by luck.
            var plan = Decide(
                new[]
                {
                    Idle("zeta", TimeSpan.FromSeconds(30), bytes: 100),
                    Idle("alpha", TimeSpan.FromSeconds(30), bytes: 100)
                },
                budget: 150);

            Assert.Equal(new[] { "alpha" }, plan.Release);
        }

        // --- the two bounds together ---

        [Fact]
        public void TheOldOneIsEvictedAndTheHeavyRecentOneIsReleased()
        {
            var plan = Decide(
                new[]
                {
                    Idle("stale", Grace * 2, bytes: 10),
                    Idle("heavy", TimeSpan.FromSeconds(30), bytes: 400),
                    Open("watched", bytes: 400)
                },
                budget: 100);

            Assert.Equal(new[] { "stale" }, plan.Evict);
            Assert.Equal(new[] { "heavy" }, plan.Release);
        }

        // --- the shipped bounds ---

        [Fact]
        public void TheShippedBoundsAreTheOnesTheCommentArguesFor()
        {
            // Both are read by SweepChats' no-argument overload and by nothing
            // else, so a change to either is a change to the app's behaviour
            // with no other test standing in its way.
            Assert.Equal(TimeSpan.FromMinutes(2), OpenClawChatMemory.DefaultGrace);
            Assert.Equal(32L * 1024 * 1024, OpenClawChatMemory.DefaultBudgetBytes);
        }
    }
}
