namespace ClaudeBuddy
{
    // Who a message belongs to when the transcript does not say.
    //
    // Pure and separate for the same reason OrbGlyph is: this rule was wrong
    // three times in a row and each attempt could only be checked by opening a
    // panel and looking at it. See tests/GlyphTests.
    internal static class ChatSpeaker
    {
        // identityName is the agent named in the session key — Aurora for
        // agent:main:…, whoever the gateway says. title is the panel's heading,
        // which is the right answer for a terminal session, whose title is its
        // agent, and the wrong one for a room, whose title is the room. So the
        // identity wins wherever there is one.
        //
        // This is still the right answer for the header, which always names
        // something and is never asked about one turn. See
        // CanFallBackToSoleSpeaker below for the case this function's own
        // answer is wrong for.
        //
        // previous is the last good answer, and it is what makes this a
        // function rather than an expression. Both inputs can be empty for
        // reasons that are about us rather than about the conversation: a
        // terminal session has no title until its first hook write lands, and
        // the gateway's agent list is emptied and refetched across a
        // reconnect. Recomputing during either window used to produce "nobody"
        // and wipe the chips off a transcript that had been showing them.
        //
        // Knowing a name and then not knowing it is a gap in what we have been
        // told, never news about who was talking. The last good answer stands.
        public static string? Resolve(string? identityName, string? title, string? previous)
        {
            var name = !string.IsNullOrWhiteSpace(identityName) ? identityName : title;

            return string.IsNullOrWhiteSpace(name) ? previous : name;
        }

        // CB-36: whether an assistant-role turn with no Speaker of its own may
        // borrow Resolve's answer for its chip.
        //
        // Resolve's answer is "the one agent in this conversation" for a
        // terminal session and "the room" for a room with no identity — both
        // correct for what asked, per the comment on Resolve. A one-to-one
        // session has only ever one speaker, so every assistant turn in it may
        // borrow that answer and be right. A room does not: several agents
        // talk in it, which is exactly why OpenClawRoomChatSession.Rebuild
        // stamps a Speaker on every turn it can attribute. An assistant turn
        // that reaches the panel with none was built by Rebuild's own "left,
        // neutral, no name" branch — the room does not know who said it — and
        // borrowing Resolve's title answer there would draw the channel
        // itself as the speaker, which is the bug this ticket is about.
        //
        // A user-role turn is excluded independently of isRoom, by the caller
        // checking the role first: your own words are yours whoever else is
        // in the room, and a system note is about the conversation rather
        // than in it, so neither should ever wear an agent's name.
        public static bool CanFallBackToSoleSpeaker(bool isRoom) => !isRoom;
    }
}
