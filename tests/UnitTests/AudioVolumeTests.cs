using System.Globalization;
using System.Text;
using Xunit;
using static Orbweaver.TextToSpeech;

namespace Orbweaver.Tests;

// CB-200: every rule about turning a volume level into a backend's own units,
// and the one decision the settings window greys the Speech slider on.
//
// Nothing here makes a sound. The backends were each measured against the
// real tool before these rules were written (see AudioVolume's header and the
// PR); what is pinned here is that the app hands each of them what those
// measurements say it needs — an invariant decimal to afplay, a clamped
// integer to SAPI, [[volm]] to `say` — and, at full volume, exactly what
// every earlier build handed them.
[Collection("Settings")]
public class AudioVolumeTests
{
    // --- the engine decision ------------------------------------------------

    // The Speech row's note from the global engine alone (no orb voices) —
    // one case per arm. The fallback flag only means anything for Kokoro; a
    // custom command's note stands whatever it says, and a system voice has
    // no note either way. The slider itself is never disabled any more: every
    // engine, a custom command included, is told the level.
    [Theory]
    [InlineData(SpeakEngine.Custom, false, AudioVolume.CustomCommandNote)]
    [InlineData(SpeakEngine.Custom, true, AudioVolume.CustomCommandNote)]
    [InlineData(SpeakEngine.Neural, true, AudioVolume.FallbackEngineNote)]
    [InlineData(SpeakEngine.Neural, false, null)]
    [InlineData(SpeakEngine.System, false, null)]
    [InlineData(SpeakEngine.System, true, null)]
    public void TheSpeechRowSaysWhenALevelMayNotBeHeard(SpeakEngine engine, bool usingFallback, string? expected) =>
        Assert.Equal(expected, AudioVolume.SpeechVolumeNote(engine, usingFallback, Array.Empty<SpeakEngine>()));

    [Fact]
    public void TheCustomCommandNoteNamesTheVariable() =>
        Assert.Contains(SpeechEngineContract.VolumeEnvVar, AudioVolume.CustomCommandNote);

    // The Alert row's description names the compressed-WAV gap on Windows
    // only; macOS's afplay applies its own gain to any format.
    [Fact]
    public void OnlyWindowsIsToldCompressedChimesPlayAtFullVolume()
    {
        var windows = AudioVolume.AlertVolumeHelp(windows: true);
        var mac = AudioVolume.AlertVolumeHelp(windows: false);

        Assert.Contains("compressed", windows);
        Assert.Contains("full volume", windows);
        Assert.DoesNotContain("compressed", mac);
        Assert.StartsWith(mac, windows);
    }

    [Theory]
    [InlineData("custom", SpeakEngine.Custom)]
    [InlineData("neural", SpeakEngine.Neural)]
    [InlineData("system", SpeakEngine.System)]
    [InlineData(null, SpeakEngine.System)]
    [InlineData("something a newer build wrote", SpeakEngine.System)]
    public void TheStoredEngineNameMapsToAnEngine(string? setting, SpeakEngine expected) =>
        Assert.Equal(expected, EngineNamed(setting));

