using System.Diagnostics.CodeAnalysis;
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
    // Excluded from coverage: the constructor calls PvRecorder.Create, which
    // opens a real input device — on a headless runner it throws before anything
    // here can run, and on a developer's machine it would take the microphone
    // and fire a permission prompt. Every method below either drives that device
    // or joins the thread reading from it.
    //
    // The logic that used to be trapped in here is now SilenceDetector, which is
    // not excluded: the ambient calibration, the thresholds and the silence hang,
    // which are the parts that decide when dictation has stopped.
    [ExcludeFromCodeCoverage]
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

        // Auto-stop on silence, so dictating doesn't need a second click. The
        // rule itself — calibrate against this recording's own ambient level,
        // then wait out a hang once speech stops — lives in SilenceDetector,
        // which needs no microphone and is tested. Its comments carry the
        // reasoning for every number, including the two rooms-and-mics failures
        // that produced them.



        private readonly PvRecorder _recorder;
        private readonly List<short> _samples = new();
        private readonly object _gate = new();
        private readonly SilenceDetector _silence = new();
        private Thread? _captureThread;
        private volatile bool _capturing;

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

        // True the moment sustained silence has followed detected speech. The
        // clock is passed in rather than read inside, so the rule is decided
        // somewhere a test can reach — see SilenceDetector.
        private bool CheckSilence(short[] frame) =>
            _silence.Observe(frame, Environment.TickCount64);

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
