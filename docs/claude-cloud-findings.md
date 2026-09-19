# Claude Code cloud sessions — credential and API findings

Everything here was measured during CB-164 unless it says otherwise, against the API as it stood on **19 September 2026**, from a real Mac running Claude Code **2.1.278**. Where something is assumed rather than observed, it says so, and the "Not established" section at the end is the part worth reading first.

**This describes an undocumented API.** Nothing about it is supported and all of it can change without notice. CB-164 accepted that cost deliberately; this document exists so the next person can tell a shape change from a bug.

## The correction this document exists because of

**An earlier version of this file described the wrong host throughout, and every measurement in it was real.** It documented `GET https://claude.ai/v1/code/sessions` with a six-header set the endpoint supposedly 400s without, an auth asymmetry established with three negative controls, and a load-bearing open question about whether the CLI's own token would be accepted. The requests were made, the responses were recorded, the controls were run. The host was wrong, and being wrong about the host made every conclusion above it wrong too.

What claude.ai actually does to a non-browser client is answer with a Cloudflare challenge. That challenge is **identical for a real token and a bogus one**, which is precisely why it was expensive: it reads as an authentication failure, so an afternoon went into the credential and none into the host. The six headers were claude.ai's web client's, reproduced faithfully and required by nothing. The "is the CLI token accepted at this audience" question was unanswerable there by construction, because no credential of any kind gets a different answer from a challenge page.

Against `api.anthropic.com` the same credential returns 200 with two headers. The lesson is the one CLAUDE.md already writes down twice: **rigour inside a wrong frame produces confident error, and it arrives with receipts.** Every control was sound and about the wrong thing. When a conclusion says something is impossible — "this token is not accepted here" — the question to ask is not whether the evidence is sound but *what else knows about this*.

The second-order lesson is in `tests/UnitTests/ClaudeCloudRequestTests.cs`, which pinned the six headers **twice**: once off a built request and once against the constants, deliberately, so a refactor changing one would fail the other. Two independent copies of a wrong measurement are still one wrong measurement, and the doubling made it look doubly confirmed.

## The short version

1. There is **no supported client-side route**. `claude agents --json` returns local sessions only, the CLI control protocol has no cloud-session subtype, and the shipped CLI binary contains no listing endpoint.
2. `GET https://api.anthropic.com/v2/ccr-sessions` lists them, with the Claude Code CLI's own OAuth token and **two** headers.
3. **The roster is overwhelmingly not cloud sessions.** 573 of 578 rows are the user's own local sessions, registered for remote control. The filter is the feature.
4. **There is no server-side filtering.** Five different query parameters were sent and all five were *silently ignored*.
5. Per-session reads and a per-session transcript exist, and the transcript is Claude Code's own format.

## The endpoint

```
GET https://api.anthropic.com/v2/ccr-sessions
Authorization: Bearer <the Claude Code CLI's OAuth token>
anthropic-version: 2023-06-01
→ 200
```

**Those two headers, and nothing else.** `anthropic-beta`, `anthropic-client-feature`, `anthropic-client-platform` and `x-organization-uuid` are all unnecessary here; they were carried over from claude.ai. No cookie is ever sent, and no body — the empty `application/json` body the old builder attached was there to reproduce a browser's GET byte for byte against the wrong host.

Response envelope is `{ data, first_id, has_more, last_id }`, paged forwards with `?after_id=<last_id>`.

| checked | result |
| --- | --- |
| `limit=100` | 200 |
| `limit=200` | **400** — `must be greater than or equal to 0 and less than 101` |
| `environment_kind=anthropic_cloud` | 200, **unfiltered** — the parameter is ignored |
| `session_status=`, `status_bucket=`, `exclude_archived=`, `order=` | same: 200, ignored, no error |
| ordering | `created_at` DESC; **`updated_at` is unsorted** |
| one page | ≈0.6s |

**A silently-ignored query parameter is the worst kind of failure**, because it reads as working: the request succeeds, the page comes back, and only counting the rows tells you the filter did nothing. That is why `ClaudeCloudRoster.Keep` is client-side and why nothing in this arm puts a filter in a query string.