    // --- clamping -----------------------------------------------------------

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(-3.0, 0.0)]
    [InlineData(40.0, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    public void ALevelIsPinnedIntoRange(double given, double expected) =>
        Assert.Equal(expected, AudioVolume.Clamp(given));

    [Fact]
    public void NaNIsTheDefaultNotSilence() =>
        Assert.Equal(AudioVolume.Default, AudioVolume.Clamp(double.NaN));

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(7.0, true)]
    [InlineData(0.95, false)]
    [InlineData(0.0, false)]
    public void FullVolumeIsOneAndAnythingAboveIt(double level, bool full) =>
        Assert.Equal(full, AudioVolume.IsFull(level));

    [Theory]
    [InlineData(0.5, "50%")]
    [InlineData(1.0, "100%")]
    [InlineData(0.0, "0%")]
    [InlineData(0.333, "33%")]
    public void PercentIsWhatTheSliderShows(double level, string expected) =>
        Assert.Equal(expected, AudioVolume.Percent(level));

    // --- units per backend --------------------------------------------------

    [Fact]
    public void ALevelIsFormattedInvariantEvenUnderACommaCulture()
    {
        // afplay reads "0,5" as zero and plays silence with exit 0 — measured.
        // The user's culture must never reach the command line.
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("0.5", AudioVolume.Format(0.5));
            Assert.Equal(new[] { "-v", "0.25" }, AudioVolume.AfplayArguments(0.25));
            Assert.Equal("[[volm 0.5]] hi", AudioVolume.SayText("hi", 0.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void FormatIsShortAndClamped()
    {
        Assert.Equal("0.333", AudioVolume.Format(1.0 / 3));
        Assert.Equal("1", AudioVolume.Format(9));
        Assert.Equal("0", AudioVolume.Format(-1));
    }

    [Fact]
    public void SayTextIsUnchangedAtFullVolumeAndCarriesVolmBelowIt()
    {
        Assert.Equal("Hello there.", AudioVolume.SayText("Hello there.", 1.0));
        Assert.Equal("[[volm 0.3]] Hello there.", AudioVolume.SayText("Hello there.", 0.3));
        Assert.Equal("[[volm 0]] Hello there.", AudioVolume.SayText("Hello there.", 0));
    }

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(0.5, 50)]
    [InlineData(0.0, 0)]
    [InlineData(0.004, 0)]
    [InlineData(0.996, 100)]
    [InlineData(1.5, 100)]   // SAPI throws past 100 — measured
    [InlineData(-0.5, 0)]    // ...and below 0
    public void SapiGetsAClampedIntegerPercent(double level, int expected) =>
        Assert.Equal(expected, AudioVolume.SapiVolume(level));

    [Fact]
    public void AfplayGetsNoArgumentsAtFullVolume() =>
        Assert.Empty(AudioVolume.AfplayArguments(1.0));

    [Fact]
    public void TheEngineIsHandedNothingAtFullVolumeAndALevelBelowIt()
    {
        Assert.Null(AudioVolume.EngineEnvironmentValue(1.0));
        Assert.Equal("0.6", AudioVolume.EngineEnvironmentValue(0.6));
        Assert.Equal("0", AudioVolume.EngineEnvironmentValue(0));
    }

    [Fact]
    public void TheChimeCacheLivesUnderTemp() =>
        Assert.StartsWith(Path.GetTempPath(), AudioVolume.ChimeCacheDirectory);

    // --- the Windows SAPI script --------------------------------------------

    [Fact]
    public void TheSapiScriptReadsTextAndVoiceFromTheEnvironmentAtFullVolume()
    {
        var script = WindowsSpeakScript(1.0);

        Assert.DoesNotContain("Volume", script);
        Assert.Equal(
            "Add-Type -AssemblyName System.Speech; " +
            "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
            "try { $s.SelectVoice($env:CLAUDEBUDDY_SPEAK_VOICE) } catch { }; " +
            "$s.Speak($env:CLAUDEBUDDY_SPEAK_TEXT)",
            script);
    }

    [Fact]
    public void TheSapiScriptSetsVolumeBeforeSpeaking()
    {
        var script = WindowsSpeakScript(0.4);

        Assert.Contains("$s.Volume = 40; ", script);
        Assert.True(script.IndexOf("$s.Volume", StringComparison.Ordinal)
                    < script.IndexOf("$s.Speak", StringComparison.Ordinal));
    }

    // CB-184: whatever the text or voice contains, it travels in the
    // environment and never appears in the script.
    public static IEnumerable<object[]> HostileValues() => new[]
    {
        "plain", "it's", "O'Brien", "x’); Write-Output INJECTED; (’",
        "‘a’ ‚b‛", "$(Write-Output INJECTED)", "`n `` `\"", "line1\r\nline2", "",
    }.Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(HostileValues))]
    public void TheSapiLaunchCarriesTextAndVoiceInTheEnvironmentOnly(string value)
    {
        var startInfo = SystemSpeechStartInfo("T:" + value, "V:" + value,
            System.Runtime.InteropServices.OSPlatform.Windows, 0.4)!;

        var script = startInfo.ArgumentList[2];
        Assert.Equal(WindowsSpeakScript(0.4), script);
        Assert.DoesNotContain("T:", script);
        Assert.DoesNotContain("V:", script);
        Assert.Equal("T:" + value, startInfo.EnvironmentVariables[SpeakTextEnvVar]);
        Assert.Equal("V:" + value, startInfo.EnvironmentVariables[SpeakVoiceEnvVar]);
    }

    // --- ScaleWav -----------------------------------------------------------

    // A minimal RIFF/WAVE file. `extra` chunks go between fmt and data, which
    // is where real files keep LIST/fact chunks.
    private static byte[] Wav(ushort format, ushort bits, byte[] data, ushort channels = 1,
        byte[]? extra = null, bool extensible = false, ushort subFormat = 1)
    {
        using var buffer = new MemoryStream();
        using var w = new BinaryWriter(buffer);
        w.Write("RIFF"u8);
        w.Write(0);                 // size, patched below
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(extensible ? 40 : 16);
        w.Write(extensible ? (ushort)0xFFFE : format);
        w.Write(channels);
        w.Write(8000);
        w.Write(8000 * channels * bits / 8);
        w.Write((ushort)(channels * bits / 8));
        w.Write(bits);
        if (extensible)
        {
            w.Write((ushort)22);    // cbSize
            w.Write(bits);          // valid bits
            w.Write(0);             // channel mask
            w.Write(subFormat);     // first two bytes of the SubFormat GUID
            w.Write(new byte[14]);
        }
        if (extra is not null) w.Write(extra);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        var bytes = buffer.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), bytes.Length - 8);
        return bytes;
    }

    private static byte[] Shorts(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), samples[i]);
        return bytes;
    }

    private static short[] ReadShorts(byte[] wav, int count)
    {
        var start = wav.Length - count * 2;
        return Enumerable.Range(0, count).Select(i => BitConverter.ToInt16(wav, start + i * 2)).ToArray();
    }

    [Fact]
    public void SixteenBitPcmIsScaledAndEveryHeaderByteIsKept()
    {
        var original = Wav(1, 16, Shorts(1000, -1000, 32767, -32768, 0));

        var scaled = AudioVolume.ScaleWav(original, 0.5)!;

        Assert.Equal(new short[] { 500, -500, 16384, -16384, 0 }, ReadShorts(scaled, 5));
        // Whatever could open the original can open this: only samples moved.
        Assert.Equal(original.AsSpan(0, original.Length - 10).ToArray(),
            scaled.AsSpan(0, scaled.Length - 10).ToArray());
        // And the input was not modified in place.
        Assert.Equal(new short[] { 1000, -1000, 32767, -32768, 0 }, ReadShorts(original, 5));
    }

    [Fact]
    public void ZeroIsSilence()
    {
        var scaled = AudioVolume.ScaleWav(Wav(1, 16, Shorts(1234, -4321)), 0)!;
        Assert.Equal(new short[] { 0, 0 }, ReadShorts(scaled, 2));
    }

    [Fact]
    public void EightBitPcmIsScaledAroundItsUnsignedMidpoint()
    {
        var scaled = AudioVolume.ScaleWav(Wav(1, 8, new byte[] { 128, 228, 28, 255, 0 }), 0.5)!;
        Assert.Equal(new byte[] { 128, 178, 78, 192, 64 }, scaled[^5..]);
    }

    [Fact]
    public void TwentyFourBitPcmKeepsItsSign()
    {
        // 0x100000 = 1048576 and its negative, little-endian in three bytes.
        var data = new byte[] { 0x00, 0x00, 0x10, 0x00, 0x00, 0xF0 };
        var scaled = AudioVolume.ScaleWav(Wav(1, 24, data), 0.5)!;

        int Read(int at) => (scaled[at] | (scaled[at + 1] << 8) | (scaled[at + 2] << 16)) << 8 >> 8;
        var start = scaled.Length - 6;
        Assert.Equal(524288, Read(start));
        Assert.Equal(-524288, Read(start + 3));
    }

    [Fact]
    public void ThirtyTwoBitPcmIsScaled()
    {
        var data = new byte[8];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), 1_000_000);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), -1_000_000);

        var scaled = AudioVolume.ScaleWav(Wav(1, 32, data), 0.25)!;

        Assert.Equal(250_000, BitConverter.ToInt32(scaled, scaled.Length - 8));
        Assert.Equal(-250_000, BitConverter.ToInt32(scaled, scaled.Length - 4));
    }

    [Fact]
    public void FloatSamplesAreScaled()
    {
        var data = new byte[8];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), 0.8f);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), -0.5f);

        var scaled = AudioVolume.ScaleWav(Wav(3, 32, data), 0.5)!;

        Assert.Equal(0.4f, BitConverter.ToSingle(scaled, scaled.Length - 8), 5);
        Assert.Equal(-0.25f, BitConverter.ToSingle(scaled, scaled.Length - 4), 5);
    }

    [Fact]
    public void ExtensibleFormatIsReadFromItsSubFormat()
    {
        var scaled = AudioVolume.ScaleWav(Wav(1, 16, Shorts(800), extensible: true, subFormat: 1), 0.5)!;
        Assert.Equal(new short[] { 400 }, ReadShorts(scaled, 1));

        // An extensible header naming something compressed is refused.
        Assert.Null(AudioVolume.ScaleWav(Wav(1, 16, Shorts(800), extensible: true, subFormat: 2), 0.5));
    }

    [Fact]
    public void ChunksBetweenFmtAndDataAreWalkedPastIncludingAnOddPad()
    {
        // A LIST chunk of odd length carries a pad byte; a scaler that did not
        // honour it would read the data tag one byte late and scale nothing.
        var list = new List<byte>();
        list.AddRange("LIST"u8.ToArray());
        list.AddRange(BitConverter.GetBytes(3));
        list.AddRange(new byte[] { 1, 2, 3, 0 });

        var scaled = AudioVolume.ScaleWav(Wav(1, 16, Shorts(600, -600), extra: list.ToArray()), 0.5)!;

        Assert.Equal(new short[] { 300, -300 }, ReadShorts(scaled, 2));
    }

    [Theory]
    [InlineData(2, 4)]    // MS ADPCM
    [InlineData(1, 12)]   // PCM at a width nothing writes
    [InlineData(3, 64)]   // double-precision float
    public void AnEncodingItCannotScaleIsRefusedRatherThanGuessed(int format, int bits) =>
        Assert.Null(AudioVolume.ScaleWav(Wav((ushort)format, (ushort)bits, new byte[8]), 0.5));

    [Fact]
    public void SomethingThatIsNotAWavIsRefused()
    {
        Assert.Null(AudioVolume.ScaleWav(Array.Empty<byte>(), 0.5));
        Assert.Null(AudioVolume.ScaleWav(Encoding.ASCII.GetBytes("FORM....AIFFCOMM"), 0.5));
        Assert.Null(AudioVolume.ScaleWav(Encoding.ASCII.GetBytes("RIFF....AVI LIST"), 0.5));
    }

    [Fact]
    public void AWavWithNoFmtOrNoDataIsRefused()
    {
        var noData = Wav(1, 16, Shorts(1));
        Assert.Null(AudioVolume.ScaleWav(noData[..36], 0.5));   // cut before the data chunk

        var noFmt = new List<byte>();
        noFmt.AddRange("RIFF"u8.ToArray());
        noFmt.AddRange(BitConverter.GetBytes(12));
        noFmt.AddRange("WAVE"u8.ToArray());
        noFmt.AddRange("data"u8.ToArray());
        noFmt.AddRange(BitConverter.GetBytes(2));
        noFmt.AddRange(new byte[] { 1, 0 });
        Assert.Null(AudioVolume.ScaleWav(noFmt.ToArray(), 0.5));
    }

    [Fact]
    public void ANegativeChunkSizeIsRefusedRatherThanLoopedOn()
    {
        var bytes = Wav(1, 16, Shorts(1));
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), -8);   // the fmt chunk's size
        Assert.Null(AudioVolume.ScaleWav(bytes, 0.5));
    }

    // CB-200 QA: a chunk size near int.MaxValue used to wrap the walk's
    // offset negative and throw ArgumentOutOfRange out of a chime. Walked in
    // long, it now steps past the end, finds no data chunk and is refused —
    // which plays the original, like any WAV this cannot scale. MaxValue-3
    // and -8 are the sizes that land the wrapped offset on a readable
    // negative and just short of the header; MaxValue is odd, so it also adds
    // the pad byte.
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 3)]
    [InlineData(int.MaxValue - 8)]
    public void AChunkSizeNearIntMaxIsRefusedRatherThanThrown(int size)
    {
        var junk = new List<byte>();
        junk.AddRange("junk"u8.ToArray());
        junk.AddRange(BitConverter.GetBytes(size));
        junk.AddRange(new byte[] { 1, 2, 3, 4 });

        Assert.Null(AudioVolume.ScaleWav(Wav(1, 16, Shorts(1000), extra: junk.ToArray()), 0.5));
    }

    // The same sizes on the fmt chunk itself, which is read before the jump.
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 3)]
    [InlineData(int.MaxValue - 8)]
    public void AnFmtChunkSizeNearIntMaxIsRefusedRatherThanThrown(int size)
    {
        var bytes = Wav(1, 16, Shorts(1000));
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), size);   // the fmt chunk's size

        Assert.Null(AudioVolume.ScaleWav(bytes, 0.5));
    }

    [Fact]
    public void ADataChunkClaimingMoreThanTheFileIsScaledOnlyAsFarAsItGoes()
    {
        var bytes = Wav(1, 16, Shorts(1000, 1000));
        BitConverter.TryWriteBytes(bytes.AsSpan(bytes.Length - 8, 4), 4000);   // data size, lying

        var scaled = AudioVolume.ScaleWav(bytes, 0.5)!;

        Assert.Equal(new short[] { 500, 500 }, ReadShorts(scaled, 2));
    }

    // --- ScaledCopy ---------------------------------------------------------

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb200-volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AtFullVolumeTheSourceItselfIsPlayedAndNothingIsWritten()
    {
        var dir = NewDir();
        var cache = Path.Combine(dir, "cache");

        Assert.Equal("/anything.wav", AudioVolume.ScaledCopy("/anything.wav", 1.0, cache));
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void AScaledCopyIsWrittenOnceAndReused()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "chime.wav");
        File.WriteAllBytes(source, Wav(1, 16, Shorts(1000)));
        var cache = Path.Combine(dir, "cache");

        var first = AudioVolume.ScaledCopy(source, 0.5, cache)!;
        Assert.StartsWith(cache, first);
        Assert.EndsWith("-50.wav", first);
        Assert.Equal(new short[] { 500 }, ReadShorts(File.ReadAllBytes(first), 1));

        // A second ask finds it rather than rewriting it.
        var stamp = File.GetLastWriteTimeUtc(first);
        File.SetLastWriteTimeUtc(first, stamp.AddMinutes(-5));
        Assert.Equal(first, AudioVolume.ScaledCopy(source, 0.5, cache));
        Assert.Equal(stamp.AddMinutes(-5), File.GetLastWriteTimeUtc(first));

        // A different level is a different copy, and no staging file is left.
        Assert.NotEqual(first, AudioVolume.ScaledCopy(source, 0.25, cache));
        Assert.Empty(Directory.GetFiles(cache, "*.tmp"));
    }

    [Fact]
    public void AnEditedSourceIsNeverAnsweredFromAStaleCopy()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "chime.wav");
        var cache = Path.Combine(dir, "cache");

        File.WriteAllBytes(source, Wav(1, 16, Shorts(1000)));
        var before = AudioVolume.ScaledCopy(source, 0.5, cache)!;

        File.WriteAllBytes(source, Wav(1, 16, Shorts(2000, 2000)));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        var after = AudioVolume.ScaledCopy(source, 0.5, cache)!;

        Assert.NotEqual(before, after);
        Assert.Equal(new short[] { 1000, 1000 }, ReadShorts(File.ReadAllBytes(after), 2));
    }

    [Fact]
    public void AFileItCannotScaleGivesNoCopy()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "chime.aiff");
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("FORM....AIFFCOMM"));

        Assert.Null(AudioVolume.ScaledCopy(source, 0.5, Path.Combine(dir, "cache")));
    }

    [Fact]
    public void AMissingSourceGivesNoCopyRatherThanThrowing()
    {
        var dir = NewDir();
        Assert.Null(AudioVolume.ScaledCopy(Path.Combine(dir, "gone.wav"), 0.5, Path.Combine(dir, "cache")));
    }

    [Fact]
    public void ACacheItCannotWriteGivesNoCopyRatherThanThrowing()
    {
        if (OperatingSystem.IsWindows()) return;   // Unix permissions are the instrument here

        var dir = NewDir();
        var source = Path.Combine(dir, "chime.wav");
        File.WriteAllBytes(source, Wav(1, 16, Shorts(1000)));
        var cache = Path.Combine(dir, "cache");
        Directory.CreateDirectory(cache);
        File.SetUnixFileMode(cache, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            Assert.Null(AudioVolume.ScaledCopy(source, 0.5, cache));
        }
        finally
        {
            File.SetUnixFileMode(cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void AnExceptionThatIsNotAboutTheFileIsNotSwallowed() =>
        // A blank path is a caller bug, not a chime that failed to load; it
        // should surface rather than read as "play the original".
        Assert.ThrowsAny<ArgumentException>(() => AudioVolume.ScaledCopy("", 0.5, NewDir()));
}
