#!/usr/bin/env python3
"""Tests for tools/check-brand.py.

    python3 tools/test-check-brand.py

Plain stdlib unittest, like the other tools/test-*.py. Every test builds a
throwaway git repository in a temp directory and runs the real script over it
with --root, so the `git ls-files` and byte-reading paths are the ones CI uses.
"""
import contextlib
import importlib.util
import io
import os
import shutil
import subprocess
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SPEC = importlib.util.spec_from_file_location(
    "check_brand", os.path.join(_HERE, "check-brand.py"))
check_brand = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(check_brand)

ALLOW = "tools/brand-allowlist.txt"


class Repo:
    """A temp git repo; write() stages bytes or text at a forward-slash path."""

    def __init__(self):
        self.root = tempfile.mkdtemp(prefix="brandcheck-")
        subprocess.run(["git", "init", "-q"], cwd=self.root, check=True)

    def write(self, path, content, track=True):
        full = os.path.join(self.root, *path.split("/"))
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "wb") as fh:
            fh.write(content if isinstance(content, bytes) else content.encode("utf-8"))
        if track:
            subprocess.run(["git", "add", "-f", path], cwd=self.root, check=True)

    def allow(self, *lines):
        self.write(ALLOW, "\n".join(lines) + "\n")

    def run(self, fn=check_brand.check):
        out = io.StringIO()
        code = fn(self.root, out)
        return code, out.getvalue()

    def cleanup(self):
        def force(func, path, _exc):
            os.chmod(path, 0o700)
            func(path)
        shutil.rmtree(self.root, onerror=force)


