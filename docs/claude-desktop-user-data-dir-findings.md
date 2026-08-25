# Claude Desktop stopped honouring `CLAUDE_USER_DATA_DIR`

Reported as: *"the Claude Desktop app is NOT opening multiple profiles — it's
opening the same profile twice."* Measured on macOS 27.0, Claude Desktop
**1.34493.1**, Claude Buddy 0.4.2-beta.

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

The variable is kept alongside the switch rather than replaced: older Claude
Desktop builds do honour it, both point at the same directory, and it costs
nothing to send.

## Not fixed here

Instances already running on the wrong directory when this lands stay there —
they have to be quit and relaunched. There is no migration for anything they
wrote into `…/Application Support/Claude` while pointed at the wrong profile.

## Windows

Unaffected and unchanged. `LaunchWindows` has passed `--user-data-dir` since
the port, and `WindowsProcessScan` has always parsed it back off the command
line. Nothing on that side ever depended on the variable. **Not re-verified on
a Windows machine as part of this change** — the claim rests on reading those
two files, which this change does not touch.
