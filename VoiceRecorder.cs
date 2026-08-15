using Pv;

namespace ClaudeBuddy
{
    // Captures mic audio for the orb's voice-dictation mic — see
    // OrbWindow's recording state and SpeechTranscriber. PvRecorder's native
    // sample rate is fixed at 16kHz mono, which is exactly what Whisper.net
    // wants, so there is no resampling step anywhere in this path.
    //
    // Only ever constructed when ClaudeBuddySettings.VoiceInputEnabled is on
    // (see OrbWindow) — nothing here runs, and no mic permission prompt fires,
    // until a user has explicitly opted in.
    internal sealed class VoiceRecorder : IDisposable
    {
        // Long enough for a real sentence or two, short enough that a mic left
        // running (a missed second click, a crashed UI thread) doesn't record
        // indefinitely. Enforced on the capture thread, not by a timer the
        // click handler owns, so it holds even if OrbWindow never calls Stop.
        private const int MaxRecordingSeconds = 30;

        // 512 samples at 16kHz is a 32ms frame — small enough that Stop()
        // (which joins the capture thread) returns quickly after the last
        // Read() call, without polling PvRecorder faster than it needs.
        private const int FrameLength = 512;

        // Auto-stop on silence, so dictating doesn't need a second click.
        // Plain RMS energy on the raw PCM16 rather than a proper VAD model
        // (Whisper.net can fetch a Silero VAD model, but that's a separate
        // download and inference pass for a problem this simple enough audio
        // already answers).
        //
        // A single hardcoded RMS threshold turned out to disagree with
        // itself across rooms and mics: quiet enough background noise meant
        // real speech was still "loud" relative to it and got cut off before
        // a sentence finished, while loud enough background noise (a fan, an
        // open window) sat above that same fixed number on its own and kept
        // re-triggering "speech" forever, so the recording never noticed
        // silence at all. Calibrating against *this* recording's own
        // ambient level fixes both: quiet rooms get a low bar, noisy ones
        // get a high one, and either way speech only needs to clear its own
        // room's baseline by a margin rather than an absolute number that
        // has no idea what room it's in.
        private const int CalibrationFrames = 12; // ~380ms — the gap between clicking and actually starting to talk
        private const double SpeechMultiplier = 2.5;
        private const int MinimumThreshold = 300; // a floor a near-silent room's own noise can't undercut
        private const int SilenceHangMs = 4000;

        // A ceiling on the *other* end, for a failure mode the calibration
        // window itself can cause: someone who starts talking within that
        // ~380ms, with no pause after clicking, has their own voice folded
        // into the "ambient" floor — the calibration has no way to tell
        // early speech from background noise, since both just look like
        // "whatever's in these first few frames." That inflates the floor
        // to roughly their own speaking volume, and multiplying an
        // already-speech-level floor by 2.5 sets a bar their own continued
        // speech can never clear, so _hasSpeech never latches and the
        // recording runs the full 30s cap regardless of what was said —
        // one sentence followed by ~25+ seconds of silence, which is
        // exactly the shape of audio that makes Whisper hallucinate "you"
        // (a well-known artifact for near-silent/long-trailing-silence
        // clips). Capping the threshold means even a contaminated floor
        // still leaves normal speaking volume able to clear it. 3000 is
        // comfortably above ordinary room noise and comfortably below
        // ordinary speaking volume into a laptop/headset mic — both are
        // rough, mic-dependent numbers, same as the rest of these.
        private const int MaximumThreshold = 3000;

        private readonly PvRecorder _recorder;
        private readonly List<short> _samples = new();
        private readonly object _gate = new();
        private Thread? _captureThread;
        private volatile bool _capturing;
        private bool _hasSpeech;
        private long? _silenceSinceTick;
        private int _framesSeen;
        private double _noiseFloor;

        // Fired from the capture thread (not the UI thread) once sustained
        // silence follows detected speech. OrbWindow marshals back to the UI
        // thread itself — this class has no Avalonia dependency of its own.
        public event Action? SilenceDetected;

        // Throws PvRecorderException (no input device, permission denied,
        // device busy) straight to the caller — OrbWindow's Start() catches
        // it, same convenience-only rule TerminalFocuser follows for focusing.
        public VoiceRecorder()
        {
            _recorder = PvRecorder.Create(FrameLength);
        }

