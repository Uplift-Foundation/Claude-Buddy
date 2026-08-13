# Windows high-quality speech — findings

Everything here was measured on one real Windows 11 machine (16 logical cores)
unless it says otherwise. Where something is assumed rather than observed, it
says so.

## Why Windows needs its own model at all

The starting point was "Zira is kind of a weak voice — I want HD voices". Windows
11 *has* HD voices. They cannot be used.

Confirmed by installing one (`Ava`, via Settings → Accessibility → Narrator → Add
natural voices) and then looking for it from every angle an app has:

| asked | answer |
| --- | --- |
| `System.Speech` in `powershell` 5.1 (what the app used) | David Desktop, Zira Desktop |
| `System.Speech` in `pwsh` 7 | + David, Mark, Zira (the OneCore variants) |
| WinRT `SpeechSynthesizer.AllVoices` | David, Zira, Mark |
| `HKLM\...\Speech\Voices\Tokens` | David, Zira |
| `HKLM\...\Speech_OneCore\Voices\Tokens` | DavidM, MarkM, ZiraM |

Ava appears in none of them. Its package,
`MicrosoftWindows.Voice.en-US.AvaHD.1`, contains only model data —
`hd_device_vocoder_v6_streaming.bin` (124 MB), `hd_am_v5_decoder.bin` (95 MB),
`MSTTSLocEnUS.dat` (34 MB) — and its `AppxManifest.xml` contains **zero**
occurrences of "sapi" or "Speech". It registers no voice token anywhere. Narrator
loads it through a private path; `HKCU\...\Speech_OneCore\Isolated` holds a set of
hashed per-app keys that look like the mechanism.

So this is not an API we failed to find. Corroborated externally: Microsoft has
not allowed third-party apps to use the Narrator/Edge voices, and the one project
that bridges them
([NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter))
does it with encryption keys extracted from system files, which its own author
calls a hack that can stop working at any time. Microsoft's official on-device
neural TTS, [Azure Embedded
Speech](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/embedded-speech),
is Limited Access — registration only, and only for customers managed by a direct
Microsoft account team. Build 2026 added an on-device *speech recognition* API,
not synthesis.

**Assumed, not verified:** that a reboot would not make Ava appear as a token. The
manifest declaring no speech extension is the reason for believing it, not a test.

## What the model costs, against the built-in voices

Same sentence, synthesis only, both measured the same way.

| | SAPI (`Zira Desktop`) | Kokoro fp16, 2 threads |
| --- | --- | --- |
| CPU per second of audio | 0.014–0.066 core-sec | ~0.99 core-sec |
| CPU for a 6 s reply | 0.42 core-sec | 5.9 core-sec |
| Wall to synthesize 6 s | 0.5 s | 2.9 s |
| RAM | ~105 MB transient child | 242 MB resident, ~640 MB peak |
| Disk | 0 | 156 MB model |

The native cost *falls* with length — 0.066 core-sec per audio-second over 6
seconds, 0.014 over 40 — because its fixed cost is starting PowerShell and loading
`System.Speech`, not the speech. That is a concatenative engine stitching recorded
fragments. Kokoro runs an 82M-parameter network per sample and is linear in output
length, so the ~15× gap is inherent rather than tuning.

Thread capping is load-bearing, not a preference. Left at ONNX Runtime's default
the same utterance cost **16.7 core-seconds across 7.6 of 16 cores**; capped at
two intra-op threads, **5.9 core-seconds across 2.0**, for 0.7 s more wall time.
An earlier attempt scaled the cap with the core count and quietly gave four
threads on this machine — 21 core-seconds for one utterance — which is why the
number is now flatly 2 (1 below four cores).

## Four things about KokoroSharp that changed the design

All four were found by building it, not by reading about it.

1. **The dependency weight is managed, not native.** `MisakiSharp.dll` is
   **66.2 MB** of embedded grapheme-to-phoneme lexicons — English, Japanese,
   Chinese, Hindi — of which this uses English. With `onnxruntime.dll` (11.8 MB)
   and `NumSharp.dll` (3.5 MB) the graph adds ~82 MB. Referencing it from the app
   would have taken the Windows installer from 34.7 MB to ~110 MB **for every
   user, enabled or not**, and put the same weight into the macOS `.app` where it
   can never be used. Embedded resources, so trimming cannot reach it.

