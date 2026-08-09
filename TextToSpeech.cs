using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Speaks text aloud using the platform's built-in TTS: `say` on macOS,
    // PowerShell's SpeechSynthesizer on Windows. A second call while speech
    // is in progress cancels the first, so the flyout button toggles
    // naturally between speak and stop.
    public static class TextToSpeech
    {
        public static readonly string DefaultVoice =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Susan (Enhanced)" : "David";

        private static Process? _speaking;
        private static readonly object Gate = new();

        private static List<string>? _cachedVoices;

        public static void ClearVoiceCache() => _cachedVoices = null;

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

        public static List<string> AvailableVoices()
        {
            if (_cachedVoices is not null) return _cachedVoices;

            var voices = new List<string>();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/usr/bin/say",
                        ArgumentList = { "-v", "?" },
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    });
                    if (proc is null) return voices;

                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);

                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        // Lines: "Ava (Premium)       en_US    # Hello! My name is Ava."
                        // Strip the sample text after '#' first, then the
                        // locale is the last whitespace-delimited token in
                        // what remains.
                        var hashIdx = line.IndexOf('#');
                        var meta = hashIdx >= 0 ? line[..hashIdx] : line;

                        var parts = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;

                        var locale = parts[^1];
                        if (!locale.StartsWith("en_")) continue;

                        var localeStart = meta.LastIndexOf(locale, StringComparison.Ordinal);
                        if (localeStart < 1) continue;
                        var name = meta[..localeStart].TrimEnd();

                        voices.Add(name);
                    }
                }
            }
            catch { }

            if (voices.Count == 0) voices.Add(DefaultVoice);

            // Premium and Enhanced voices first — they're what people want.
            int Tier(string n) =>
                n.Contains("(Premium)") ? 0 :
                n.Contains("(Enhanced)") ? 1 : 2;
            voices.Sort((a, b) =>
            {
                var cmp = Tier(a).CompareTo(Tier(b));
                return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            _cachedVoices = voices;
            return voices;
        }

        public static void Speak(string text, string? voice = null)
        {
            Cancel();

            if (string.IsNullOrWhiteSpace(text)) return;

            voice ??= DefaultVoice;

            Process proc;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/say",
                        ArgumentList = { "-v", voice, text },
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
                var escaped = text.Replace("'", "''");
                var voiceEscaped = voice.Replace("'", "''");
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        ArgumentList =
                        {
                            "-NoProfile", "-Command",
                            $"Add-Type -AssemblyName System.Speech; " +
                            $"$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                            $"$s.SelectVoice('{voiceEscaped}'); " +
                            $"$s.Speak('{escaped}')"
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
