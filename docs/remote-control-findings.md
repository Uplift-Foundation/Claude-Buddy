# Remote Control (`/rc`) sessions — spike findings

Everything here was measured on 23 Aug 2026 against two real machines on one
account: a MacBook at `192.168.0.189` running the bridge, and a Mac mini
(`avatar.internal`, `192.168.0.127`) running the session being controlled.
Both used Claude Code `2.1.241` and the `~/.claude-board` CLI profile
(`CLAUDE_CONFIG_DIR`), which is a second Anthropic account on the same box.
Where something is assumed rather than observed, it says so.

The premise being tested: Buddy cannot talk to Anthropic's Remote Control relay
directly — there is no third-party API for it. But a Claude Code session that
*itself* has RC connected is given peer tools (`ListAgents`, `SendMessage`) that
reach the account's other sessions. So Buddy can launch its own hidden **bridge**
session and use it as the doorway. That premise held.

The short version, in the order it was discovered:

1. **`claude agents --json` is local-only** — it never sees another machine, so
   there is no cheap way to skip the bridge for discovery.
2. **The bridge works, detached and unattended.** `--remote-control` is a real
   flag; RC came up in a `tmux new-session -d` with no client attached and no
   trust or login prompt.
3. **Cross-machine control works end to end.** The bridge listed the mini's
   session and got a reply back from it.
4. **The protocol does not need the model to format anything.** Both discovery
   and replies land in the bridge's own transcript in deterministic, parseable
   shapes — which removed a planned fenced-JSON convention entirely.

## `claude agents --json` cannot do this

The cheapest imaginable design was to skip the bridge and just enumerate. It
does not work: every row carries a local `pid`, and the two machines never see
each other.

| run | result |
| --- | --- |
| MacBook, `~/.claude` | 8 rows — 6 local `interactive` tmux panes, 2 local `background`; all with a live local `pid` |
| MacBook, `~/.claude-board` | 1 row — a `background` job from ~29 days earlier with **no** `pid` and a local `cwd`: a stale orphan record, not a remote session |
| mini, `~/.claude` | `[]` |
| mini, `~/.claude-board` | 2 rows — `resumes-2b`, `job-hunter`; both local to the mini |

That `pid`-less row on the MacBook briefly looked like a remote session. It is
not — it is a month-old dead background job. Recorded because it is exactly the
kind of thing that would make someone conclude enumeration works when it
doesn't. **`--json` is a local process listing.** The bridge is required.

## The bridge: launching it

`claude --help` documents the flag, so nothing has to be typed interactively:

```
--remote-control [name]                    Start an interactive session with Remote Control enabled (optionally named)
--remote-control-session-name-prefix <prefix>   Prefix for auto-generated Remote Control session names (default: hostname)
```

What worked, in a detached tmux session with **no attached client**:

```sh
tmux new-session -d -s rc-bridge -x 200 -y 50 -c <cwd>
tmux send-keys -t rc-bridge \
  'TMPDIR=<private> CLAUDE_CONFIG_DIR=$HOME/.claude-board \
   CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1 <claude> --remote-control <name>' Enter
```

Two things about that command are load-bearing:

- **It must not be piped.** The first attempt appended `| tee log`, and Claude
  Code correctly decided it wasn't a TTY: `Error: Input must be provided either
  through stdin or as a prompt argument when using --print`. The bridge needs a
  real pty, which is what tmux is for. Capture output with `capture-pane` or
  `pipe-pane`, never a shell pipe on the launch line.
- **`tmux new-session -d` is a new primitive for this repo.** Existing code only
  ever calls `new-window` into a session that already has a client
  (`AgentTeamViewer.PlaceInTmux`). Detached creation works fine and needs no
  client — which is what makes a hidden bridge possible.

RC came up with no trust prompt, no login prompt, and no interactive step.

### The private-`TMPDIR` trick works

The bridge must not show up as an orb. Since `ClaudeBuddyHook.sh` writes its
status file to `$TMPDIR/claude_buddy/<session-id>.txt`, pointing the bridge at a
private `TMPDIR` sends its hook output somewhere only Buddy reads.

Observed exactly as designed. The private directory received:

```json
{"state":"idle","cli":"claude","cwd":"/Users/warrenthompson/Source/Claude-Buddy",
 "title":"","color":"","term_program":"tmux","term_id":"","tty":"ttys014",
 "tmux_socket":"/private/tmp/tmux-501/default","tmux_pane":"%17",
 "tmux_bin":"/opt/homebrew/bin/tmux","session_pid":7184,
 "transcript_path":"/Users/warrenthompson/.claude-board/projects/-Users-warrenthompson-Source-Claude-Buddy/ec074105-d5c0-46cd-99f5-bab441847ccd.jsonl"}
