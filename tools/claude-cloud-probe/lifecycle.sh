#!/bin/bash
# CB-225 lifecycle gate: can the CLI's stored login archive and delete a cloud
# session from a desktop client? Aim it ONLY at a session created for the
# probe (`claude --cloud '<description>'`, interactive) — it deletes it.
# Same output rules as gate.sh: status codes, shapes and enum values only.
#
#   ./lifecycle.sh <session_id>
set -u
S="${1:?session id}"
P="$(dirname "$0")/bin/Debug/net10.0/osx-arm64/ClaudeCloudProbe"
run() { echo; echo "################ $*"; "$P" "$@" 2>&1; }

echo "== 1. before, and the router's own refusal as the control"
run v1-session "$S"
run route "$S" --throwaway                    # expect 404 text/plain
run archive "$S" --throwaway --auth bogus     # expect 401 OAuth
run delete "$S" --throwaway --auth none       # expect 401 Authentication failed

echo; echo "== 2. archive, twice, then what a write and a read see"
run archive "$S" --throwaway                  # expect 200, status archived
run archive "$S" --throwaway                  # idempotent?
run send "$S" --throwaway --text "CB-225 probe: sent after archive"   # expect 409
run v2-events "$S"

echo; echo "== 3. delete, then the handler's refusal on the gone id"
run delete "$S" --throwaway                   # expect 200
sleep 3
run v1-session "$S"                           # expect 404 not_found_error
run v2-events "$S"                            # expect SessionGone
run archive "$S" --throwaway --absent-id      # expect 404 JSON
run delete "$S" --throwaway --absent-id       # expect 404 JSON
run route "$S" --throwaway --absent-id        # expect 404 text/plain
