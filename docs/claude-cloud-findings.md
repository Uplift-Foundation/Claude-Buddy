# Claude Code cloud sessions — credential and API findings

Everything here was measured during CB-164 unless it says otherwise, against claude.ai as it stood on **19 September 2026**, from a real Mac running Claude Code **2.1.278**. Where something is assumed rather than observed, it says so, and the "Not established" section at the end is the part worth reading first.

**This describes an undocumented private web API.** It is the endpoint claude.ai/code itself calls on page load, found by watching a real browser session, not by reading documentation. Nothing about it is supported and all of it can change without notice. CB-164 accepted that cost deliberately; this document exists so the next person can tell a shape change from a bug.

## The short version

1. There is **no supported client-side route**. `claude agents --json` returns local sessions only, the CLI control protocol has no cloud-session subtype, and the shipped CLI binary contains no listing endpoint — only `/v2/ccr-sessions/<id>/...` forms that take an id the caller already holds.
2. The **web client's own endpoint exists and is rich**: `GET https://claude.ai/v1/code/sessions`, returning `{ data, resume_token }`, plus a `/v1/code/sessions/watch` live-update stream.
3. It **accepts OAuth Bearer as a first-class scheme**, not just a session cookie. This was measured through a refusal asymmetry, with three negative controls.
4. **Whether the Claude Code CLI's own stored token is accepted at this audience is still unmeasured.** That is what `tools/claude-cloud-probe` exists to settle, and it needs a human to run it.

## Confirmed by running it

| checked | result |
| --- | --- |
| `claude agents --json` | local sessions only — every row carries a `pid` and a local `cwd`; no field could describe a cloud session |
| CLI control protocol, invented subtype | `Unsupported control request subtype` — the negative control showing the probe itself worked |
| `claude --help` | offers `--cloud [description\|session_id\|url]`; creates or attaches by an id you already hold. No list, gallery or roster command |
| shipped CLI binary, `/api/` paths | 117 distinct paths, none enumerating cloud sessions |
| `GET /v1/code/sessions?statuses=active&statuses=paused&limit=50` | **200** in a real browser session |
| `GET /v1/code/sessions?tags=cowork-remote&limit=100&include_trigger_sessions=true` | 200 |
| `GET /v1/code/sessions/watch?exclude_tags=-&resume_token=<token>` | 200 — a live update stream, not a poll |
| the same call without the header set below | **400** — the negative control showing the 200 is a real answer rather than an open endpoint |

Response envelope is `{ data, resume_token }`. Union of keys across rows: `config, connection_status, created_at, environment_id, environment_kind, external_metadata, id, last_event_at, participants, relations, status, status_bucket, tags, title, unread, updated_at, user_message_count, worker_status`. `external_metadata` carries `post_turn_summary, context_usage, rate_limit_info, model, effort_level, cross_session_inbound, container_cc_version, flag_settings`.

## The header set — the endpoint 400s without it

Six headers, plus `Authorization`. They live as constants in `CloudRequest` and there is exactly one copy of them in the repository, which is why `tools/claude-cloud-probe` references the app rather than building its own request: a probe that assembled these itself could answer a question differently from the app and be believed.

| header | value |
| --- | --- |
| `anthropic-client-platform` | `web_claude_ai` |
| `anthropic-version` | `2023-06-01` |
| `anthropic-beta` | `ccr-byoc-2025-07-29` |
| `anthropic-client-feature` | `ccr` |
| `x-organization-uuid` | the account's org uuid, out of `~/.claude.json` → `oauthAccount.organizationUuid` |
| `content-type` | `application/json` |
| `Authorization` | `Bearer <token>` |

**No cookie is ever sent.** The browser observation rode a session cookie; ours rides the CLI's OAuth token and nothing else. Sending both would make it impossible to say which one was accepted, which is the entire question the probe answers.

## The auth measurement — five requests, one asymmetry

All five against `GET /v1/code/sessions?statuses=active&limit=1` with the header set above.

| # | sent | status | body |
| --- | --- | --- | --- |
| A | no cookie, no auth | 401 | `Authentication failed`, real `request_id` |
| **B** | **no cookie, well-formed but invalid `Authorization: Bearer <placeholder>`** | **401** | **`OAuth access token is invalid.`, `request_id: null`** |
| C | no cookie, `Authorization: Bearer` with no token | 401 | `Authentication failed`, real `request_id` |
| D | no cookie, invalid `x-api-key` | 401 | `Authentication failed`, real `request_id` |
| E | cookie, no auth header (positive control) | **200** | — |

B is the only one with an OAuth-specific message and the only one with a null `request_id`. A, C and D all fall through to the same generic handler; B is rejected earlier, by something that parsed the header, recognised it as an OAuth access token and tried to validate it. A, C and D are the negative controls that make this a measurement rather than an inference from a single result, and E confirms the request was otherwise well-formed throughout.

