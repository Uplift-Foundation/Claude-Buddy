# OpenClaw gateway — spike findings

Everything here was measured against one real gateway — OpenClaw **2026.7.1-2**
(`0790d9f`), protocol **4**, running on a Mac mini at `192.168.0.127`, probed
from a MacBook at `192.168.0.189` on the same subnet — unless it says otherwise.
Where something is assumed rather than observed, it says so.

The short version, in the order it was discovered:

1. The planned **ssh tunnel is unnecessary** — the gateway is LAN-bound and
   reachable directly.
2. **Ed25519 signing is unavoidable.** A bearer token alone is refused with
   `DEVICE_IDENTITY_REQUIRED`, and scopes belong to a *paired device*.
3. **The gateway is TLS 1.3-only and .NET on macOS cannot speak TLS 1.3.** This
   overrides (1): the tunnel doesn't help either, because loopback is TLS too.
   The connection has to be made with a managed TLS stack — BouncyCastle, which
   (2) already put in the project.

Everything below is measured. Where a conclusion was later overturned by a
further measurement, both are kept, because the wrong one was reached honestly
and the reason it was wrong is the useful part.

## Reachability — the tunnel is not needed for *this*

The plan assumed a loopback-only gateway reached through
`ssh -N -L`. That premise is false here:

| checked | result |
| --- | --- |
| `gateway.bind` | `lan` — the listener is `*:18789`, not `127.0.0.1:18789` |
| `gateway.mode` | `local` |
| `gateway.tls.enabled` | `true`, cert `~/.openclaw/tls-cert.pem` |
| TCP from the laptop | open, ~3 ms |
| `https://192.168.0.127:18789/` | 200, serves the Control UI |
| `http://…` (plain) | `curl: (52) Empty reply` — TLS only |
| WebSocket upgrade | **101 on every path tried** (`/`, `/ws`, `/gateway`, `/api/ws`, `/socket`) |

So the client connects directly to `wss://192.168.0.127:18789/`. No child
process, no port allocation, no orphan sweep, no `ControlMaster`/
`ExitOnForwardFailure` hazards — the entire `SshTunnel` component and its
shutdown-hook story can be deleted from the plan.

The token is not exposed by dropping the tunnel: TLS is on, so it is encrypted
in transit exactly as it would have been inside ssh.

**Still true after the TLS finding below.** The tunnel stays deleted — not
because TLS made it redundant, but because it would not have helped: the
gateway refuses plain HTTP on loopback as well, so a forwarded port arrives at
the same TLS 1.3 handshake.

### TLS 1.3 only — and .NET on macOS cannot speak it

**This is the finding that decides the transport, and it kills the direct
connection.**

```
openssl -tls1_2 → alert 70, "tlsv1 alert protocol version"
openssl -tls1_3 → New, TLSv1.3, TLS_AES_256_GCM_SHA384
```

The gateway sets `minVersion: "TLSv1.3"` in its own source
(`dist/gateway-*.js`) — hardcoded, not exposed in config, so it cannot be
relaxed without patching OpenClaw.

.NET cannot meet it on this platform:

| from .NET 10.0.9 / osx-arm64 | result |
| --- | --- |
| `cloudflare.com:443`, `Tls12` | OK |
| `cloudflare.com:443`, `Tls13` | **"The requested security protocol is not supported"** |
| gateway, `Tls12` | "bad protocol version" (the server's alert 70) |
| gateway, `Tls13` | "The requested security protocol is not supported" |

Failing against Cloudflare as well is what makes this conclusive: it is a
property of .NET on macOS (SecureTransport tops out at TLS 1.2), not of this
gateway or this network. Windows is unaffected — but macOS is this app's
primary platform.

An ssh tunnel does **not** rescue it. The gateway is TLS-only on loopback too
(`http://127.0.0.1:18789/` → "Empty reply from server"), so forwarding the port
still lands on a TLS 1.3 handshake that .NET cannot complete.

That leaves BouncyCastle's managed TLS 1.3 client — already a dependency for
Ed25519 — with the WebSocket upgrade hand-rolled over it and handed to
`WebSocket.CreateFromStream`.

### The served certificate is not the configured one

`gateway.tls.cert` points at `~/.openclaw/tls-cert.pem`, an mkcert leaf
(`O=mkcert development certificate`, SAN `localhost, 192.168.0.127, 127.0.0.1`,
sha256 `48911757…`). **The listener does not serve it.** What it actually
presents is self-signed with no SAN at all:

```
subject  CN=openclaw-gateway
issuer   CN=openclaw-gateway          (self-signed)
valid    Aug 16 2026 → Aug 13 2036
SAN      (none)
sha256   34da256e1eaedac88e0e01b251d57c56a7a5ae85221a3affa7da0b752b56a617
```

So **pin the fingerprint and skip name validation entirely** — with no SAN there
is no hostname to validate against, for any host or IP. Pin `34da256e…`, not the
mkcert one. Why the configured cert isn't being served is unexplained and worth
a look if TLS is ever reconfigured.

**Method note:** an earlier round of these probes used `timeout 10 openssl …`.
macOS has no `timeout`, so those commands never ran and their empty output read
as "no TLS handshake". Two conclusions were briefly drawn from that and both
were wrong. `timeout` is a GNU coreutils binary; don't reach for it here.

## Ed25519 signing is required — the risk landed

The plan hoped a bearer token alone would authenticate. Measured, in order:

| attempt | result |
| --- | --- |
| `client.id: "claude-buddy-spike"` | rejected — `client.id` is an enum |
| `client.mode: "operator"` (as the docs show) | rejected — `mode` is a different enum from `role` |
| `client: {id: "cli", mode: "node"}` + `auth.token` (the gateway token) | **`hello-ok`** — but `auth.scopes: []` |
| `sessions.list` on that connection | `missing scope: operator.read` |
| `auth.token` = a paired device's token, as `cli`/`cli` | `unauthorized: gateway token mismatch`, `canRetryWithDeviceToken: false` |
| `openclaw-control-ui`/`webchat` identity | `origin not allowed` — browser-only path |
| `auth.deviceToken` (correct field), no `device` block | **`NOT_PAIRED` / `DEVICE_IDENTITY_REQUIRED`** |

**Scopes are granted per paired device, not per connect request.** Asking for
`scopes: ["operator.read"]` in `connect` is ignored; `openclaw devices list`
shows nine paired devices each carrying their own role, scopes and token. The
gateway token gets you *connected* with zero scopes, which is useless — every
read method refuses.

So Claude Buddy must be a paired device, and a paired device must present the
optional-in-schema-but-mandatory-in-practice `device` block. **`BouncyCastle`
goes into the plan**, for the packaging reasons already recorded there (NSec is
libsodium, and a native dylib inside a single-file hardened-runtime bundle is a
signing problem this project has been bitten by before).

### The exact signing format

From `dist/src-DZzKBMa7.js` and `dist/device-identity-UW4cZXf5.js`:

- Keypair: **Ed25519**, generated once and persisted.
- `deviceId` = `sha256(raw 32-byte public key)`, hex — matches the 64-hex ids in
  `devices/paired.json`.
- `publicKey` on the wire = **base64url** of the raw 32 bytes (43 chars, no
  padding). Same encoding for `signature`.
- Signed payload is a pipe-joined string, signed raw (no prehash):

```
v3|deviceId|clientId|clientMode|role|scopes(comma-joined)|signedAtMs|token|nonce|platform|deviceFamily
v2|deviceId|clientId|clientMode|role|scopes|signedAtMs|token|nonce            (older)
```

`nonce` is the one from the `connect.challenge` event.

**`token` is not the device token**, which is the obvious reading and is wrong.
The gateway's `resolveSignatureToken` takes

```js
connectParams.auth?.token ?? connectParams.auth?.deviceToken ?? connectParams.auth?.bootstrapToken ?? null
```

so whenever a gateway token is being sent — which is always, see below — it is
the **gateway** token that gets signed, and `null` becomes `""`. Signing the
device token instead fails as `DEVICE_AUTH_SIGNATURE_INVALID`, which reads like a
broken key rather than a field mix-up and cost an hour to spot.

### All three credentials are required at once

`auth.token` alone → connected, no scopes. Device identity alone →
`AUTH_TOKEN_MISSING`. Both, unapproved → `PAIRING_REQUIRED`. So a working
connect carries the gateway token (*may this client talk to this gateway*), the
signed device block (*and is it who it claims*), and — once paired — the scopes
the gateway holds against that device record.

The device token itself turned out **not** to be needed for a connect that
already carries the gateway token; the approved device record is what grants the
scopes.

**The `requestId` rotates on every attempt.** Each refused connect replaces the
pending entry, so an id read from the pending list is stale as soon as the client
retries — approve with `openclaw devices approve --latest`. The stable
identifier is the `deviceId`.

### Pairing is the on-ramp

`~/.openclaw/devices/bootstrap.json` holds a single-use bootstrap token whose
profile grants exactly what this feature needs:

```
roles  ["node", "operator"]
scopes ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"]
```

`redeemedProfile` was empty at probe time, i.e. unredeemed. `connect.params.auth`
accepts `token | bootstrapToken | deviceToken | password`, so the first-run flow
is: generate a keypair → connect presenting `bootstrapToken` + a signed `device`
block → receive a device token → store it → sign with it thereafter.
`openclaw devices approve` is the manual alternative.

**Not yet verified end-to-end**, because redeeming the bootstrap token writes a
new paired device to the gateway. That is the one remaining gap.

### Enums, measured not guessed

```
client.mode   webchat | cli | test | probe | ui | backend | node
auth          token | bootstrapToken | deviceToken | password
device        { id, publicKey, signature, signedAt, nonce }   -- optional in the
                                                                 schema, required in practice
```

The published docs say `client.mode: "operator"`; the gateway rejects it. `role`
is the separate field that takes `operator`.

## Numbers the implementation needs

From `hello-ok`:

```
protocol            4
server.version      2026.7.1-2
policy.maxPayload   26214400      (25 MB — the frame-accumulation bound)
policy.tickIntervalMs 30000       (the last-frame watchdog threshold)
snapshot keys       presence, health, stateVersion, uptimeMs, sessionDefaults
```

**Event rate is trivial**: over 25 s on an idle connection, `health` ×2 and
`tick` ×1. This confirms the plan's decision to cut the `Channel<T>` and
coalescing pump — a direct `Dispatcher.UIThread.Post` is ample.

## Pairing works, and `sessions.list` is nothing like a Claude Code session list

Paired as `gateway-client` / `ui` / `operator` with `operator.read`, approved
with `openclaw devices approve --latest`. Two notes on the flow itself:

- **The `requestId` rotates on every connection attempt.** Each refused connect
  replaces the pending entry with a new id, so a request id read from the
  pending list is stale the moment the client retries. `--latest` avoids the
  race; the stable identifier is the `deviceId`.
- The gateway grants scopes from the *paired device record*, so the
  `scopes` in `connect.params` is a request, not a claim.

### 59 sessions, and almost none of them are interesting

This is the finding that changes the feature's shape:

| measure | value |
| --- | --- |
| total sessions | **59** |
| `hasActiveRun` | 0 at rest |
| active in the last 5 minutes | 2 |
| ...last hour | 8 |
| ...last 24 hours | 20 |
| `status` values | `done`=32, absent=24, `failed`=3 |
| `origin.provider` | `discord`=45, absent=12, `webchat`=2 |

A Claude Code status directory holds the handful of sessions actually running.
An OpenClaw gateway holds **every conversation it has ever had** — every Discord
channel and DM, every cron job, every named agent. One orb per session is 59
orbs, which is not a display, it's a wall.

The existing "Keep orbs for" setting is the natural filter, and it needs no new
concept: stamp each entry's `Written` with the session's own `lastActivityAt`
rather than `UtcNow`, and the lifetime timer prunes quiet sessions exactly as it
does for a quiet Claude Code session. This **reverses** the plan's instruction to
stamp `UtcNow` while connected — that rule was written on the assumption that a
listed session is a live one, which is true of the hook's status files and false
here. A session with `hasActiveRun` should be exempt from pruning, the same way
`waiting` already is.

### The fields that matter

```
sessionKey        agent:main:discord:direct:246722755112861696
                  agent:alexis:main | agent:main:cron:<uuid>
                  -> "agent:<name>:<surface>[:<type>:<id>]"; the agent name is
                     in the key and nowhere else useful
label             usually empty — set on cron sessions ("Cron: disney-news-daily")
origin            { label: "#general channel id:...", provider: "discord",
                    surface: "discord", chatType: "direct" | "channel" }
                  -> origin.label is the best human name (47 of 59 have one)
hasActiveRun      bool  -> the "generating" signal
status            "done" | "failed" | absent
lastActivityAt    epoch ms  -> what the stale timer should use
updatedAt         epoch ms
model             "claude-sonnet-4-6"    modelProvider "claude-cli"
totalTokens, runtimeMs, startedAt, endedAt
lastChannel       "discord"
```

There is **no `cwd`**, which retires the plan's worry about position keys
colliding with a local checkout of the same path — an OpenClaw session has no
directory to collide with.

Naming an orb: `origin.label` first, then `label`, then the agent name parsed out
of `sessionKey`. The raw labels are workmanlike (`wtvamp user id:2467…`), so the
agent name plus surface (`alexis · discord`) may read better than either.

## A live turn: the state signal is the event stream, not the session list

Observed by watching a connection for six minutes while a cron job
(`stalled-session-watchdog`) ran on its own. Event totals for that window:

```
agent 15 | task 4 | session.tool 4 | presence 3 | cron 3 | chat 3 | session.message 1
```

| event | payload |
| --- | --- |
| `agent` | `stream: "thinking" \| "assistant"`, `data.text` (full snapshot) **and** `data.delta`, `runId`, `seq` |
| `session.tool` | `stream: "tool"`, `data.phase: "start" \| "result"`, `data.name`, `args`, `isError` |
| `chat` | `state: "delta"`, `deltaText` |
| `task` | `action: "upserted"`, `task.status: "completed"`, `kind: "cron"` |
| `cron` | `action: "finished"` |

**`session.operation` — which the docs name as the in-flight signal — never
fired.** That is the third documented detail contradicted by the running
gateway, after `client.mode` and the signature token.

Two structural facts:

- **Event `sessionKey`s are run-scoped**: `agent:main:cron:2f54203e…:run:aa07ba9a…`,
  the listed key with `:run:<runId>` appended. Attributing an event to a session
  means stripping that suffix. Run-scoped keys never appear in `sessions.list`
  (checked: 59 sessions, 0 containing `:run:`).
- **`hasActiveRun` never flipped**, across the whole window, through a complete
  run — zero transitions from a 2.5 s poll. So "working" must be driven by
  event arrival, with idle inferred from a terminal event (`task.status:
  "completed"`, `cron action: "finished"`) or a quiet period. This is the better
  design anyway: push-based and immediate, where polling was both laggy and, as
  measured, wrong.

`data.text` carrying a **full snapshot** rather than only a delta is a gift for
the chat panel — it is exactly the "TurnUpdated carries the whole turn, not a
delta" property the panel design asked the transport to guarantee, so a dropped
or coalesced event cannot desync the view.

## Reading and writing a conversation

Two methods beyond the session index, both confirmed working against the real
gateway:

**`chat.history { sessionKey, limit }`** returns
`{ sessionKey, sessionId, messages, defaults, sessionInfo, thinkingLevel }`.
Each message is:

```
role       "user" | "assistant"
content    [ { type: "text", text: "…" }, … ]   -- blocks, not a string
timestamp  epoch ms
model / provider / usage / stopReason
```

`content` being a block list matters: a message can hold tool_use blocks
alongside text, so a naive read of the whole array renders JSON at the reader.
Only the `text` blocks are worth showing in a backlog — live tool calls arrive
as their own `session.tool` events and read better as one line each.

**`chat.send { sessionKey, message, idempotencyKey }`** (also accepts
`agentId`, `sessionId`, `thinking`, `deliver`, `timeoutMs`). Needs
`operator.write`, which means the device has to be **re-approved**: changing the
requested scope set makes the gateway treat it as a new pairing, so it lands
back in the pending queue with a fresh `requestId`.

That re-approval is the reason replying is a second, separate switch in the app
rather than part of turning the feature on. Reading what your agents are doing
and being able to make them do things are different powers.

## `sessions.list` lies about recency

The single most misleading thing measured here. `lastActivityAt` and `updatedAt`
are **hours stale for an active Discord conversation**:

| session | claimed age | actually |
| --- | --- | --- |
| `agent:main:discord:direct:…` | 6640 s (1.8 h) | being chatted in at that moment |
| `agent:main:discord:direct:amber` | 9959 s (2.8 h) | recent |
| `agent:main:cron:…` | 135 s | correct — cron updates every 5 min |

So an activity filter built on those fields hides live conversations. Worse, it
hides them *intermittently*: the event stream reveals the session while a reply
is streaming, and the stale timestamp buries it again twenty seconds later,
which reads as a broken feature rather than as a bad timestamp.

A client that wants "is this live" has to keep its own record from the event
stream and take whichever is later. The list is still needed for sessions that
were active before the client connected — there is no better source for those —
which is why the window is a user setting rather than a constant.

## Agents have names, and they are not the ids

`agents.list` (read scope is enough) returns `{ defaultId, mainKey, scope, agents }`
where each agent is `{ id, name, displayName }`:

```
main → Lilibeth      comfyui → Zara        kubernetes → Amber
alexis → Alexis      social  → Annabel Lee modder     → Sara
```

Session keys are built from the **id**, so a naive title shows `main` for the
agent its owner calls Lilibeth — and shows the same letter on four different
orbs. `displayName` is empty on all of them here, so `name` is the field that
matters.

Names alone still under-identify: one agent commonly holds a DM with you, a DM
with someone else and two channels at once, all `agent:<id>:discord:*`. The
distinguishing part is `origin.label`, which is written for a log —
`"#general channel id:1474991965354463274"`, `"wtvamp user id:2467…"` — and
cleans up to `#general` and `wtvamp` by cutting at `" id:"` and dropping the
noun before it.

## Still unknown

- **The amber "needs you" state.** No `session.approval` fired in six minutes of
  watching, because nothing asked for permission in that window. The event
  exists in the protocol and requires `includeApprovals: true` on
  `sessions.messages.subscribe`, which in turn needs `operator.approvals` —
  a scope this device did not request. Unverified end to end.
- Whether a Discord-originated turn behaves identically to a cron one. Only cron
  activity occurred while watching.
- What a terminal `chat` state looks like (only `state: "delta"` was seen).

## Incidental

`~/.openclaw/openclaw.json` also carries `gateway.remote.transport: "ssh"` and
`gateway.tailscale.mode: "off"`. Neither affects this design, but the first
suggests OpenClaw's own remote story is ssh-based, which is presumably where the
plan's original assumption came from.
