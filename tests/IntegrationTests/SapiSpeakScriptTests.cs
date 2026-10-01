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
// Windows PowerShell on the macOS runner) is skipped by returning.
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

    private static (bool Ran, string Out) Run(string exe, string script, string? text, string? voice)
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

        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return (false, ""); }
        if (p is null) return (false, "");
        using (p)
        {
            var o = p.StandardOutput.ReadToEnd();
            var e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (true, o + "\n" + e);
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
        foreach (var exe in new[] { "powershell", "pwsh" })
        {
            yield return new object[] { exe, "hello", "x’); Write-Output INJECTED; (’" };
            yield return new object[] { exe, "he said ‘hi’); Write-Output INJECTED; (‛", "O'Brien" };
            yield return new object[] { exe, "$(Write-Output INJECTED) `n `` \"q\" 'a' é中", "Microsoft Zira Desktop" };
            yield return new object[] { exe, "line1\nline2", "v" };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheGeneratedScriptRoundTripsHostileTextAndVoiceWithoutRunningThem(
        string exe, string text, string voice)
    {
        // Full volume: the Volume line is a property set on the stub-less
        // real synthesizer and is not what is under test here.
        var (ran, output) = Run(exe, Stubbed(TextToSpeech.WindowsSpeakScript(1.0)), text, voice);
        if (!ran) return;

        // Only the base64 echoes may appear in the output; a command that
        // ran would print the bare word.
        var scrubbed = output
            .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "")
            .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(voice)), "");
        Assert.DoesNotContain("INJECTED", scrubbed);
        Assert.Equal(voice, Decode(output, "VOICE"));
        Assert.Equal(text, Decode(output, "TEXT"));
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void NegativeControl_TheOldInterpolationRanTheInjectedCommand(string exe)
    {
        var (ran, output) = Run(exe, Old("hi", "x’); Write-Output INJECTED; (’"), null, null);
        if (!ran) return;

        Assert.Contains("INJECTED", output);
    }
}