```

and that session id was **absent** from the real `$TMPDIR/claude_buddy/`, so no
orb appeared. This is better than synthesising a status file by hand: the hook
hands over the session id, the pane, the socket, the binary and the transcript
path for free. Readiness detection is just "wait for a file to appear here".

## Discovery: `ListAgents` returns text, not JSON

This is the finding that changed the design. The raw `tool_result` in the
bridge's transcript is human-readable and **deterministic**:

```
This session is claude-buddy-52 [30e947] — the name other sessions use to message it (it is not listed below; a message to it would be a message to yourself).

Peer sessions (1):
  job-hunter [94f106]  ·  Remote Control  ·  idle
```

Row shape is `  <name> [<ref>]  ·  <kind>  ·  <status>`, separated by ` · `.
Consequences:

- **`kind` is the discriminator.** The peer is labeled literally
  `Remote Control`. On the other account, whose peers were all local, the same
  field read `interactive` or `bg`. That is how a remote session is told apart
  from a local one.
- **Only RC-connected sessions are peers.** `resumes-2b` was running and *busy*
  on the mini at the time and did **not** appear — it simply wasn't RC-connected.
  This is the right scope for the feature, and it means Buddy shows exactly the
  sessions a user could reach from their phone.
- **Self-exclusion is automatic** ("it is not listed below"). The planned
  "filter the bridge's own session out of the snapshot" step is unnecessary.
- The name passed to `--remote-control` did **not** become the peer name: the
  session self-named `claude-buddy-52`, which looks derived from the working
  directory. Not chased further; worth pinning down so the bridge is
  recognisable in a user's own session list.
- Peer rows carry **no machine or hostname**. Asked directly, the bridge
  answered `"machine":"unknown"`. Orb titles will have to use the session name,
  or get the hostname another way.

## Control: `SendMessage` round trip

Sent to the mini's `job-hunter`; it replied with its hostname,
`avatar.internal`, and that reply reached the bridge. The full path works.

The `tool_use` input shape:

```json
{"to":"job-hunter","summary":"bridge connectivity test, asking for hostname",
 "message":"Bridge connectivity test from Claude Buddy - reply with your hostname only.",
 "type":"message","recipient":"job-hunter","content":"…"}
```

The `tool_result` — note it names the transport explicitly, and returns an id:

```json
{"success":true,
 "message":"“bridge connectivity test, asking for hostname” → job-hunter (a Claude session on another machine, over Remote Control)",
 "msg_id":"e547dcf7-4510-4992-b14e-faa5b95e1872"}
