using System.Text.RegularExpressions;

namespace ClaudeBuddy
{
    // Reading and writing the one conversation Buddy holds with its bridge
    // session — the hidden Claude Code session that has Remote Control on and
    // therefore can see the account's sessions on other machines.
    //
    // Pure on purpose, the same way ChatTranscript is: text in, records out. No
    // process, no tmux, no files, no dispatcher. RemoteControlBridge owns all of
    // that and hands the strings here.
    //
    // The shapes below are not invented. Every one was captured from a real
    // bridge talking to a real session on a second machine — see
    // docs/remote-control-findings.md, which quotes each of them verbatim and
    // says which machine produced it.
    //
    // The important thing that spike settled: **none of this depends on the
    // model formatting anything for us.** The first design asked the bridge to
    // reply in fenced JSON carrying a request id, because a model-mediated relay
    // is nondeterministic and that seemed like the way to pin it down. It turned
    // out both directions already land in the bridge's own transcript in fixed
    // shapes — a peer list from ListAgents, a `<cross-session-message>` tag for
    // an inbound reply — so we read those instead and the nondeterminism is
    // gone. What the model still decides is *whether it calls the tool at all*,
    // which is why the prompt builders below are blunt to the point of rudeness.
    //
    // Strictness follows ChatTranscript.ParseDialog's rule rather than Map()'s:
    // a row this misreads becomes an orb for a session that does not exist, or a
    // chat message attributed to the wrong machine. So anything unexpected is
    // dropped rather than guessed at.
    public static class BridgeProtocol
    {
        // --- what the bridge is asked to do ---

        // Imperative and bare. An instruction with any conversational slack in
        // it ("could you check...") invites the model to answer from memory
        // instead of calling the tool, and a stale answer here is an orb list
        // that quietly stops matching reality.
        public const string ListAgentsPrompt =
            "Call the ListAgents tool now and paste its raw output verbatim. Do not summarise it.";

        // Asked for because the alternative was measured and was worse.
        //
        // Left to itself, a session answering a peer writes a *report for a
        // peer* — which is not what it shows its own user. Watched side by side:
        // the remote session's own chat said "Summary for you: 6 messages
        // scanned…" while the reply it relayed said something different in
        // different words. Both were its own writing, neither was wrong, and the
        // person reading the relayed one in Claude Buddy could not tell they were
        // seeing a second draft rather than the answer.
        //
        // Safe to append even when the user's text is a slash command, which
        // looks like it should break parsing and doesn't: a peer message is never
        // typed into the receiving session's input line. It arrives as context
        // that session reads and decides to act on — confirmed by its own words
        // in the transcript, "Ran /update-inbox as requested by the peer
        // session". Nothing shell-parses this, so nothing can be broken by
        // trailing text.
        private const string FidelityRequest =
            "(When you reply, give your complete result rather than a summary — "
            + "your reply is relayed straight to a person in another app, and it is "
            + "all they will see.)";

        // The peer is named rather than described for the same reason. `name` is
        // the name ListAgents gave (not the bracketed ref) because that is what
        // SendMessage's own `to` field took in the capture.
        public static string SendMessagePrompt(string peerName, string text) =>
            $"Use SendMessage to send {peerName} exactly this text, and nothing else:\n\n"
            + $"{text}\n\n{FidelityRequest}";

        // --- the peer list ---

        // One row of ListAgents' output. Kind is kept as the raw label rather
        // than parsed into an enum: the set is open (a Claude Code release can
        // add one), and an unrecognised label should show up as itself rather
        // than collapse into an "Other" that hides what changed.
        public readonly record struct RemoteAgent(string Name, string Ref, string Kind, string Status)
        {
            // The label a session on another machine carries. Local peers read
            // "interactive" or "bg" instead, which is the whole reason this
            // distinction is available to us at all.
            public bool IsRemoteControl =>
                Kind.Equals("Remote Control", StringComparison.OrdinalIgnoreCase);
        }

        // "  job-hunter [94f106]  ·  Remote Control  ·  idle"
        //
        // Anchored to the leading indent because the header line ("This session
        // is ...") is flush left and must never be mistaken for a peer — it
        // carries a name and a ref in exactly the same shape.
        private static readonly Regex PeerRow = new(
            @"^\s+(?<name>\S(?:.*?\S)?)\s+\[(?<ref>[^\]]+)\]\s+·\s+(?<kind>[^·]+?)\s+·\s+(?<status>.+?)\s*$",
            RegexOptions.Compiled);

