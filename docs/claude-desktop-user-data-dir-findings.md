# Claude Desktop stopped honouring `CLAUDE_USER_DATA_DIR`

Reported as: *"the Claude Desktop app is NOT opening multiple profiles — it's
opening the same profile twice."* Measured on macOS 27.0, Claude Buddy
0.4.2-beta, against Claude Desktop **1.34493.1** and **1.37937.0**.

Both builds, deliberately. The first is what `/Applications/Claude.app` was
sitting at; the second is what one of the cloned profile bundles had already
self-updated itself to, which made it free to re-run the same measurements
against a newer build. The variable is ignored and the switch works on both, so
this is not a single-build regression that will fix itself if you wait.

## What was actually happening

Two Claude Desktop main processes were running. Read straight off the machine:

```
pid 49337  bundles/Claude-Board/Claude.app  CLAUDE_USER_DATA_DIR=…/Claude-Board
pid 38854  bundles/Claude-Board/Claude.app  (no variable — i.e. Default)
```

So the menu was doing its job: one row launched Board with the variable set,
and Claude Buddy's own scan mapped the two to two different profiles. The app
disagreed:

```
$ lsof -p 49337 | grep "Application Support/Claude"
45 files under  …/Application Support/Claude          <- Default
 0 files under  …/Application Support/Claude-Board    <- what it was asked for
```

`…/Application Support/Claude-Board` had not been written since the previous
day, while `…/Application Support/Claude` was being modified continuously by
both processes. Both were also logging to `~/Library/Logs/Claude` rather than
`<profile>/Logs`, which the variable is supposed to redirect as well.

Two Chromium processes on one user-data directory is exactly the concurrent
leveldb/SQLite access the profile feature exists to prevent, so this was
worse than a cosmetic mix-up.

## Confirmed on a real machine

- **The variable is ignored, and no Claude Buddy code is involved.** Launching
  the *installed* `/Applications/Claude.app` — not a tinted clone — with
  `open -n -a /Applications/Claude.app --env CLAUDE_USER_DATA_DIR=<empty
  scratch dir>` left the scratch directory completely empty and gave the new
  pid 38 open files under `…/Application Support/Claude`.

- **`--user-data-dir` still works.** The same bundle, launched with
  `open -n -a /Applications/Claude.app --args --user-data-dir=<scratch>`,
  populated the scratch directory with a full profile (30 entries, including
  `Cookies`, `config.json`, `Local Storage`, `claude_desktop_config.json`) and
  opened nothing under `…/Application Support/Claude`.

- **Both together are fine, including a path containing a space.** `open -n -a
  <app> --env CLAUDE_USER_DATA_DIR=<dir> --args --user-data-dir=<dir>` against
  a directory named `probe combined` landed entirely in that directory. `ps`
  shows the switch arriving as a single argv token with the space intact.

- **`open(1)` accepts an operand before `--args`.** Verified with a throwaway
  `.app` that logs its own `argv`: `open -n -a Probe.app --env FOO=bar
  /tmp/probefile.txt --args --user-data-dir="/some/path with space"` delivers
  the file as an operand *and* the switch as `argv[1]`. This is what lets the
  URL router pass both a `claude://` URL and the switch.

- **The fix works end to end through a tinted clone.** Launching
  `bundles/Claude-Board/Claude.app` with both selectors gave a process with 79
  open files under `…/Application Support/Claude-Board` and none under
  `…/Application Support/Claude`.

- **The scan reads it back.** `MacOSProcessScan.Scan()` against that live
  process returns `dir=…/Application Support/Claude-Board`, and still returns
  `<none>` (→ Default) for the instance that has neither selector.

- **`open -a <path>` is not the ambiguity.** Two byte-identical `.app` bundles
  sharing one `CFBundleIdentifier`, both registered with `lsregister`, each
  launched correctly by path — including while the other was already running.
  So the earlier bundle-id ambiguity fix is still doing its job and is not
  implicated here.

## Read out of the app, not measured

`/Applications/Claude.app/Contents/Resources/app.asar` still contains

```js
if (process.env.CLAUDE_USER_DATA_DIR) {
  let e = process.env.CLAUDE_USER_DATA_DIR;
  app.setPath(`userData`, e);
  app.setPath(`logs`, path.resolve(e, `Logs`));
}
```

so the variable has not been deleted from the source. **Why it no longer takes
effect was not determined** — the asar carries more than one bundled copy of
the startup path, and which one 1.34493.1 actually executes was not traced.
That matters only for predicting whether Anthropic will restore the behaviour;
it does not change the fix, since `--user-data-dir` is handled in the Electron
framework rather than in that JavaScript.

Also read rather than measured: `dl = "-3p"` in the same bundle, which is where
the `Claude-3p` sidecar directory the profile scanner already skips comes from.

## The change

- `ClaudeDesktopManager.LaunchArguments` appends `--args --user-data-dir=<dir>`
  for created profiles. Default still gets neither selector.
- `ClaudeDesktopUrlRouter.Arguments` does the same, with the URL placed before
  `--args`.