2. **`dotnet publish` silently drops the voices.** KokoroSharp ships 54 voice
   `.npy` files via an MSBuild `Copy` running `AfterTargets="Build"` that produces
   no items. They land in `bin\` and never reach `publish\`. Verified directly:

   ```
   publish/ has NO voices folder
   ```

   A published engine finds no voices and refuses to speak, while every
   `dotnet run` looks perfect — the same dev-works/installed-broken shape as the
   `SendInput` and hook bugs elsewhere in these docs.
   `tools/build-speech-engine.ps1` copies them explicitly and fails the build if
   the count is zero.

3. **`KokoroLoader` downloads relative to the process.** Its model fetch writes
   beside the executable, which is not writable under `%ProgramFiles%`. The app
   owns the download instead (`NeuralSpeech`), pinned to a tagged release URL so
   the model cannot change under us — a silently updated model is a silently
   changed voice.

4. **ONNX Runtime is extremely chatty on stderr.** At its default log level it
   writes a warning per graph node it declines to constant-fold: **over 20 KB of
   stderr for one utterance**. The parent captures stderr to diagnose a failed
   engine, so this is set to errors only.

## Why a side-car process rather than an in-process engine

Loading the engine inside the app would have needed an `AssemblyLoadContext`, a
native-library resolver working inside a single-file self-extracting exe,
reflection over every call, and hand-parsing the `.npy` voices — plus a new state
machine, because `KokoroTTS.StopPlayback()` is cooperative and asynchronous, so
"cancelled" and "not speaking" stop being the same instant and a late
`OnSpeechCanceled` can arrive after a newer utterance has started.

A separate process avoids all of it. `TextToSpeech` already modelled speaking as
"a child process is alive" and cancelling as killing it, which a side-car fits
exactly — so a completely different engine slotted in without redesigning
cancellation. It also means **no resident memory to reclaim**: the process exits
when the utterance does, which removed the idle-unload timer the plan had called
for.

Measured phases of the side-car, which is what the latency budget is made of:

```
process start + load 54 voices : 0.15 s
first audio (repeatable)       : 3.3 s
```

Startup is nearly free; the ~3.3 s is the model load plus phoneme-lexicon init
plus the first segment's synthesis, paid per process. Streaming is what keeps it
to 3.3 s rather than far worse: `TranscriptReader.MaxSpokenChars` is 1500, about
100 seconds of audio, and synthesising all of it before making a sound would mean
most of a minute of silence. `SpeakFast` with `MaxFirstSegmentLength = 60`
synthesises a short opening segment and streams the rest behind it.

**The known trade:** a resident daemon would cut later utterances to about a
second by paying the load once. That needs a stdin framing protocol and process
lifecycle management, and was deliberately deferred until the 3.3 s is judged
annoying in real use rather than in theory.

## Open / unverified

- Bundle size is **132 MB** zipped (self-contained runtime + 82 MB of
  dependencies + 27 MB of voices), so the full first-enable download is ~290 MB
  with the model. Every release re-uploads it.
- **Not verified end to end:** the download path itself. Local testing used
  `tools/build-speech-engine.ps1 -Install`, which places the bundle straight into
  `%APPDATA%` — so extraction, validation and the URL have never actually been
  exercised against a real release, because no release containing the asset has
  been cut yet. The first tagged release is what proves it.
- Quality on *real* assistant turns — full of identifiers, paths and punctuation —
  has not been compared against SAPI. Kokoro's own notes warn it is weak under
  10–20 tokens and rushes over 400.
- The dependency graph pins `OpenTK.Audio.OpenAL` at a **prerelease** and
  `NumSharp` last published in 2021. `KokoroSharp.dll` itself is ~100 KB of glue
  over a 66 MB dependency, from a single maintainer — expect to vendor or fork it
  if it goes quiet.
- `dotnet list package --vulnerable --include-transitive` on the engine reports
  **no vulnerable packages**. The advisory the throwaway spike suppressed with a
  `NoWarn` was the app's own pre-existing `Tmds.DBus.Protocol` (NU1903, arriving
  via Avalonia), nothing to do with this — checked so the suppression was not
  copied into a shipped project on a wrong assumption.
