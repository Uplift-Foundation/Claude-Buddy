using System;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;
using static ClaudeBuddy.TextToSpeech;

namespace ClaudeBuddy.Tests;

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
        var saved = ClaudeBuddySettings.SpeechVolume;
        try
        {
            ClaudeBuddySettings.SpeechVolume = level;
            body();
        }
        finally
        {
            ClaudeBuddySettings.SpeechVolume = saved;
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
        var saved = ClaudeBuddySettings.SpeakEngine;
        try
        {
            ClaudeBuddySettings.SpeakEngine = "custom";
            var options = new System.Collections.Generic.List<VoiceOption>
            {
                new(SpeakEngine.System, "Samantha", "Samantha (system)")
            };

            Assert.Equal(SpeakEngine.System, SelectedFrom(options)!.Engine);
            Assert.Equal(SpeakEngine.System, EngineThatWillSpeak("custom", customCommandConfigured: false));
        }
        finally
        {
            ClaudeBuddySettings.SpeakEngine = saved;
        }
    }
}
