namespace ClaudeBuddy
{
    // Who a message belongs to when the transcript does not say.
    //
    // Pure and separate for the same reason OrbGlyph is: this rule was wrong
    // three times in a row and each attempt could only be checked by opening a
    // panel and looking at it. See tests/GlyphTests.
    internal static class ChatSpeaker
    {
        // identityName is the agent named in the session key — Lilibeth for
        // agent:main:…, whoever the gateway says. title is the panel's heading,
        // which is the right answer for a terminal session, whose title is its
        // agent, and the wrong one for a room, whose title is the room. So the
        // identity wins wherever there is one.
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
    }
}
