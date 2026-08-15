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
        private static List<VoiceOption>? _cachedOptions;

        public enum SpeakState { Idle, Preparing, Speaking }

        private static SpeakState _state = SpeakState.Idle;

        public static event Action<SpeakState>? StateChanged;

        public static SpeakState State
        {
            get { lock (Gate) return _state; }
        }

        // Still the shape callers use to decide whether the button means "speak"
        // or "stop" — Preparing counts, because pressing it again must cancel
        // rather than start a second utterance.
        public static bool IsSpeaking => State != SpeakState.Idle;

        private static void Enter(SpeakState state)
        {
            lock (Gate)
            {
                if (_state == state) return;
                _state = state;
            }

            // Raised outside the lock: handlers reach into Avalonia, and holding
            // this lock across UI work is how a click that cancels deadlocks
            // against a callback that reports.
            StateChanged?.Invoke(state);
        }

        public static void Cancel()
        {
            Process? victim;
            lock (Gate)
            {
                victim = _speaking;
                _speaking = null;
            }

            if (victim is not null) KillTree(victim);

            Enter(SpeakState.Idle);
        }

        // Stops the process and everything it started, which for anything but the
        // simplest engine is where the sound actually comes from: a `.cmd` wrapper
        // is cmd.exe that spawns the real speaker, and killing only the tracked
        // child leaves that grandchild talking with nothing left to stop it.
        //
        // Written against the pid rather than the Process object on purpose. The
        // object can be disposed concurrently by the Exited handler the instant the
        // parent dies — observed in a real run, where the id was already
        // unreadable by the time the kill returned — and anything that reads
        // `victim.Id` or `victim.HasExited` after that point is working with a
        // corpse. The pid is captured once, first, and everything else is done with
        // that number.
        //
        // Windows gets taskkill /T /F because it is a single call that kills a tree
        // and does not care about the state of our Process object. Process.Kill(
        // entireProcessTree: true) is meant to do exactly this and could not be
        // made to fail in isolation — every standalone attempt killed the
        // grandchild correctly — yet a real run through the app orphaned one
        // surviving speech process per utterance, every time. That mechanism is
        // therefore not understood well enough to be the only thing standing
        // between a user and audio they cannot stop.
        //
        // Verified after the change: a log of the descendants either side of the
        // kill showed "conhost.exe, powershell.exe" before and "(none)" after,
        // across repeated cycles, with no survivors left running.
        private static void KillTree(Process victim)
        {
            int pid;
            try
            {
                pid = victim.Id;
            }
            catch
            {
                return;   // already gone; nothing to kill and no id to kill it by
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var taskkill = Process.Start(new ProcessStartInfo("taskkill")
                    {
                        ArgumentList = { "/PID", pid.ToString(), "/T", "/F" },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    taskkill?.WaitForExit(3000);
                }
                catch { /* fall through to Kill below */ }
            }

            // Also asked directly, and last: on macOS this is the whole mechanism,
            // and on Windows it catches the case where taskkill is unavailable.
            try
            {
                if (!victim.HasExited) victim.Kill(entireProcessTree: true);
            }
            catch { /* already gone, or the object is disposed — both fine here */ }
        }

        // Cleared when the engine choice changes. The cache below lasts for the
        // process, which is right for a list the OS won't change underneath us —
        // but the *which list* question changes the moment the neural toggle
        // moves, and without this the picker would keep offering the other
        // engine's names, every one of which the new engine rejects.
        public static void InvalidateVoiceCache()
        {
            lock (Gate)
            {
                _cachedVoices = null;
                _cachedOptions = null;
            }
        }

        // Which of the three ways of speaking a voice belongs to.
        //
        // These used to be decided by precedence — a configured command beat the
        // neural engine, which beat the system voices — which meant installing one
        // silently took the others away, and the settings picker could only ever
        // show a third of what the machine could do. The engine is now a property
        // of the chosen voice instead: pick "af_bella" and you are using Kokoro,
        // pick "Microsoft Zira Desktop" and you are using SAPI, and all of them sit
        // in one list.
        public enum SpeakEngine { System, Neural, Custom }

        // One selectable voice. Label is what the settings window shows — the bare
        // name plus where it comes from, because "af_bella" and "Microsoft Zira
        // Desktop" and whatever a user's own command calls its voices are otherwise
        // three naming conventions in one dropdown with no clue which is which.
        public sealed record VoiceOption(SpeakEngine Engine, string Name, string Label);

        public static bool CustomCommandConfigured =>
            !string.IsNullOrWhiteSpace(ClaudeBuddySettings.SpeakCommand);

        // Everything this machine can speak with, from every engine that is
        // actually available, in the order they are worth trying: the system voices
        // always exist, Kokoro only once downloaded, a user command only once
        // configured.
        public static List<VoiceOption> AllVoiceOptions()
        {
            // Cached with the same lifetime as the system list, and for a sharper
            // reason: building this asks the neural engine to enumerate itself and
            // runs the user's listing command, both of which are process launches.
            // The settings window rebuilds its whole content on every toggle and
            // every theme change, so without this each of those would spawn two
            // programs and wait for them.
            if (_cachedOptions is not null) return _cachedOptions;

            var options = new List<VoiceOption>();

            foreach (var name in SystemVoices())
            {
                options.Add(new VoiceOption(SpeakEngine.System, name, $"{name} (system)"));
            }

            if (NeuralSpeech.Available)
            {
                foreach (var name in NeuralSpeech.Voices())
                {
                    options.Add(new VoiceOption(SpeakEngine.Neural, name, $"{name} (Kokoro)"));
                }
            }

            foreach (var name in CustomCommandVoices())
            {
                options.Add(new VoiceOption(SpeakEngine.Custom, name, $"{name} (custom)"));
            }

            // A command that speaks but lists nothing still needs to be selectable,
            // or there is no way to choose it at all. It gets one entry standing for
            // "whatever that command decides".
            if (CustomCommandConfigured && !options.Any(o => o.Engine == SpeakEngine.Custom))
            {
                options.Add(new VoiceOption(SpeakEngine.Custom, "", "Custom command"));
            }

            lock (Gate) _cachedOptions = options;
            return options;
        }

        // The voice currently selected, resolved against what is actually
        // available. Falls back rather than failing: a saved selection can name an
        // engine that has since been uninstalled or a voice that no longer exists,
        // and speaking in the wrong voice beats not speaking.
        public static VoiceOption? SelectedVoice()
        {
            var options = AllVoiceOptions();
            if (options.Count == 0) return null;

            var engine = ClaudeBuddySettings.SpeakEngine switch
            {
                "custom" => SpeakEngine.Custom,
                "neural" => SpeakEngine.Neural,
                _ => SpeakEngine.System
            };

            var name = engine switch
            {
                SpeakEngine.Custom => ClaudeBuddySettings.SpeakCommandVoice ?? "",
                SpeakEngine.Neural => ClaudeBuddySettings.NeuralVoice,
                _ => ClaudeBuddySettings.SpeakVoice
            };

            return options.FirstOrDefault(o => o.Engine == engine
                       && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? options.FirstOrDefault(o => o.Engine == engine)
                   ?? options[0];
        }

        // Records a choice made in the settings window, writing both which engine
        // speaks and that engine's own voice key. The per-engine keys are kept
        // separate so switching away from an engine and back remembers what was
        // chosen there — see the comments on them in ClaudeBuddySettings.
        public static void SelectVoice(VoiceOption option)
        {
            switch (option.Engine)
            {
                case SpeakEngine.Custom:
                    ClaudeBuddySettings.SpeakCommandVoice = option.Name;
                    ClaudeBuddySettings.SpeakEngine = "custom";
                    break;
                case SpeakEngine.Neural:
                    ClaudeBuddySettings.NeuralVoice = option.Name;
                    ClaudeBuddySettings.SpeakEngine = "neural";
                    break;
                default:
                    ClaudeBuddySettings.SpeakVoice = option.Name;
                    ClaudeBuddySettings.SpeakEngine = "system";
                    break;
            }
        }

        // The voices a user's own command says it has, one name per line on its
        // stdout. Empty when no listing command is configured, which is the normal
        // case — most wrappers speak with one fixed voice and have nothing to list.
        public static List<string> CustomCommandVoices()
        {
            var found = new List<string>();

            var command = ClaudeBuddySettings.SpeakVoicesCommand;
            if (string.IsNullOrWhiteSpace(command)) return found;

            try
            {
                var startInfo = new ProcessStartInfo(command)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Its own arguments, deliberately not the speak command's: one
                // script serving both roles branches on a flag, and reusing the
                // speak arguments would hand that flag to the speaking invocation
                // too, which would list voices instead of talking.
                foreach (var argument in ClaudeBuddySettings.SpeakVoicesCommandArgs)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                var process = Process.Start(startInfo);
                if (process is null) return found;

                var output = process.StandardOutput.ReadToEnd();

                // Bounded, unlike the speak path: this runs while a settings
                // window is being built, and a wrapper that hangs must not hang
                // the UI with it.
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    Console.Error.WriteLine(
                        "Claude Buddy: the speak voices command took too long and was stopped");
                    return found;
                }

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = line.Trim();
                    if (name.Length > 0) found.Add(name);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Claude Buddy: couldn't list voices from the speak command: {ex.Message}");
            }

            return found;
        }

        // The platform's own voices — `say` on macOS, SAPI on Windows — and only
        // those. Kept separate from the neural and custom lists because all three
        // are now offered side by side rather than one shadowing the others; see
        // AllVoiceOptions.
        public static List<string> SystemVoices()
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

            // Whichever engine owns the selected voice, rather than a fixed
            // precedence: all three are offered together now, so the choice made in
            // the settings window is the choice, not a hint that something else can
            // override.
            var selected = SelectedVoice();

            // A user command is *not* fallen back from. Someone who configured
            // their own engine wants that engine; a silent substitution to a
            // robotic system voice would look like their command working badly
            // rather than not running, which is the harder failure to diagnose. It
            // reports and stays quiet instead.
            if (selected?.Engine == SpeakEngine.Custom && StartCustomCommand(text)) return;

            // The neural engine *is* fallen through from rather than trusted: if it
            // can't start — a partial download, a model deleted by hand — the same
            // click still speaks with a system voice. Nobody hand-configured this
            // one, so quietly using another is a repair rather than a substitution,
            // and speaking worse is a much better failure than not speaking, which
            // is the shape of bug the comment at the top of this file exists
            // because of.
            if (selected?.Engine == SpeakEngine.Neural
                && NeuralSpeech.Available
                && StartNeural(text))
            {
                return;
            }

            // A system voice, either because it was the one chosen or because
            // whichever engine was chosen could not be reached.
            voice ??= selected?.Engine == SpeakEngine.System && selected.Name.Length > 0
                ? selected.Name
                : DefaultVoice;

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

            proc.Exited += (_, _) => Finished(proc);

            lock (Gate) _speaking = proc;

            // Straight to Speaking: `say` and SAPI both start audio in about half
            // a second, so a Preparing state would flicker rather than inform.
            Enter(SpeakState.Speaking);

            try
            {
                proc.Start();
            }
            catch
            {
                lock (Gate) _speaking = null;
                proc.Dispose();
                Enter(SpeakState.Idle);
            }
        }

        // Whatever the user pointed ClaudeBuddySettings.SpeakCommand at. Returns
        // false only when no command is configured — a configured command that
        // fails to launch returns true, having reported why, because falling
        // through to a system voice would disguise the problem.
        //
        // The interface is the one this class already had for `say` and
        // PowerShell, which is the point: text on stdin, exit when finished,
        // killed to cancel. Nothing else is required of it — printing "speaking"
        // on stdout when audio starts is optional and only sharpens the button's
        // state, never a condition of working.
        private static bool StartCustomCommand(string text)
        {
            var command = ClaudeBuddySettings.SpeakCommand;
            if (string.IsNullOrWhiteSpace(command)) return false;

            var startInfo = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in ClaudeBuddySettings.SpeakCommandArgs)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // The chosen voice arrives as an environment variable rather than an
            // argument. SpeakCommandArgs belongs to the user, and appending a
            // positional argument would break any wrapper that takes fixed ones;
            // a command that doesn't read this never notices it. Set even when
            // empty so a wrapper can tell "no choice made" from a stale value
            // inherited from this process's own environment.
            startInfo.Environment["CLAUDEBUDDY_VOICE"] =
                ClaudeBuddySettings.SpeakCommandVoice ?? "";

            var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            // Preparing until the command says otherwise, which is the opposite
            // default to the system voices and deliberate. Those start audio in
            // about half a second; someone else's chain could be instant or could
            // load a model for ten seconds, and there is no way to tell from out
            // here. So the hourglass is the honest starting claim — "we asked, it
            // is running" — and printing the readiness marker is what upgrades it
            // to a stop square at the real moment.
            //
            // A command that never prints anything therefore shows the hourglass
            // for its whole run. That is the right way round: the button still
            // cancels either way, and an hourglass over speech is a smaller lie
            // than a stop square over silence.
            Enter(SpeakState.Preparing);

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null && e.Data.StartsWith("speaking", StringComparison.Ordinal))
                {
                    Enter(SpeakState.Speaking);
                }
            };

            // Surfaced, not swallowed: this is somebody else's program, and its
            // stderr is the only explanation anyone will get for why their voice
            // didn't happen.
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Console.Error.WriteLine($"Claude Buddy: speak command: {e.Data}");
                }
            };

            proc.Exited += (_, _) => Finished(proc);
            lock (Gate) _speaking = proc;

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                proc.StandardInput.Write(text);
                proc.StandardInput.Close();

                // Exited can fire before the handler above is attached for a
                // command that fails instantly, which would otherwise leave the
                // button stuck showing stop with nothing behind it.
                if (proc.HasExited) Finished(proc);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Claude Buddy: couldn't start the configured speak command '{command}': {ex.Message}");

                lock (Gate) _speaking = null;
                proc.Dispose();
                Enter(SpeakState.Idle);
                return true;
            }
        }

        private static bool StartNeural(string text)
        {
            // Announced before the process exists, because starting it is itself
            // part of the wait being announced.
            Enter(SpeakState.Preparing);

            var proc = NeuralSpeech.Start(
                text,
                ClaudeBuddySettings.NeuralVoice,
                onSpeaking: () => Enter(SpeakState.Speaking));

            if (proc is null)
            {
                Enter(SpeakState.Idle);
                return false;
            }

            proc.Exited += (_, _) => Finished(proc);
            lock (Gate) _speaking = proc;

            // Exited can fire before the handler above is attached for a process
            // that failed instantly, which would leave the button stuck showing
            // Preparing forever. Checking afterwards closes that window.
            if (proc.HasExited) Finished(proc);

            return true;
        }

        // One exit path for both engines. Guarded on identity rather than a flag:
        // a cancel replaces _speaking immediately, so the *previous* process's
        // Exited arriving late must not report Idle over speech that has already
        // started.
        private static void Finished(Process proc)
        {
            bool current;
            lock (Gate)
            {
                current = _speaking == proc;
                if (current) _speaking = null;
            }

            try { proc.Dispose(); } catch { }

            if (current) Enter(SpeakState.Idle);
        }
    }
}
