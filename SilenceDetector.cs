namespace ClaudeBuddy
{
    // When dictation has stopped, from the audio alone.
    //
    // Split out of VoiceRecorder, which cannot be constructed without a
    // microphone — its constructor calls PvRecorder.Create, so on a headless
    // runner it throws before any of this could be reached. That left the most
    // carefully reasoned logic in the recording path untestable by association,
    // which is the situation CLAUDE.md describes as a seam to fix rather than a
    // reason to skip the test.
    //
    // Nothing here reads a device or a clock. Frames arrive as an argument and
    // so does the time, which is what makes the four-second hang below assertable
    // rather than something a test has to wait out.
    //
    // Plain RMS energy on the raw PCM16 rather than a proper VAD model
    // (Whisper.net can fetch a Silero VAD model, but that is a separate download
    // and inference pass for a problem this simple enough audio already answers).
    internal sealed class SilenceDetector
    {
        // A single hardcoded RMS threshold turned out to disagree with itself
        // across rooms and mics: quiet enough background noise meant real speech
        // was still "loud" relative to it and got cut off before a sentence
        // finished, while loud enough background noise (a fan, an open window)
        // sat above that same fixed number on its own and kept re-triggering
        // "speech" forever, so the recording never noticed silence at all.
        // Calibrating against *this* recording's own ambient level fixes both:
        // quiet rooms get a low bar, noisy ones get a high one, and either way
        // speech only needs to clear its own room's baseline by a margin rather
        // than an absolute number that has no idea what room it is in.
        internal const int CalibrationFrames = 12; // ~380ms — the gap between clicking and actually starting to talk
        internal const double SpeechMultiplier = 2.5;
        internal const int MinimumThreshold = 300; // a floor a near-silent room's own noise can't undercut
        internal const int SilenceHangMs = 4000;

        // A ceiling on the *other* end, for a failure mode the calibration
        // window itself can cause: someone who starts talking within that ~380ms,
        // with no pause after clicking, has their own voice folded into the
        // "ambient" floor — the calibration has no way to tell early speech from
        // background noise, since both just look like "whatever's in these first
        // few frames." That inflates the floor to roughly their own speaking
        // volume, and multiplying an already-speech-level floor by 2.5 sets a bar
        // their own continued speech can never clear, so speech never latches and
        // the recording runs the full 30s cap regardless of what was said — one
        // sentence followed by ~25+ seconds of silence, which is exactly the shape
        // of audio that makes Whisper hallucinate "you" (a well-known artifact
        // for near-silent/long-trailing-silence clips). Capping the threshold
        // means even a contaminated floor still leaves normal speaking volume able
        // to clear it. 3000 is comfortably above ordinary room noise and
        // comfortably below ordinary speaking volume into a laptop/headset mic —
        // both are rough, mic-dependent numbers, same as the rest of these.
        internal const int MaximumThreshold = 3000;

        private bool _hasSpeech;
        private long? _silenceSinceTick;
        private int _framesSeen;
        private double _noiseFloor;

        // Exposed for tests and for nothing else: the calibrated ambient level,
        // the bar speech has to clear, and whether speech has been heard at all.
        // Reading these is how a test says which of the three regimes it is in
        // without inferring it from the return value alone.
        internal double NoiseFloor => _noiseFloor;

        internal bool HasSpeech => _hasSpeech;

        internal int Threshold =>
            (int)Math.Min(MaximumThreshold, Math.Max(MinimumThreshold, _noiseFloor * SpeechMultiplier));

        // True the moment sustained silence has followed detected speech.
        //
        // `now` is the caller's clock in milliseconds — VoiceRecorder passes
        // Environment.TickCount64.
        internal bool Observe(short[] frame, long now)
        {
            var rms = FrameRms(frame);
            _framesSeen++;

            // Unconditional for a fixed opening window — assumed to be pure
            // ambient noise, however loud, since there's always some gap between
            // clicking and actually speaking. This has to be unconditional rather
            // than "fold it in only while below the current threshold": gating it
            // on the threshold was the first attempt, and in a room whose ambient
            // noise already sits above the *default* threshold, that gate is
            // exactly what keeps the floor from ever rising to meet it — the bug
            // where a noisy room's own background hum reads as continuous speech
            // forever.
            if (_framesSeen <= CalibrationFrames)
            {
                _noiseFloor += (rms - _noiseFloor) / _framesSeen;
                return false;
            }

            if (rms >= Threshold)
            {
                _hasSpeech = true;
                _silenceSinceTick = null;
                return false;
            }

            if (!_hasSpeech) return false;

            _silenceSinceTick ??= now;
            return now - _silenceSinceTick >= SilenceHangMs;
        }

        // int, not short: a short overflows (wrapping negative) for RMS values
        // approaching int16's own ceiling, which a loud-enough frame can genuinely
        // hit — silently corrupting the floor/threshold math above rather than
        // throwing anywhere near the mistake.
        internal static int FrameRms(short[] frame)
        {
            if (frame.Length == 0) return 0;

            long sumOfSquares = 0;
            foreach (var sample in frame) sumOfSquares += (long)sample * sample;

            return (int)Math.Sqrt(sumOfSquares / (double)frame.Length);
        }
    }
}