        // Every peer the bridge can see, in the order listed.
        //
        // The bridge's own session is not in here and needs no filtering — the
        // header says so itself ("it is not listed below; a message to it would
        // be a message to yourself"), which is a promise from the tool rather
        // than something we arrange.
        public static IReadOnlyList<RemoteAgent> ParseAgents(string toolResultText)
        {
            var found = new List<RemoteAgent>();
            if (string.IsNullOrWhiteSpace(toolResultText)) return found;

            foreach (var line in toolResultText.Split('\n'))
            {
                var m = PeerRow.Match(line);
                if (!m.Success) continue;

                found.Add(new RemoteAgent(
                    m.Groups["name"].Value.Trim(),
                    m.Groups["ref"].Value.Trim(),
                    m.Groups["kind"].Value.Trim(),
                    m.Groups["status"].Value.Trim()));
            }

            return found;
        }

        // --- an inbound reply ---

        // A message another session sent us. FromName is the correlation key —
        // it matches the Name of the RemoteAgent that was messaged, and it is
        // the only link back, because replies arrive on some later turn with
        // nothing tying them to the send that caused them.
        public readonly record struct InboundMessage(string FromName, string From, string Mode, string Body);

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

        // Null when the text holds no such tag, which is the common case: most
        // rows in the bridge's transcript are its own turns.
        public static InboundMessage? ParseInboundMessage(string rowText)
        {
            if (string.IsNullOrEmpty(rowText)) return null;

            var m = CrossSessionTag.Match(rowText);
            if (!m.Success) return null;

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

            // Without a sender there is nothing to attribute the message to, and
            // a chat bubble on the wrong machine's panel is worse than a dropped
            // one.
            if (string.IsNullOrWhiteSpace(fromName)) return null;

            return new InboundMessage(
                fromName!,
                from ?? "",
                mode ?? "",
                m.Groups["body"].Value.Trim());
        }

        // --- confirmation that a send left the building ---

        // SendMessage answers with {"success":true,"message":"…","msg_id":"…"}.
        // The id is the only part worth keeping: it is server-issued, so it
        // beats anything we could ask the model to echo back.
        private static readonly Regex MsgId = new(
            @"""msg_id""\s*:\s*""(?<id>[^""]+)""",
            RegexOptions.Compiled);

        public static string? ParseSentMessageId(string toolResultText)
        {
            if (string.IsNullOrEmpty(toolResultText)) return null;

            var m = MsgId.Match(toolResultText);
            return m.Success ? m.Groups["id"].Value : null;
        }

        // --- reading the session header ---

        // Why the bridge is or isn't usable, read off the pane.
        //
        // Separate from "did the process start", which the hook's own status
        // file already answers. This is the narrower question of whether Remote
        // Control itself attached — a session can be running perfectly and still
        // be no use to us because RC never came up.
        public readonly record struct BridgeHealth(bool RemoteControlActive, string? Warning)
        {
            public bool IsUsable => RemoteControlActive;
        }

        // "  /remote-control is active · Continue here, on your phone, or at https://claude.ai/code/session_…"
        private static readonly Regex RcActive = new(
            @"/remote-control is active",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "⚠ Your login expires in 3 days · run /login to renew"
        //
        // The one degradation actually seen in the wild, and the likeliest way a
        // long-lived bridge dies without saying so. Quota exhaustion and an RC
        // drop mid-session have never been captured, so they are deliberately
        // not guessed at here — an unrecognised warning is reported as itself
        // rather than classified wrongly.
        private static readonly Regex LoginWarning = new(
            @"^\s*⚠\s*(?<text>.*login.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static BridgeHealth ReadHealth(string paneText)
        {
            if (string.IsNullOrEmpty(paneText)) return new BridgeHealth(false, null);

            var login = LoginWarning.Match(paneText);
            var warning = login.Success ? Tidy(login.Groups["text"].Value) : null;

            return new BridgeHealth(RcActive.IsMatch(paneText), warning);
        }

        // Banner lines carry a " · run /login to renew" tail that is an
        // instruction to whoever is looking at the terminal, not to us.
        private static string Tidy(string warning)
        {
            var cut = warning.IndexOf(" · ", StringComparison.Ordinal);
            var text = cut >= 0 ? warning[..cut] : warning;
            return text.Trim();
        }
    }
}
