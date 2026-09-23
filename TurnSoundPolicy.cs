namespace ClaudeBuddy
{
    // One event this scan noticed, already reduced to what the policy needs
    // to act on it: which kind of signal, which key its override (if any) is
    // filed under, and which session gets the summary if that is what gets
    // decided. SessionManager builds these from TurnSignalTracker.Observe;
    // TurnSoundPolicy never sees a SessionStatus.
    internal readonly record struct TurnSoundEvent(TurnSignal Signal, string SoundKey, string SessionId);

    // The three things this feature can do, and nothing it can't: a chime is
    // a file to hand ChimePlayer, a summary names the session whose last turn
    // gets spoken, and Silent is not "an error" — it is the answer on most
    // scans, since most scans have nothing to say.
    internal enum SoundActionKind { Silent, Chime, Summary }

    internal sealed record SoundAction(SoundActionKind Kind, string? Path = null, string? SessionId = null)
    {
        internal static readonly SoundAction Silent = new(SoundActionKind.Silent);
        internal static SoundAction Chime(string path) => new(SoundActionKind.Chime, Path: path);
        internal static SoundAction Summary(string sessionId) => new(SoundActionKind.Summary, SessionId: sessionId);
    }

    // Everything this decision needs to know about what the user has asked
    // for, gathered up front by the caller so this stays a function of its
    // arguments rather than a reader of ClaudeBuddySettings. The two resolver
    // delegates carry the filesystem lookup and the platform default name
    // together, because "null means the platform default" is a fact about
    // *which trigger* the setting belongs to (Ping for attention, Glass for
    // finished on macOS) and this record is what closes over that without
    // TurnSoundPolicy itself having to know either name.
    internal sealed record SoundSettingsSnapshot(
        bool MasterEnabled,
        string? DefaultFinishedSetting,
        string? DefaultAttentionSetting,
        Func<string, ClaudeBuddySettings.OrbTurnSound?> OverrideFor,
        Func<string?, string?> ResolveFinishedSound,
        Func<string?, string?> ResolveAttentionSound);

    // What actually happens for one scan's worth of signals: no process, no
    // settings file, no clock but the one it is handed. Every rule the plan
    // lists — coalescing, the rate limit, an override beating the default,
    // the busy-speech fallback — is a branch here and nowhere else, which is
    // what makes each of them a one-line assertion instead of something only
    // provable by listening to the app for two minutes.
    internal static class TurnSoundPolicy
    {
        // At least this long between two sounds this feature plays. Not a
        // debounce on any one session — TurnSignalTracker already stops a
        // session repeating itself — but a floor on how often the *app*
        // makes a noise, so four orbs finishing across two adjacent two-
        // second scans still reads as one sound rather than a rattle.
        internal static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(2);

        private const string Off = "off";
        private const string SummarySetting = "summary";

        internal static SoundAction Decide(
            IReadOnlyList<TurnSoundEvent> signals,
            SoundSettingsSnapshot settings,
            DateTime now,
            DateTime lastPlayed,
            bool speechBusy)
        {
            if (signals.Count == 0) return SoundAction.Silent;
            if (!settings.MasterEnabled) return SoundAction.Silent;

            // Coalesced: one sound per scan, ever, and attention outranks a
            // turn finishing — someone waiting on a permission prompt matters
            // more than someone being told a different orb is done. The
            // losing signals are simply not looked at again; there is no
            // queue for them to sit in.
            var winner = Winner(signals);
            if (winner is not { } found) return SoundAction.Silent;

            if (now - lastPlayed < MinimumGap) return SoundAction.Silent;

            var isAttention = found.Signal == TurnSignal.NeedsAttention;
            var over = settings.OverrideFor(found.SoundKey);
            var setting = isAttention
                ? over?.Attention ?? settings.DefaultAttentionSetting
                : over?.Finished ?? settings.DefaultFinishedSetting;

            if (IsOff(setting)) return SoundAction.Silent;

            // Vibe summary is a turn-finished thing only — see the settings
            // comment on needsAttentionSound for why the attention side of
            // this record has no equivalent value to check for.
            if (!isAttention && IsSummary(setting))
            {
                // A summary never interrupts speech already in progress. It
                // falls back to the ordinary finished chime rather than to
                // silence, because the alternative is a turn ending with no
                // feedback at all whenever the user happened to be listening
                // to something else — which is worse than the chime someone
                // who never asked for speech gets every time.
                return speechBusy
                    ? ChimeOrSilent(settings.ResolveFinishedSound(null))
                    : SoundAction.Summary(found.SessionId);
            }

            var resolve = isAttention ? settings.ResolveAttentionSound : settings.ResolveFinishedSound;
            return ChimeOrSilent(resolve(setting));
        }

        private static TurnSoundEvent? Winner(IReadOnlyList<TurnSoundEvent> signals)
        {
            TurnSoundEvent? finished = null;
            foreach (var signal in signals)
            {
                if (signal.Signal == TurnSignal.NeedsAttention) return signal;
                if (signal.Signal == TurnSignal.Finished) finished ??= signal;
            }

            return finished;
        }

        private static SoundAction ChimeOrSilent(string? path) =>
            path is null ? SoundAction.Silent : SoundAction.Chime(path);

        private static bool IsOff(string? setting) =>
            setting is not null && string.Equals(setting, Off, StringComparison.OrdinalIgnoreCase);

        private static bool IsSummary(string? setting) =>
            setting is not null && string.Equals(setting, SummarySetting, StringComparison.OrdinalIgnoreCase);
    }
}
