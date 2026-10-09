#!/usr/bin/env python3
"""CI brand check: no unrecorded "Claude Buddy" left in the tree (CB-256, phase 3).

    python tools/check-brand.py             # the CI check; exit 1 on any violation
    python tools/check-brand.py --residue   # print every non-allow-listed hit (the worklist)
    python tools/check-brand.py --suggest   # print allow-list lines for the tree as it is now
    python tools/check-brand.py --root DIR  # check another repository (the tests use this)

What it does: scans every tracked text file (`git ls-files`) for the pattern
`(?i)claude[ _-]?buddy` after masking the permanent tokens below, and compares
the per-file number of matching LINES with tools/brand-allowlist.txt.

Allow-list format, one entry per line, TAB separated, `/` in paths on every OS:

    path<TAB>count<TAB>reason

`#` starts a comment line; blank lines are ignored.

WHY THE COUNTS ARE EXACT, NOT A MAXIMUM. CLAUDE.md's argument about numbers in
prose is that a number nobody re-derives rots silently. A ceiling rots the same
way: when a file drops from 9 hits to 3, a "max 9" list quietly lets six new
ones in later. An exact count makes drift in EITHER direction a failing build,
so every change to the legacy surface is a decision somebody wrote down in the
allow-list diff, with a reason, reviewed like any other line.

It fails (exit 1) on:
  * a hit in a file with no allow-list entry;
  * a hit count that differs from the entry;
  * an entry whose file has zero hits (stale entries rot the list);
  * an entry naming a file that does not exist.
A MISSING allow-list file is a hard error too (exit 2), so it cannot be forgotten.
Every offending line is printed as path:line:text, followed by a summary.

PERMANENT TOKENS (MASK_PATTERNS below) are blanked before matching because they
are never a decision again: the macOS bundle id, the relay prefix, the five
shipped CLAUDEBUDDY_* environment variables, and the two repository URL forms
(the GitHub repository is not renamed by phase 3).

Excluded from scanning: .github/release-notes/, docs/*-findings.md, .claude/,
LICENSE, binary files, and the script, its test and the allow-list themselves.

Reads bytes, decodes UTF-8 with errors='replace', splits on LF and strips a
trailing CR, so CRLF and LF checkouts count the same. Never writes anything.
Standard library only; runs as plain `python` / `python3` on Windows and macOS.
"""
import argparse
import fnmatch
import os
import re
import subprocess
import sys

BRAND_PATTERN = re.compile(r"(?i)claude[ _-]?buddy")

# Permanent tokens: blanked out before BRAND_PATTERN runs. Add here, with a
# reason, only for a string that can never be renamed; everything else belongs
# in the allow-list with a count.
MASK_PATTERNS = [
    re.compile(r"(?i)io\.github\.wtvamp\.claudebuddy"),            # macOS bundle id / LaunchAgent label
    re.compile(r"claude-buddy-rc-"),                               # MachineNames.RelayPrefix
    re.compile(r"CLAUDEBUDDY_(?:CHIME|VOICE|SPEECH_VOLUME|SPEAK_TEXT|SPEAK_VOICE)"),  # shipped env vars
    re.compile(r"(?i)Uplift-Foundation/Claude-Buddy"),             # repo URL (repo not renamed)
    re.compile(r"(?i)wtvamp/Claude-Buddy"),                        # repo URL (repo not renamed)
]

# Directory/glob exclusions, matched against the forward-slash repo path.
EXCLUDE_PREFIXES = (".github/release-notes/", ".claude/")
EXCLUDE_GLOBS = ("docs/*-findings.md",)
EXCLUDE_FILES = ("LICENSE",)

SELF_FILES = (
    "tools/check-brand.py",
    "tools/test-check-brand.py",
    "tools/brand-allowlist.txt",
)

ALLOWLIST_PATH = "tools/brand-allowlist.txt"


def is_excluded(path):
    return (path in EXCLUDE_FILES or path in SELF_FILES
            or path.startswith(EXCLUDE_PREFIXES)
            or any(fnmatch.fnmatchcase(path, g) for g in EXCLUDE_GLOBS))


def tracked_files(root):
    out = subprocess.run(["git", "ls-files", "-z"], cwd=root, check=True,
                         stdout=subprocess.PIPE).stdout
    return [p.decode("utf-8", "replace").replace("\\", "/")
            for p in out.split(b"\0") if p]


def mask(text):
    for pat in MASK_PATTERNS:
        text = pat.sub("", text)
    return text


