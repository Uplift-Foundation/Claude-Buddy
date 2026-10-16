using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;
using static Orbweaver.TextToSpeech;

namespace Orbweaver.Tests;

// CB-200 QA: the Speech level's plumbing, where it meets a process launch.
//
// QA planted four mutants and every suite stayed green, because each sat
// inside a method excluded for starting a real process: `say`'s text built
// without SayText, the level read in Speak replaced with 1.0, the same in
// StartNeural, and the engine's environment variable never set. The argv,
// script and environment are now built by SystemSpeechStartInfo and
// NeuralSpeech.StartInfoFor, outside the exclusion, and each case below kills
// one of those four — the setting is moved off full volume and the builder
// that Speak and Start actually call is asked what it would launch.
//
// In the Settings collection: every case writes SpeechVolume.
[Collection("Settings")]
public class SpeechLaunchTests
{
    private static void WithSpeechVolume(double level, Action body)
    {
        var saved = OrbweaverSettings.SpeechVolume;
        try
        {
            OrbweaverSettings.SpeechVolume = level;
            body();
        }
        finally
        {
            OrbweaverSettings.SpeechVolume = saved;
        }
    }

    // --- system voices ------------------------------------------------------

    // Kills "SayText replaced with text" and "the read replaced with 1.0" on
    // macOS: the setting alone has to reach `say`'s text.
    [Fact]
    public void SayIsHandedTheSavedLevelInItsText() =>
        WithSpeechVolume(0.3, () =>
        {
            var startInfo = SystemSpeechStartInfo("Hello there", "Samantha", OSPlatform.OSX)!;

            Assert.Equal("/usr/bin/say", startInfo.FileName);
            Assert.Equal(new[] { "-v", "Samantha", "[[volm 0.3]] Hello there" }, startInfo.ArgumentList);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.CreateNoWindow);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
        });

    // And the same read on Windows, where it becomes SAPI's Volume property.
    [Fact]
    public void SapiIsHandedTheSavedLevelInItsScript() =>
        WithSpeechVolume(0.3, () =>
        {
            var startInfo = SystemSpeechStartInfo("Hello", "Microsoft Zira Desktop", OSPlatform.Windows)!;

            Assert.Equal("powershell", startInfo.FileName);
            Assert.Equal("-NoProfile", startInfo.ArgumentList[0]);
            Assert.Equal("-Command", startInfo.ArgumentList[1]);
            Assert.Contains("$s.Volume = 30; ", startInfo.ArgumentList[2]);
            Assert.True(startInfo.RedirectStandardError);
        });

    // At full volume both are exactly what every earlier build launched.
    [Fact]
    public void AtFullVolumeTheSystemVoicesLaunchAsTheyAlwaysDid() =>
        WithSpeechVolume(1.0, () =>
        {
            Assert.Equal(new[] { "-v", "Samantha", "Hello" },
                SystemSpeechStartInfo("Hello", "Samantha", OSPlatform.OSX)!.ArgumentList);
            Assert.DoesNotContain("Volume",
                SystemSpeechStartInfo("Hello", "Zira", OSPlatform.Windows)!.ArgumentList[2]);
        });

    [Fact]
    public void ThereIsNoSystemVoiceToStartElsewhere() =>
        Assert.Null(SystemSpeechStartInfo("Hello", "anything", OSPlatform.Linux));

    // --- the neural engine --------------------------------------------------

    // Kills "the env assignment removed" and "the read in StartNeural replaced
    // with 1.0": Start now calls this overload, which reads the setting.
    [Fact]
    public void TheEngineIsHandedTheSavedLevelThroughItsEnvironment() =>
        WithSpeechVolume(0.3, () =>
        {
            var startInfo = NeuralSpeech.StartInfoFor("/engine", "af_heart", rate: null);

            Assert.Equal("0.3", startInfo.Environment[AudioVolume.SpeechVolumeEnvVar]);
            Assert.Equal(SpeechEngineContract.VolumeEnvVar, AudioVolume.SpeechVolumeEnvVar);
        });

    // Unset at full volume, the way the engine has always been run — and the
    // negative control for the case above: if the test process happened to
    // carry the variable, this would fail rather than the one above passing
    // for the wrong reason.
    [Fact]
    public void AtFullVolumeTheEngineIsRunAsItAlwaysWas() =>
        WithSpeechVolume(1.0, () =>
        {
            var startInfo = NeuralSpeech.StartInfoFor("/engine", voice: null, rate: null);

            Assert.False(startInfo.Environment.ContainsKey(AudioVolume.SpeechVolumeEnvVar));
            Assert.Equal("/engine", startInfo.FileName);
            Assert.Equal(new[] { "--model", NeuralSpeech.ModelPath, "--voice", NeuralSpeech.DefaultVoiceName,
                "--user-voices", NeuralSpeech.UserVoicesDirectory }, startInfo.ArgumentList);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.True(startInfo.CreateNoWindow);
            Assert.False(startInfo.UseShellExecute);
        });

    // The rate still rides as an argument, invariant, and only when given.
    [Fact]
    public void ARateIsPassedAsAnInvariantArgument() =>
        WithSpeechVolume(1.0, () =>
        {
            var args = NeuralSpeech.StartInfoFor("/engine", "af_heart", rate: 1.25).ArgumentList.ToList();

            Assert.Equal(new[] { "--rate", "1.25" }, args.Skip(args.Count - 2));
        });

    // --- a custom speak command (CB-200 second review) ----------------------

    private static void WithCommandVoice(Action body)
    {
        var savedVoice = OrbweaverSettings.SpeakCommandVoice;
        try
        {
            OrbweaverSettings.SpeakCommandVoice = "female_03";
            body();
        }
        finally
        {
            OrbweaverSettings.SpeakCommandVoice = savedVoice;
        }
    }

    // Kills "the assignment removed" and "the read replaced with 1.0": the
    // saved level has to reach the command's environment through the
    // overload StartCustomCommand calls.
    [Fact]
    public void ACustomCommandIsHandedTheSavedLevelInItsEnvironment() =>
        WithSpeechVolume(0.3, () => WithCommandVoice(() =>
        {
            var startInfo = CustomCommandStartInfo("/Users/me/bin/cb-voice.sh", voice: null);

            Assert.Equal("0.3", startInfo.Environment[SpeechEngineContract.VolumeEnvVar]);
            Assert.Equal("female_03", startInfo.Environment["CLAUDEBUDDY_VOICE"]);
            Assert.Equal("/Users/me/bin/cb-voice.sh", startInfo.FileName);
            Assert.Equal(OrbweaverSettings.SpeakCommandArgs, startInfo.ArgumentList);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.True(startInfo.CreateNoWindow);
            Assert.False(startInfo.UseShellExecute);
        }));

    // Always set, "1" included, so a wrapper never reads a stale value
    // inherited from this process's environment.
    [Fact]
    public void AtFullVolumeACustomCommandIsStillToldOne() =>
        WithSpeechVolume(1.0, () => WithCommandVoice(() =>
            Assert.Equal("1", CustomCommandStartInfo("cmd", "v").Environment[SpeechEngineContract.VolumeEnvVar])));

    // Invariant whatever the user's culture: "0.5", never "0,5" — a wrapper
    // doing arithmetic on "0,5" gets zero or an error.
    [Fact]
    public void ACustomCommandsLevelIsWrittenInvariantUnderACommaCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            WithCommandVoice(() =>
                Assert.Equal("0.5", CustomCommandStartInfo("cmd", "v", 0.5).Environment[SpeechEngineContract.VolumeEnvVar]));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    // The voice passed in wins over the saved one, as before.
    [Fact]
    public void AnExplicitVoiceWinsOverTheSavedOne() =>
        WithCommandVoice(() =>
            Assert.Equal("male_01", CustomCommandStartInfo("cmd", "male_01", 1.0).Environment["CLAUDEBUDDY_VOICE"]));

    // --- which engine will actually speak -----------------------------------

    // A stale "custom" with no command behind it speaks with a system voice,
    // which honours a level — so the slider must not grey out for it.
    [Theory]
    [InlineData("custom", false, SpeakEngine.System)]
    [InlineData("custom", true, SpeakEngine.Custom)]
    [InlineData("neural", false, SpeakEngine.Neural)]
    [InlineData("system", true, SpeakEngine.System)]
    [InlineData(null, false, SpeakEngine.System)]
    public void TheEngineThatWillSpeakFollowsWhetherACommandExists(string? setting, bool configured,
        SpeakEngine expected) =>
        Assert.Equal(expected, EngineThatWillSpeak(setting, configured));

    // Agrees with SelectedFrom, which is what Speak actually routes on: with
    // no command configured there is no custom option, and the selection
    // falls off the engine onto a system voice.
    [Fact]
    public void AStaleCustomSelectionResolvesToASystemVoice()
    {
        var saved = OrbweaverSettings.SpeakEngine;
        try
        {
            OrbweaverSettings.SpeakEngine = "custom";
            var options = new System.Collections.Generic.List<VoiceOption>
            {
                new(SpeakEngine.System, "Samantha", "Samantha (system)")
            };

            Assert.Equal(SpeakEngine.System, SelectedFrom(options)!.Engine);
            Assert.Equal(SpeakEngine.System, EngineThatWillSpeak("custom", customCommandConfigured: false));
        }
        finally
        {
            OrbweaverSettings.SpeakEngine = saved;
        }
    }
}
