# Windows Terminal tab selection — findings

## Item 1: what can be seen (read-only)

Environment: one real WT process (pid stayed constant at `61364` throughout —
confirms the "one process per launch context" fact) hosting, at baseline,
two pre-existing windows belonging to Owner:

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
