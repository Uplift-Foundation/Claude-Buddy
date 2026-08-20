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

### Names: not in the rollout, but Codex has them

An earlier version of this document said Codex names nothing. That was wrong,
and it was wrong in the way worth guarding against — true of the file being
read, and false about the product.

The rollout carries nothing corresponding to Claude Code's `custom-title`,
`ai-title` or `agent-color` rows. But `$CODEX_HOME/state_<n>.sqlite` has a
`threads` table, and it holds both halves of what Claude Code keeps in its
transcript:

| column | is |
| --- | --- |
| `name` | what `/rename` set. Null until someone renames the thread. |
| `title` | Codex's own, taken from the first thing you asked. Always set. |
| `preview` | the same first message, truncated. |
| `first_user_message` | likewise. |
| `agent_nickname` | null on every thread seen so far. |

So the precedence is exactly Claude Code's — a name set by hand outranks a
generated one — and the query is:

```sql
select coalesce(nullif(name,''), nullif(title,'')) from threads where id = ?
```

Measured against the live database while Codex was running: **7 ms**, read-only
so a hook can never take a write lock on a database Codex is using. A full Codex
hook invocation including this costs about 98 ms.

"While Codex was running" is load-bearing, and was found the hard way. The
database is in WAL mode, so a read-only connection needs the `-shm` file to
attach to — and when Codex is not running, that file and the `-wal` are
checkpointed away, at which point a read-only open fails outright with
`SQLITE_CANTOPEN`. The same query that returned `aiea` in one minute returned
nothing in the next, purely because Codex had exited in between.

That is benign in production, because a hook only ever fires while the CLI that
called it is running — and the fallback to the rollout's first message is exactly
what should happen anyway. It matters for anyone *testing* this: a title that
comes back as the first prompt rather than the `/rename` name is not necessarily
a bug in the precedence, it may just be that nothing has Codex open.

Two things to know before relying on it:

- **The filename carries a schema version** — `state_5.sqlite` today. Take the
  highest-numbered match rather than naming one, so a bump alone doesn't break
  the lookup.
- **A thread appears in the table a moment after it starts**, so the very first
  hook of a session can legitimately find nothing. The fallback reads the first
  `UserMessage` out of the rollout, which is the same message Codex builds its
  own `title` from — so the two agree rather than compete.

There is **no per-session colour**, but there is a colour. It lives on a
*section* — a named group of threads, managed with `/section` and
`/createsection` — as `thread_sections.appearance`, a JSON `{"icon":…,"color":…}`,
which a thread points at through `threads.thread_section_id`. Confirmed by
creating one over the app-server:

```
threadSection/create {"name":"cb-probe","appearance":{"icon":"circle","color":"blue"}}
  -> {"section":{"id":"01a02004-…","name":"cb-probe","appearance":{"icon":"circle","color":"blue"}}}
```

`threadSection/create`, `/update`, `/delete` and `/list` all exist as RPCs, as
does `thread/section`. So writing one is possible — and is deliberately not
done. Filing a session under a section to give it a colour would reorganise the
user's own thread list in Codex's sidebar. The hook reads the section's colour
when there is one and leaves the ring plain otherwise.

Note that a derived title is not unique. Two of the rollouts on this machine
began with the *same* first message in different directories — the same
instruction run against two repositories, which is a normal way to work. The
working directory therefore has to stay part of anything identifying a session,
the saved orb position included.

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

### Where hooks are configured, and what is accepted

Measured by writing a `hooks.json` into a scratch `CODEX_HOME` and asking a
running `codex app-server` for `hooks/list` over JSON-RPC on stdio — which is
also the cheapest way to check an installer's work without starting a session:

```
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"probe","title":"probe","version":"0.0.1"}}}
{"jsonrpc":"2.0","method":"initialized","params":{}}
{"jsonrpc":"2.0","id":2,"method":"hooks/list","params":{}}
```

- **`$CODEX_HOME/hooks.json` is discovered automatically.** Nothing needs adding
  to `config.toml`. Entries come back tagged `source: "user"`.
- **The config shape is Claude Code's**, exactly:
  `{"hooks": {"<Event>": [{"matcher": …, "hooks": [{"type": "command",
  "command": …}]}]}}`. `matcher` works. Event keys are PascalCase in the file
  and normalise to camelCase in the listing.
