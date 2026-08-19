# Codex CLI: what was measured

Everything below was read off **codex-cli 0.148.0** as installed on this
machine (`~/.codex/packages/standalone/releases/0.148.0-aarch64-apple-darwin`)
and off two real rollouts written by it on 19 Aug 2026. Anything not confirmed
that way is in **Still unknown** at the bottom, and stays there until someone
runs it.

The reason for the split is the same one `docs/claude-code-chat-findings.md`
gives: this reads a format nobody here controls, it fails quietly when it fails,
and a plausible guess written down as a fact is worse than an admitted gap.

## The transcript

`~/.codex/sessions/<yyyy>/<mm>/<dd>/rollout-<iso>-<session-id>.jsonl`. The
session id is in the filename, which is what makes the file findable from a
hook payload that may not name it.

Every row:

```json
{"timestamp":"2026-08-19T16:57:08.663Z","ordinal":9,"type":"event_msg","payload":{…}}
```

### The same conversation is written twice

Two values of `type` carry content, and choosing between them is the single
biggest decision in `CodexTranscript`:

| `type` | what it is |
| --- | --- |
| `response_item` | the model-facing wire log — raw request items, the developer preamble, the environment context, and the full stdout of every command |
| `event_msg` / `payload.type: "item_completed"` | the TUI's own record of what it drew |

Only the second describes the conversation as a person experienced it. Measured
across the two rollouts: **253 of 504 rows were `response_item`, and not one of
them contained the string `item_completed`** — so a substring pre-filter
separates them exactly, which is what `CodexTranscript.IsInteresting` relies on.

Other row types seen and ignored: `session_meta` (row 0, carries the whole
system prompt), `world_state`, `turn_context`, and `event_msg` payloads
`task_started`, `task_complete`, `token_count`, `thread_settings_applied`.

### Item types

`payload.item.type`, counted across both rollouts:

| item | n | median bytes | max bytes |
| --- | --- | --- | --- |
| `CommandExecution` | 71 | 4,567 | **1,046,104** |
| `Reasoning` | 109 | 395 | 395 |
| `AgentMessage` | 26 | 741 | 1,613 |
| `FileChange` | 15 | 3,768 | 10,339 |
| `Extension` | 15 | 976 | 7,320 |
| `UserMessage` | 4 | 510 | 557 |

