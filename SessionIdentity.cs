namespace ClaudeBuddy
{
    // Who a session is, whoever it happens to be talking to.
    //
    // There are two answers to that question in this app and they arrived a
    // year apart. A gateway session's name and picture come from OpenClaw's
    // agent list; a local one's now come from the CLAUDE.md sitting beside its
    // work. The chat panel had the first of those wired straight into it — it
    // asked OpenClawSessions directly — which is why a local persona would
    // have needed the same three questions asked a second way in the same
    // method, with the two answers diverging at whichever call site somebody
    // forgot.
    //
    // So the panel asks one function and this decides which registry knows.
    // The orb does not: it is handed a SessionStatus and already knows its own
    // source, and the branch it wants is about *drawing* (a picture replaces
    // the letters) rather than about who the session is.
    internal static class SessionIdentity
    {
        // Every gateway session id carries this, agents and rooms alike — see
        // OpenClawSessions.AvatarForSession, which splits the two below it.
        // Keying on the prefix rather than on SessionSource is deliberate: the
        // chat panel is bound to an IRemoteChatSession and has no status to
        // read a source off, and this is the discriminator it already has.
        private const string GatewayPrefix = "openclaw:";

        // Emoji is a gateway-only field: OpenClaw keeps one per agent, and a
        // CLAUDE.md persona deliberately has no equivalent — the grammar names
        // a picture or nothing (see PersonaMarkdown), because an emoji in
        // prose is a decoration far more often than it is an identity.
        internal sealed record Face(
            string? Name, string? Emoji, OpenClawAvatars.Avatar? Avatar, bool Gateway)
        {
            // Whether this session has a face of its own to draw, as opposed to
            // a header that should borrow the letters and colour off the orb it
            // opened from.
            //
            // A picture always counts. A *name* counts only for a gateway
            // agent, and that asymmetry is the point: a local persona's name is
            // already on its orb, as the letters in the glyph, so borrowing
            // gives the header "Le" on the orb's own colour. Treating the name
            // as a face instead would send it down the path that asks
            // OpenClawSessions for an agent colour, get null — there is no
            // agent — and draw those same letters on an invisible circle. The
            // worse-looking of two answers to a question that was already
            // answered correctly.
            internal bool DrawsItsOwnCircle =>
                Avatar is not null
                || (Gateway && (!string.IsNullOrEmpty(Name) || !string.IsNullOrEmpty(Emoji)));
        }

        internal static readonly Face Unknown = new(null, null, null, false);

        internal static bool IsGateway(string? sessionId) =>
            sessionId is not null && sessionId.StartsWith(GatewayPrefix, StringComparison.Ordinal);

        // Just the name, without asking anybody for a picture.
        //
        // Its own entry point rather than For().Name because the two are asked
        // at very different rates: the header's title and the speaker chip are
        // recomputed on every poll tick, twice a second, where the portrait is
        // applied once at bind. Both halves of the name are a dictionary
        // lookup; the picture half is not — a *room's* avatar is a composite
        // built from whoever is in the room, and reaching it means sorting a
        // member list and assembling a cache key before the cache can answer.
        // Small work, done pointlessly, forever.
        internal static string? NameFor(string? sessionId) =>
            string.IsNullOrEmpty(sessionId) ? null
                : IsGateway(sessionId) ? OpenClawSessions.IdentityForSession(sessionId)?.Name
                : LocalPersonas.For(sessionId)?.Name;

        // The local half of that alone: what a CLAUDE.md persona is called, and
        // null for a gateway session.
        //
        // The asymmetry is not an oversight. A gateway session's display name is
        // already built from its identity — "Nova — #general", who and where —
        // and a *room's* display name is the room while its identity is
        // whichever agent is in the session key, so substituting the identity
        // name there would put one agent's name on the header of a channel four
        // of them are talking in. See RefreshSoleSpeaker's own comment, which
        // has drawn that distinction since long before personas existed.
        internal static string? LocalNameFor(string? sessionId) =>
            IsGateway(sessionId) ? null : NameFor(sessionId);

        internal static Face For(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return Unknown;

            if (IsGateway(sessionId))
            {
                var identity = OpenClawSessions.IdentityForSession(sessionId);

                // Asked even when there is no identity, because a room has no
                // identity and still has a picture — the composite of everyone
                // in it. That is the one case where these two disagree.
                return new Face(
                    identity?.Name,
                    identity?.Emoji,
                    OpenClawSessions.AvatarForSession(sessionId),
                    Gateway: true);
            }

            var persona = LocalPersonas.For(sessionId);
            if (persona is null) return Unknown;

            // Decoded through the same cache the gateway's pictures use, under
            // a key that cannot collide with an agent id — which is what
            // LocalPersonas.AvatarKey exists to promise. Re-decoding a portrait
            // on every poll tick is what that cache was built to avoid, and a
            // persona is read on the same two-second cadence.
            return new Face(
                persona.Name,
                null,
                persona.Avatar is null
                    ? null
                    : OpenClawAvatars.For(LocalPersonas.AvatarKey(sessionId), persona.Avatar),
                Gateway: false);
        }
    }
}
