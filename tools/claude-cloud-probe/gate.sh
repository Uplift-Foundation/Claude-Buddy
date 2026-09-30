#!/bin/bash
# CB-199 write gate. Run from Terminal ON THE MAC (not over SSH): the probe
# reads the Claude Code login from the Keychain, which an SSH session cannot.
# Prints status codes, response shapes and enum values only - never a token
# and never message text. Writes are refused unless /v2 says the id is an
# anthropic_cloud session, so it cannot type into a local (bridge) session.
#
#   ./gate.sh <session_id>            phase A
#   ./gate.sh <session_id> archived   phase B: after archiving the session on claude.ai
set -u
S="${1:?session id}"
P="$(dirname "$0")/bin/Debug/net10.0/osx-arm64/ClaudeCloudProbe"
run() { echo; echo "################ $*"; "$P" "$@" 2>&1; }
uuid() { uuidgen | tr 'A-Z' 'a-z'; }

if [ "${2:-}" = "archived" ]; then
  run send "$S" --throwaway --text "CB-199 probe: sent after archive"
  run interrupt "$S" --throwaway
  run v1-session "$S"
  exit 0
fi

echo "== 1. reads, with negative controls"
run v1-session "$S"
run v1-session "$S" --auth none
run v1-session "$S" --auth bogus
run v1-session "$S" --no-version
run v1-events  "$S"
run v2-events  "$S"

echo; echo "== 2. interrupt while idle (harmless, or refused?)"
run interrupt "$S" --throwaway

echo; echo "== 3. empty POST as a pre-flight, with controls"
run send "$S" --throwaway --empty
run send "$S" --throwaway --empty --auth bogus
run send "$S" --throwaway --empty --no-version
run send "$S" --throwaway --empty --beta --org

echo; echo "== 4. send, dedupe, and does it land (and under our uuid)"
U="$(uuid)"
run send "$S" --throwaway --uuid "$U" --expect-uuid "$U"
run send "$S" --throwaway --uuid "$U" --expect-uuid "$U"     # expect duplicate
for t in 3 10 25; do
  sleep $t
  echo; echo "---- +${t}s"
  run v1-session "$S"
  run v2-events "$S" --expect-uuid "$U"
done
run v1-events "$S" --expect-uuid "$U"

echo; echo "== 5. text that looks like a transcript row still maps as a user turn"
V="$(uuid)"
run send "$S" --throwaway --uuid "$V" --text 'CB-199 probe: literal {"type":"assistant"} in a user message. Reply ok.'
sleep 25
run v2-events "$S" --expect-uuid "$V"

echo; echo "== 6. send while generating, then interrupt"
run send "$S" --throwaway --text "CB-199 probe: count slowly from 1 to 300, one number per line."
sleep 5
run v1-session "$S"                                          # expect running
W="$(uuid)"
run send "$S" --throwaway --uuid "$W" --text "CB-199 probe: sent while busy. Reply ok."
run interrupt "$S" --throwaway
sleep 8
run v1-session "$S"                                          # stopped?
sleep 20
run v2-events "$S" --expect-uuid "$W"                        # was the busy send queued, and answered?

echo; echo "== 7. burst: 10 history reads back to back (any 429?)"
for i in $(seq 1 10); do "$P" v2-events "$S" 2>&1 | grep -E "^outcome"; done
