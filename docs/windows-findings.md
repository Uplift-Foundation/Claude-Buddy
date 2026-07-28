# Windows verification findings

Run against `windows-verification` branch, built locally with
`dotnet publish ClaudeBuddy.csproj -c Release -r win-x64 -o publish` (.NET 10
SDK, targeting net8.0, RollForward=LatestMajor). Executed unattended on the
Windows box per `docs/windows-verification.md`.

## 1. It starts and stays up — PASS

Launched `publish\ClaudeBuddy.exe` from a background shell. Confirmed via
`tasklist` that the process (PID 29592) was still running ~15s later, under
session `RDP-Tcp#2` (Owner's interactive desktop, not a headless session).
No console window, no output in redirected stdout/stderr — expected for a
`WinExe` notification-area app. No crash, no exception.

## 2. Notification-area icon — PASS

The icon landed behind the `^` chevron (Windows 11's default for new icons),
as the brief warned. Expanded the overflow flyout and zoomed a screenshot crop
4x: it renders as a blue-grey coloured ring, matching the "coloured ring"
description in the brief. Confirmed it's ours (not another app's icon) via the
hover tooltip, which read `Claude Buddy — no sessions`.

## 3. Right-click menu — PASS, with a Windows shell quirk worth flagging

Direct click on the icon once it was promoted out of the overflow (Windows
sometimes pins a recently-used hidden icon onto the visible tray strip)
produced the full, correct menu:

```
No Claude Code sessions        (disabled — 0 sessions)
Show orbs                      (checked)
Reset all sessions to idle     (disabled — 0 sessions)
---
Settings…
Quit Claude Buddy
```

No Claude Desktop section — correct, that feature is macOS-only
(`ClaudeDesktopSection.Append` no-ops on this platform).

**Quirk, not a defect:** while the icon was still hidden behind the `^`
chevron, right-clicking it from inside that overflow flyout produced a menu
missing the `Settings…` row entirely, in two independent trials — "No Claude
Code sessions" / "Show orbs" / "Reset all sessions to idle" / --- / "Quit
Claude Buddy", with the gap where Settings should be simply absent, not
blank. I read `TrayController.cs:153-217` end to end: `Settings…` is built
identically to the three items around it (a plain `NativeMenuItem` + `Click`
lambda, same pattern as `Quit`), added unconditionally, no platform guard. The
same code, same running process, produced the complete 5-item menu once the
icon sat directly on the tray instead of inside the hidden-icons flyout — so
this isn't the app dropping the item, it's the Windows 11 overflow flyout's
own popup-menu host clipping a row. Judgement call: not fixing this, since
there's nothing in `TrayController.cs` to fix — the item is always added. If
it recurs for Owner, the workaround is dragging the icon out of the overflow
(Settings → Personalization → Taskbar → Other system tray icons).
