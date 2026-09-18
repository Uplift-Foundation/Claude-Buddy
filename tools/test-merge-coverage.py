#!/usr/bin/env python3
"""Tests for tools/merge-coverage.py's merge rules.

    python3 tools/test-merge-coverage.py

Plain stdlib `unittest`, no pytest and no virtualenv, because this is the only
Python in the repository that decides anything and adding a dependency to test
one script would cost more than the script. CI runs it as its own step for the
same reason every other suite is in CI: a merge rule that is wrong produces a
number, not a failure, and this repo has already spent hours three times on a
coverage figure that was fiction.

The seam under test is merge_secondary(), which is where coverlet's report is
made the authority over the Microsoft.CodeCoverage ones. Everything above it —
finding reports, parsing cobertura, resolving paths — is glue this deliberately
does not re-test; the arithmetic is what has been wrong.
"""
import importlib.util
import os
import unittest
from collections import defaultdict

_HERE = os.path.dirname(os.path.abspath(__file__))
_SPEC = importlib.util.spec_from_file_location(
    "merge_coverage", os.path.join(_HERE, "merge-coverage.py"))
merge_coverage = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(merge_coverage)


def merged(coverlet_lines, coverlet_branches, mtp_lines, mtp_branches, keep):
    """Run the merge over plain dicts and hand back what it produced."""
    lines = defaultdict(dict, {p: dict(v) for p, v in coverlet_lines.items()})
    branches = defaultdict(dict, {p: dict(v) for p, v in coverlet_branches.items()})
    merge_coverage.merge_secondary(
        lines, branches,
        defaultdict(dict, mtp_lines), defaultdict(dict, mtp_branches),
        keep)
    return lines, branches


class BranchAuthority(unittest.TestCase):
    """coverlet decides which branch points exist; MTP only fills in hits."""

    def test_mtp_cannot_invent_a_branch_point(self):
        # The real shape of CB-100: OpenClawSessions.cs 1628/1647 are lines
        # coverlet instrumented, hit, and recorded no branch on, while the ui
        # and shots reports — which never executed that parser — claim 0/6 and
        # 0/2 there. Eight arcs the code does not have, none of them takeable.
        keep = {"OpenClawSessions.cs": {1628, 1647}}
        _, branches = merged(
            {"OpenClawSessions.cs": {1628: True, 1647: True}},
            {},
            {"OpenClawSessions.cs": {1628: False, 1647: False}},
            {"OpenClawSessions.cs": {1628: (0, 6), 1647: (0, 2)}},
            keep)
        self.assertEqual({}, dict(branches["OpenClawSessions.cs"]))

    def test_mtp_still_contributes_hits_to_a_real_branch_point(self):
        # The whole reason the MTP reports are merged at all: a branch arm only
        # a UI test takes must still count.
        keep = {"OrbWindow.axaml.cs": {10}}
        _, branches = merged(
            {"OrbWindow.axaml.cs": {10: True}},
            {"OrbWindow.axaml.cs": {10: (1, 2)}},
            {"OrbWindow.axaml.cs": {10: True}},
            {"OrbWindow.axaml.cs": {10: (2, 2)}},
            keep)
        self.assertEqual({10: (2, 2)}, dict(branches["OrbWindow.axaml.cs"]))

    def test_widest_denominator_wins_for_a_real_branch_point(self):
        # The two engines do not always agree how many arcs one `if` has, and
        # pairing a taken from the wider reading with a total from the narrower
        # one prints a line as fully covered when neither suite covered it.
        keep = {"SessionManager.cs": {42}}
        _, branches = merged(
            {"SessionManager.cs": {42: True}},
            {"SessionManager.cs": {42: (2, 2)}},
            {"SessionManager.cs": {42: True}},
            {"SessionManager.cs": {42: (1, 4)}},
            keep)
        self.assertEqual({42: (2, 4)}, dict(branches["SessionManager.cs"]))

    def test_file_coverlet_never_reported_keeps_its_mtp_branches(self):
        # No authority to defer to, so the only engine that saw the file is
        # believed — the same per-file reasoning `keep` already uses for lines.
        keep = {"SessionManager.cs": {42}}
        _, branches = merged(
            {"SessionManager.cs": {42: True}},
            {"SessionManager.cs": {42: (2, 2)}},
            {"OnlyMtpSawThis.cs": {7: True}},
            {"OnlyMtpSawThis.cs": {7: (1, 2)}},
            keep)
        self.assertEqual({7: (1, 2)}, dict(branches["OnlyMtpSawThis.cs"]))

    def test_no_authority_at_all_keeps_everything(self):
        # keep is None when there was no coverlet report to be the authority.
        # Nothing may be dropped in that case.
        _, branches = merged(
            {}, {},
            {"Whatever.cs": {3: True}},
            {"Whatever.cs": {3: (1, 2)}},
            None)
        self.assertEqual({3: (1, 2)}, dict(branches["Whatever.cs"]))


class LineUnion(unittest.TestCase):
    """Lines stay a union of hits over whatever survived load()'s `keep`."""

    def test_mtp_hit_covers_a_line_coverlet_missed(self):
        lines, _ = merged(
            {"ClaudeDesktopBundles.cs": {90: False}}, {},
            {"ClaudeDesktopBundles.cs": {90: True}}, {},
            {"ClaudeDesktopBundles.cs": {90}})
        self.assertEqual({90: True}, dict(lines["ClaudeDesktopBundles.cs"]))

    def test_mtp_miss_does_not_uncover_a_line_coverlet_hit(self):
        lines, _ = merged(
            {"ClaudeDesktopBundles.cs": {90: True}}, {},
            {"ClaudeDesktopBundles.cs": {90: False}}, {},
            {"ClaudeDesktopBundles.cs": {90}})
        self.assertEqual({90: True}, dict(lines["ClaudeDesktopBundles.cs"]))


if __name__ == "__main__":
    unittest.main()
