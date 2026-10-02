using System.Diagnostics;
using System.Text;
using ClaudeBuddy;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-184: TextToSpeech's Windows script used to interpolate the spoken text
// and the voice name into single-quoted literals, escaping only U+0027.
// PowerShell also treats U+2018..U+201B as quote delimiters, so a curly quote
// closed the literal early. The unit tests prove the script text holds no
// user value; these run the *generated* script under real PowerShell, 5.1
// (`powershell`, which is what speaks) and 7 (`pwsh`), with the speech
// engine swapped for a stub that echoes what it was handed, so nothing is
// ever played and nothing needs SAPI.
//
// The negative control reproduces the old construction and shows it does
// run the injected command, which is what makes a clean result for the new
// one a measurement rather than a hope. A shell that is not installed (no
// Windows PowerShell on the macOS runner) is reported as Skipped, never as
// Passed; on Windows both shells must run and a missing one fails.
public class SapiSpeakScriptTests
{
    private const string Engine =
        "Add-Type -AssemblyName System.Speech; " +
        "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; ";

    private const string Stub =
        "function B($x) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$x)) }; " +
        "$s = New-Object psobject; " +
        "$s | Add-Member ScriptMethod SelectVoice { param($v) Write-Output ('VOICE=' + (B $v)) }; " +
        "$s | Add-Member ScriptMethod Speak { param($t) Write-Output ('TEXT=' + (B $t)) }; ";

    private static string Stubbed(string script)
    {
        Assert.StartsWith(Engine, script);
        return Stub + script.Substring(Engine.Length);
    }

    private static string Old(string text, string voice) =>
        Stub +
        $"try {{ $s.SelectVoice('{voice.Replace("'", "''")}') }} catch {{ }}; " +
        $"$s.Speak('{text.Replace("'", "''")}')";

    private static string Run(string exe, string script, string? text, string? voice)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        if (text is not null) psi.Environment[TextToSpeech.SpeakTextEnvVar] = text;
        if (voice is not null) psi.Environment[TextToSpeech.SpeakVoiceEnvVar] = voice;

        // A shell that was meant to be here but is not throws and fails the
        // test; the skip attributes below are the only place one is excused.
        using (var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe))
        {
            var o = p.StandardOutput.ReadToEnd();
            var e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return o + "\n" + e;
        }
    }

    private static string Decode(string output, string key)
    {
        foreach (var line in output.Split('\n'))
            if (line.StartsWith(key + "="))
                return Encoding.UTF8.GetString(Convert.FromBase64String(line[(key.Length + 1)..].Trim()));
        throw new Xunit.Sdk.XunitException(key + " not found in: " + output);
    }

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "hello", "x\u2019); Write-Output INJECTED; (\u2019" };
        yield return new object[] { "he said \u2018hi\u2019); Write-Output INJECTED; (\u201b", "O'Brien" };
        yield return new object[] { "$(Write-Output INJECTED) `n `` \"q\" 'a' \u00e9\u4e2d", "Microsoft Zira Desktop" };
        yield return new object[] { "line1\nline2", "v" };
    }

    private static void RoundTrips(string exe, string text, string voice)
    {
        // Full volume: the Volume line is a property set on the stub-less
        // real synthesizer and is not what is under test here.
        var output = Run(exe, Stubbed(TextToSpeech.WindowsSpeakScript(1.0)), text, voice);

        // Only the base64 echoes may appear in the output; a command that
        // ran would print the bare word.
        var scrubbed = output
            .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "")
            .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(voice)), "");
        Assert.DoesNotContain("INJECTED", scrubbed);
        Assert.Equal(voice, Decode(output, "VOICE"));
        Assert.Equal(text, Decode(output, "TEXT"));
    }

    private static void OldConstructionInjects(string exe) =>
        Assert.Contains("INJECTED",
            Run(exe, Old("hi", "x\u2019); Write-Output INJECTED; (\u2019"), null, null));

    [WindowsPowerShellTheory]
    [MemberData(nameof(Cases))]
    public void Powershell51_RoundTripsHostileTextAndVoiceWithoutRunningThem(string text, string voice) =>
        RoundTrips("powershell", text, voice);

    [PwshTheory]
    [MemberData(nameof(Cases))]
    public void Pwsh_RoundTripsHostileTextAndVoiceWithoutRunningThem(string text, string voice) =>
        RoundTrips("pwsh", text, voice);

    [WindowsPowerShellFact]
    public void NegativeControl_Powershell51_TheOldInterpolationRanTheInjectedCommand() =>
        OldConstructionInjects("powershell");

    [PwshFact]
    public void NegativeControl_Pwsh_TheOldInterpolationRanTheInjectedCommand() =>
        OldConstructionInjects("pwsh");
}

// Windows PowerShell 5.1 ships with Windows and exists nowhere else, so off
// Windows it is skipped (reported as such, not passed); on Windows it is
// never skipped, and a missing one fails in Run.
internal static class Shells
{
    internal static string? SkipWindowsPowerShell() =>
        OperatingSystem.IsWindows() ? null : "Windows PowerShell 5.1 only exists on Windows";

    // pwsh is not guaranteed on a dev machine. Off Windows a missing one is a
    // skip; on Windows this box is expected to have it and a miss should fail.
    internal static string? SkipPwsh()
    {
        if (OperatingSystem.IsWindows()) return null;
        var sep = Path.PathSeparator;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, "pwsh"))) return null;
        return "pwsh is not installed";
    }
}

public sealed class WindowsPowerShellTheoryAttribute : TheoryAttribute
{
    public WindowsPowerShellTheoryAttribute() { Skip = Shells.SkipWindowsPowerShell(); }
}

public sealed class WindowsPowerShellFactAttribute : FactAttribute
{
    public WindowsPowerShellFactAttribute() { Skip = Shells.SkipWindowsPowerShell(); }
}

public sealed class PwshTheoryAttribute : TheoryAttribute
{
    public PwshTheoryAttribute() { Skip = Shells.SkipPwsh(); }
}

public sealed class PwshFactAttribute : FactAttribute
{
    public PwshFactAttribute() { Skip = Shells.SkipPwsh(); }
}
