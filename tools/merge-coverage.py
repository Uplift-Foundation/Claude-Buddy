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

It also reports what has been held *out* of the number by
[ExcludeFromCodeCoverage]. That is not decoration. Both coverage engines honour
the attribute by omitting the code entirely, so an excluded file and a deleted
file look identical in a report, and a percentage can be walked to 100% by
excluding whatever refuses to be covered. The exclusions are read back out of
the sources and printed next to the number, so the number always ships with the
size of its own blind spot.
"""
import glob
import io
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


def instrumented_lines(reports, root):
    """file -> {line numbers} that these reports instrumented at all."""
    seen = defaultdict(set)

    for report in reports:
        tree = ET.parse(report).getroot()
        sources = [s.text for s in tree.findall("./sources/source") if s.text]
        for cls in tree.iter("class"):
            filename = cls.get("filename")
            if not filename:
                continue
            path = resolve(filename, sources, root)
            for line in cls.iter("line"):
                seen[path].add(int(line.get("number")))

    return seen


def resolve(filename, sources, root):
    path = filename.replace("\\", "/")
    if not os.path.isabs(path):
        for src in sources:
            if os.path.exists(os.path.join(src, path)):
                path = os.path.join(src, path)
                break
    return os.path.relpath(os.path.abspath(path), root)


def load(reports, root, keep=None):
    """file -> {line: covered}, file -> {line: (taken, total)}

    `keep`, when given, is the set of lines each file is allowed to contribute —
    see the comment in main() about the two engines disagreeing about
    [ExcludeFromCodeCoverage] on a *method*.
    """
    lines = defaultdict(dict)
    branches = defaultdict(dict)

    for report in reports:
        tree = ET.parse(report).getroot()
        sources = [s.text for s in tree.findall("./sources/source") if s.text]
        for cls in tree.iter("class"):
            filename = cls.get("filename")
            if not filename:
                continue
            path = resolve(filename, sources, root)
            allowed = None if keep is None else keep.get(path)

            for line in cls.iter("line"):
                number = int(line.get("number"))

                # A line this engine instrumented but the authority did not is a
                # line the authority excluded. Skipping it here is what makes a
                # member-level exclusion mean the same thing in both reports.
                if allowed is not None and number not in allowed:
                    continue

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
                # Max on BOTH halves, not just the numerator. The two engines do
                # not always agree on how many arcs a line has — the same `if`
                # can be reported as 2 arcs by one and 4 by the other — and
                # keeping the last-seen total while maxing the taken count can
                # pair a taken from the wider reading with a total from the
                # narrower one and print a line as fully covered when neither
                # suite covered it fully. Taking the widest denominator anyone
                # reported is the conservative reading, and being wrong in the
                # pessimistic direction is the only acceptable direction here.
                branches[path][number] = (
                    max(previous[0], int(taken)), max(previous[1], int(total)))

    return lines, branches


def exclusions(root):
    """path -> number of [ExcludeFromCodeCoverage] sites in it.

    Read out of the sources rather than out of the reports, because a coverage
    report cannot tell you this: both engines honour the attribute by *omitting*
    the code entirely, so an excluded member and a member that was deleted look
    identical from here. Counting the attribute is the only way to say how much
    of the app the headline number has stopped being about.
    """
    found = {}
    for name in sorted(os.listdir(root)):
        if not name.endswith(".cs"):
            continue
        with io.open(os.path.join(root, name), encoding="utf-8", errors="replace") as f:
            body = f.read()
        # Matches whether the attribute stands alone or shares its brackets with
        # others, and tolerates the fully-qualified spelling.
        sites = len(re.findall(
            r"\[(?:[^\]]*[\s,\[])?(?:System\.Diagnostics\.CodeAnalysis\.)?"
            r"ExcludeFromCodeCoverage(?:Attribute)?\b", body))
        if sites:
            found[name] = sites
    return found


def source_lines(root, path):
    """Physical lines in a file, for saying how big an exclusion is."""
    try:
        with io.open(os.path.join(root, path), encoding="utf-8", errors="replace") as f:
            return sum(1 for _ in f)
    except OSError:
        return 0


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

    # The two engines do not agree about [ExcludeFromCodeCoverage] on a *method*,
    # and the disagreement is silent. coverlet honours it — an excluded method's
    # body is not instrumented at all — while Microsoft.CodeCoverage, which the
    # two MTP suites use, instruments it anyway and reports every line unhit.
    # Both honour it on a *class*, which is why this went unnoticed until enough
    # member-level exclusions existed to matter: 157 sites, every one of which the
    # MTP reports were quietly putting back into the denominator.
    #
    # So coverlet's view of *which lines exist* is the authority, and the MTP
    # reports contribute hits for those lines only. Not the other way round, and
    # not a union: a union means the attribute does nothing wherever it is on a
    # method, which is exactly the kind of claim-that-is-not-true this script was
    # extended to stop making.
    #
    # Only applied to files coverlet reported. It instruments the assembly at
    # build time rather than on execution, so it reports every app file whether or
    # not a unit test touched it — but a file it genuinely never saw must not be
    # silently dropped from the MTP reports, so `keep` is consulted per file.
    coverlet = [r for r in reports if os.path.basename(r) == "coverage.cobertura.xml"]
    others = [r for r in reports if r not in coverlet]

    keep = instrumented_lines(coverlet, root) if coverlet and others else None

    lines, branches = load(coverlet, root)
    if others:
        more_lines, more_branches = load(others, root, keep)
        for path, hits in more_lines.items():
            for number, covered in hits.items():
                lines[path][number] = lines[path].get(number, False) or covered
        for path, arcs in more_branches.items():
            for number, (taken, total) in arcs.items():
                previous = branches[path].get(number, (0, total))
                branches[path][number] = (
                    max(previous[0], taken), max(previous[1], total))

    app = sorted(p for p in lines if is_app_file(p))

    print(f"merged {len(reports)} report(s)\n")

    hit, total, taken, arcs = totals(app, lines, branches)
    print(f"WHOLE APP   lines {hit}/{total} = {rate(hit, total)}"
          f"   branches {taken}/{arcs} = {rate(taken, arcs)}")
    print(f"            {len(app)} source files instrumented")

    # A file absent from every report is not 0% — it is missing from the
    # denominator, which inflates every number above it. But there are two very
    # different reasons a file can be absent, and lumping them together is how a
    # 100% headline gets to hide an assembly's worth of untested code:
    #
    #   * [ExcludeFromCodeCoverage] — a decision somebody made and can defend.
    #   * anything else — a suite that never loaded the type, a project missing
    #     from the run, a rename. A bug in the measurement, not a decision.
    #
    # Both leave the denominator, so the percentage cannot tell them apart. This
    # is the only place that can, and it reads the attribute out of the sources
    # to do it.
    on_disk = {p for p in os.listdir(root) if p.endswith(".cs")}
    excluded = exclusions(root)
    absent = on_disk - set(app)

    fully_excluded = sorted(p for p in absent if p in excluded)
    unexplained = sorted(p for p in absent if p not in excluded)
    partly_excluded = sorted(p for p in excluded if p in set(app))

    if fully_excluded:
        cost = sum(source_lines(root, p) for p in fully_excluded)
        print(f"            EXCLUDED: {len(fully_excluded)} file(s) held out of the "
              f"number above by [ExcludeFromCodeCoverage] ({cost} source lines)")
        for path in fully_excluded:
            print(f"                      {source_lines(root, path):5d} lines  {path}")

    if partly_excluded:
        sites = sum(excluded[p] for p in partly_excluded)
        print(f"            EXCLUDED: {sites} more [ExcludeFromCodeCoverage] site(s) "
              f"inside {len(partly_excluded)} measured file(s)")
        for path in partly_excluded:
            print(f"                      {excluded[path]:5d} site(s)  {path}")

    if unexplained:
        print(f"            WARNING: {len(unexplained)} app file(s) in no report and "
              f"not excluded: {', '.join(unexplained)}")

    if fully_excluded or partly_excluded:
        print("            Read the percentage as coverage OF WHAT REMAINS. An "
              "exclusion is a claim")
        print("            that a headless runner cannot execute the code, and it "
              "is only as good as")
        print("            the reviewer who checked it.")
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
