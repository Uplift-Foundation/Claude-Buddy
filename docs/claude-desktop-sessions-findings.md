# Claude Desktop-hosted Claude Code sessions (CB-265)

What the hook sees when Claude Desktop's Code tab spawns Claude Code, and what is only assumed. Confirmed means measured on a real Mac on 2026-10-14; assumed means reasoned from that or from the code and not run.

## Confirmed on a Mac

Process chain: `claude` (the session) was a child of `<Claude.app>/Contents/Helpers/disclaimer --pgroup -- ...`, which was a child of the Electron main process `<...>/Claude.app/Contents/MacOS/Claude`, which was a child of launchd. The claude command line carried `--output-format stream-json --input-format stream-json --permission-prompt-tool=stdio --setting-sources=user,project,local`. The Claude.app in that chain can be a cloned bundle under `~/Library/Application Support/Orbweaver/bundles/<profile>/Claude.app`, so its path can contain spaces and is never necessarily `/Applications/Claude.app`.

Session environment: `CLAUDE_CODE_ENTRYPOINT=claude-desktop`, `CLAUDE_CODE_HOST_SESSION_ID=local_<uuid>`, no `TERM_PROGRAM`, no tty, no tmux.

Hook behaviour before this change: it fires, and writes a status file with empty `tty`, `term_program` and `tmux_pane`, a real `session_pid` and an existing `transcript_path`. `SessionManager.JudgeReachability`'s NoTerminal rule then dropped the file, so no orb appeared.

Hook behaviour after this change, confirmed by running the real `OrbweaverHook.sh` as a subprocess (`DesktopStatusRoundTripTests`, `HookScriptShTests`): with `CLAUDE_CODE_ENTRYPOINT=claude-desktop` the file carries `"entrypoint":"claude-desktop"` and deserializes to a `SessionStatus` whose `IsDesktopHosted` is true. `cli`, unset, and a Codex or Grok file written under the same environment are not Desktop-hosted. Quotes, backslashes, newlines and spaces are stripped from the value. In every case exit code 0, empty stdout, empty stderr.

The PowerShell hook produces the same field and values; the cases run under `pwsh` 7 on the machine that wrote this.

## Assumed, not confirmed

Windows Desktop: that Claude Desktop sets `CLAUDE_CODE_ENTRYPOINT=claude-desktop` for the sessions it spawns on Windows as it does on macOS. Nobody has run a Windows Desktop session against this hook. The Windows hook is exercised only by test with the variable injected, and Windows PowerShell 5.1 itself was not run (the 5.1 cases skip off Windows).

Other entrypoints: `sdk-cli` and any value other than `claude-desktop` are treated as terminal-style and unchanged. Whether other Desktop-adjacent hosts use a different value is not known.

Existing installs: a hook script installed before this change keeps writing no `entrypoint`, so Desktop sessions on such an install stay invisible until the script is replaced. That is handled app-side (a separate task in CB-265), not by this hook.

The host app pid is deliberately not recorded by the hook; the app derives it at click time from the process table. Whether that walk finds the Electron main process on a Windows install is likewise assumed.