def scan_file(root, path):
    """Return [(line_number, original_line)] for lines that still match after masking."""
    full = os.path.join(root, path)
    if os.path.islink(full) or not os.path.isfile(full):
        return []
    with open(full, "rb") as fh:
        data = fh.read()
    if b"\0" in data[:8192]:
        return []
    hits = []
    text = data.decode("utf-8", errors="replace")
    for n, line in enumerate(text.split("\n"), 1):
        line = line.rstrip("\r")
        if BRAND_PATTERN.search(mask(line)):
            hits.append((n, line))
    return hits


def scan_tree(root):
    found = {}
    for path in tracked_files(root):
        if is_excluded(path):
            continue
        hits = scan_file(root, path)
        if hits:
            found[path] = hits
    return found


def parse_allowlist(root):
    """Return ({path: (count, reason)}, [problems]). Raises FileNotFoundError if absent."""
    entries, problems = {}, []
    with open(os.path.join(root, ALLOWLIST_PATH), "rb") as fh:
        text = fh.read().decode("utf-8", errors="replace")
    for n, raw in enumerate(text.split("\n"), 1):
        line = raw.rstrip("\r")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        parts = line.split("\t", 2)
        if len(parts) != 3 or not parts[2].strip():
            problems.append(f"{ALLOWLIST_PATH}:{n}: expected path<TAB>count<TAB>reason: {line}")
            continue
        path, count, reason = parts[0].strip(), parts[1].strip(), parts[2].strip()
        if not count.isdigit():
            problems.append(f"{ALLOWLIST_PATH}:{n}: count is not a whole number: {line}")
            continue
        if path in entries:
            problems.append(f"{ALLOWLIST_PATH}:{n}: duplicate entry for {path}")
            continue
        entries[path] = (int(count), reason)
    return entries, problems


def check(root, out):
    """Run the check, writing to `out`. Returns the exit code."""
    try:
        entries, problems = parse_allowlist(root)
    except FileNotFoundError:
        out.write(f"ERROR: {ALLOWLIST_PATH} is missing. It is generated last "
                  "(python tools/check-brand.py --suggest) and must exist.\n")
        return 2
    found = scan_tree(root)
    failures = list(problems)

    def dump(path):
        return [f"  {path}:{n}:{text}" for n, text in found.get(path, [])]

    for path in sorted(found):
        if path not in entries:
            failures.append(f"UNLISTED: {path} has {len(found[path])} hit(s) and no allow-list entry")
            failures.extend(dump(path))
    for path in sorted(entries):
        count = entries[path][0]
        actual = len(found.get(path, []))
        if not os.path.isfile(os.path.join(root, path)):
            failures.append(f"MISSING FILE: allow-list entry {path} names a file that does not exist")
        elif actual == 0:
            failures.append(f"STALE: {path} is allow-listed for {count} but has 0 hits; remove the entry")
        elif actual != count:
            failures.append(f"COUNT: {path} has {actual} hit(s), allow-list says {count}")
            failures.extend(dump(path))
    if failures:
        out.write("\n".join(failures) + "\n")
        out.write(f"\nBrand check FAILED: {len(failures)} problem line(s) "
                  f"({len(found)} file(s) with hits, {len(entries)} allow-list entries).\n")
        return 1
    out.write(f"Brand check passed: {len(found)} file(s) with hits, all allow-listed with exact counts.\n")
    return 0


def residue(root, out):
    try:
        entries, _ = parse_allowlist(root)
    except FileNotFoundError:
        entries = {}
    found = scan_tree(root)
    files = hits = 0
    for path in sorted(found):
        if path in entries:
            continue
        files += 1
        hits += len(found[path])
        for n, text in found[path]:
            out.write(f"{path}:{n}:{text}\n")
    out.write(f"\n{hits} hit(s) in {files} file(s) not allow-listed.\n")
    return 0


def suggest(root, out):
    for path, hits in sorted(scan_tree(root).items()):
        out.write(f"{path}\t{len(hits)}\tTODO-reason\n")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--residue", action="store_true", help="print every non-allow-listed hit")
    mode.add_argument("--suggest", action="store_true", help="print allow-list lines for the current tree")
    ap.add_argument("--root", default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                    help="repository root (default: the repo this script lives in)")
    args = ap.parse_args(argv)
    # Source lines carry arrows and quotes a Windows cp1252 console cannot
    # encode; a crash while printing the evidence would hide the failure.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if args.residue:
        return residue(args.root, sys.stdout)
    if args.suggest:
        return suggest(args.root, sys.stdout)
    return check(args.root, sys.stdout)


if __name__ == "__main__":
    sys.exit(main())
