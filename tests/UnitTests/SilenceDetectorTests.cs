using Xunit;

namespace ClaudeBuddy.Tests
{
    // When dictation has stopped, from the audio alone.
    //
    // This is the most carefully reasoned logic in the recording path and it had
    // no tests, because it lived inside VoiceRecorder, whose constructor opens a
    // microphone. Its comments record two failures that reached users, and both
    // are now cases below: a noisy room whose own hum read as continuous speech
    // forever, and a contaminated calibration that set a bar the speaker's own
    // voice could never clear, so the recording ran its full 30-second cap and
    // handed Whisper the shape of audio that makes it hallucinate "you".
    //
    // Frames are synthesised rather than recorded. RMS of a constant-amplitude
    // frame is that amplitude, which makes "a frame at level N" the natural unit
    // and keeps every number below readable against the thresholds.
    public class SilenceDetectorTests
    {
        // RMS of a frame every sample of which is ±level is exactly level.
        private static short[] Frame(int level, int length = 512)
        {
            var frame = new short[length];
            for (var i = 0; i < length; i++) frame[i] = (short)(i % 2 == 0 ? level : -level);
            return frame;
        }

        // Feeds the opening window so the detector leaves calibration with a
        // known floor.
        private static SilenceDetector Calibrated(int ambient, long now = 0)
        {
            var detector = new SilenceDetector();

            for (var i = 0; i < SilenceDetector.CalibrationFrames; i++)
            {
                Assert.False(detector.Observe(Frame(ambient), now));
            }

            return detector;
        }

        // --- FrameRms ---

        [Fact]
        public void RmsOfAConstantAmplitudeFrameIsThatAmplitude()
        {
            Assert.Equal(1000, SilenceDetector.FrameRms(Frame(1000)));
        }

        [Fact]
        public void RmsOfSilenceIsZero()
        {
            Assert.Equal(0, SilenceDetector.FrameRms(Frame(0)));
        }

        [Fact]
        public void RmsOfAnEmptyFrameIsZeroRatherThanADivideByZero()
        {
            Assert.Equal(0, SilenceDetector.FrameRms(Array.Empty<short>()));
        }

        // The reason the accumulator is a long and the return is an int. Every
        // sample at int16's floor squares to about 1.07e9, and summing 512 of
        // those overflows a 32-bit accumulator into a negative number — which
        // would not throw, it would silently corrupt the noise floor and every
        // threshold derived from it.
        [Fact]
        public void AFullScaleFrameDoesNotOverflowIntoNonsense()
        {
            var rms = SilenceDetector.FrameRms(Frame(short.MaxValue));

            Assert.InRange(rms, 32760, 32767);
        }

        [Fact]
        public void TheMostNegativeSampleIsHandled()
        {
            var frame = new short[512];
            Array.Fill(frame, short.MinValue);

            Assert.InRange(SilenceDetector.FrameRms(frame), 32760, 32768);
        }

        // --- Calibration ---

        // The opening window is folded in unconditionally, and never reports
        // silence however loud or quiet it is: there is always some gap between
        // clicking and actually speaking, and whatever is in it is the room.
        [Fact]
        public void TheOpeningWindowNeverReportsSilence()
        {
            var detector = new SilenceDetector();

            for (var i = 0; i < SilenceDetector.CalibrationFrames; i++)
            {
                Assert.False(detector.Observe(Frame(0), 1_000_000));
            }

            Assert.False(detector.HasSpeech);
        }

        [Fact]
        public void TheFloorSettlesOnTheAmbientLevel()
        {
            var detector = Calibrated(ambient: 800);

            Assert.Equal(800, detector.NoiseFloor, 1);
        }

        // The bug this fixes, stated as a test. Gating the calibration on the
        // current threshold was the first attempt, and in a room whose ambient
        // noise already sits above the default threshold that gate is exactly
        // what stops the floor rising to meet it — so the hum reads as
        // continuous speech and the recording never notices silence at all.
        // Unconditional folding is what makes the loud room's floor loud.
        [Fact]
        public void ANoisyRoomsFloorRisesToMeetIt()
        {
            var detector = Calibrated(ambient: 2000);

            Assert.Equal(2000, detector.NoiseFloor, 1);

            // ...and the room's own hum is therefore below the bar rather than
            // above it, which is the difference between noticing silence and
            // never noticing it.
            Assert.True(detector.Threshold > 2000);
        }

        // --- Thresholds ---

