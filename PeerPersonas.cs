namespace ClaudeBuddy
{
    // Personas received from a paired Buddy. This is deliberately separate
    // from LocalPersonas: the two records have the same presentation fields,
    // but only one has filesystem meaning. Giving a peer persona an AvatarPath
    // would invite the receiver to open a path that belongs to another machine.
    internal static class PeerPersonas
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, MirrorProtocol.PeerPersona> Registry = new(StringComparer.Ordinal);

        internal static string AvatarKey(string sessionId) => "peer:" + sessionId;

        internal static MirrorProtocol.PeerPersona? For(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            lock (Gate) return Registry.GetValueOrDefault(sessionId);
        }

        // The sending Buddy bounds a portrait while reading it off its own disk
        // (PersonaFiles.ReadAvatarFile, the CB-146 cap). That is a check on the
        // *sender's* file, and it is the sender who is trusted to have run it —
        // which is a different statement from the bytes on this socket being
        // bounded. A pinned peer is not a hostile party, but it can be an older
        // or newer build, and a portrait that arrives over the cap would sit
        // resident in this registry for the session's whole life on nothing but
        // the far machine's word.
        //
        // So the same cap is applied again on arrival, and the persona survives
        // without its picture rather than being dropped whole: a name and a
        // voice are still the right answer for the orb, and refusing all three
        // over an oversized image would lose more than the image. Bytes that
        // are within the cap but are not a decodable image need nothing here —
        // OpenClawAvatars.Decode already answers null for those, and a null
        // picture is the fall-back-to-letters case every orb already has.
        //
        // Pure, and separate from Set, because it is the whole of the arrival
        // rule and the only part worth asserting without a registry behind it.
        internal static MirrorProtocol.PeerPersona? Sanitize(MirrorProtocol.PeerPersona? persona)
        {
            if (persona is null || persona.IsEmpty) return null;

            if (persona.Avatar is not null && persona.Avatar.LongLength > PersonaFiles.MaxAvatarBytes)
            {
                persona = persona with { Avatar = null };
            }

            return persona.IsEmpty ? null : persona;
        }

        internal static void Set(string sessionId, MirrorProtocol.PeerPersona? persona)
        {
            persona = Sanitize(persona);

            lock (Gate)
            {
                if (persona is null)
                {
                    Registry.Remove(sessionId);
                }
                else
                {
                    Registry[sessionId] = persona;
                }
            }

            // The wire can replace a portrait under the same roster session.
            // Forgetting by a receiver-owned key is the only cache operation
            // needed; it cannot and must not ask the sender for a file again.
            OpenClawAvatars.Forget(AvatarKey(sessionId));
        }

        internal static void Forget(string sessionId)
        {
            lock (Gate) Registry.Remove(sessionId);
            OpenClawAvatars.Forget(AvatarKey(sessionId));
        }

        internal static TextToSpeech.VoiceOption? VoiceForSession(
            string? sessionId, IEnumerable<TextToSpeech.VoiceOption> options) =>
            TextToSpeech.VoiceForPersona(For(sessionId)?.Voice, options);

        internal static double? RateForSession(string? sessionId) => For(sessionId)?.Rate;

        internal static void SetForTests(IReadOnlyDictionary<string, MirrorProtocol.PeerPersona> personas)
        {
            lock (Gate)
            {
                foreach (var id in Registry.Keys.ToList()) OpenClawAvatars.Forget(AvatarKey(id));
                Registry.Clear();
                foreach (var (id, persona) in personas) Registry[id] = persona;
            }
            foreach (var id in personas.Keys) OpenClawAvatars.Forget(AvatarKey(id));
        }
    }
}
