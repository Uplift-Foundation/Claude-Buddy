#!/usr/bin/env bash
#
# CB-119: refuses to diff a merge commit.
#
# `git show <merge-sha>` prints an EMPTY diff — a merge commit has no changes
# of its own against either parent by default — so a per-commit audit (the
# privacy scrub in CLAUDE.md's "Claims, and the checks that are worth their
# cost", or any other "did this commit introduce X" check) that runs `git
# show` straight against a merge SHA examines nothing and looks exactly like
# a clean pass. That trap has bitten three times in this project in one
# evening, including once against an agent that had just written the rule
# down. Use this wherever such an audit would otherwise call `git show`
# directly.
#
# Usage: tools/audit-diff.sh <commit> [git show args...]
set -euo pipefail

commit="${1:?usage: tools/audit-diff.sh <commit> [git show args...]}"
shift

# `git rev-list --parents -n 1 <sha>` prints "<sha> <parent1> <parent2> ...";
# a normal commit has one parent (two tokens total), a merge has two or more
# (four or more tokens for an octopus merge).
parent_count=$(( $(git rev-list --parents -n 1 "$commit" | wc -w) - 1 ))

if [ "$parent_count" -gt 1 ]; then
  echo "error: $commit is a merge commit ($parent_count parents) — its diff is empty; audit each side individually instead of this SHA" >&2
  exit 1
fi

exec git show "$commit" "$@"