- `MacOSProcessScan.ParseUserDataDir` reads the switch out of argv first and
  falls back to the environment variable, so instances started by an older
  Claude Buddy still map to their own profile.
- `ClaudeDesktopManager.LaunchArguments` also declines to emit either selector
  for an empty directory, which `ClaudeDesktopUrlRouter.Arguments` had always
  guarded and the launcher had not. A bare `--user-data-dir=` is not the same
  thing to Chromium as no switch at all — it is the switch carrying an empty
  value, which resolves back to the default directory. Nothing produces one
  today; every directory here comes from a scanned path. The guard is there
  because two functions building the same command line drifting apart is how
  one of them quietly stops meaning what the other does.
- `ClaudeDesktopBundles.IsStale` now compares bundle versions rather than
  testing them for inequality, so a clone that is *ahead* of the installed
  bundle is left alone. See **Clone staleness** below.

The variable is kept alongside the switch rather than replaced: older Claude
Desktop builds do honour it, both point at the same directory, and it costs
nothing to send.

## Clone staleness

Found while measuring the above, and fixed here because it is two lines of
rule and shares the cause.

Each profile's Dock icon comes from a *clone* of Claude.app, and `IsStale`
decided whether to rebuild one by comparing its `CFBundleVersion` to the
installed bundle's for **inequality**. That was written for the only drift
anyone expected — Squirrel updates `/Applications/Claude.app` and the clones
fall behind — and it is wrong in the other direction, which turns out to be the
direction that actually happens. Squirrel updates whichever bundle is *running*,
and the bundle that is running is usually a clone:

```
/Applications/Claude.app         1.34493.1
bundles/Claude/Claude.app        1.34493.1
bundles/Claude-Board/Claude.app  1.37937.0   <- ahead of the installed bundle
```

Under the old rule the next Board launch would have rebuilt that clone from
`/Applications` — downgrading that profile from 1.37937.0 to 1.34493.1, and
then opening userData written by a newer Chromium with an older one. Re-cloning
costs 0.3s and undoes; that does not.

So staleness is now "the clone is behind", not "the clone is different". The
comparison is numeric per dotted component, because a string comparison gets
`1.9.0` vs `1.10.0` backwards. Missing trailing components count as zero, so a
dropped `.0` is not an upgrade. A version either side cannot parse falls back to
the old inequality rule, which errs towards rebuilding — the recoverable
direction — and a version that could not be read at all is still stale.

The decision was pulled out of `IsStale` into a pure `IsStaleVersion` so it
could be covered rather than excluded. Only the half that shells out to `plutil`
for the two `CFBundleVersion`s stays `[ExcludeFromCodeCoverage]`.

## Not fixed here

Instances already running on the wrong directory when this lands stay there —
they have to be quit and relaunched. There is no migration for anything they
wrote into `…/Application Support/Claude` while pointed at the wrong profile.

**Claude Desktop's own auto-updater relaunches without either selector, and
this change cannot stop it.** Filed as **CB-7**. Squirrel relaunches the bundle
it just updated with `launchAfterInstallation`, and the process ShipIt starts
carries neither `--user-data-dir` nor `CLAUDE_USER_DATA_DIR` — so an instance
Claude Buddy launched correctly onto a profile silently moves to Default the
first time it updates itself. Read off this machine while QA-ing the fix:

```
pid 451    bundles/Claude/Claude.app        no selectors  -> 66 files under Application Support/Claude
pid 26126  bundles/Claude-Board/Claude.app  no selectors  -> 39 files under Application Support/Claude
```

`ps -Eww -p 26126` shows neither selector. That is two Chromium processes on one
userData directory — the concurrent leveldb/SQLite access this whole feature
exists to prevent — arriving by a route neither the launcher nor the URL router
can reach. Note the second row: a process running from the *Board* clone while
writing into *Default*, which is the signature of the relaunch rather than of
anything a user did.

**This is not a regression from this change.** The pre-fix code failed here
identically: ShipIt propagates the environment variable no better than it
propagates argv, so the older launcher's `--env` was dropped by the same step.
It is also not fixable by the same means — Squirrel is upstream and its relaunch
is not argument-injectable, and rewriting an updater inside a signed bundle
would break the identical-CDHash property the tinted clones depend on (see the
header comment on `ClaudeDesktopBundles.cs`).

A real fix would detect the orphan rather than prevent it: the scan already
knows both halves, and a process running from a per-profile clone under
`ClaudeDesktopBundles.Root` while carrying no userData selector is definitionally
sitting on Default — a combination no launch this app performs can produce, so
it is evidence rather than a heuristic. The profile row would then offer to quit
and relaunch it with both selectors, reusing the Quit and Launch paths that
already exist. Nothing here implements that.

## Windows

Unaffected and unchanged. `LaunchWindows` has passed `--user-data-dir` since
the port, and `WindowsProcessScan` has always parsed it back off the command
line. Nothing on that side ever depended on the variable. **Not re-verified on
a Windows machine as part of this change** — the claim rests on reading those
two files, which this change does not touch.
