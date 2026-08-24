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

        // --- asking a session what colour it is ---

        // A remote session's colour cannot be derived, only asked for.
        //
        // A local orb gets its colour from the hook, which reads it one of two
        // ways: `/color` writes {"type":"agent-color","agentColor":…} into that
        // session's own transcript, and auto-colour hashes the session's *cwd*.
        // A peer row carries neither the transcript nor the cwd, so there is no
        // arithmetic that recovers it — the only way is to ask the session
        // itself.
        //
        // The marker is the important part. A reply comes back as an ordinary
        // cross-session message, indistinguishable from an answer meant for the
        // person reading the panel, so without something to recognise it by the
        // word "green" would appear in their chat as though the remote session
        // had said it to them. Echoing a fixed prefix makes the answer
        // identifiable and lets it be swallowed.
        public const string InfoMarker = "CB-INFO:";

        // One question, two answers, because both cost the same round trip and
        // asking twice would double a bill nobody wants.
        //
        // The commands half exists because the first version of this feature got
        // it exactly backwards. It offered Claude Code's *built-in* commands to
        // remote sessions and withheld the custom ones, on the reasoning that
        // built-ins ship with the CLI and custom ones live on the far machine.
        // That is true and it is the wrong way round: a peer message is never
        // typed into the receiving session's input line, so its command handler
        // never sees it. The model reads the message and decides what to do,
        // which means a *custom* command works — it can read the command file and
        // follow it — and a built-in cannot, because only the CLI itself can run
        // one.
        //
        // Measured, not reasoned: /update-inbox ran on the far machine and came
        // back with results, while /color came back with "I can't run /color —
        // it's not one of my available skills/tools ... only the harness's own
        // command handler can set" it. So the only honest list is the one the
        // session itself reports.
        public static string CapabilitiesQueryPrompt(string peerName) =>
            $"Use SendMessage to send {peerName} exactly this text, and nothing else:\n\n"
            + $"Reply with only one line, no other words, in this exact form: "
            + $"{InfoMarker} color=<the colour your /color is set to, or none>; "
            + $"commands=<a comma-separated list of the custom slash commands and skills you can "
            + $"actually run, or none>";

        // Known colour names, from the palette OrbWindow actually draws
        // (AgentColors). Matched against that rather than accepted as free text
        // so a chatty answer — "I'm set to green!" — yields "green" and an
        // unrecognised one yields nothing, leaving the derived colour in place.
        private static readonly string[] ColorNames =
        {
            "red", "orange", "yellow", "green", "teal", "cyan", "blue",
            "purple", "violet", "magenta", "pink", "gray", "grey", "white"
        };

        private static readonly Regex HexColor = new(@"#[0-9a-fA-F]{6}", RegexOptions.Compiled);

        // True for a reply that is answering the colour question rather than
        // talking to the user. Kept separate from ParseColorReply so an
        // unparseable answer is still swallowed rather than shown — the person
        // never asked the question, so they should not see the fumbled answer.
        public static bool IsInfoReply(string body) =>
            body is not null && body.Contains(InfoMarker, StringComparison.OrdinalIgnoreCase);

        // The commands the far session says it can run, as SlashCommand rows.
        //
        // Capped, because this becomes an autocomplete list and a session with a
        // hundred skills would push the input box off the screen. Descriptions
        // are deliberately empty: the far session was asked for names only, and
        // inventing a description for someone else's command would be a guess
        // presented as documentation.
        //
        // The slash is optional, and that is the whole lesson of this parser.
        // The first version required it, because the question asks for slash
        // commands and the answer is a list of them; the mini answered
        // `commands=apply,apply-ic,cold-intro,…` — twenty-seven of them, not one
        // wearing a slash — and the parser read zero. Reasonable of it: it was
        // asked what it can run, and it named them. Asking again more firmly
        // would still be trusting a model to punctuate a list, so the slash goes
        // on here instead, where it cannot be forgotten.
        //
        // Which cannot mean "any word after commands= is a command", or a
        // session that answers in a sentence would fill the autocomplete with
        // /I, /can and /run. So the shape of the answer decides how to read it:
        // if anything in it wears a slash, only slashed names count, because
        // that session punctuates and the unslashed words around them are
        // prose. Otherwise it has to be a list — split on commas and semicolons,
        // and every piece has to be a bare name on its own. "none available" has
        // a space in it and is thrown away, which is the correct reading of it.
        private static readonly Regex SlashedName = new(@"/[A-Za-z][\w-]*", RegexOptions.Compiled);
        private static readonly Regex BareName = new(@"^[A-Za-z][\w-]*$", RegexOptions.Compiled);

        public static IReadOnlyList<SlashCommand> ParseCommandsReply(string body)
        {
            var found = new List<SlashCommand>();
            if (string.IsNullOrEmpty(body)) return found;

            var at = body.IndexOf("commands=", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return found;

            var segment = body[(at + "commands=".Length)..];

            // The other half of the reply, if it came second, is not a command.
            var other = segment.IndexOf("color=", StringComparison.OrdinalIgnoreCase);
            if (other >= 0) segment = segment[..other];

            var names = segment.Contains('/')
                ? SlashedName.Matches(segment).Select(m => m.Value)
                : segment.Split(',', ';').Select(p => p.Trim())
                    .Where(p => BareName.IsMatch(p))
                    .Select(p => "/" + p);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                // "none" is how a session with nothing to offer answers, and must
                // not become a command called /none.
                if (name.Equals("/none", StringComparison.OrdinalIgnoreCase)) continue;

                if (!seen.Add(name)) continue;
                found.Add(new SlashCommand(name, ""));
                if (found.Count == 60) break;
            }

            return found;
        }

        // The colour, or null for "none" and for anything unrecognised.
        public static string? ParseColorReply(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;

            var at = body.IndexOf("color=", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            // Stops at the separator so "commands=/colorise" downstream cannot
            // be read as the colour.
            var rest = body[(at + "color=".Length)..];
            var end = rest.IndexOf(';');
            var answer = (end >= 0 ? rest[..end] : rest).Trim();

            var hex = HexColor.Match(answer);
            if (hex.Success) return hex.Value;

            foreach (var name in ColorNames)
            {
                // Word-ish match so "none" cannot match "orange" by substring,
                // and a sentence around the word still resolves.
                if (Regex.IsMatch(answer, $@"\b{name}\b", RegexOptions.IgnoreCase)) return name;
            }

            return null;
        }

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

            // A relay of Buddy's own, not a session anyone wants an orb for.
            //
            // The current relay excludes itself — ListAgents says so in its own
            // header — but a *previous* one does not: its Remote Control
            // registration outlives the process, so an earlier relay turns up as
            // a peer with status "offline". Observed exactly that way, and it
            // would have put a phantom orb on screen named after Buddy's own
            // plumbing.
            //
            // Matched on the name prefix RemoteControlBridge builds, which is the
            // only thing about a relay that is recognisable from out here.
            public bool IsOwnRelay =>
                Name.StartsWith("claude-buddy-rc-", StringComparison.OrdinalIgnoreCase);

            // A session that has gone away. Worth an orb only if it is actually
            // there — "offline" is the state a peer's registration sits in after
            // its process is gone, and drawing it would be an orb for something
            // nothing can be sent to.
            public bool IsOffline =>
                Status.Contains("offline", StringComparison.OrdinalIgnoreCase);

            // Everything that has to be true for this to become an orb.
            public bool IsWorthAnOrb => IsRemoteControl && !IsOwnRelay && !IsOffline;
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
        // Account is filled in by RemoteControlSessions, not by the parser — a
        // transcript row has no idea which relay read it. It is here rather than
        // alongside because routing needs it: with one relay per account, a name
        // alone no longer identifies which conversation a reply belongs to.
        public readonly record struct InboundMessage(
            string FromName, string From, string Mode, string Body, string Account = "");

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

        // Why a relay is stuck on a screen instead of starting, or null if it
        // isn't.
        //
        // This exists because the failure it catches is silent and expensive. An
        // account whose `.claude.json` has no `lastOnboardingVersion` gets the
        // onboarding theme picker on every new session — measured on a real
        // machine where one account recorded 2.1.220 and skipped it while
        // another recorded nothing and hit it every time. In a detached tmux
        // session nobody is looking at that screen, so nothing answers it: the
        // relay waits out its whole 45-second timeout and then reports "failed
        // to start", which is true and useless.
        //
        // Detected rather than answered. Picking a theme on someone's behalf is
        // not this app's business, and answering an unknown prompt blind is how
        // you end up accepting something nobody agreed to. Saying which screen
        // it is, so a person can run `claude` once themselves, is both cheaper
        // and honest.
        //
        // Matched on the numbered options rather than a title, because the title
        // varies by version while the option list is the distinctive part.
        private static readonly Regex ThemePrompt = new(
            @"Auto \(match terminal\)|Dark mode \(colorblind-friendly\)|Syntax theme:",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "Do you trust the files in this folder?" — the workspace-trust gate.
        // Same shape of problem: an interactive question with nobody to answer.
        private static readonly Regex TrustPrompt = new(
            @"Do you trust the files|trust the files in this folder",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string? ReadSetupBlock(string paneText)
        {
            if (string.IsNullOrEmpty(paneText)) return null;

            if (ThemePrompt.IsMatch(paneText))
            {
                return "this account has not finished Claude Code's first-run setup "
                     + "(it is asking which theme to use). Run `claude` in a terminal once "
                     + "under that account, answer it, then try again.";
            }

            if (TrustPrompt.IsMatch(paneText))
            {
                return "Claude Code is asking whether to trust this folder. Run `claude` in a "
                     + "terminal once from the same directory to answer it, then try again.";
            }

            return null;
        }

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
