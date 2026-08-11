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
        // On Windows this is a last resort, not a real default: SAPI voice names
        // are fully qualified ("Microsoft David Desktop"), and the bare "David"
        // this used to be is not a name SelectVoice accepts. Measured in the
        // host Speak actually uses — Windows PowerShell 5.1 — SelectVoice('David')
        // and even SelectVoice('Microsoft David') both threw "No matching voice
        // is installed or the voice was disabled", so speaking failed outright on
        // Windows for every session that never picked a voice by hand.
        //
        // "Microsoft David Desktop" is the voice that ships with Windows itself,
        // so it is the safest literal to fall back to; anything actually
        // installed is preferred over it — see ResolveDefaultVoice.
        private const string WindowsFallbackVoice = "Microsoft David Desktop";

        public static readonly string DefaultVoice =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "Susan (Enhanced)"
                : WindowsFallbackVoice;

        private static Process? _speaking;
        private static readonly object Gate = new();

        private static List<string>? _cachedVoices;

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
                        // Lines: "Samantha            en_US    # Hello!..."
                        // Only include English voices.
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;

                        var locale = parts.Length > 1 ? parts[^2] : "";
                        if (!locale.StartsWith("en_")) continue;

                        // Voice name is everything before the locale — names
                        // like "Flo (English (US))" have spaces and parens.
                        var localeStart = line.IndexOf(locale, StringComparison.Ordinal);
                        if (localeStart < 1) continue;
                        var name = line[..localeStart].TrimEnd();

                        voices.Add(name);
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    voices.AddRange(WindowsVoices());
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

        // Asked of the same host that does the speaking — `powershell`, i.e.
        // Windows PowerShell 5.1 — and that detail is the whole point rather
        // than an implementation convenience. Enumerated from PowerShell 7 this
        // machine reported five voices including "Microsoft David"; from 5.1 it
        // reported two, "Microsoft David Desktop" and "Microsoft Zira Desktop".
        // 5.1's System.Speech sees only the SAPI5 registry hive, while 7's sees
        // the Speech_OneCore hive too, so a list gathered from the wrong host
        // offers voices that SelectVoice then refuses. Both names 5.1 reported
        // were confirmed to select successfully.
        //
        // A consequence worth knowing, and the reason the settings link says
        // what it says: voices added through Windows' own Speech settings land
        // in Speech_OneCore, so they will not appear here until this speaks
        // through something that reads that hive.
        private static List<string> WindowsVoices()
        {
            var found = new List<string>();

            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    "Add-Type -AssemblyName System.Speech; " +
                    "(New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | " +
                    "Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq 'en' } | " +
                    "ForEach-Object { $_.VoiceInfo.Name }"
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true   // a WinExe parent shows a console without this
            });
            if (proc is null) return found;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim();
                if (name.Length > 0) found.Add(name);
            }

            return found;
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
                            // SelectVoice is guarded so an unusable voice name
                            // costs the *choice* of voice, not the speech.
                            // It throws rather than returning false when a name
                            // doesn't match, and with stderr discarded the only
                            // symptom was silence — which is how a bad default
                            // ("David", never a real SAPI name) read as "the
                            // speak button does nothing". A voice saved before
                            // this fix, or one that has since been uninstalled,
                            // lands in the same place and now still speaks.
                            $"Add-Type -AssemblyName System.Speech; " +
                            $"$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                            $"try {{ $s.SelectVoice('{voiceEscaped}') }} catch {{ }}; " +
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
