#!/usr/bin/env python3
"""Merge this repo's coverage reports into one honest number.

Why a script instead of a flag: `dotnet test tests/Tests.sln` runs three
projects on **two different test platforms**, and they emit coverage two
different ways (see tools/coverage.sh). That leaves three cobertura files
measuring the *same* ClaudeBuddy assembly, and no single number in any of them
is the truth — a line exercised only by a UI test is reported as unhit by the
unit-test run.

So the merge is a union: a line counts as covered if any suite covered it, and a
branch point takes the best taken-count any suite recorded. Summing the reports
instead would be wrong in both directions at once, double-counting the
denominator while undercounting the numerator.

Usage:
    tools/merge-coverage.py <report.xml> [more.xml ...] [--base <git-ref>]

With --base, also reports coverage restricted to the lines added since that ref
— which is usually the number you actually want when reviewing a change, since
a file-level percentage is dominated by whatever was already there.
"""
import glob
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def parse_args(argv):
    base = None
    patterns = []
    i = 0
    while i < len(argv):
        if argv[i] == "--base":
            base = argv[i + 1]
            i += 2
            continue
        patterns.append(argv[i])
        i += 1
    reports = [p for pat in patterns for p in glob.glob(pat, recursive=True)]
    return base, reports


def repo_root():
    return subprocess.run(["git", "rev-parse", "--show-toplevel"],
                          capture_output=True, text=True, check=True).stdout.strip()


def load(reports, root):
    """file -> {line: covered}, file -> {line: (taken, total)}"""
    lines = defaultdict(dict)
    branches = defaultdict(dict)

    for report in reports:
        tree = ET.parse(report).getroot()
        sources = [s.text for s in tree.findall("./sources/source") if s.text]
        for cls in tree.iter("class"):
            filename = cls.get("filename")
            if not filename:
                continue
            path = filename.replace("\\", "/")
            if not os.path.isabs(path):
                for src in sources:
                    if os.path.exists(os.path.join(src, path)):
                        path = os.path.join(src, path)
                        break
            path = os.path.relpath(os.path.abspath(path), root)

            for line in cls.iter("line"):
                number = int(line.get("number"))
                hits = int(line.get("hits", "0"))
                lines[path][number] = lines[path].get(number, False) or hits > 0

                # The attribute is "True"/"False" in these reports, not
                # "true"/"false". Comparing case-sensitively made an early
                # version of this script confidently print "0/0 branches".
                if (line.get("branch") or "").casefold() != "true":
                    continue
                condition = line.get("condition-coverage") or ""
                if "(" not in condition:
                    continue
                taken, total = condition.split("(")[1].rstrip(")").split("/")
                previous = branches[path].get(number, (0, int(total)))
                branches[path][number] = (max(previous[0], int(taken)), int(total))

    return lines, branches


def is_app_file(path):
    """The app's own sources only — not the suites, not generated code."""
    if path.startswith("tests/") or path.startswith("obj/") or "/obj/" in path:
        return False
    return path.endswith(".cs")


def rate(part, whole):
    return f"{100.0 * part / whole:.1f}%" if whole else "n/a"


def totals(paths, lines, branches):
    hit = total = taken = arcs = 0
    for path in paths:
        for covered in lines[path].values():
            total += 1
            hit += 1 if covered else 0
        for got, want in branches[path].values():
            taken += got
            arcs += want
    return hit, total, taken, arcs


def added_lines(base):
    diff = subprocess.run(["git", "diff", "-U0", f"{base}...HEAD", "--", "*.cs"],
                          capture_output=True, text=True, check=True).stdout
    added = defaultdict(set)
    current = None
    for row in diff.splitlines():
        if row.startswith("+++ b/"):
            current = row[6:]
        elif row.startswith("@@") and current:
            match = re.search(r"\+(\d+)(?:,(\d+))?", row)
            if match:
                start = int(match.group(1))
                for n in range(start, start + int(match.group(2) or "1")):
                    added[current].add(n)
    return added


def main():
    base, reports = parse_args(sys.argv[1:])
    if not reports:
        sys.exit("no cobertura reports matched — run tools/coverage.sh first")

    root = repo_root()
    lines, branches = load(reports, root)
    app = sorted(p for p in lines if is_app_file(p))

    print(f"merged {len(reports)} report(s)\n")

    hit, total, taken, arcs = totals(app, lines, branches)
    print(f"WHOLE APP   lines {hit}/{total} = {rate(hit, total)}"
          f"   branches {taken}/{arcs} = {rate(taken, arcs)}")
    print(f"            {len(app)} source files instrumented")

    # A file absent from every report is not 0% — it is missing from the
    # denominator, which inflates every number above it. Say so out loud.
    on_disk = {p for p in os.listdir(root) if p.endswith(".cs")}
    missing = sorted(on_disk - set(app))
    if missing:
        print(f"            WARNING: {len(missing)} app file(s) in no report: "
              f"{', '.join(missing)}")
    print()

    print("BY FILE (most-covered first)")
    for path in sorted(app, key=lambda p: -sum(1 for c in lines[p].values() if c)):
        hit, total, taken, arcs = totals([path], lines, branches)
        print(f"  {rate(hit, total):>6}  lines {hit:5d}/{total:<5d}"
              f"  branches {rate(taken, arcs):>6}  {path}")

    if not base:
        return

    print(f"\nRESTRICTED TO LINES ADDED SINCE {base}")
    added = added_lines(base)
    new_hit = new_total = new_taken = new_arcs = 0
    uncovered = []
    for path in sorted(added):
        if not is_app_file(path):
            continue
        instrumented = sorted(n for n in added[path] if n in lines.get(path, {}))
        if not instrumented:
            continue
        covered = [n for n in instrumented if lines[path][n]]
        missed = [n for n in instrumented if not lines[path][n]]
        new_hit += len(covered)
        new_total += len(instrumented)
        for number in instrumented:
            if number in branches.get(path, {}):
                got, want = branches[path][number]
                new_taken += got
                new_arcs += want
        print(f"  {path}: {len(added[path])} added, {len(instrumented)} instrumented,"
              f" {len(covered)} covered ({rate(len(covered), len(instrumented))})")
        if missed:
            uncovered.append((path, missed))

    print(f"\n  NEW CODE  lines {new_hit}/{new_total} = {rate(new_hit, new_total)}"
          f"   branches {new_taken}/{new_arcs} = {rate(new_taken, new_arcs)}")
    for path, missed in uncovered:
        print(f"  UNCOVERED {path}: {missed}")


if __name__ == "__main__":
    main()