```

**Replies are asynchronous and arrive on a later turn**, as a `role=user`
transcript row wrapping a tag:

```
Another Claude session sent a message:
<cross-session-message from="bridge:session_01SX9H3aCQbpjVN9hM4njAXd" from-name="job-hunter" from-mode="prompting">
avatar.internal
</cross-session-message>
```

`from-name` is the correlation key. (`from` is the relay's own session id, whose
`bridge:` prefix is the relay's terminology and coincidental to ours.)

The bridge's own narration of the gap is worth keeping, because it is the timing
contract stated plainly: *"a reply from job-hunter would arrive asynchronously as
a `<cross-session-message>` on a later turn, and nothing has arrived. I can't
fill in spike-002 without inventing content."* It then reported the reply
correctly on the following turn once it landed.

## What this changes in the design

The plan assumed the bridge would be driven by a strict fenced-JSON convention
with model-generated `request_id`s, because a model-mediated relay is
nondeterministic. **That is not needed.** Both directions land in the transcript
in machine-parseable form:

| need | deterministic source |
| --- | --- |
| list remote sessions | the `Peer sessions (N):` block in the `ListAgents` `tool_result` row |
| confirm a send | `msg_id` in the `SendMessage` `tool_result` row |
| receive a reply | `<cross-session-message from-name="…">body</…>` row |

So `BridgeProtocol` parses transcript rows and correlates on `from-name` and
`msg_id`. The only model-mediated step left is getting the bridge to *call* the
tool at all. Dropping fenced JSON removes the feature's main nondeterminism risk.

It also confirms a planned correction: since a reply can arrive on *any* later
turn, the bridge needs **one continuous transcript tail, demultiplexed by
`from-name`** — not a tail per chat session.

For the record, the model did obey a strict fenced-JSON instruction on the first
try (`{"request_id":"spike-001","agents":[…]}`, no surrounding prose). It works;
it is just unnecessary, and parsing the tool result is strictly more reliable.

## Startup banners — what `BridgeState` can read

A second bridge launch captured the header, which is where readiness and health
are actually legible:

```
 ⚠ 2 MCP servers need authentication · run /mcp
 ⚠ Your login expires in 3 days · run /login to renew
  /remote-control is active · Continue here, on your phone, or at https://claude.ai/code/session_01XfZfJnPe9EGxapEmtzrhBL
```

- **`/remote-control is active`** is the readiness signal, and it carries the
  relay session URL. Worth preferring over the private status file for
  confirming *RC specifically* came up: the status file proves the session
  started, this proves RC attached.
- **`Your login expires in N days · run /login to renew`** is a real
  degradation pattern — the one failure mode observed in the wild. An expired
  login is the likeliest way a long-lived bridge dies quietly.
- Banner lines are prefixed `⚠` and use ` · ` as an internal separator, the same
  convention as the peer rows.

Still not captured: quota exhaustion and RC-dropped-mid-session. `Degraded`
classification for those remains guesswork.

Note `daemon-auth-status.json` in the profile read `{"status":"auth_required"}`
throughout, while RC itself worked normally — so **daemon auth state is not a
proxy for RC health** and should not be used as one.

## Assumed, not confirmed

- **Whether `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` is required — unresolved,
  and deliberately made moot.** Both profiles on this machine set it in
  `settings.json`'s `env`, so it could not be isolated from an existing profile;
  relaunching with `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=0` on the launch line
  still produced a working `ListAgents`, which only shows that a process-env `0`
  does not beat a settings-file `1` — not that the flag is unnecessary. Rather
  than keep chasing it, **Buddy sets the flag unconditionally on the bridge it
  launches.** Setting it is harmless if redundant and load-bearing if not, which
  retires the risk without needing the answer. A clean-profile test would still
  be worth doing before claiming the feature works for users who have never
  enabled agent teams.
- **Windows.** Untested and out of scope for v1; the bridge as designed is
  tmux-shaped, and chat-send is already macOS-only in this app.

## One product-shaped warning: the bridge is not actually hidden

Text kept appearing in the bridge's input box that the spike did not send
("which machine is job-hunter on?", "send job-hunter a message"). The banner
explains it: *"Continue here, on your phone, or at https://claude.ai/code/…"*.
Enabling RC on the bridge **publishes it to the account's own RC surface**, so it
shows up on the user's phone and web session list, and they can type into it.
During this spike the user did exactly that, from a diner.

Two consequences, and the first is a real design problem:

1. **The private `TMPDIR` hides the bridge from Buddy's orb scan, not from the
   user.** They will see an unexplained session appear in their own Claude
   session list whenever Buddy starts the bridge. It needs a name that says what
   it is — which makes the unresolved `--remote-control <name>` behaviour (the
   name passed was ignored in favour of a cwd-derived `claude-buddy-52` /
   `claude-buddy-a8`) worth fixing rather than shrugging at.
2. **Injected prompts and a human's typing interleave in the same pty.** Buddy
   can paste a prompt into the same input line a user is mid-sentence in. So the
   bridge must be a session Buddy owns exclusively, and Buddy should tolerate
   unexpected turns in the transcript rather than assuming every row is a reply
   to something it sent — which the `from-name`/`msg_id` correlation already
   handles, and is another reason not to rely on turn ordering.
