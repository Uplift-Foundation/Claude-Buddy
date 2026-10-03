using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;
using Speak = ClaudeBuddy.TextToSpeech.SpeakState;

namespace ClaudeBuddy.Tests;

// CB-222: the one seam the voice preview has with something this process does
// not own — the user's own speakCommand, run as a real subprocess.
//
// The command here is silent by construction: it records what it was given, prints
// the readiness marker, then sits in a long-running child so that stopping it has a
// process *tree* to kill, which is the Windows failure a `.cmd` wrapper produces
// (cmd.exe plus a grandchild) and the one KillTree exists for.
//
// TextToSpeech.SilenceForTests is what keeps every test in this assembly from
// reaching a real engine (CB-168), and it has to be switched off here, since the
// point is the real Speak. Two things make that safe: the command makes no sound,
// and the collection below runs alone, so nothing else can reach Speak inside the
// window. The flag goes back in a finally.
[CollectionDefinition("Speech", DisableParallelization = true)]
public sealed class SpeechCollection
{
}

[Collection("Speech")]
public class VoicePreviewCustomCommandTests
{
    // Unique enough to find by command line among everything else on the machine,
    // and long enough that a survivor is unmistakable rather than about to exit.
    private const string Marker = "4731";

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-voicepreview-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteCommand(string dir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = Path.Combine(dir, "speak.cmd");
            File.WriteAllText(cmd,
                "@echo off\r\n" +
                "echo %CLAUDEBUDDY_VOICE%> \"%~dp0voice.txt\"\r\n" +
                "more > \"%~dp0stdin.txt\"\r\n" +
                "echo speaking\r\n" +
                $"ping -n {Marker} 127.0.0.1 > nul\r\n");
            return cmd;
        }

        var sh = Path.Combine(dir, "speak.sh");
        File.WriteAllText(sh,
            "#!/bin/sh\n" +
            "d=\"$(dirname \"$0\")\"\n" +
            "printf '%s' \"$CLAUDEBUDDY_VOICE\" > \"$d/voice.txt\"\n" +
            "cat > \"$d/stdin.txt\"\n" +
            "echo speaking\n" +
            $"sleep {Marker}\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private static void Point(string dir, string command)
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.SpeakCommand = command;
    }

    // Pids of the long-running child, found by its command line. Read from the
    // process table rather than remembered, since a survivor is precisely a
    // process nobody is holding a handle to.
    private static List<int> Survivors()
    {
        var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("powershell",
                "-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='PING.EXE'\\\" | "
                + $"Where-Object {{ $_.CommandLine -like '*-n {Marker} *' }} | ForEach-Object {{ $_.ProcessId }}\"")
            : new ProcessStartInfo("ps", "-axo pid=,args=");

        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var ps = Process.Start(psi)!;
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();

        var pids = new List<int>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (int.TryParse(text, out var pid)) pids.Add(pid);
            }
            else
            {
                var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].Trim() == $"sleep {Marker}"
                    && int.TryParse(parts[0], out var pid))
                {
                    pids.Add(pid);
                }
            }
        }

        return pids;
    }

    private static void KillSurvivors()
    {
        foreach (var pid in Survivors())
        {
            try { Process.GetProcessById(pid).Kill(); } catch { }
        }
    }

    private static async Task WaitFor(Speak wanted)
    {
        var reached = new TaskCompletionSource();
        void Handler(Speak s) { if (s == wanted) reached.TrySetResult(); }

        TextToSpeech.StateChanged += Handler;
        try
        {
            if (TextToSpeech.State == wanted) return;
            var winner = await Task.WhenAny(reached.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(winner == reached.Task, $"speech never reached {wanted}; state is {TextToSpeech.State}");
        }
        finally
        {
            TextToSpeech.StateChanged -= Handler;
        }
    }

    [Fact]
    public async Task ThePreviewRunsTheUsersCommandWithTheVoiceAndSampleAndStopsItsWholeTree()
    {
        var dir = NewDir();
        var previousDir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR");
        var transitions = new List<Speak>();
        void Record(Speak s) { lock (transitions) transitions.Add(s); }

        VoicePreview.ResetForTests();
        VoicePreview.UiPostForTests = a => a();
        TextToSpeech.StateChanged += Record;
        TextToSpeech.SilenceForTests = false;
        try
        {
            Point(dir, WriteCommand(dir));

            await VoicePreview.Toggle(new TextToSpeech.VoiceOption(
                TextToSpeech.SpeakEngine.Custom, "test-voice", "test-voice (custom)"));
            await WaitFor(Speak.Speaking);

            // Preparing first, upgraded to Speaking by the command's own marker.
            lock (transitions)
            {
                Assert.Equal(new[] { Speak.Preparing, Speak.Speaking }, transitions.Where(t => t != Speak.Idle));
            }
            Assert.Equal(Speak.Speaking, VoicePreview.Look);

            // What the command was given, exactly as a read-aloud gives it.
            Assert.Equal("test-voice", File.ReadAllText(Path.Combine(dir, "voice.txt")).Trim());
            Assert.Contains(VoicePreview.SampleText, File.ReadAllText(Path.Combine(dir, "stdin.txt")));

            var before = Survivors();
            Assert.NotEmpty(before);   // the grandchild is really there to be killed

            await VoicePreview.StopIfLive();
            await WaitFor(Speak.Idle);

            Assert.Empty(Survivors());
            Assert.Equal(Speak.Idle, VoicePreview.Look);
        }
        finally
        {
            TextToSpeech.SilenceForTests = true;
            TextToSpeech.StateChanged -= Record;
            KillSurvivors();
            TextToSpeech.Cancel();
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", previousDir);
            ClaudeBuddySettings.ReloadForTests();
            VoicePreview.ResetForTests();
        }
    }

    [Fact]
    public async Task ACommandThatCannotStartLeavesTheButtonIdleAndSubstitutesNothing()
    {
        var dir = NewDir();
        var previousDir = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR");
        var transitions = new List<Speak>();
        void Record(Speak s) { lock (transitions) transitions.Add(s); }

        VoicePreview.ResetForTests();
        VoicePreview.UiPostForTests = a => a();
        TextToSpeech.StateChanged += Record;
        TextToSpeech.SilenceForTests = false;
        try
        {
            Point(dir, Path.Combine(dir, "no-such-command"));

            await VoicePreview.Toggle(new TextToSpeech.VoiceOption(
                TextToSpeech.SpeakEngine.Custom, "", "Custom command"));

            Assert.Equal(Speak.Idle, VoicePreview.Look);
            Assert.Equal(Speak.Idle, TextToSpeech.State);

            // Preparing, then straight back: a system voice would have gone through
            // Speaking, and that is the substitution this must never make.
            lock (transitions)
            {
                Assert.DoesNotContain(Speak.Speaking, transitions);
            }
        }
        finally
        {
            TextToSpeech.SilenceForTests = true;
            TextToSpeech.StateChanged -= Record;
            TextToSpeech.Cancel();
            Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", previousDir);
            ClaudeBuddySettings.ReloadForTests();
            VoicePreview.ResetForTests();
        }
    }
}