class BrandCheckTests(unittest.TestCase):
    def setUp(self):
        self.repo = Repo()
        self.addCleanup(self.repo.cleanup)

    def test_clean_tree_with_empty_allowlist_passes(self):
        self.repo.write("a.txt", "Orbweaver only\n")
        self.repo.allow("# nothing")
        self.assertEqual(self.repo.run()[0], 0)

    def test_missing_allowlist_is_a_hard_error(self):
        self.repo.write("a.txt", "Orbweaver\n")
        code, out = self.repo.run()
        self.assertEqual(code, 2)
        self.assertIn("missing", out)

    def test_unlisted_hit_fails_and_prints_path_line_text(self):
        self.repo.write("src/a.cs", "x\nvar n = \"Claude Buddy\";\n")
        self.repo.allow("# empty")
        code, out = self.repo.run()
        self.assertEqual(code, 1)
        self.assertIn("src/a.cs:2:var n = \"Claude Buddy\";", out)
        self.assertIn("UNLISTED", out)

    def test_all_spellings_match_case_insensitively(self):
        self.repo.write("a.txt", "Claude Buddy\nclaude-buddy\nCLAUDE_BUDDY\nClaudeBuddy\nclaudebuddy\n")
        self.repo.allow("a.txt\t5\tfive spellings")
        self.assertEqual(self.repo.run()[0], 0)

    def test_count_is_exact_too_many(self):
        self.repo.write("a.txt", "ClaudeBuddy\nClaudeBuddy\n")
        self.repo.allow("a.txt\t1\tr")
        code, out = self.repo.run()
        self.assertEqual(code, 1)
        self.assertIn("COUNT", out)
        self.assertIn("a.txt:2:ClaudeBuddy", out)

    def test_count_is_exact_too_few(self):
        self.repo.write("a.txt", "ClaudeBuddy\n")
        self.repo.allow("a.txt\t3\tr")
        code, out = self.repo.run()
        self.assertEqual(code, 1)
        self.assertIn("says 3", out)

    def test_count_matches_passes(self):
        self.repo.write("a.txt", "ClaudeBuddy\nother\nClaudeBuddy\n")
        self.repo.allow("a.txt\t2\tr")
        self.assertEqual(self.repo.run()[0], 0)

    def test_counts_lines_not_occurrences(self):
        self.repo.write("a.txt", "ClaudeBuddy and Claude Buddy\n")
        self.repo.allow("a.txt\t1\tr")
        self.assertEqual(self.repo.run()[0], 0)

    def test_stale_entry_with_zero_hits_fails(self):
        self.repo.write("a.txt", "Orbweaver\n")
        self.repo.allow("a.txt\t2\tr")
        code, out = self.repo.run()
        self.assertEqual(code, 1)
        self.assertIn("STALE", out)

    def test_entry_for_nonexistent_file_fails(self):
        self.repo.allow("ghost.txt\t1\tr")
        code, out = self.repo.run()
        self.assertEqual(code, 1)
        self.assertIn("MISSING FILE", out)
        self.assertIn("ghost.txt", out)

    def test_comments_and_blank_lines_are_ignored(self):
        self.repo.write("a.txt", "ClaudeBuddy\n")
        self.repo.allow("# header", "", "   ", "a.txt\t1\twhy", "# trailing")
        self.assertEqual(self.repo.run()[0], 0)

    def test_reason_may_contain_spaces_and_further_tabs(self):
        self.repo.write("a.txt", "ClaudeBuddy\n")
        self.repo.allow("a.txt\t1\tlegacy arm\twith a tab")
        self.assertEqual(self.repo.run()[0], 0)

    def test_malformed_lines_fail(self):
        self.repo.write("a.txt", "ClaudeBuddy\n")
        for bad in ("a.txt 1 spaces not tabs", "a.txt\tmany\tr", "a.txt\t1"):
            self.repo.allow(bad)
            code, out = self.repo.run()
            self.assertEqual(code, 1, bad)
            self.assertIn("brand-allowlist.txt:1", out)

    def test_duplicate_entry_fails(self):
        self.repo.write("a.txt", "ClaudeBuddy\n")
        self.repo.allow("a.txt\t1\tr", "a.txt\t1\tr")
        code, out = self.repo.run()
        self.assertEqual(code, 1)
        self.assertIn("duplicate", out)

    def test_crlf_files_count_the_same(self):
        self.repo.write("a.txt", b"ClaudeBuddy\r\nplain\r\nClaudeBuddy\r\n")
        self.repo.allow("a.txt\t2\tr")
        code, out = self.repo.run()
        self.assertEqual(code, 0, out)

    def test_crlf_not_in_printed_text(self):
        self.repo.write("a.txt", b"ClaudeBuddy\r\n")
        self.repo.allow("# empty")
        self.assertNotIn("\r", self.repo.run()[1])

    def test_invalid_utf8_does_not_crash(self):
        self.repo.write("a.txt", b"\xff\xfe ClaudeBuddy \xff\n")
        self.repo.allow("a.txt\t1\tr")
        self.assertEqual(self.repo.run()[0], 0)

    def test_paths_with_directories_use_forward_slashes(self):
        self.repo.write("tests/deep/x.cs", "ClaudeBuddy\n")
        self.repo.allow("tests/deep/x.cs\t1\tr")
        self.assertEqual(self.repo.run()[0], 0)

    def test_untracked_files_are_ignored(self):
        self.repo.write("a.txt", "ClaudeBuddy\n", track=False)
        self.repo.allow("# empty")
        self.assertEqual(self.repo.run()[0], 0)


class MaskingTests(unittest.TestCase):
    def setUp(self):
        self.repo = Repo()
        self.addCleanup(self.repo.cleanup)

    def check_masked(self, text):
        self.repo.write("a.txt", text + "\n")
        self.repo.allow("# empty")
        code, out = self.repo.run()
        self.assertEqual(code, 0, f"{text!r} should be masked: {out}")

    def test_permanent_tokens_are_masked(self):
        for tok in ("io.github.wtvamp.claudebuddy",
                    "id io.github.wtvamp.claudebuddy.app",
                    "claude-buddy-rc-host42",
                    "CLAUDEBUDDY_CHIME", "CLAUDEBUDDY_VOICE", "CLAUDEBUDDY_SPEECH_VOLUME",
                    "CLAUDEBUDDY_SPEAK_TEXT", "CLAUDEBUDDY_SPEAK_VOICE",
                    "https://github.com/Uplift-Foundation/Claude-Buddy/releases",
                    "git@github.com:wtvamp/Claude-Buddy.git"):
            with self.subTest(tok=tok):
                self.check_masked(tok)

    def test_other_claudebuddy_env_vars_are_not_masked(self):
        self.repo.write("a.txt", "CLAUDEBUDDY_OTHER\n")
        self.repo.allow("# empty")
        self.assertEqual(self.repo.run()[0], 1)

    def test_unmasked_hit_on_same_line_as_masked_token_still_counts(self):
        self.repo.write("a.txt", "io.github.wtvamp.claudebuddy and Claude Buddy\n")
        self.repo.allow("a.txt\t1\tr")
        self.assertEqual(self.repo.run()[0], 0)

    def test_mask_constant_is_labelled_and_nonempty(self):
        self.assertGreaterEqual(len(check_brand.MASK_PATTERNS), 5)


