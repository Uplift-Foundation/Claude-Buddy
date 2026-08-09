# Windows Terminal tab selection — findings

## Item 1: what can be seen (read-only)

Environment: one real WT process (pid stayed constant at `61364` throughout —
confirms the "one process per launch context" fact) hosting, at baseline,
two pre-existing windows belonging to Warren:

- hwnd `2166490`, one tab, `Name == "claude"` (untitled real session, sitting idle).
- hwnd `59772442`, two tabs, `Name == "✳ Test asking question"` and
  `Name == "✳ test"`.

Cross-checked the two titled tabs against the hook's status files in
`%TEMP%\claude_buddy\*.txt`: `"title":"Test asking question"` and
`"title":"test"` respectively — exact match once the leading glyph and space
are stripped. **The predecessor's "it's always literally `claude`" conclusion
was an artifact of testing only an untitled session.** A titled interactive
session's WT tab name is `"✳ " + status.Title` (U+2733, EIGHT SPOKED
ASTERISK), not a bare app name.

Then started a real interactive `claude` session myself (throwaway dir
`C:\Users\warre\wt-tab-test-throwaway`, launched via
`wt -w last nt -d <dir> cmd /k claude`) and watched its tab name over time,
untouched:

1. Immediately after launch: `"claude"` — same as the pre-existing untitled
   window. Matches the doc's known case.