**`updated_at` being unsorted is the reason the fetch strategy has two halves.** A cloud session created last month that resumes today does not move to the front of a `created_at` DESC roster, so page one alone would show it frozen at whatever the last full walk saw.

## Per-session

The control is a nonsense subpath, which 404s — so the 200s below are real answers rather than a permissive router.

| path | result |
| --- | --- |
| `GET /v2/ccr-sessions/<id>` | 200 — a single session object, same row shape as a listing row |
| `GET /v2/ccr-sessions/<id>/events` | 200 — **Claude Code's own transcript format**, same `{data, has_more, last_id}` paging |
| `/messages`, `/history`, `/transcript`, `/turns`, `/conversation`, `/input`, `/state`, `/logs` | 404 |

The events rows carry `type` (`user`/`assistant`/`system`/`result`/`control_request`/…), a `message` with a `role` and content blocks of `text`/`thinking`/`tool_use`/`tool_result`, plus `usage`, `model`, `stop_reason`, `uuid` and `parent_tool_use_id`. That is what `ChatTranscript` already reads, so `ClaudeCloudEvents` parses the envelope and hands the rows to that parser rather than writing a second one — two parsers over one format is how a panel comes to show something a terminal does not.

**There is no input route, and that is why the panel shows no composer for a cloud session.** Every plausible write path 404s.

## The roster, measured across 578 rows

| `environment_kind` | rows | what they are |
| --- | --- | --- |
| `bridge` | 573 | the user's **own local sessions**, registered for remote control |
| `anthropic_cloud` | 5 | genuinely running in Anthropic's cloud; 4 archived, 1 live |

**Claude Buddy already draws an orb for every one of those 573 from its hooks.** Drawing them again would double every local orb on the screen, and the doubling would read as a layout bug rather than as a filter bug. So the filter is `environment_kind == "anthropic_cloud" && session_status != "archived"`, and `tests/IntegrationTests/ClaudeCloudRosterPayloadTests` walks a fixture in exactly those proportions to one orb, with the bridge rows as the negative control.

**Two fields are empty on all 578 rows:** `session_context.cwd` and `session_url`. So the link is built — `https://claude.ai/code/<id>` — which puts a payload-supplied string into a URL that on macOS reaches `open`. `ClaudeCloudRoster.UrlFor` therefore checks the id against an allow-list of the characters an id is made of, plus the `session_` prefix, before it can get that far. An allow-list rather than a deny-list, because a deny-list is a promise about every character somebody might one day put in a payload.

Fields worth reading: `id`, `title`, `session_status`, `status_bucket`, `updated_at`, `external_metadata.context_usage {max_tokens, used_tokens}`, `external_metadata.model`, and `external_metadata.post_turn_summary {status_detail, recent_action, needs_action, …}`.

**`post_turn_summary` is a dict on cloud rows and `null` on bridge rows.** That asymmetry is ordinary rather than a shape change, and it is the field most likely to trip a parser that assumes a shape from one example.

`LastActivity` on the orb row is the session's own `updated_at`, never the time of the read, and an unparseable one becomes `MinValue` rather than now — stamping now would give every session the account has ever had a permanent orb, because the recency window would be measuring a time this app invented.

## The auth measurement

| sent | status | body |
| --- | --- | --- |
| no `Authorization` header | 401 | `Authentication failed` |
| well-formed but bogus `Bearer` | 401 | `OAuth access token is invalid.` |
| **the real CLI token** | **200** | the roster |
| the real CLI token, at `/v1/organizations/me` | 403 | — |

The first two are the negative controls; the fourth is the one that makes the third mean something, since a token that is refused *somewhere* is demonstrably a real token being scoped rather than a string that happens to be accepted everywhere.

`CloudOutcomes.OutcomeFor` turns the 401 asymmetry into the difference between `TokenRefused` — our token was read and rejected, stop and tell the user to sign in — and `AuthFailed`, which means we presented nothing recognisable as a credential and is our bug rather than theirs.

### The 403, and the detail that used to be a misdiagnosis

