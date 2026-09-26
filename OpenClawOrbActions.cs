using System.Text.Json;

namespace ClaudeBuddy
{
    // CB-170: what an OpenClaw orb's two gateway rows — "Interrupt the current
    // run" and "End the conversation" — need to know, and what to make of the
    // answers. Everything here is pure: no socket, no window, no settings. The
    // requests themselves are OpenClawSessions.InterruptAsync and
    // EndConversationAsync; the rule for when a row is offered is in
    // SessionPresence beside CanEndSession; the wording is OpenClawActionText.
    //
    // Measured against a real gateway (OpenClaw 2026.9.2) before any of this was
    // written, and recorded on CB-170: both verbs need operator.write and
    // nothing more; chat.abort on a session with nothing running answers
    // aborted:false rather than failing; sessions.patch refuses to archive
    // without expectedSessionId; the gateway refuses to archive an agent's main
    // session. The rest of this file is shaped by those four facts.

    // What the menu is allowed to assume about one orb's conversation at the
    // moment it is drawn. Built by OpenClawSessions.CapabilitiesFor from the
    // live connection and the last session list; a test builds one by hand.
    internal sealed record OpenClawActionContext(
        bool Connected,

        // hello-ok's features.methods. Not filtered by scope — the gateway
        // lists sessions.reset to a device that cannot call it — which is why
        // every rule reads Scopes as well.
        IReadOnlySet<string> Methods,

        // What the gateway actually granted this device on connect.
        IReadOnlyList<string> Scopes,

        bool IsMain,

        // The id sessions.patch demands as expectedSessionId. Null when the
        // session is not in the last list, which is also when End cannot work.
        string? SessionId,

        // The gateway's key for the conversation, without the app's
        // "openclaw:" prefix.
        string? Key)
    {
        public static readonly OpenClawActionContext None = new(
            false, new HashSet<string>(), Array.Empty<string>(), false, null, null);
    }

    public enum OpenClawAction
    {
        Interrupt,
        End,
    }

    public enum OpenClawActionOutcome
    {
        Done,

        // chat.abort answered aborted:false. Not a failure: the gateway was
        // asked to stop a run and there was none.
        NothingRunning,

        // chat.abort refused "unauthorized". A device without admin may abort
        // only runs it started itself or runs with no owner, so a run started
        // from somewhere else — the Control UI in a browser, another Buddy — is
        // out of reach. Declared by the gateway's source, not yet seen live.
        OtherDevice,

        // Any other answer from the gateway, carried verbatim as the detail.
        Refused,

        NotConnected,
    }

    internal static class OpenClawOrbActions
    {
        public const string AbortMethod = "chat.abort";
        public const string PatchMethod = "sessions.patch";
        public const string WriteScope = "operator.write";

        // The gateway's code for a refusal worth asking again. An archive sent
        // while the run it follows is still settling is refused this way, with
        // "retry the archive" in the message.
        public const string RetryableCode = "UNAVAILABLE";

        // chat.abort's answer. Only an explicit true is "interrupted", and only
        // an explicit false is "nothing was running"; a reply that says neither
        // is the gateway not telling us, and claiming either would be a guess
        // presented as a result.
        internal static (OpenClawActionOutcome Outcome, string? Detail) ParseAbortResult(JsonElement res)
        {
            if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("aborted", out var aborted))
            {
                if (aborted.ValueKind == JsonValueKind.True) return (OpenClawActionOutcome.Done, null);
                if (aborted.ValueKind == JsonValueKind.False) return (OpenClawActionOutcome.NothingRunning, null);
            }

            return (OpenClawActionOutcome.Refused, "the gateway didn't say whether anything stopped");
        }

        // sessions.patch's answer. The gateway sends ok:true with the updated
        // row; anything else is not a confirmed archive, whatever it is.
        internal static (OpenClawActionOutcome Outcome, string? Detail) ParsePatchResult(JsonElement res) =>
            res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("ok", out var ok)
            && ok.ValueKind == JsonValueKind.True
                ? (OpenClawActionOutcome.Done, null)
                : (OpenClawActionOutcome.Refused, "the gateway didn't confirm the archive");

        // A request that threw. "unauthorized" is matched exactly, on the
        // message, because that is the whole of what the gateway sends for it —
        // an INVALID_REQUEST whose message is the one word, with no detail code.
        // Everything else is shown as the gateway worded it: "missing scope:
        // operator.write" is more useful on the row than any paraphrase.
        internal static (OpenClawActionOutcome Outcome, string? Detail) ClassifyFailure(Exception ex) =>
            ex is OpenClawRequestException { Message: "unauthorized" }
                ? (OpenClawActionOutcome.OtherDevice, null)
                : (OpenClawActionOutcome.Refused, ex.Message);

        internal static bool IsRetryable(Exception ex) =>
            ex is OpenClawRequestException { Code: RetryableCode };
    }

    // Every word the two rows say, in one table so the menu and its tests read
    // the same sentences.
    internal static class OpenClawActionText
    {
        // Says it can be undone, because it can: an archive is reversed from
        // OpenClaw with archived:false, history intact (measured). Worded as
        // permanent it would be a warning about something that isn't true.
        public const string Armed = "End it? Click again (can be restored on the gateway)";

        public static string Header(OpenClawAction action) => action == OpenClawAction.Interrupt
            ? "Interrupt the current run"
            : "End the conversation";

        public static string Tip(OpenClawAction action) => action == OpenClawAction.Interrupt
            ? "Stops the agent generating. The conversation stays."
            : "Archives this conversation on the gateway, so every client stops showing it. It can be restored from OpenClaw.";

        public static string Working(OpenClawAction action) => action == OpenClawAction.Interrupt
            ? "Interrupting…"
            : "Ending…";

        public static string For(OpenClawActionOutcome outcome, string? detail, OpenClawAction action)
        {
            var verb = action == OpenClawAction.Interrupt ? "interrupt" : "end";

            return outcome switch
            {
                OpenClawActionOutcome.Done => action == OpenClawAction.Interrupt ? "Interrupted" : "Ended",
                OpenClawActionOutcome.NothingRunning => "Nothing was running",
                OpenClawActionOutcome.OtherDevice => action == OpenClawAction.Interrupt
                    ? "Started from another device"
                    : "Couldn't end: a run started from another device is still going",
                OpenClawActionOutcome.NotConnected => $"Couldn't {verb}: not connected to the gateway",
                _ => $"Couldn't {verb}: {detail}",
            };
        }
    }
}