        [Fact]
        public void TheThresholdIsAMultipleOfTheFloor()
        {
            var detector = Calibrated(ambient: 400);

            Assert.Equal(
                (int)(400 * SilenceDetector.SpeechMultiplier), detector.Threshold);
        }

        // A near-silent room's own noise cannot undercut the floor, or the
        // faintest hiss would latch as speech.
        [Fact]
        public void ANearSilentRoomStillGetsAMinimumBar()
        {
            var detector = Calibrated(ambient: 0);

            Assert.Equal(SilenceDetector.MinimumThreshold, detector.Threshold);
        }

        // The ceiling, and the failure it exists for: someone who starts talking
        // inside the calibration window has their own voice folded into the
        // "ambient" floor, because the calibration cannot tell early speech from
        // background noise. Without a cap, multiplying an already-speech-level
        // floor by 2.5 sets a bar their continued speech can never clear.
        [Fact]
        public void AFloorContaminatedByEarlySpeechIsStillClearable()
        {
            var detector = Calibrated(ambient: 6000);   // their own voice, not the room

            Assert.Equal(SilenceDetector.MaximumThreshold, detector.Threshold);

            // Ordinary speaking volume clears it, which is the whole point of
            // the cap.
            Assert.False(detector.Observe(Frame(4000), 0));
            Assert.True(detector.HasSpeech);
        }

        // --- The hang ---

        [Fact]
        public void SpeechLatchesOnceAFrameClearsTheBar()
        {
            var detector = Calibrated(ambient: 400);

            Assert.False(detector.Observe(Frame(5000), 0));
            Assert.True(detector.HasSpeech);
        }

        // Silence before any speech is not the end of an utterance — it is
        // somebody who has not started yet, and cutting them off there is the
        // bug the hang exists to avoid.
        [Fact]
        public void SilenceBeforeAnySpeechIsNeverTheEnd()
        {
            var detector = Calibrated(ambient: 400);

            for (var t = 0; t < 60_000; t += 32)
            {
                Assert.False(detector.Observe(Frame(0), t));
            }
        }

        [Fact]
        public void SilenceEndsTheUtteranceOnlyAfterTheHang()
        {
            var detector = Calibrated(ambient: 400);

            detector.Observe(Frame(5000), 0);        // speech at t=0

            // Just short of the hang: not yet.
            Assert.False(detector.Observe(Frame(0), 100));
            Assert.False(detector.Observe(Frame(0), SilenceDetector.SilenceHangMs + 99));

            // And at the hang, measured from when silence *began* rather than
            // from when speech ended.
            Assert.True(detector.Observe(Frame(0), 100 + SilenceDetector.SilenceHangMs));
        }

        // A pause inside a sentence must not end it. Any frame above the bar
        // clears the silence clock, so the hang restarts from the next quiet
        // frame — this is what lets someone think mid-sentence.
        [Fact]
        public void APauseMidSentenceRestartsTheHang()
        {
            var detector = Calibrated(ambient: 400);

            detector.Observe(Frame(5000), 0);
            Assert.False(detector.Observe(Frame(0), 1000));      // silence starts at 1000
            Assert.False(detector.Observe(Frame(5000), 2000));   // ...and they carry on

            // The clock was reset, so the old start time no longer counts.
            Assert.False(detector.Observe(Frame(0), 3000));
            Assert.False(detector.Observe(Frame(0), 1000 + SilenceDetector.SilenceHangMs));

            // The hang is now measured from 3000.
            Assert.True(detector.Observe(Frame(0), 3000 + SilenceDetector.SilenceHangMs));
        }

        // The whole sequence a real dictation produces, in one case: room tone,
        // a sentence, a pause, more of the sentence, then stopping.
        [Fact]
        public void AWholeUtteranceRunsToItsEndAndThenStops()
        {
            var detector = new SilenceDetector();
            var now = 0L;
            bool Feed(int level)
            {
                var done = detector.Observe(Frame(level), now);
                now += 32;
                return done;
            }

            for (var i = 0; i < SilenceDetector.CalibrationFrames; i++) Assert.False(Feed(500));

            for (var i = 0; i < 30; i++) Assert.False(Feed(6000));   // "fix the arrangement test"
            for (var i = 0; i < 20; i++) Assert.False(Feed(200));    // a breath
            for (var i = 0; i < 30; i++) Assert.False(Feed(6000));   // "and push it"

            var stopped = false;
            for (var i = 0; i < 200 && !stopped; i++) stopped = Feed(200);

            Assert.True(stopped, "sustained silence after speech must end the recording");
        }
    }
}