`OutcomeFor`'s 403 arm used to say **"this account may not list cloud sessions"** for every 403. That is a confident claim about an account's permissions, and what had actually produced it was the Cloudflare challenge in front of claude.ai — which says nothing whatsoever about the account. A wrong explanation that reads as a finding is worse than no explanation, because it ends the inquiry.

`CloudOutcomes.LooksLikeEdgeBlock` now separates the two. Three signals, any one of which is enough: a `cf-mitigated` response header, a body that is not a JSON object, or a JSON error carrying no `request_id`. It is deliberately biased towards "edge", because the failure being guarded against is asserting something about the account on evidence that is about the network path, and the reverse mistake misleads nobody into a wrong fix.

## The fetch strategy

Two cycles, and both intervals are placeholders that say so.

- **The deep walk** pages the whole roster, following `last_id` until `has_more` is false, capped at `CloudRequest.MaxPagesPerWalk` (10 — the measured roster is 6 pages, so this is headroom). When the cap is hit, what was found is kept, the reduction is marked truncated, and `Describe` says so in the status text. **A cap that silently showed fewer sessions would be the same class of bug as the filter getting it wrong, and much harder to notice.**
- **The short cycle** reads page one plus one `GET /v2/ccr-sessions/<id>` per cloud session already known. This is what stops an older cloud session resuming from being invisible until the next walk, and it exists because `updated_at` is unsorted.

A session the short cycle never asked about survives untouched; one it got a definitive answer about and which is no longer kept has genuinely ended and loses its orb. A per-session read that merely *failed* is treated as **no news** rather than as a death — including a 404, since "ended" rather than "briefly unreachable" is not something anybody has measured — and the next deep walk is the authority that will drop it.

**A mid-walk failure never publishes an empty snapshot over a good one.** `StepResult.Snapshot` is nullable and null means "leave what is on screen alone": orbs vanishing because a request timed out would read as sessions having ended, which is a lie the app would be telling on the strength of a network hiccup. A *definite* refusal — the token was rejected, we are blocked — does clear them, because that is a real statement that there is no access.

## Where the credential lives

| platform | store |
| --- | --- |
| macOS | login Keychain, generic password, service `Claude Code-credentials` |
| Windows, Linux | `~/.claude/.credentials.json` |

Note the file is **inside** the config root, where `UsageAccounts.AccountFilePath`'s `.claude.json` is a **sibling** of it. Two neighbouring files with different rules; reasoning from one to the other is a trap that has already cost this repository once.

The stored blob's shape, as parsed: `claudeAiOauth.accessToken` and `claudeAiOauth.expiresAt` (Unix milliseconds, with an ISO string accepted as a fallback). It also carries a refresh token, which this code deliberately does not read — `CredentialRead` has no field one could land in.

`ClaudeCliCredentials.OrganizationUuidFrom` survives, but **nothing sends what it returns**. It was there because `x-organization-uuid` was believed mandatory; it is diagnostic now, and `tools/claude-cloud-probe` prints only whether one was found.

**The Keychain is read by P/Invoke into Security.framework, never by shelling out to `security find-generic-password`.** The macOS consent dialog names the calling binary. Going through the command-line tool would put `security` on that screen, and an "Always Allow" answered there would grant `/usr/bin/security` — every script on the machine — instead of Claude Buddy. CB-164 rejected minting our own OAuth token precisely because the consent screen would have named the wrong application; a helper binary is the same mistake with a broader blast radius.

## Token custody, and one honest limit

The rules the code is written to, stated so a later change can be checked against them:

- The access token is a local everywhere it appears — never a static, never a field, never cached, never in anything that outlives the request it authorises.
- `ParseCredentials` reads two fields and no others.
- Nothing token-derived reaches `Detail`, `Describe`, a status string, a log or the disk. Negative-control tests feed a distinctive canary token through every outcome, every status string the arm produces, and the largest payload fixture in the repository, and assert it appears nowhere.
- `Stamp()` returns a change detector — a file mtime in ticks, or a Keychain modification date — and never secret bytes. On macOS it is an attributes-only query, which returns no data and so is not the query the consent prompt guards; that is why it is a separate call from `Read()`.
- **The credential is re-read only when `Stamp()` moves**, or when the user acts. `Denied`, `NotLoggedIn` and `TokenRefused` halt the arm until then, and a halted arm sends no request and reads no secret — it calls `Stamp()` and nothing else. That is what keeps "never a repeated OS prompt" true, and it is why the halt is a distinct state rather than a very long backoff: a backoff that merely got long would still eventually prompt again.
- We never refresh. The CLI owns refresh; our copy goes stale and is re-read. Refreshing ourselves would need the refresh token and a client id, which is the option CB-164 rejected.

