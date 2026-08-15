# macOS high-quality speech — findings

Companion to `windows-neural-voice-findings.md`. Everything here was measured on
one real Mac — macOS 27.0, Apple Silicon (arm64), .NET SDK 10.0.301 — unless it
says otherwise. Where something is assumed rather than observed, it says so.

The short version: the engine that was written for Windows runs on macOS
unchanged, but **KokoroSharp's playback does not**, and enabling the feature on
macOS without replacing it ships a voice that costs twelve seconds of CPU and
says nothing.

## The engine itself was already portable

Nothing about Kokoro, ONNX Runtime or the voice files is Windows-bound. Publishing
`tools/ClaudeBuddySpeech` for `osx-arm64` produced a working engine on the first
attempt:

| checked | result |
| --- | --- |
| `libonnxruntime.dylib` for arm64 | present, and signed by its package |
| `--list-voices` | the same English voice list Windows reports |
| Synthesis | works; ~half real time, matching the Windows measurement |
| Apphost signature | ad-hoc, applied by the SDK at publish time |
| Publish size / zip | 209 MB on disk, 129 MB zipped |

`af_maple` and `af_sol` are missing here exactly as they are on Windows — the
package ships no `.npy` for either — so that gap is the package's, not the
platform's.

## Playback is the part that does not port

KokoroSharp plays through NAudio on Windows and falls back to OpenTK's OpenAL
binding everywhere else. On macOS that fallback segfaults:

```
EXC_BAD_ACCESS (SIGSEGV), KERN_INVALID_ADDRESS at 0x0000000000000008
Thread ".NET TP Worker" crashed:
  OpenAL  OALDeviceMap::Add(unsigned long, OALDevice**)
  OpenAL  alcOpenDevice
```

Deterministic — identical on every run. It dies in `alcOpenDevice`, which is the
*first* step of opening the device, so **no sample is ever played**.

This was nastier than an ordinary crash because of what the caller sees. The
engine prints its `speaking` marker from `OnSpeechStarted`, which fires before
the device is open, so the orb reported that it was speaking, sat silent for the
full synthesis, and then exited 134. The app reads any non-zero exit as "the
engine failed" and falls back, so the *symptom* would have been a pause followed
by the system voice — with nothing pointing at OpenAL.

### It is not Apple's framework being broken

That was the obvious suspicion, and it is wrong. Apple deprecated OpenAL in 10.15
but it still works. A C harness against `OpenAL.framework` opened the device
successfully in every configuration worth trying:

| harness | result |
| --- | --- |
| `alcOpenDevice(NULL)` on the main thread | handle `0x18`, context created |
| `alcOpenDevice("MacBook Pro Speakers")` by enumerated name | handle `0x18` |
| `alcOpenDevice(NULL)` from a `pthread` | handle `0x19` |
| ...as the first-ever OpenAL call in the process, off the main thread | handle `0x18` |

And .NET through OpenTK is fine too, in isolation:

| harness | result |
| --- | --- |
| `ALC.OpenDevice(null)`, main thread | device + context, survived |
| ...from `Task.Run` (a thread-pool worker) | survived |
| ...from a dedicated `Thread` | survived |
| ...on OpenTK **4.9.4** and on **5.0.0-pre.13** | both survived |

KokoroSharp resolves OpenTK 5.0.0-pre.13, so a prerelease binding was the leading
suspect; it opens the device happily on its own. Only KokoroSharp's own playback
path reproduces the crash, and the exact trigger inside it was not isolated.

**Not established:** what specifically in that path corrupts the call. Chasing it
further was judged not worth it — the fix below removes the dependency on OpenAL
altogether, and the answer would have lived in a prerelease binding to a
framework Apple has deprecated.

## What replaced it

macOS synthesises through `KokoroWavSynthesizer` and plays through `afplay`. See
`SpeakThroughAfplay` in `tools/ClaudeBuddySpeech/Program.cs`.

This fits the shape the engine already had everywhere else — `TextToSpeech` has
always modelled speaking as "a child process is alive" and stopping as killing
it — so nothing in the app had to change to accept it.

Three things worth knowing about that API, all learned the hard way:

- The streaming overload's `OnProgress` hands over **`float[]` samples**, not
  bytes, and the segments are incremental rather than cumulative.
- It is **non-blocking**. It returns as soon as the job is queued and the
  segments arrive later on KokoroSharp's own engine thread. Closing the queue
  when the call returns marks it complete before the first segment exists, and
  the synthesiser then throws into a thread nobody is catching on. The wait has
  to be on the completion callback.
- The *blocking* overload returns **headerless PCM** despite the type being
  called `KokoroWavSynthesizer` — measured, the bytes begin with samples, not
  `RIFF`, and `afinfo` rejects the file. `SaveAudioToFile` is what adds a header.
  The streaming path writes its own: 24kHz, mono, 16-bit.

