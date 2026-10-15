# Claude Desktop-hosted Claude Code sessions (CB-265)

What the hook sees when Claude Desktop's Code tab spawns Claude Code, and what is only assumed. Confirmed means measured on a real Mac on 2026-10-14; assumed means reasoned from that or from the code and not run.

## Confirmed on a Mac

Process chain: `claude` (the session) was a child of `<Claude.app>/Contents/Helpers/disclaimer --pgroup -- ...`, which was a child of the Electron main process `<...>/Claude.app/Contents/MacOS/Claude`, which was a child of launchd. The claude command line carried `--output-format stream-json --input-format stream-json --permission-prompt-tool=stdio --setting-sources=user,project,local`. The Claude.app in that chain can be a cloned bundle under `~/Library/Application Support/Orbweaver/bundles/<profile>/Claude.app`, so its path can contain spaces and is never necessarily `/Applications/Claude.app`.

Session environment: `CLAUDE_CODE_ENTRYPOINT=claude-desktop`, `CLAUDE_CODE_HOST_SESSION_ID=local_<uuid>`, no `TERM_PROGRAM`, no tty, no tmux.

Hook behaviour before this change: it fires, and writes a status file with empty `tty`, `term_program` and `tmux_pane`, a real `session_pid` and an existing `transcript_path`. `SessionManager.JudgeReachability`'s NoTerminal rule then dropped the file, so no orb appeared.

Hook behaviour after this change, confirmed by running the real `OrbweaverHook.sh` as a subprocess (`DesktopStatusRoundTripTests`, `HookScriptShTests`): with `CLAUDE_CODE_ENTRYPOINT=claude-desktop` the file carries `"entrypoint":"claude-desktop"` and deserializes to a `SessionStatus` whose `IsDesktopHosted` is true. `cli`, unset, and a Codex or Grok file written under the same environment are not Desktop-hosted. Quotes, backslashes, newlines and spaces are stripped from the value. In every case exit code 0, empty stdout, empty stderr.

The PowerShell hook produces the same field and values; the cases run under `pwsh` 7 on the machine that wrote this.

## Desktop's per-chat record and the Remote Control bridge (CB-266), confirmed on a Mac

Measured 2026-10-14 on one Mac. Desktop keeps one record per Code-tab chat at `<profile dir>/claude-code-sessions/<uuid>/<uuid>/local_<id>.json`, where the profile dir is `~/Library/Application Support/Claude` (default) or `~/Library/Application Support/Claude-<Name>` for an Orbweaver clone (the clone measured here was `Claude-Board`). The keys that matter: `sessionId` (the `local_` id, equal to the hook's `host_session_id`), `cliSessionId` (the Claude Code session id, equal to the status file's id), `bridgeSessionIds` (an array of `session_` ids, one for the live chat measured), `isArchived`, `remoteControlAutoEligible` and `steeredByRemoteClient`; it also carries `cwd`, `title` and `lastActivityAt`, which nothing reads. The hook records `CLAUDE_CODE_HOST_SESSION_ID` as `host_session_id`, so the app opens `local_<id>.json` directly; checked by running the real `OrbweaverHook.sh` (`HookScriptShTests`, `DesktopStatusRoundTripTests`) and, for the lookup, against scratch profile roots laid out in that shape (`DesktopSessionRecordFilesTests`). The fixtures use invented ids with this key set; no real id is committed.

What the app does with it, all covered by tests rather than all observed live: profile directories come from `ClaudeDesktopManager.Discover()` (so `Claude-3p`, Desktop's sidecar directory, is skipped), never from the host process's `--user-data-dir`, because Desktop's updater can relaunch a clone onto the default profile (CB-7) while the record stays where the chat was made. An archived record, or one whose `cliSessionId` is not this session's, yields no bridge. A record is re-read only when its mtime moves; a chat whose record is not found is looked for again at most every five seconds. A status file without `host_session_id` is matched by `cliSessionId` across all records, and two live matches are refused rather than guessed between. A bridged chat is one orb: the cloud row for the same session id is not given a second one.

## Assumed, not confirmed

Windows Desktop: that Claude Desktop sets `CLAUDE_CODE_ENTRYPOINT=claude-desktop` for the sessions it spawns on Windows as it does on macOS. Nobody has run a Windows Desktop session against this hook. The Windows hook is exercised only by test with the variable injected, and Windows PowerShell 5.1 itself was not run (the 5.1 cases skip off Windows).

Other entrypoints: `sdk-cli` and any value other than `claude-desktop` are treated as terminal-style and unchanged. Whether other Desktop-adjacent hosts use a different value is not known.

Existing installs: a hook script installed before this change keeps writing no `entrypoint`, so Desktop sessions on such an install stay invisible until the script is replaced. That is handled app-side (a separate task in CB-265), not by this hook.

The host app pid is deliberately not recorded by the hook; the app derives it at click time from the process table. Whether that walk finds the Electron main process on a Windows install is likewise assumed.

Windows record location (CB-266): that Claude Desktop keeps the same `claude-code-sessions/<uuid>/<uuid>/local_<id>.json` records under `%APPDATA%\Claude` (and `Claude-<Name>`), which is where `ClaudeDesktopManager.ProfileRoot` points, and that Windows Desktop sets `CLAUDE_CODE_HOST_SESSION_ID` as macOS does. Nobody has looked at a Windows install. If the folder is somewhere else the lookup finds no record and the chat simply has no bridge, which is the safe failure: the composer stays read-only. The hook (`OrbweaverHook.ps1`) change is exercised by test with the variable injected, as for `entrypoint`.

Order of `bridgeSessionIds` (CB-266): the live chat measured had exactly one id. The app takes the last well-formed one on the assumption that reconnecting appends; that order is not observed.

Existing installs (CB-266): a hook script installed before this change writes no `host_session_id`; the app falls back to matching `cliSessionId`, so such a chat still finds its bridge, at the cost of reading every record.