2. ~2 minutes later, still untouched, no prompt ever submitted: `"✳ Claude Code"`.
   Its own hook status file existed by then (`SessionStart` apparently isn't
   what wrote it — this install only hooks `UserPromptSubmit`/`Stop`/`SessionEnd`,
   so the file's presence itself was a surprise) but its `title` field was
   **empty** (`"title":""`).

So there's a third state the doc didn't anticipate: WT can show a
glyph-prefixed title (`"✳ Claude Code"`) that is **not** the per-chat title —
it's Claude Code's own generic placeholder, set directly via console title
escape sequences, independent of whatever the hook has recorded. Matching
logic must treat "starts with ✳ but text isn't a real chat title yet" the
same as untitled, not attempt to match it against `status.Title` (which is
empty at that point anyway — an empty-string match would be as wrong as a
random one). Practically: match only when `status.Title` is non-empty AND
the tab name equals `"✳ " + status.Title` exactly. Anything else (bare
`"claude"`, `"✳ Claude Code"`, or any other text) is a non-match, not an
error — just falls through to the existing single-`claude`-tab handling or
window-level fallback.

Non-Claude comparison tab: a plain `powershell` shell in a new tab showed
`Name == "powershell"` (i.e. the running command name) — confirms Claude
sessions are distinguishable from ordinary shells by tab name pattern, no
special-casing needed to exclude them (they'll simply never match the `"✳ "`
prefix or literal `"claude"`).

`SelectionItemPattern`: every `TabItem` supports it (`GetCurrentPattern`
never threw). Tested `.Select()` twice:

- Same-window case: window already foreground, background tab in it selected
  → active tab switched (`IsSelected` flipped, title bar updated). Unsurprising.
- **Cross-window case (the one that matters for orb click):** with a
  *different* WT window foregrounded, called `.Select()` on the single tab of
  a background window (hwnd `2166490`). Result: that window became the
  foreground window (`GetForegroundWindow()` returned its hwnd) **and** its
  tab was active — in one call, no separate "bring window to front" step
  needed. Confirmed both via `GetForegroundWindow`/`GetWindowThreadProcessId`
  and a cropped screenshot (deleted after reading) showing the right window
  frontmost with the right tab highlighted.

Enumeration cost: one full round trip (spawn `powershell.exe`, walk
`RootElement` → 3 windows → all `TabItem` descendants, 6 tabs total) took
~400ms including process-launch overhead. In-process (no new `powershell.exe`)
will be well under that. Comfortably comes in under the "second or two"
budget in item 2 for this window/tab count; a machine with many more WT
windows/tabs open would need to be watched, hence still bounding it in the
implementation.

**Bottom line: tabs can be enumerated with live, distinguishing `Name`s, and
`.Select()` genuinely switches both the tab and the window. Item 2 is safe to
attempt**, matching on `"✳ " + status.Title` when `status.Title` is non-empty,
and falling back to the existing window-activation path otherwise (including
the untitled/placeholder-title cases above).

**Correction to the "~2 minutes" timing above**, found while implementing
item 2: that delay was an artifact of a broken test, not real behavior. The
`cmd /k claude` launch was run through this agent's Bash tool (Git Bash), which
silently mangled `/k` into a drive path (`cmd K:/ claude`) — MSYS's automatic
`/x` → `X:/` conversion for anything that looks like a Unix absolute path
starting a command-line token. `claude.exe` never actually started; the tab
just sat on `cmd`'s own default title indefinitely. Once launched correctly
(via the PowerShell tool, no path mangling), a fresh session reads bare
`"claude"` for **well under a second**, then flips to `"✳ Claude Code"` and
stays there indefinitely with no further prompt. This matters a lot for item
2's matching strategy — see below.

## Item 2: implementation

Wired into `TerminalFocuser.FocusWindows` (Windows-only, per the ground
rules): before the existing hwnd-resolution/`BringToFront` fallback, try
`TrySelectWindowsTerminalTab(status)`. Only on a confirmed unambiguous select
does it return and skip the fallback; every other outcome (title empty, zero
matches, ambiguous matches, PowerShell missing, timeout, any exception) falls
straight through to the pre-existing, completely unmodified code path. Also
updated the stale file-header comment claiming tab selection was impossible.

**Deviated from the doc's suggested "bare `claude` with exactly one match is
still worth handling" fallback — deliberately left it out.** The doc's
reasoning assumed a session with no title sits at a stable `"claude"` tab
name indefinitely (that's what the original, since-corrected finding
implied). Timing it for real (see correction above) shows the opposite: by
the time a human reacts to an orb and clicks it, that session's own tab has
already moved on to `"✳ Claude Code"` in under a second. A bare `"claude"`
tab present at click time therefore almost certainly belongs to some *other*
session that happens to be mid-startup at that exact instant — matching it
would confidently activate the wrong window, which is exactly the outcome
item 2's "never worse than today" rule forbids. So: `status.Title` empty →
don't attempt tab selection at all, fall straight to window activation. This
is a judgment call, recorded here because it contradicts what the task doc
itself suggested trying.

Injection safety: the chat title is arbitrary text a user can influence (it's
LLM-generated from conversation content, so not fully trusted). The pid and
target tab name are passed as genuine process arguments to a `-File` script,
never spliced into the PowerShell script's source text, so there's no way
for a title containing quotes, `$`, backticks, etc. to be interpreted as
script code.

**A real bug caught only by end-to-end testing, not code review:** the first
version invoked `powershell.exe -NoProfile -NonInteractive -Command
<script-text> <pid> <target>`, expecting `<pid>`/`<target>` to arrive as
`$args` inside the script — that's how `-File <script> <pid> <target>` works,
and it's what every manual test in item 1 used. It is **not** how `-Command`
works: `powershell.exe` treats everything after `-Command` as one command
line and joins all remaining argv tokens onto the end of the script text,
then reparses the whole thing as PowerShell source. The title ends up
appended as bare script tokens, not delivered as `$args`, and it reliably
failed to parse (`Unexpected token '?' in expression or statement` — the
glyph itself, mangled by the re-parse). `dotnet build` and a plain read of
the code both looked correct; only running it against a real WT process
caught it. Fixed by writing `SelectTabScript` to a temp `.ps1` file per
invocation (unique GUID name, deleted in a `finally`) and invoking with
`-File`, which is the only form where trailing arguments actually become
`$args`.

Verification method: rather than run a second `ClaudeBuddy.exe` (this app has
no per-instance isolation the way Claude Desktop does — one settings file, one
`%TEMP%\claude_buddy` watcher — so a second copy would just create duplicate
orbs for Warren's real sessions, a needless disruption for a focus-only
feature with no destructive side effects to justify it), built a throwaway
console harness containing an unmodified copy of `TerminalFocuser.cs` (plus
its `WindowsForegroundWindow.cs` dependency and a minimal `SessionStatus`
stand-in) and called the real, public `TerminalFocuser.Focus(status)` —
the exact method `OrbWindow`'s click handler calls — directly. This is the
same production code the orb's `OnPointerReleased` invokes, just triggered
without a simulated click, consistent with "prefer resolving by pid/hwnd
over simulating clicks" from the ground rules. Harness deleted after use.

## Item 3: verify

All four scenarios run via the harness described above, against real WT
windows (Warren's two pre-existing ones, plus disposable windows/tabs of my
own for the cases that needed a second identically-titled tab — cleaned up
after each check, confirmed by re-enumerating until the tab/window list
matched the original baseline exactly):

- **Background tab in a background window, unambiguous title.** Foregrounded
  a different window first (baseline), then called `Focus()` with
  `TermProgram="WindowsTerminal"`, `TermPid=61364`, `Title="Test asking
  question"` (Warren's real second window/tab, untouched otherwise).
  `GetForegroundWindow()` came back as hwnd `59772442` — the right window,
  raised by the tab select alone. PASS.
- **Ambiguous case.** Built two tabs with an identical synthetic title
  (`$Host.UI.RawUI.WindowTitle`, a plain non-Claude way to get two tabs with
  the same glyph-prefixed name without needing two real hours-long
  conversations) in a throwaway window, foregrounded something else, then
  called `Focus()` targeting that title. Script reported `NOMATCH:2`,
  `TrySelectWindowsTerminalTab` returned `false`, and control fell into the
  **same unmodified `MainWindowHandle`-based fallback** the empty-title
  baseline call had just used — same window came forward both times. Not a
  new kind of wrongness: bit-for-bit the pre-existing behavior, exercised via
  a path that used to be unreachable by title but is now reached by ambiguity
  too. PASS (falls back correctly, doesn't guess).
- **Single-tab / untitled regression check.** `Title=""` (Warren's real
  single-tab window). Guard clause returns `false` immediately —
  `TrySelectWindowsTerminalTab` body never runs, zero PowerShell spawned —
  and the existing `MainWindowHandle`/`BringToFront` path runs exactly as it
  did before this change. PASS, and by construction can't regress since the
  code is untouched on this path.
- **Different WT window than the currently-active one.** Covered by the
  first bullet — the target window (`59772442`) was not the foreground window
  when `Focus()` was called.
- **Non-WT hosts.** `status.TermProgram == "WindowsTerminal"` gates the new
  code entirely; called `Focus()` with `TermProgram="vscode"` and it returned
  having done nothing beyond what the pre-existing VS Code fallback already
  does (by inspection: the guard short-circuits before `TrySelectWindowsTerminalTab`
  is ever called, so this is a code guarantee, not something that needed a
  live VS Code/`conhost` re-check the way a shared code path would have).

macOS: untouched. The new code lives entirely inside the existing
`OperatingSystem.IsWindows()` branch; the one shared helper touched
(`TryRun`) keeps its original 3-argument overload with the same default
3000ms timeout, so every macOS call site is unaffected.

Cleanup: all throwaway WT windows/tabs, the console test harness, the
interactive `claude` sessions started for testing, and their processes were
removed; final UIA enumeration matched the original two-window baseline
exactly before stopping.

## Item 4: corrections from real use

Everything above was tested by *focusing* a terminal. Voice dictation later
started typing into one, which exercises the same code with a much stricter
success condition — the keystrokes have to arrive in the shell, not merely in
the right window — and three conclusions above turned out to be narrower than
they read. All four items here were measured on a real machine, the same way
the originals were; what changed is the scope of the test, not the method.

**`.Select()` raises the window only when the selection actually changes.**
Item 1's cross-window result is real and reproduces. What it does not cover is
selecting the tab that is *already* selected: that changes no selection, so it
is a no-op and raises nothing. Item 2 read the finding as "a confirmed select
means the window is up" and returned early on that basis, which is correct for
every case item 3 exercised (all of them targeted a background tab, a
background window, or both) and wrong for the one nobody thought to try —
clicking the orb of the session you are already looking at. Clicking an orb or
its mic makes Claude Buddy the foreground app first, so nothing brought the
terminal back. `FocusWindows` now raises the tab's window explicitly after a
successful select.

**Raise the tab's own window, not `MainWindowHandle`.** Fallout from the
same change. One process owning several WT windows is the premise this whole
document starts from, and `Process.MainWindowHandle` picks an arbitrary one of
them — fine as a last-resort fallback, not as the thing you raise when you
have just identified an exact tab. The select script now reports its window's
`NativeWindowHandle` (`SELECTED:<hwnd>`) and the caller raises that.

**`.Select()` moves keyboard focus to the tab header.** Measured with
`AutomationElement.FocusedElement` immediately either side of the call:
`TermControl` (class name, the terminal pane) before, `TabItem` /
`ListViewItem` after. This is invisible when the selection changes, because
switching tabs then moves focus into the newly shown pane — which is why item
3 never saw it, and why focus never came up in this document at all. When the
tab is already current there is no such follow-up, so focus is left sitting on
the tab header, and typing into a focused WT tab header starts an inline
rename: the tab title highlights and nothing reaches the shell. Fixed by not
selecting an already-selected tab, then calling `SetFocus()` on the on-screen
`TermControl` regardless. The `SetFocus()` is deliberate belt-and-braces —
without it, a window already left focused on its header by an earlier run has
nothing to move focus back, and confirmed recovering from exactly that state.
There is only ever one on-screen `TermControl` (WT exposes just the active
tab's), so "the one that isn't offscreen" is unambiguous.

**`"✳ " + status.Title` is the wrong thing to match.** Item 1 established the
prefix correctly for the sessions it looked at — all idle. Claude Code
replaces that glyph with an animated braille spinner (`⠂`, `⠐`, …) while a
session is actually working, so a generating session's tab reads
`"⠐ Check Claude Code status"` and the exact match never fires. Observed live
with two sessions in one window: the idle one's orb worked, the generating
one's did not, which presents as intermittency rather than as a rule. Note
this failure was *not* harmless in the way item 2's "never worse than today"
rule intended — falling through to window activation raises the window with
whatever other tab was in front, so the orb appeared to work while showing the
wrong session. Matching is now on the tab name's ending, so any status glyph
passes; adding spinner frames to a list of accepted prefixes was rejected as
tracking someone else's animation detail.

**Item 2's `-NonInteractive: this must never pop a console of its own` was
wrong.** `-NonInteractive` has nothing to do with it. This app is a `WinExe`,
so a console child gets a console allocated and shown unless
`CREATE_NO_WINDOW` says otherwise, and redirecting stdout/stderr does not
suppress it. Measured from a `WinExe` parent with the same
`ProcessStartInfo`: without `CreateNoWindow` the child owns a visible
`PseudoConsoleWindow`, with it none, and stdout and exit code are identical.
It flashed on screen for the ~400ms of every orb click, and — the part that
mattered — held the foreground while it lived, so the terminal this code
exists to raise lost the race. `TryRun` sets `CreateNoWindow = true` now;
this is the one change here that touches the shared helper item 3 was careful
about, and it is inert on macOS, where the flag is ignored.
