namespace ClaudeBuddy
{
    // What one OpenClaw transcript costs while nobody is looking at it, and
    // which ones to let go of — CB-92.
    //
    // The leak this answers is not the transcript cap. `OpenClawChatSession.Add`
    // has always held the live tail to 500 turns, and `PrependHistory`
    // deliberately does not trim, because scrolling back is *meant* to go past
    // the cap — a reader who has paged an hour into a conversation should not
    // have the top of it disappear behind them. That was cheap while an inline
    // picture was a url waiting to be fetched. It stopped being cheap once CB-91
    // began decoding `{type:"image", data:"<base64>"}` blocks into real byte
    // arrays, because the array is then the picture, in full, held by the turn.
    //
    // The dominant cost, though, is that nothing ever let a *session* go at all.
    // `OpenClawSessions.Chats` gained an entry the first time a panel — or the
    // orb's speak button, or a room merge — asked for a conversation, and had no
    // line anywhere that removed one. So a transcript scrolled back through in
    // the morning was still resident in the evening with no window open on it
    // and no `TurnView` in existence: no amount of virtualising the rows can
    // reach that, because the rows are not what is holding the bytes.
    //
    // A turn-count cap on `PrependHistory` was considered and rejected. It
    // fights the scroll-back behaviour above, and turn count is a poor proxy for
    // what is actually being spent: the turns in one page range from about forty
    // bytes of text to a two-hundred-kilobyte screenshot, so a count that is
    // generous enough for the text case is meaningless for the picture case and
    // a count tight enough for the picture case throws away the conversation.
    // Bytes are what ran out, so bytes are what is measured.
    //
    // Pure on purpose, in the same spirit as OrbArrangement and OrbGlyph: no
    // dictionary, no lock, no clock of its own. It is handed a description of
    // what is resident and answers what to do about it, which is the part worth
    // having a case per outcome for.
    internal static class OpenClawChatMemory
    {
        // How long a conversation stays whole after its panel closes.
        //
        // Not zero, because closing a panel is routinely something the user is
        // about to undo: clicking a second orb closes the first panel, and
        // clicking back is one of the commonest things anybody does here. An
        // eviction on that click would spend a round trip re-fetching a page
        // that was on screen a second ago, and the user would watch it arrive.
        // Two minutes is long enough that a glance at another agent costs
        // nothing and short enough that a conversation left behind does not
        // survive the afternoon.
        internal static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(2);

        // How many bytes of decoded picture may sit in conversations nobody has
        // open, before the oldest of them start giving theirs up early.
        //
        // The grace above is a *time* bound and says nothing about size: six
        // panels closed in the same minute are all inside it, and six
        // screenshot-heavy transcripts is exactly the shape that hurt. This is
        // the second bound, so a burst is capped even while every one of them is
        // still young enough to keep.
        //
        // Deliberately generous. It is not a budget for what the app should use;
        // it is the point past which holding pictures for conversations that are
        // not on screen stops being a convenience and starts being the bug.
        internal const long DefaultBudgetBytes = 32L * 1024 * 1024;

        // One conversation, as the sweep sees it.
        //
        // `IdleSince` is when the last panel on this conversation closed, or
        // when it was created for a conversation that never had one — the orb's
        // speak button and a room's member merge both make sessions nobody ever
        // opened, and they age out on exactly the same clock rather than living
        // forever because no panel was ever there to close.
        internal readonly record struct ChatResidency(
            string Key, bool PanelOpen, DateTime IdleSince, long ImageBytes);

        // What to do: forget these conversations entirely, and — for ones still
        // inside the grace — drop the pictures out of these while keeping the
        // words.
        //
        // Two lists rather than one verdict per chat because they are genuinely
        // different acts. Evicting gives back the turns as well as the pictures
        // and costs a re-fetch when the conversation is next opened; releasing
        // gives back only the pictures, which the gateway re-sends with the
        // history anyway, and leaves the transcript where it was.
        internal readonly record struct Plan(
            IReadOnlyList<string> Evict, IReadOnlyList<string> Release);

        // What this transcript's inline pictures weigh.
        //
        // Only `ImageBytes`: a turn carrying an `ImageUrl` has not fetched
        // anything into this process, and the text is not what ran the app out
        // of memory.
        internal static long ResidentBytes(IReadOnlyList<ChatTurn> turns)
        {
            long total = 0;
            foreach (var turn in turns)
            {
                if (turn.ImageBytes is { Length: > 0 } bytes) total += bytes.Length;
            }

            return total;
        }

        internal static Plan Decide(
            IReadOnlyList<ChatResidency> chats, DateTime now, long budgetBytes, TimeSpan grace)
        {
            var evict = new List<string>();
            var release = new List<string>();

            // Oldest idle first, so both bounds spend the conversation that has
            // been closed longest before one that was closed a moment ago. The
            // order is also what makes the answer deterministic, which is what
            // lets a test assert *which* chat gave its pictures up rather than
            // only how many did.
            var idle = chats
                .Where(c => !c.PanelOpen)
                .OrderBy(c => c.IdleSince)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .ToList();

            var keeping = new List<ChatResidency>();
            foreach (var chat in idle)
            {
                if (now - chat.IdleSince >= grace) evict.Add(chat.Key);
                else keeping.Add(chat);
            }

            // An open panel is what the user is looking at and is never touched
            // by either bound — not even counted towards the budget. The budget
            // is about what is held for nobody; a conversation on screen is
            // held for somebody, and taking its pictures away is a visible
            // defect rather than a saving.
            var resident = keeping.Sum(c => c.ImageBytes);

            foreach (var chat in keeping)
            {
                if (resident <= budgetBytes) break;

                // A transcript with no pictures in it cannot pay anything
                // towards the budget. Skipped rather than listed, so the plan
                // does not name a chat that releasing would do nothing to and
                // so the loop cannot spin on one.
                if (chat.ImageBytes <= 0) continue;

                release.Add(chat.Key);
                resident -= chat.ImageBytes;
            }

            return new Plan(evict, release);
        }
    }
}