Streaming is preserved, which was the point of the original segmented design.
Measured on a three-sentence input: the first segment arrives at **989 ms**
carrying 2.02 s of audio, the second at 2478 ms carrying 4.78 s. Playback starts
well inside a second, and synthesis stays ahead of it.

### Two behaviours that follow from using a child player

- **Cancelling works, and is immediate.** Verified with the app's actual call,
  `Process.Kill(entireProcessTree: true)`: `afplay` was gone within 400 ms of the
  kill. This only works because `TextToSpeech.KillTree` already kills the whole
  tree — it was written that way after a `.cmd` wrapper's grandchild kept talking,
  and an `afplay` started here is exactly that grandchild.
- **A bare kill of the engine alone does not cut the audio.** The segment already
  handed to `afplay` finishes on its own — up to a few seconds — because nothing
  told the player to stop. Not a problem through the app, which kills the tree,
  but worth knowing before testing this from a shell and concluding cancellation
  is broken.

### Exiting has to skip the runtime's own shutdown

Once every sample has played, tearing the synthesiser and its ONNX session down
aborts the process: libc++abi reports `mutex lock failed: Invalid argument` and
the runtime turns that into exit 134. Since the caller treats any non-zero exit
as failure, that would have made the app speak the whole turn a second time in
the system voice — a crash the user hears as a stutter rather than an error.

Traced rather than guessed: `OnComplete`, then every `afplay` exiting 0, then the
player thread joining, and only then the abort. `Environment.Exit` is not enough
because it still runs managed shutdown, which is where the abort happens. The
engine calls libc `_exit(2)` after flushing both streams.

**Assumed, not verified:** that the underlying disposal bug is KokoroSharp's
rather than something this code provokes. It also appeared in a scratch harness
that used KokoroSharp directly, with no `afplay` and none of this engine's code
in the path, which is the reason for believing it — but it was not reduced to a
minimal case or chased upstream.

## Packaging

`tools/build-speech-engine.sh` is the macOS twin of the `.ps1`. Two constraints
shaped it:

- **It must run on a Mac.** The SDK ad-hoc signs the apphost with `codesign`,
  which only exists on macOS, and Apple Silicon refuses to exec an arm64 binary
  with no signature at all. That is why the release workflow builds this in the
  macOS job, in the same rid matrix as the DMGs, rather than beside the Windows
  installer. *Assumed, not verified:* that a Windows cross-publish leaves the
  apphost unsigned — the local publish being ad-hoc signed is what was observed,
  and no Windows machine was used to check the negative case.
- **Both macOS RIDs need an asset.** `NeuralSpeech.EngineRid` used to hardcode
  `osx-arm64` for every Mac while the release ships an `osx-x64` DMG too, so an
  Intel Mac would have downloaded 130 MB and been killed on exec. It now asks
  `RuntimeInformation.ProcessArchitecture`, which also gets the Rosetta case
  right: an x64 build on Apple Silicon needs the x64 engine.

### Signing the engine needs its own entitlements

The macOS release job exports `MACOS_SIGNING_IDENTITY`, so the engine is signed
with a real Developer ID there, and that turns on the hardened runtime. A
self-contained .NET process signed that way and given no entitlements does not
start at all:

```
Failed to create CoreCLR, HRESULT: 0x80070008
```

