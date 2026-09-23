namespace ClaudeBuddy
{
    // What a state transition means for CB-167's purposes, and nothing else.
    //
    // The name is deliberately not "TurnEvent" or "SoundTrigger": this file
    // does not know sounds exist. It answers one question — did a turn just
    // finish, or does this session just start needing you — from two state
    // strings and nothing more. TurnSoundPolicy is the layer that turns an
    // answer into an actual sound, and it is a separate file for the same
    // reason SpeechPlan is separate from TextToSpeech: a decision inside the
    // method that has side effects is a decision no test can see.
    internal enum TurnSignal
    {
        None,
        Finished,
        NeedsAttention,
    }

    // The rule itself, pure, so every arm of it is a one-line unit test
    // rather than something only provable by watching an orb for a while.
    //
    // Transitions, not states — the distinction the plan insists on and the
    // one a level-triggered check gets wrong. A poll that asks "is this
    // session idle right now" fires again on every tick it stays idle; a
    // poll that asks "did it just *become* idle" fires once. The first shape
    // is the one that plays a chime on every scan of a session somebody left
    // sitting there overnight. Fifteen years of the same argument applied to
    // a beep that isn't supposed to happen twice.
    internal static class TurnSignals
    {
        // "ended" is the hook's own marker for a session about to have its
        // status file removed, not a state a live orb settles into. Treating
        // it as a real destination would let a session that quits mid-turn
        // read as having finished one — the one case where silence is
        // unambiguously correct regardless of what came before it.
        private const string EndedState = "ended";
        private const string IdleState = "idle";
        private const string GeneratingState = "generating";
        private const string WaitingState = "waiting";

        internal static TurnSignal Classify(string? previous, string next)
        {
            if (string.Equals(next, EndedState, StringComparison.Ordinal)) return TurnSignal.None;

            // No prior state is not "unknown, so guess" — it is the one case
            // the plan calls out by name: a first scan, an orb the sweep
            // pruned and picked back up, an orb that only just appeared.
            // Every one of those would otherwise read whatever the file
            // happens to say as a transition into it, which is exactly how a
            // relaunch with three idle orbs on screen would announce three
            // finished turns that happened before the app was even running.
            if (previous is null) return TurnSignal.None;

            if (string.Equals(previous, GeneratingState, StringComparison.Ordinal)
                && string.Equals(next, IdleState, StringComparison.Ordinal))
            {
                return TurnSignal.Finished;
            }

            // Any known state moving into waiting counts, except waiting
            // moving into itself — a scan that finds the same permission
            // prompt still up is not a new one, and without this guard every
            // tick spent waiting would ding again.
            if (string.Equals(next, WaitingState, StringComparison.Ordinal)
                && !string.Equals(previous, WaitingState, StringComparison.Ordinal))
            {
                return TurnSignal.NeedsAttention;
            }

            return TurnSignal.None;
        }
    }

    // The per-session memory Classify needs to have anything to compare
    // against, and the two ways something other than an ordinary scan is
    // allowed to touch it.
    //
    // Kept here rather than as a dictionary on SessionManager because the
    // memory and the rule that reads it are one idea: a tracker with no
    // Classify behind it is just a dictionary, and a Classify with no
    // tracker in front of it never gets called with a real previous state.
    internal sealed class TurnSignalTracker
    {
        private readonly Dictionary<string, string> _previous = new(StringComparer.Ordinal);

        // The scan's entry point: what changed, and remember what state this
        // session is in now regardless of the answer. The remembering has to
        // happen unconditionally — a Finished turn that stayed unobserved
        // because nobody called this again would leave the session parked on
        // its old state forever, ready to fire again on the next different
        // state it reaches.
        internal TurnSignal Observe(string sessionId, string state)
        {
            var previous = _previous.TryGetValue(sessionId, out var found) ? found : null;
            var signal = TurnSignals.Classify(previous, state);
            _previous[sessionId] = state;
            return signal;
        }

        // A silent update: record the state without asking Classify anything
        // about it. This is what "Reset to idle" calls, and the reason it
        // exists rather than everyone going through Observe is that a manual
        // reset is not a turn finishing — it is a person clearing a stuck
        // orb, and playing a Glass chime for that would reward exactly the
        // situation nobody wants a sound for.
        internal void Settle(string sessionId, string state) => _previous[sessionId] = state;

        // Drops anything the scan no longer sees. Without this, an orb that
        // vanished mid-generation and reappeared later — a backgrounded husk
        // picked back up, a session ID reused after the sweep — would compare
        // its new first state against whatever it was doing when it dropped
        // out of sight, which is a comparison across a gap this tracker has
        // no business making. Pruned means the next Observe has no previous
        // state, which Classify already treats as a baseline.
        internal void Prune(IReadOnlySet<string> seen)
        {
            if (_previous.Count == 0) return;

            List<string>? stale = null;
            foreach (var id in _previous.Keys)
            {
                if (seen.Contains(id)) continue;
                (stale ??= new List<string>()).Add(id);
            }

            if (stale is null) return;
            foreach (var id in stale) _previous.Remove(id);
        }
    }
}