        public void Start()
        {
            _recorder.Start();
            _capturing = true;

            // PvRecorder.Read() blocks until a frame is ready, so this needs a
            // dedicated thread rather than a pooled Task — a thread-pool
            // worker parked on a blocking native call for up to 30s would
            // starve every other Task.Run in the app, TerminalFocuser's own
            // process-wait calls included.
            _captureThread = new Thread(CaptureLoop) { IsBackground = true };
            _captureThread.Start();
        }

        private void CaptureLoop()
        {
            var maxSamples = (long)_recorder.SampleRate * MaxRecordingSeconds;
            var silenceDetected = false;

            try
            {
                while (_capturing)
                {
                    var frame = _recorder.Read();

                    lock (_gate)
                    {
                        _samples.AddRange(frame);
                        if (_samples.Count >= maxSamples) _capturing = false;
                    }

                    if (CheckSilence(frame))
                    {
                        _capturing = false;
                        silenceDetected = true;
                    }
                }
            }
            catch
            {
                // A read failure mid-recording (device unplugged, backend
                // hiccup) just ends capture early with whatever was collected
                // — better than losing it all or taking the app down over a
                // mic problem.
                _capturing = false;
            }

            // Raised after the loop exits, and only for this reason
            // specifically — a manual Stop() or the hard 30s cap already has
            // its own caller waiting on Stop()'s return value, and doesn't
            // need telling.
            if (silenceDetected) SilenceDetected?.Invoke();
        }

        // True the moment sustained silence has followed detected speech.
        private bool CheckSilence(short[] frame)
        {
            var rms = FrameRms(frame);
            _framesSeen++;

            // Unconditional for a fixed opening window — assumed to be pure
            // ambient noise, however loud, since there's always some gap
            // between clicking and actually speaking. This has to be
            // unconditional rather than "fold it in only while below the
            // current threshold": gating it on the threshold was the first
            // attempt, and in a room whose ambient noise already sits above
            // the *default* threshold, that gate is exactly what keeps the
            // floor from ever rising to meet it — the bug where a noisy
            // room's own background hum reads as continuous speech forever.
            if (_framesSeen <= CalibrationFrames)
            {
                _noiseFloor += (rms - _noiseFloor) / _framesSeen;
                return false;
            }

            var threshold = Math.Min(MaximumThreshold, Math.Max(MinimumThreshold, _noiseFloor * SpeechMultiplier));

            if (rms >= threshold)
            {
                _hasSpeech = true;
                _silenceSinceTick = null;
                return false;
            }

            if (!_hasSpeech) return false;

            _silenceSinceTick ??= Environment.TickCount64;
            return Environment.TickCount64 - _silenceSinceTick >= SilenceHangMs;
        }

        // int, not short: a short overflows (wrapping negative) for RMS
        // values approaching int16's own ceiling, which a loud-enough frame
        // can genuinely hit — silently corrupting the floor/threshold math
        // above rather than throwing anywhere near the mistake.
        private static int FrameRms(short[] frame)
        {
            if (frame.Length == 0) return 0;

            long sumOfSquares = 0;
            foreach (var sample in frame) sumOfSquares += (long)sample * sample;

            return (int)Math.Sqrt(sumOfSquares / (double)frame.Length);
        }

        // Stops capture and hands back everything recorded as 32-bit float
        // samples in [-1, 1] — the shape Whisper.net's ProcessAsync wants.
        // Safe to call once; the buffer is drained on the way out.
        public float[] Stop()
        {
            _capturing = false;
            try { _recorder.Stop(); } catch { /* already stopped is fine */ }

            // Read() is blocking, so the capture thread only notices
            // _capturing went false once its current frame arrives (at most
            // one FrameLength, ~32ms) — bounded well under this join's own
            // budget rather than actually needing it, but a wedged native
            // call must not hang the UI thread that called Stop().
            _captureThread?.Join(TimeSpan.FromSeconds(2));

            short[] pcm;
            lock (_gate)
            {
                pcm = _samples.ToArray();
                _samples.Clear();
            }

            var floats = new float[pcm.Length];
            for (var i = 0; i < pcm.Length; i++) floats[i] = pcm[i] / 32768f;
            return floats;
        }

        public void Dispose()
        {
            _capturing = false;
            try { _recorder.Dispose(); } catch { }
        }
    }
}
