using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClaudeBuddy
{
    // Speaks text aloud using the platform's built-in TTS: `say` on macOS,
    // PowerShell's SpeechSynthesizer on Windows. A second call while speech
    // is in progress cancels the first, so the flyout button toggles
    // naturally between speak and stop.
    public static class TextToSpeech
    {
        private static Process? _speaking;
        private static readonly object Gate = new();

        public static bool IsSpeaking
        {
            get
            {
                lock (Gate)
                    return _speaking is not null && !_speaking.HasExited;
            }
        }

        public static void Cancel()
        {
            lock (Gate)
            {
                if (_speaking is null) return;
                try
                {
                    if (!_speaking.HasExited)
                        _speaking.Kill();
                }
                catch { }
                _speaking = null;
            }
        }

        public static void Speak(string text)
        {
            Cancel();

            if (string.IsNullOrWhiteSpace(text)) return;

            Process proc;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/say",
                        ArgumentList = { text },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // PowerShell one-liner using .NET's built-in SpeechSynthesizer;
                // avoids a NuGet dependency for a feature every Windows box has.
                var escaped = text.Replace("'", "''");
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        ArgumentList =
                        {
                            "-NoProfile", "-Command",
                            $"Add-Type -AssemblyName System.Speech; " +
                            $"(New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak('{escaped}')"
                        },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };
            }
            else
            {
                return;
            }

            proc.Exited += (_, _) =>
            {
                lock (Gate)
                {
                    if (_speaking == proc)
                        _speaking = null;
                }
                proc.Dispose();
            };

            lock (Gate) _speaking = proc;

            try
            {
                proc.Start();
            }
            catch
            {
                lock (Gate) _speaking = null;
                proc.Dispose();
            }
        }
    }
}
