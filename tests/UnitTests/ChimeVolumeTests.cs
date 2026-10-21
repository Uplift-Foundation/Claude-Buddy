using Xunit;

namespace Orbweaver.Tests;

// CB-200's Alert level at the one place every chime passes through —
// ChimePlayer.BuildProcess — plus the Windows path decision, which is not
// platform-gated so that both CI legs run it.
//
// Nothing is started: building a Process has no side effect, which is the
// same reasoning ChimePlayerTests' own BuildProcess test rests on.
[Collection("Settings")]
public class ChimeVolumeTests
{
    [Fact]
    public void OnMacAQuieterChimeHandsAfplayItsGainAheadOfThePath()
    {
        if (!OperatingSystem.IsMacOS()) return; // the only branch this runner can take

        using var proc = ChimePlayer.BuildProcess("/System/Library/Sounds/Glass.aiff", 0.35);

        Assert.Equal("/usr/bin/afplay", proc!.StartInfo.FileName);
        Assert.Equal(new[] { "-v", "0.35", "/System/Library/Sounds/Glass.aiff" }, proc.StartInfo.ArgumentList);
    }

    [Fact]
    public void OnMacFullVolumeIsTheArgvEveryEarlierBuildRan()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var proc = ChimePlayer.BuildProcess("/System/Library/Sounds/Glass.aiff", 1.0);

        Assert.Equal(new[] { "/System/Library/Sounds/Glass.aiff" }, proc!.StartInfo.ArgumentList);
    }

    [Fact]
    public void TheOneArgumentOverloadReadsTheAlertSetting()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var saved = OrbweaverSettings.AlertVolume;
        try
        {
            OrbweaverSettings.AlertVolume = 0.2;
            using var proc = ChimePlayer.BuildProcess("/System/Library/Sounds/Ping.aiff");
            Assert.Equal(new[] { "-v", "0.2", "/System/Library/Sounds/Ping.aiff" }, proc!.StartInfo.ArgumentList);
        }
        finally
        {
            OrbweaverSettings.AlertVolume = saved;
        }
    }

    [Fact]
    public void TheSpeechSettingDoesNotReachAChime()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var saved = OrbweaverSettings.SpeechVolume;
        try
        {
            OrbweaverSettings.SpeechVolume = 0.1;
            using var proc = ChimePlayer.BuildProcess("/System/Library/Sounds/Ping.aiff", OrbweaverSettings.AlertVolume);
            Assert.DoesNotContain("0.1", proc!.StartInfo.ArgumentList);
        }
        finally
        {
            OrbweaverSettings.SpeechVolume = saved;
        }
    }

    // --- WindowsPlayablePath ------------------------------------------------

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb200-chime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] SixteenBitWav(short sample)
    {
        using var buffer = new MemoryStream();
        using var w = new BinaryWriter(buffer);
        w.Write("RIFF"u8); w.Write(38); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((ushort)1); w.Write((ushort)1);
        w.Write(8000); w.Write(16000); w.Write((ushort)2); w.Write((ushort)16);
        w.Write("data"u8); w.Write(2); w.Write(sample);
        w.Flush();
        return buffer.ToArray();
    }

    [Fact]
    public void AtFullVolumeWindowsPlaysTheSourceItself()
    {
        var dir = NewDir();
        Assert.Equal(@"C:\Windows\Media\chimes.wav",
            ChimePlayer.WindowsPlayablePath(@"C:\Windows\Media\chimes.wav", 1.0, dir));
    }

    [Fact]
    public void BelowFullWindowsPlaysAScaledCopy()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "Windows Notify Messaging.wav");
        File.WriteAllBytes(source, SixteenBitWav(1000));

        var playable = ChimePlayer.WindowsPlayablePath(source, 0.5, Path.Combine(dir, "cache"));

        Assert.NotEqual(source, playable);
        var bytes = File.ReadAllBytes(playable);
        Assert.Equal(500, BitConverter.ToInt16(bytes, bytes.Length - 2));
    }

    [Fact]
    public void AFileItCannotMakeQuieterIsStillPlayedAndSaysSo()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "compressed.wav");
        File.WriteAllBytes(source, System.Text.Encoding.ASCII.GetBytes("not a wav at all"));

        var saved = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.Equal(source, ChimePlayer.WindowsPlayablePath(source, 0.5, Path.Combine(dir, "cache")));
        }
        finally
        {
            Console.SetError(saved);
        }

        Assert.Contains("compressed.wav", captured.ToString());
        Assert.Contains("full volume", captured.ToString());
    }

    // CB-200 QA: the file that used to throw out of the chunk walk now plays
    // unscaled, through the same fallback as a compressed WAV, instead of
    // taking the chime down with it.
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 3)]
    [InlineData(int.MaxValue - 8)]
    public void AWavWithAnAbsurdChunkSizeStillPlaysAtFullVolume(int size)
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "malformed.wav");
        var bytes = SixteenBitWav(1000);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), size);
        File.WriteAllBytes(source, bytes);

        var saved = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            Assert.Equal(source, ChimePlayer.WindowsPlayablePath(source, 0.5, Path.Combine(dir, "cache")));
        }
        finally
        {
            Console.SetError(saved);
        }
    }

    [Fact]
    public void TheDefaultCacheIsUsedWhenNoneIsGiven()
    {
        var dir = NewDir();
        var source = Path.Combine(dir, "chime.wav");
        File.WriteAllBytes(source, SixteenBitWav(800));

        var playable = ChimePlayer.WindowsPlayablePath(source, 0.25);

        Assert.StartsWith(AudioVolume.ChimeCacheDirectory, playable);
        File.Delete(playable);
    }
}
