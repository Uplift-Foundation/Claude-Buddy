namespace ClaudeBuddy
{
    // What kind of conversation a gateway session is.
    //
    // Only meaningful for OpenClaw: a local session is always someone at a
    // terminal, so it is Unknown and draws no badge. For a gateway session the
    // distinction is the difference between a room other people can read, a
    // private message, and a scheduled job with nobody on the other end — which
    // the title alone cannot carry, since "Zara — general" and "Zara — wtvamp"
    // are the same shape.
    public enum SessionKind
    {
        Unknown,

        // The agent's own session, reached through the TUI. Every agent has
        // one, so it is deliberately not badged — a mark on almost every orb
        // distinguishes nothing.
        Main,

        Direct,
        Channel,
        Cron
    }

    // Working out what kind of conversation a gateway session is, from the two
    // things the gateway says about it.
    //
    // Pure and string-valued rather than reaching into JSON, so it can be tested
    // — the same reason OrbArrangement and ChatTranscript are. This one earns it
    // for a reason those don't: the mistake it can make is **silent and
    // directional**. A channel mislabelled as a direct message says a room other
    // people can read is private, and nothing on screen would contradict it.
    public static class OpenClawSessionKind
    {
        // key is "agent:<name>:<surface>[:<type>:<id>]" and chatType is
        // origin.chatType, either of which may be missing.
        //
        // The key is consulted first because it is structural: an
        // "agent:x:cron:<uuid>" cannot be anything but a scheduled job, whatever
        // else is attached to it. chatType is the gateway's own word for a
        // conversation and is the only thing separating a DM from a channel, so
        // it decides wherever the key is uninformative — which it usually is,
        // since "agent:main:discord:…" says only which surface.
        public static SessionKind From(string? key, string? chatType)
        {
            var parts = (key ?? "").Split(':');

            if (parts.Length >= 3 && parts[0] == "agent")
            {
                if (Is(parts[2], "cron")) return SessionKind.Cron;

                // Every agent has one of these and it is reached through the
                // TUI rather than any chat surface, so it is its own kind rather
                // than a direct message.
                if (Is(parts[2], "main")) return SessionKind.Main;
            }

            // The key's fourth segment carries the same word when origin is
            // absent: "agent:main:discord:direct:2467…".
            var type = string.IsNullOrWhiteSpace(chatType)
                ? parts.Length >= 4 ? parts[3] : null
                : chatType;

            if (type is null) return SessionKind.Unknown;

            // Unrecognised is Unknown rather than a guess at the commoner of the
            // two. An unbadged orb says "I don't know"; a wrong badge says
            // something false about who can read the conversation.
            if (Is(type, "direct") || Is(type, "dm") || Is(type, "im")) return SessionKind.Direct;
            if (Is(type, "channel") || Is(type, "group") || Is(type, "guild")) return SessionKind.Channel;

            return SessionKind.Unknown;
        }

        private static bool Is(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