- **A handler accepts `async: true`**, confirmed on `PermissionRequest` and
  `PreToolUse`. It also accepts `timeout`, in seconds, defaulting to 600.
- **An event name Codex does not know is dropped silently** — `Notification`
  was accepted into the file, produced no entry, and reported nothing in either
  `warnings` or `errors`. So a wrong event name is indistinguishable from a hook
  that never fires, which is the single easiest way to waste an afternoon here.
  It also means a config carried over by Codex's own `/import` from a Claude
  Code setup can contain dead entries that nothing complains about.
- **A `hooks.json` written by anything other than Codex starts untrusted**:
  every entry came back `trustStatus: "untrusted"`, alongside a
  `currentHash: "sha256:…"`, which is what re-asks after an edit. Nothing fires
  until the user accepts it.

## The TUI, measured in a tmux pane

Captured with `tmux capture-pane -p` against a real Codex 0.148 session. Three
things were expected to differ from Claude Code and do not — which is worth
writing down, because "we checked and it is the same" and "nobody looked" are
indistinguishable otherwise.

**The approval prompt is a numbered 1..n list**, and
`ChatTranscript.ParseDialog` reads it correctly, keys and labels both. A real
escalation, verbatim:

```
  Would you like to run the following command?

  Environment: local

  Reason: Allow creating the requested empty file cb-approval-probe in your home directory?

  $ touch $HOME/cb-approval-probe

› 1. Yes, proceed (y)
  2. Yes, and don't ask again for commands that start with `touch $HOME/…` (p)
  3. No, and tell Codex what to do differently (esc)

  Press enter to confirm or esc to cancel
```

This contradicts what the binary's strings suggest. Codex ships `Allow` /
`Allow for this session` / `Allow and don't ask me again` wording, which would
have broken the contiguous-1..n rule — but the TUI does not use it for command
approval. A Codex-specific dialog parser was planned and turned out to be dead
code. The fixture is in `tests/TranscriptTests`.

**A digit answers immediately.** Sending `3` dismissed the prompt and acted on
it. The "Press enter to confirm" line describes the arrow-key route, not the
numeric one.

**Escape interrupts.** Codex's own working indicator says so — `• Working (0s •
esc to interrupt)`.

**Submitting works with the sequence the app already uses**: set a tmux paste
buffer, `paste-buffer -p -d`, then `send-keys Enter`. Replayed against a live
Codex pane and it submitted exactly as it does for Claude Code. Note that the
Enter has to be its own key — sending the text with a trailing `C-m` typed it
into the composer without submitting.

**The sandbox does not block the hook.** `codex exec` defaults to a read-only
sandbox, which looked like a likely cause when status files failed to appear;
it is not. `mkdir`, `>` redirection and `touch` all succeed from inside a hook,
and `TMPDIR` is passed through untouched. The real cause of the missing file was
`SessionEnd` firing at the end of a one-shot `exec` run and correctly deleting
it — the session really had ended.

## Still unknown

- On Windows: `install-codex-hooks.ps1`'s install path and the hook script's
  own `-Agent codex` branch have never run, because no runner or machine here
  has Codex installed. CI does cover the wrapper, the installer wiring and the
  skip-when-absent path.

Not measured. Do not write these down as facts until they are.

- Whether a hook declared `async: true` still receives its payload on stdin. It
  is accepted and stored — see above — but nothing here has watched one fire.
  This is the one thing the amber "waiting" state depends on.
- Whether `allowManagedHooksOnly` is set on any machine this ships to.
- What accepting the trust prompt actually requires of the user, and whether it
  can be done without an interactive TUI.
- What `PermissionRequest` stdin actually contains for a shell approval —
  specifically whether `tool_input` carries a `cwd`-like key, which would defeat
  a greedy `sed` extraction.
- Whether the hook's ancestor walk lands on the `codex` process, and what
  `$SHELL -lc` adds to the chain.
- Whether a side conversation shares a pid or a session id with its parent.
- Whether compaction rewrites a rollout in place — a reader that treats a
  shrinking file as "start over" depends on the answer.
