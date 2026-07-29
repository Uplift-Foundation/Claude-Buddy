# Windows: select the Windows Terminal tab on orb click

For a Claude Code instance running **unattended on the Windows machine**, in the
interactive session started by a `/IT` scheduled task. Read the "Your situation",
"Ground rules", "You get exactly one turn", and the click-safety rules in
`docs/windows-recheck.md` and `docs/windows-showhidden.md` first — all apply
unchanged. In particular: never simulate clicks on window chrome, and prefer
resolving windows by pid/hwnd.

Clicking an orb activates the Windows Terminal *window* but not the *tab*, so a
session in a background tab leaves you looking at the wrong shell. This has been
recorded twice as impossible; that was wrong in an important way, and this task is
to find out how far it can actually go.

## What's already known

- WT puts every window of a launch context in **one process**, so
  `Process.MainWindowHandle` can't identify a window, let alone a tab. Real.
- What was concluded from that — "WT doesn't expose its tabs to other processes" —
  is **false**. A previous run verified that UI Automation enumerates WT's tab
  strip as real `TabItem` elements with live `Name` properties **for every tab,
  not just the active one**. `TabItem` normally supports the SelectionItem
  pattern, which means selecting one is likely possible, not merely reading it.
- The blocker was matching: that run saw the tab title as the literal string
  `claude`, with nothing session-specific to match on. But it observed that on a
  headless `claude -p` process, which may set a plainer title than an interactive
  session does. **Check this properly before believing it.**

## 1. Establish what can be seen (read-only, do this first)

With at least two Windows Terminal tabs open, at least one running a real
*interactive* `claude` session (start one yourself in a throwaway directory —
interactive, not `-p`, since that's the case that matters):

- Enumerate every WT window and its `TabItem` elements via
  `System.Windows.Automation`. Record each tab's `Name` verbatim.
- Say plainly whether an interactive Claude Code session's tab name contains
  anything session-specific — the chat name, the cwd, a spinner glyph — or
  whether it really is just `claude`.
- Check whether the `TabItem` supports `SelectionItemPattern`, and whether
  `.Select()` on a background tab actually switches to it. Test that directly.
- Record what the tab name looks like for a non-Claude tab (a plain shell), so
  we know what we'd be distinguishing against.

Push this before writing any code. Even if the answer is "still can't match", the
enumeration facts are worth having on record.

## 2. Implement it, if and only if it can be done safely

If tabs can be selected and Claude sessions' tabs can be identified, wire it into
the Windows click-to-focus path (`TerminalFocuser`, Windows side).

Non-negotiable rule: **never worse than today.** Today's behaviour activates a
window belonging to the right process, which is imperfect but never actively
wrong. So:

- Only select a tab when the match is **unambiguous**. Exactly one candidate
  across all WT windows of that process → select it. Zero, or two or more →
  change nothing and fall back to today's window activation.
- A wrong tab is worse than no tab switch, because it silently shows the user
  someone else's session. Prefer doing nothing.
- Keep it cheap: this runs on a click, not a timer, but UIA can be slow. If
  enumeration takes more than a second or two, bound it and fall back.
- macOS must be untouched.

What to match on depends on what item 1 found. If the tab name carries the chat
title, match it against the session's recorded `title` (the hook already records
it — see `SessionStatus`). If it's only ever `claude`, then a single-Claude-tab
case is still worth handling: exactly one tab named `claude` across that process
is unambiguous, and two are not.

Do **not** add a mechanism that renames the user's tabs to make matching work.
That was considered and rejected: the console title is shared per-conpty and any
prompt that rewrites it would erase the marker, so it would be unreliable as well
as intrusive.

## 3. Verify

- Two WT tabs, one with an interactive Claude session in a *background* tab.
  Click its orb. The right tab should come to the front, in the right window.
- The ambiguous case: two Claude sessions in two tabs. Click one orb. Confirm it
  falls back to plain window activation rather than picking a tab at random —
  and say which window came forward.
- The single-tab case still works (no regression).
- A session in a *different* WT window than the active one still gets its window
  raised.
- Non-WT hosts unaffected: re-check plain `conhost` and VS Code briefly.

## Reporting

Branch `windows-wt-tabs` off `main`. Append to `docs/windows-wt-tabs-findings.md`,
commit and push **after item 1** and again after item 2/3. ~2 cropped screenshots
per item, deleted after reading. No nested `claude -p` for the app's own testing —
but note this task legitimately needs a real interactive `claude` session as the
subject; start it in a separate WT tab and leave it alone otherwise. Clean up what
you create.
