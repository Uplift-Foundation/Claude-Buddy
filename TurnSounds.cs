namespace ClaudeBuddy
{
    // The one call SessionManager makes at the end of a scan to turn whatever
    // TurnSignalTracker noticed into an actual sound.
    //
    // Everything that decides *whether* to make a noise lives in
    // TurnSoundPolicy, pure and covered by its own tests with no clock but
    // the one it is handed. This class is the seam between that decision and
    // the world: it reads the settings the decision needs, remembers when
    // this app last actually made a sound, and carries out whichever of
    // Silent/Chime/Summary came back. Kept this thin on purpose — a bug in
    // "should this play" belongs in a pure function with a name, not folded
    // into the one place that also touches a settings file and a speaker.
    internal static class TurnSounds
    {
        // When this app last actually delivered a sound — a chime played, or
        // a summary was handed off to speak. Process-wide rather than
        // per-session, because the rate limit the plan asks for is on the
        // app making noise at all, not on any one orb repeating itself
        // (TurnSignalTracker already owns that half).
        private static DateTime _lastPlayed = DateTime.MinValue;

        // Test seam: a scan-level test wants a clean rate-limit clock between
        // cases without sleeping two real seconds to clear it, the same
        // reason ClaudeBuddySettings.ReloadForTests exists.
        internal static void ResetForTests() => _lastPlayed = DateTime.MinValue;

        // `speakTurnSummary` is how this reaches an orb without holding a
        // reference to one: TurnSounds has no _windows dictionary to look
        // into, only SessionManager does, so SessionManager hands over a
        // callback that closes over it. TurnSoundPolicy never sees this
        // either — it names the winning session id and nothing more, and
        // what "speak that session's last turn" actually means is entirely
        // OrbWindow.SpeakTurnSummary's business.
        internal static void Deliver(
            IReadOnlyList<TurnSoundEvent> events,
            Action<string> speakTurnSummary,
            DateTime? now = null)
        {
            if (events.Count == 0) return;

            var moment = now ?? DateTime.UtcNow;
            var decision = TurnSoundPolicy.Decide(
                events, Snapshot(), moment, _lastPlayed, TextToSpeech.IsSpeaking);

            // No case for Silent: a switch statement (unlike a switch
            // expression) is already a no-op for any value nothing matches,
            // and Silent is the only one left once Chime and Summary are
            // spoken for — so a third arm here would be dead code asking to
            // be covered for no reason, not a safety net. If SoundActionKind
            // ever grows a fourth member, the right fix is a case for it,
            // not a default that silently swallows something new.
            switch (decision.Kind)
            {
                case SoundActionKind.Chime:
                    _lastPlayed = moment;
                    ChimePlayer.Play(decision.Path!);
                    break;

                case SoundActionKind.Summary:
                    _lastPlayed = moment;
                    speakTurnSummary(decision.SessionId!);
                    break;
            }
        }

        // Read once per scan rather than once per event — the settings file
        // does not change mid-scan, and TurnSoundPolicy.Decide only ever
        // needs one snapshot regardless of how many sessions signalled.
        //
        // The two resolver closures are where "null means the platform
        // default" actually gets resolved into a name: SystemSoundCatalog
        // itself has no opinion on which trigger it is being asked about, so
        // that substitution happens here, once, rather than being duplicated
        // wherever a resolver gets called.
        private static SoundSettingsSnapshot Snapshot()
        {
            var directory = SystemSoundCatalog.DefaultDirectory;
            var extensions = SystemSoundCatalog.DefaultExtensions;

            return new SoundSettingsSnapshot(
                MasterEnabled: ClaudeBuddySettings.TurnSoundsEnabled,
                DefaultFinishedSetting: ClaudeBuddySettings.TurnFinishedSound,
                DefaultAttentionSetting: ClaudeBuddySettings.NeedsAttentionSound,
                OverrideFor: ClaudeBuddySettings.OrbTurnSoundFor,
                ResolveFinishedSound: setting => SystemSoundCatalog.Resolve(
                    setting ?? SystemSoundCatalog.DefaultFinishedSoundName, directory, extensions),
                ResolveAttentionSound: setting => SystemSoundCatalog.Resolve(
                    setting ?? SystemSoundCatalog.DefaultAttentionSoundName, directory, extensions));
        }
    }
}
