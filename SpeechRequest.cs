using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // Reading a reply out loud, from whichever button was pressed.
    //
    // There are two of those buttons — one on the orb's flyout, one in the chat
    // panel's header — and CB-165 shipped with each of them implementing half of
    // this. The orb resolved the session's persona voice and then spoke the raw
    // reply, ignoring the scope setting entirely. The panel honoured the scope
    // and then spoke it in the wrong voice, because its own resolver had no arm
    // for a local session. Every user pressing either button got exactly the
    // half the other one was missing.
    //
    // Two parallel fixes would have left that structure in place, which is what
    // produced the bug: nothing about two independent implementations of one
    // feature says which of them is authoritative, so they drift and nobody
    // notices until a user describes both halves in one sentence. So both
    // buttons now enter here and the drift has nowhere to happen.
    //
    // What it is *not* is a speech engine. The decisions — which text, whose
    // voice, what the button shows while waiting, what happens when the
    // summariser fails — all live here, in ordinary methods a headless test can
    // call. Only the utterance itself is excluded, and it is one line.
    internal static class SpeechRequest
    {
        // The seam the UI tests drive the whole path through.
        //
        // Not optional politeness: TextToSpeech.Speak starts a real speech
        // engine, and an earlier version of OrbWindowSpeakTests genuinely made
        // the machine running the suite talk out loud. With this set, a test can
        // press either button for real and assert the text, the voice and the
        // rate that would have been uttered — which is the thing no test did
        // before, and the reason this shipped broken while both halves were
        // covered.
        internal static Action<string, TextToSpeech.VoiceOption?, double?>? UtteranceForTests;

        // Likewise for the voice list: enumerating the real one asks Kokoro to
        // list itself and runs the user's own listing command.
        internal static IEnumerable<TextToSpeech.VoiceOption>? VoiceOptionsForTests;

        // The entry point both buttons use. Everything below it is the same
        // sequence for both, which is the whole point of the file.
        //
        // The cancel branch stays with the callers rather than moving here: the
        // orb's is reached before it has gone looking for any text at all (a
        // gateway session's costs a round trip over the wire), so folding it in
        // would mean fetching a reply in order to discover it was not wanted.
        internal static void Speak(string? reply, string? sessionId)
        {
            var plan = SpeechPlan.For(reply, ClaudeBuddySettings.SpeakScope);
            if (plan.Silent) return;

            if (!plan.NeedsSummary)
            {
                Utter(plan.Text!, sessionId);
                return;
            }

            // Deliberately not awaited: the summariser takes seconds and the UI
            // thread is the one drawing the hourglass that says so.
            _ = SpeakSummaryAsync(plan.Text!, sessionId);
        }

        // The summary leg, which is the one with a wait in it.
        //
        // The hourglass goes up before the round trip rather than after, because
        // starting it is itself part of the wait being announced — the same
        // argument StartNeural's own comment makes about loading a model. The
        // measured round trip is several seconds, and a speaker that goes silent
        // for that long with no indication is the failure this ticket's
        // refinement notes predicted. The orb's flyout shows the same hourglass
        // off the same state change (SessionManager broadcasts it to every orb),
        // so this one line is what makes the two buttons look alike as well as
        // behave alike.
        internal static async Task SpeakSummaryAsync(string reply, string? sessionId)
        {
            TextToSpeech.Enter(TextToSpeech.SpeakState.Preparing);

            var text = await SpeechSummary.SummarizeOrSayWhyAsync(reply).ConfigureAwait(true);

            // The user pressed the button again while the summariser was
            // running, so TextToSpeech is back to Idle and speaking now would
            // start audio they have already asked to stop. This is the only
            // point at which that can be honoured: the round trip is several
            // seconds and there is nothing else watching it.
            if (TextToSpeech.State != TextToSpeech.SpeakState.Preparing) return;

            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
            Utter(text, sessionId);
        }

        // Whose voice, at what rate — asked here rather than by the callers, so
        // that a new caller cannot arrive answering it a third way.
        internal static void Utter(string text, string? sessionId)
        {
            var options = VoiceOptionsForTests;
            var voice = options is null
                ? SessionIdentity.VoiceFor(sessionId)
                : SessionIdentity.VoiceFor(sessionId, options);
            var rate = SessionIdentity.RateFor(sessionId);

            var seam = UtteranceForTests;
            if (seam is not null)
            {
                seam(text, voice, rate);
                return;
            }

            Say(text, voice, rate);
        }

        // Excluded from coverage: makes the machine make a noise. TextToSpeech.Speak
        // is itself already excluded for that, and scoping the exclusion to this one
        // call is what keeps every decision above it measured — which is the shape
        // OrbWindowSpeakTests' header argues for at length, and the shape whose
        // absence let CB-165 ship with the choice of voice untested on one path and
        // the choice of text untested on the other.
        //
        // A null voice is the user's own setting rather than silence, in both
        // arms of every resolver that can produce one.
        [ExcludeFromCodeCoverage]
        private static void Say(string text, TextToSpeech.VoiceOption? voice, double? rate)
        {
            if (voice is null) TextToSpeech.Speak(text, ClaudeBuddySettings.SpeakVoice);
            else TextToSpeech.Speak(text, voice, rate);
        }
    }
}