**The token is not zeroed and cannot be.** A .NET string is immutable and its bytes live wherever the GC last copied them until the heap is reused. This is recorded as a known limit rather than claimed away. Reducing it would mean carrying a `byte[]` or a `SecureString` through `HttpClient`, and neither survives the framework's own string copies.

## The probe

```
dotnet run --project tools/claude-cloud-probe -- stamp
dotnet run --project tools/claude-cloud-probe -- read --keys-only
dotnet run --project tools/claude-cloud-probe -- list --shape
dotnet run --project tools/claude-cloud-probe -- roster
```

It references the app rather than building its own request, so its answer is the app's answer rather than a second opinion. `stamp` prompts for nothing. `read` prints a length and a four-character prefix and has no mode that prints a token. `list --shape` prints field names and JSON types with every value stripped, so a fixture can be designed without a single session title. `roster` prints the `environment_kind` histogram and the status sentence — **the cheapest available check that the filter still matches something**, and much cheaper than diagnosing it from a screenshot of missing orbs.

On macOS `read`, `list` and `roster` raise a Keychain consent prompt naming *this binary*, which is a separate unsigned executable — expected, and not the shipped app's grant. It is run by a human on purpose.

## Not established

**The two cadences.** `CloudRequest.UnmeasuredWalkInterval` and `UnmeasuredFirstPageInterval` are placeholders and their comments say so. CB-122 is the cautionary tale for exactly this kind of constant: a cadence defended by a comment asserting a fact about an external system that nobody had ever checked, which then ruled out the cheapest explanation for a real bug for hours. Three questions would settle them, and none has been asked:

- **Does `updated_at` advance during a running turn**, or only when a turn ends? If only at the end, a fast cadence buys nothing; if it advances continuously, the cadence is what decides whether a generating orb looks alive.
- **Does this endpoint 429 anybody at this rate over a whole day?** One page is ≈0.6s and the roster is six pages, so a walk is a handful of seconds — but rate limits are not something to infer from one afternoon's probing.
- **How often is a cloud session first seen only on a deep page?** That is the entire argument for the walk existing beside the first-page poll, and nobody has counted it.

**The page caps.** `MaxPagesPerWalk = 10` and `ClaudeCloudChatSession.MaxEventPages = 10` are round numbers with headroom over the one roster anybody has seen. Neither is measured. What *is* deliberate is that hitting the roster cap is visible in the status text rather than silent.

**The history cap.** `ClaudeCloudChatSession.HistoryTurns = 200`. Nobody has counted how long a cloud session's transcript runs.

**Rate limits or terms on calling this endpoint from a third-party desktop client.** Not investigated. `Backoff` is written to be a well-behaved guest — a 60-second floor on any 429 whatever `Retry-After` says — but that is caution, not knowledge.

**Whether a 404 on a per-session read means the session ended.** Treated as "no news" and left for the next deep walk to resolve, because the alternative would drop an orb on one unlucky request.

**Whether `kSecAttrAccount` needs constraining.** The query matches on service alone. The CLI appears to write one item under this service and the account name it uses was not measured, so pinning it would be an assumption that fails silently with `ItemNotFound` the day it is wrong.

**Whether `/v2/ccr-sessions` has a watch or stream form.** claude.ai had one; this host was not probed for an equivalent. If one exists it would make polling the wrong shape entirely, and it is the first thing to look for before tuning either interval above.

## What this file does not cover

The `SessionManager` mapping and the orb itself — the UI half of CB-164. The privacy constraint from the ticket applies there and not here: an account's cloud sessions are not all work, and a title on an always-on-top overlay is a different act from a listing in a web UI somebody opened deliberately.