`McpToolCall` is known to exist (it is in the binary's item enum) but did not
occur in either rollout, so it is **not** mapped — an MCP call currently shows
as nothing in the panel.

### The one that would cost every reply

**`UserMessage` content blocks are typed `"text"`. `AgentMessage` content blocks
are typed `"Text"`.** Confirmed on real rows, both directions. A parser that
assumes one casing covers both silently drops half the conversation. There is a
test named for this in `tests/TranscriptTests`.

### Why oversized rows are not parsed

The largest `item_completed` row measured is **1,046,104 bytes** — a
`CommandExecution`. In that row:

| field | byte offset |
| --- | --- |
| `command` | 311 |
| `parsed_cmd` | 445 |
| `aggregated_output` | 503,008 |

Everything the panel shows sits in the first kilobyte and the megabyte is
output. `CodexTranscript` therefore parses rows up to 2 MB (above the largest
measured, so every ordinary row takes the exact path) and reads anything larger
from a bounded scan of its head. A `cat` of something big has no upper bound at
all, which is the case that makes this a rule rather than an optimisation.

### Other measured shapes

- **`ordinal`** is dense and unique within a rollout (0..503 in the file
  checked) and is what a reader should dedupe on. Rows carry no `uuid`; item
  `id`s are absent on some items and shared between a call and its output on
  others.
- **`Reasoning.summary_text` was empty on all 109 items.** Codex writes a
  summary only when the model produces one. Both plausible element shapes
  (a bare string, an object with `text`) are accepted rather than guessed at.
- **`AgentMessage.phase`** is `commentary` (22) or `final_answer` (4). Both are
  drawn by the TUI, so both are shown.
- **`CommandExecution.parsed_cmd[].type`** is `unknown` (43), `search` (27),
  `read` (21) or `list_files` (5). Its `cmd` is preferred over `command`, which
  begins `/bin/zsh -lc` on every entry and would make every row look the same.
- **`FileChange.changes`** is keyed by absolute path, values `update` (35),
  `delete` (1), `add` (1).
- **`Extension.kind`** was `web.search` on all 15. `action.type` is `search`,
  `openPage`, `findInPage` or `other`; when it is `other` the top-level `query`
  is the empty string, so a bare `· web.search` is the honest reading rather
  than a parse failure.

### There is no title and no colour in the rollout

Nothing corresponding to Claude Code's `custom-title`, `ai-title` or
`agent-color` rows. Codex *does* have a `/rename` slash command, but the name it
sets is not written to the rollout — the thread metadata lives in
`~/.codex/thread_history_1.sqlite`.

## The hooks

Codex ships a hook system that is deliberately Claude-Code-shaped — its own
embedded output schema even says *"Claude requires `reason` when `decision` is
`block`; we enforce that semantic rule during output parsing"*.

**Events**: `PreToolUse`, `PostToolUse`, `PermissionRequest`, `PreCompact`,
`PostCompact`, `SessionStart`, `SessionEnd`, `SubagentStart`, `SubagentStop`,
`UserPromptSubmit`, `Stop`. **There is no `Notification`** — `PermissionRequest`
is the analogue, and carries `tool_name` and `tool_input`, which Claude Code's
`Notification` does not.

**Input**, from the binary's own JSON schemas (`stop.command.input` and friends):
`session_id`, `cwd`, `hook_event_name`, `model`, `permission_mode`
(`default` | `acceptEdits` | `plan` | `dontAsk` | `bypassPermissions`), and a
Codex extension `turn_id`. `transcript_path` is **nullable**. `Stop` adds
`last_assistant_message` and `stop_hook_active`; `SessionStart` adds `source`
(`startup` | `resume` | `clear` | `compact`); `SessionEnd` adds `reason`.

**Output**: every property of `permission-request.command.output` has a default
and none is required, so **empty stdout is "no opinion"** and the normal prompt
is shown. The failure modes to design against are elsewhere:

- **exit code 2 is a deny** — `PermissionRequest hook exited with code 2 but did
  not write a denial reason to stderr`
- the output schema is `additionalProperties: false`, so even valid-but-unknown
  JSON on stdout is `hook returned invalid permission-request JSON output`

"Fails closed" in the schemas refers only to the reserved `interrupt`,
`updatedInput` and `updatedPermissions` fields, not to silence.

A handler may also declare `async: true`, which makes it structurally unable to
return a decision at all.

**`SubagentStart` / `SubagentStop` carry the parent's `session_id`** alongside
`agent_id`, so a subagent's hook writes would land on the parent's status file.

## Still unknown

Not measured. Do not write these down as facts until they are.

- Whether `async: true` is accepted on `PermissionRequest` and the other events.
- Whether the trust flow (`hooks.state`, `trustStatus`, `startup_hooks_review`,
  the `/hooks` command) blocks a `hooks.json` written out of band, and what
  editing one afterwards does to its trust state.
- Whether `allowManagedHooksOnly` is set on any machine this ships to.
- The exact discovery path for a user-level `hooks.json` under `CODEX_HOME`.
- What `PermissionRequest` stdin actually contains for a shell approval —
  specifically whether `tool_input` carries a `cwd`-like key, which would defeat
  a greedy `sed` extraction.
- Whether the hook's ancestor walk lands on the `codex` process, and what
  `$SHELL -lc` adds to the chain.
- The approval modal's real geometry, and which keys answer it. Codex's options
  are worded `Allow` / `Allow for this session` / `Allow and don't ask me again`
  / `Cancel`, each with a description line underneath, which breaks
  `ChatTranscript.ParseDialog`'s requirement of a contiguous run numbered
  strictly 1..n. It returns null on those — safe, but useless.
- Whether Escape or Ctrl+C is a safe interrupt. In Codex, Esc is
  edit/backtrack, and interrupting raises its own dialog whose options include
  "Stop the current task and exit Codex".
- Whether a side conversation shares a pid or a session id with its parent.
- Whether compaction rewrites a rollout in place — a reader that treats a
  shrinking file as "start over" depends on the answer.
- Whether `/rename` puts a name anywhere the app can read.
