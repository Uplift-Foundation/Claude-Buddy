using System.Text.RegularExpressions;

namespace ClaudeBuddy
{
    // The one piece of the Remote Control relay's protocol still in use: reading
    // a `<cross-session-message>` tag out of a piece of transcript text.
    //
    // This file used to be the whole relay protocol — the prompts Buddy pasted
    // into its hidden relay session, the parser for the `ListAgents` peer list,
    // the send-receipt and health readers, the stall detector. The relay went in
    // 937de9ec and CB-238 removed what it stranded here; the formats it read are
    // recorded in docs/remote-control-findings.md, which is where they belong
    // now that nothing parses them.
    //
    // What stays is used by RemoteControlChatSession.Echoes: a message typed
    // into a far session over the direct link can come back in that session's
    // transcript wrapped in the tag, and recognising it is how the panel avoids
    // showing it twice.
    //
    // Strictness follows ChatTranscript.ParseDialog's rule: anything unexpected
    // is dropped rather than guessed at.
    public static class BridgeProtocol
    {
        // A message another session sent, as a `<cross-session-message>` tag
        // carries it. It once also carried the account whose relay read it, for
        // routing; nothing routes these any more (CB-238), so that is gone.
        public readonly record struct InboundMessage(
            string FromName, string From, string Mode, string Body);

        // <cross-session-message from="bridge:session_01SX9H…" from-name="job-hunter" from-mode="prompting">
        // avatar.internal
        // </cross-session-message>
        //
        // Attributes are matched individually rather than as one fixed sequence,
        // so a reordering or a new attribute doesn't drop the message. Singleline
        // so a multi-paragraph reply survives intact.
        private static readonly Regex CrossSessionTag = new(
            @"<cross-session-message\s+(?<attrs>[^>]*)>(?<body>.*?)</cross-session-message>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex Attr = new(
            @"(?<key>[a-z\-]+)\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.Compiled);

        // Every message in the row, in the order they appear.
        //
        // Plural because Match was wrong and quietly so. One transcript row can
        // carry two tags — two sessions answering at once land in one turn, and
        // the mirror makes that ordinary rather than rare, since it sends frames
        // as fast as the relay will take them. Reading only the first dropped
        // the rest with nothing to show it had happened: no error, no gap, just
        // a message that never arrived.
        public static IReadOnlyList<InboundMessage> ParseInboundMessages(string rowText)
        {
            var found = new List<InboundMessage>();
            if (string.IsNullOrEmpty(rowText)) return found;

            foreach (Match m in CrossSessionTag.Matches(rowText))
            {
                string? fromName = null, from = null, mode = null;
                foreach (Match a in Attr.Matches(m.Groups["attrs"].Value))
                {
                    switch (a.Groups["key"].Value)
                    {
                        case "from-name": fromName = a.Groups["value"].Value; break;
                        case "from": from = a.Groups["value"].Value; break;
                        case "from-mode": mode = a.Groups["value"].Value; break;
                    }
                }

                // Without a sender there is nothing to attribute the message to,
                // and a chat bubble on the wrong machine's panel is worse than a
                // dropped one.
                if (string.IsNullOrWhiteSpace(fromName)) continue;

                found.Add(new InboundMessage(
                    fromName!,
                    from ?? "",
                    mode ?? "",
                    m.Groups["body"].Value.Trim()));
            }

            return found;
        }
    }
}