Measured, not predicted — signed both ways on this machine and run. So the
engine carries `tools/ClaudeBuddySpeech.entitlements`: `allow-jit`,
`allow-unsigned-executable-memory` and `disable-library-validation` (ONNX
Runtime's dylib is signed by its publisher, not by this team). Deliberately
shorter than the app's list, which also needs Apple Events and the microphone;
the engine sends no events and opens no input device.

Verified on the real artifact: a signed engine extracted from the zip keeps
`flags=0x10000(runtime)` and all three entitlements, lists its voices, speaks, and
exits 0.

One trap in writing that file: `codesign` parses entitlements through
`AMFIUnserializeXML`, which rejects a double hyphen inside an XML comment and
reports only `syntax error near line N`. Naming a codesign flag in a comment is
enough to break it.

The archive was verified through the app's own extraction path — .NET's
`ZipFile.ExtractToDirectory`, not `unzip` — and comes out with
`ClaudeBuddySpeech` and `voices/` at the top level, no `__MACOSX` entries, and
the executable bit already set at 755 before `SetUnixFileMode` is applied. The
extracted artifact was then run: voices listed, spoke, exit 0.

`zip` rather than `ditto` on purpose: `ditto` adds `__MACOSX` resource-fork
entries that `ZipFile.ExtractToDirectory` materialises as junk beside the binary.
There are no symlinks in a self-contained publish (checked), so nothing needs
ditto's handling of them.

One trap that cost a confusing half hour: `cp -R voices publish/voices` nests as
`publish/voices/voices` when the target already exists, and the engine then loads
every voice twice — visible only as `--list-voices` quietly reporting double
the voices it should. The script removes the target first and copies the `.npy`
files by name.

## Gatekeeper does not block the downloaded engine

The engine is signed with a Developer ID but is **not notarized** — it is not a
bundle and is not submitted with the DMGs — so `spctl` assesses it as rejected:

```
$ spctl -a -vv -t exec .../ClaudeBuddySpeech
rejected
source=Unnotarized Developer ID
```

That assessment does not stop it running, because Gatekeeper enforces on files
carrying `com.apple.quarantine`, and nothing in this path sets one. Quarantine is
applied by download*ers* that participate in it — browsers, and anything using
LaunchServices — not by an arbitrary `HttpClient`, and `ZipFile.ExtractToDirectory`
does not propagate an attribute that was never there.

Verified rather than reasoned about: the zip was served over HTTP, fetched with
the same `HttpClient` calls `DownloadCoreAsync` makes, extracted with the same
`ZipFile` call, and then checked and run.

| checked | result |
| --- | --- |
| `com.apple.quarantine` on the executable | absent |
| Quarantined files anywhere in the extracted tree | 0 |
| `codesign --verify --strict` after the roundtrip | valid, satisfies its Designated Requirement |
| Running it | listed voices, spoke, exit 0 |

**Assumed, not verified:** that a real GitHub HTTPS release URL behaves the same
as the localhost HTTP one. The same API does the fetching either way, and
quarantine is a property of the downloader rather than the origin, which is the
reason for believing it.

## Custom voices

Tested, because the whole feature exists for them.

| file dropped in the user voices directory | listed? | spoke? |
| --- | --- | --- |
| `af_warrentest.npy` | yes | yes, exit 0 |
| `nopfx_custom.npy` (no recognised prefix) | yes | yes, exit 0 |
| `bf_british.npy` | yes | yes, exit 0 |
| `zf_chinese.npy` | no — filed under Mandarin | n/a |

Two things this contradicted, both of which had been written down as fact:

- **A prefix-less name is not invisible.** It falls through to the American
  English list and behaves like any other voice. What hides a voice is a prefix
  claiming a *different* language.
- **British voices were being filtered out entirely.** `EnglishVoices()` asked
  for `AmericanEnglish` only, so `bf_alice`, `bf_emma`, `bf_isabella`, `bf_lily`,
  `bm_daniel`, `bm_fable`, `bm_george` and `bm_lewis` shipped inside every 130 MB
  bundle and appeared in no picker — while the README told users `bf_`/`bm_` were
  prefixes that would work. It now asks for both English variants: 28 bundled
  voices instead of 20, and a custom British voice shows up.

That bug was **pre-existing and not macOS-specific** — Windows filtered the same
eight voices out of its own picker.

The eight were listened to before being let into the picker, rather than assumed
to be fine because they shipped: they are the same quality as the American set
and belong there. That was a product call as much as a bug fix, since restoring
them changes what every user sees on both platforms.

## Open / unverified

- **`osx-x64` has never run on a real Intel Mac.** It builds, signs and packs
  (135.9 MB), and it was run under **Rosetta** on Apple Silicon — 22 voices
  listed, spoke, exit 0, noticeably slower as translation implies. That exercises
  the x86_64 binary and the x64 ONNX library, but it is not the same as native
  Intel hardware.
- **Nothing has been driven through the app's own UI.** Every test here invoked
  the engine binary directly. The settings toggle, the voice picker and the orb's
  speak button have not been exercised against this engine.
- Not tested against a real published asset — the release URL cannot be exercised
  until a tag carries one.
- Only macOS 27.0 was tested. The app's `LSMinimumSystemVersion` is 11.0, and
  nothing older was tried; `afplay` and the OpenAL-free path are not expected to
  be version-sensitive, but that is an expectation, not a measurement.
- A full-length turn plays through — 770 characters, 54 s, many segments, exit 0.
  Each segment is a separate `afplay`, so there is a process start between them,
  and the concern was that this would read as a stutter rather than a pause.
  **Listened to and judged fine**, which is the only test that settles it. Worth
  re-checking if the segmentation config ever changes, since the gap scales with
  how often a new `afplay` starts rather than with anything measured here.
- Bluetooth and external audio devices were not tested; `afplay` follows the
  system default output, so device switching mid-utterance is unexplored.
- A cancelled utterance leaves its temp WAV behind in `$TMPDIR`, because the tree
  kill skips the cleanup. They are named per pid and macOS reaps that directory,
  so this is untidiness rather than a leak.