class ExclusionTests(unittest.TestCase):
    def setUp(self):
        self.repo = Repo()
        self.addCleanup(self.repo.cleanup)

    def test_excluded_paths_are_not_scanned(self):
        for path in (".github/release-notes/v1.md", "docs/phase3-findings.md",
                     ".claude/persona.md", "LICENSE",
                     "tools/check-brand.py", "tools/test-check-brand.py"):
            self.repo.write(path, "ClaudeBuddy\n")
        self.repo.allow("# ClaudeBuddy in the allow-list itself is fine")
        code, out = self.repo.run()
        self.assertEqual(code, 0, out)

    def test_exclusions_are_narrow(self):
        # Same names one level off: all must still be scanned.
        for path in (".github/workflows/ci.yml", "docs/guide.md",
                     "docs/sub/x-findings.md", "src/LICENSE", "tools/other.py"):
            self.repo.write(path, "ClaudeBuddy\n")
        self.repo.allow("# empty")
        code, out = self.repo.run()
        self.assertEqual(code, 1)
        for path in (".github/workflows/ci.yml", "docs/guide.md", "src/LICENSE", "tools/other.py"):
            self.assertIn(path + ":1:", out)

    def test_binary_files_are_skipped(self):
        self.repo.write("img.png", b"\x89PNG\0\0ClaudeBuddy\0")
        self.repo.allow("# empty")
        self.assertEqual(self.repo.run()[0], 0)


class ModeTests(unittest.TestCase):
    def setUp(self):
        self.repo = Repo()
        self.addCleanup(self.repo.cleanup)
        self.repo.write("a.cs", "ClaudeBuddy\nClaude Buddy\n")
        self.repo.write("b.cs", "claude_buddy\n")
        self.repo.write("c.cs", "Orbweaver\n")

    def test_residue_prints_only_unlisted_hits_and_exits_zero(self):
        self.repo.allow("a.cs\t2\tr")
        code, out = self.repo.run(check_brand.residue)
        self.assertEqual(code, 0)
        self.assertIn("b.cs:1:claude_buddy", out)
        self.assertNotIn("a.cs", out)
        self.assertIn("1 hit(s) in 1 file(s)", out)

    def test_residue_works_without_an_allowlist(self):
        code, out = self.repo.run(check_brand.residue)
        self.assertEqual(code, 0)
        self.assertIn("3 hit(s) in 2 file(s)", out)

    def test_suggest_lists_every_file_with_count_and_todo(self):
        code, out = self.repo.run(check_brand.suggest)
        self.assertEqual(code, 0)
        self.assertEqual(out.splitlines(), ["a.cs\t2\tTODO-reason", "b.cs\t1\tTODO-reason"])

    def test_suggest_output_round_trips_into_a_passing_check(self):
        _, suggested = self.repo.run(check_brand.suggest)
        self.repo.write(ALLOW, "# generated\n" + suggested)
        code, out = self.repo.run()
        self.assertEqual(code, 0, out)

    def test_main_runs_with_root_argument(self):
        self.repo.allow("# empty")
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(check_brand.main(["--root", self.repo.root]), 1)
            _, suggested = self.repo.run(check_brand.suggest)
            self.repo.write(ALLOW, suggested)
            self.assertEqual(check_brand.main(["--root", self.repo.root]), 0)


if __name__ == "__main__":
    unittest.main()