`CloudOutcomes.OutcomeFor` turns that asymmetry into the difference between `TokenRefused` — our token was read and rejected, stop and tell the user to sign in — and `AuthFailed`, which means we presented nothing the endpoint recognised as a credential and is our bug, not theirs.

## Where the credential lives

| platform | store |
| --- | --- |
| macOS | login Keychain, generic password, service `Claude Code-credentials` |
| Windows, Linux | `~/.claude/.credentials.json` |

Note the file is **inside** the config root, where `UsageAccounts.AccountFilePath`'s `.claude.json` is a **sibling** of it. Two neighbouring files with different rules; reasoning from one to the other is a trap that has already cost this repository once.

The stored blob's shape, as parsed: `claudeAiOauth.accessToken` and `claudeAiOauth.expiresAt` (Unix milliseconds, with an ISO string accepted as a fallback). It also carries a refresh token, which this code deliberately does not read — `CredentialRead` has no field one could land in.

**The Keychain is read by P/Invoke into Security.framework, never by shelling out to `security find-generic-password`.** The macOS consent dialog names the calling binary. Going through the command-line tool would put `security` on that screen, and an "Always Allow" answered there would grant `/usr/bin/security` — every script on the machine — instead of Claude Buddy. CB-164 rejected minting our own OAuth token precisely because the consent screen would have named the wrong application; a helper binary is the same mistake with a broader blast radius.

## Token custody, and one honest limit

The rules the code is written to, stated so a later change can be checked against them:

- The access token is a local everywhere it appears — never a static, never a field, never cached, never in anything that outlives the request it authorises.
- `ParseCredentials` reads two fields and no others.
- Nothing token-derived reaches `Detail`, `Describe`, a status string, a log or the disk. A negative-control test feeds distinctive canary access and refresh token values through every outcome and asserts they appear in no wording anywhere.
- `Stamp()` returns a change detector — a file mtime in ticks, or a Keychain modification date — and never secret bytes. On macOS it is an attributes-only query, which returns no data and so is not the query the consent prompt guards; that is why it is a separate call from `Read()`.
- We never refresh. The CLI owns refresh; our copy goes stale and is re-read. Refreshing ourselves would need the refresh token and a client id, which is the option CB-164 rejected.

**The token is not zeroed and cannot be.** A .NET string is immutable and its bytes live wherever the GC last copied them until the heap is reused. This is recorded as a known limit rather than claimed away. Reducing it would mean carrying a `byte[]` or a `SecureString` through `HttpClient`, and neither survives the framework's own string copies.

## Not established

**That the Claude Code CLI's own stored token is accepted at this audience.** This is the load-bearing unknown and everything above leaves it open. A token minted for one audience can be structurally valid and refused by another, and that refusal reads *identically* to row B — `OAuth access token is invalid.` Telling "wrong audience" from "the placeholder was never a real token" needs the real token.

Two attempts to settle it during refinement were refused by a safety classifier and **not worked around**: reading the credential from the login Keychain, and a credential-free scheme probe. `tools/claude-cloud-probe` is the supported way to answer it, and it is a separate binary run by a human on purpose — on macOS it raises the Keychain consent prompt, and nobody should be asked to approve that on an agent's behalf.

```
dotnet run --project tools/claude-cloud-probe -- stamp
dotnet run --project tools/claude-cloud-probe -- read --keys-only
dotnet run --project tools/claude-cloud-probe -- list --shape
```

A 200 says the feature is buildable on this footing. A 401 carrying the OAuth-specific message says it is not, which is as real an answer and closes the question as firmly.

**The poll cadence.** `CloudRequest.UnmeasuredPollInterval` is a placeholder and its comment says so. CB-122 is the cautionary tale for this exact constant: a cadence defended by a comment asserting a fact about an external system that nobody had ever checked. Two things have to happen before it becomes a decision — somebody measures what the endpoint does under repeated calls, and somebody weighs `/v1/code/sessions/watch`, which would make polling the wrong shape entirely.

**Rate limits or terms on calling this endpoint from a third-party desktop client.** Not investigated. `Backoff` is written to be a well-behaved guest — a 60-second floor on any 429 whatever `Retry-After` says — but that is caution, not knowledge.

**Whether `kSecAttrAccount` needs constraining.** The query matches on service alone. The CLI appears to write one item under this service and the account name it uses was not measured, so pinning it would be an assumption that fails silently with `ItemNotFound` the day it is wrong.

## What this file does not cover

The roster parse, the filter rule deciding which cloud sessions become orbs, the `SessionManager` mapping and the orb itself. Those are the rest of CB-164 and are built on top of this layer. The privacy constraint from the ticket applies to them and not to anything here: an account's cloud sessions are not all work, and a title on an always-on-top overlay is a different act from a listing in a web UI somebody opened deliberately.
