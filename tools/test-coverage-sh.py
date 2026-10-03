#!/usr/bin/env python3
"""Runs tools/coverage.sh's own failing_names and run_suite functions in bash.

CB-239: coverage.sh piped every suite into `tail -2`, which keeps the summary
and throws away the lines that say *which* tests failed. It now keeps each
suite's whole log and reads the failing names back out of it. The functions
are cut out of the script text and run as they ship, so what is tested is the
shell in coverage.sh, not a copy of it.

Both runners are covered, because this repo has both: VSTest prints
`[xUnit.net ...] <name> [FAIL]` into the log (UnitTests, IntegrationTests),
and the Microsoft Testing Platform suites (UiTests, UiScreenshots) print no
per-test line under `dotnet test`, so their names come from the xUnit report.
So is CRLF, because the Windows box is where the names went missing.
"""
import os
import re
import shutil
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "coverage.sh")


def bash():
    """Git Bash on Windows, never C:\\Windows\\System32\\bash.exe — that one is WSL,
    a different filesystem, and the script is not meant to run there."""
    if os.name == "nt":
        for candidate in (r"C:\Program Files\Git\bin\bash.exe", r"C:\Program Files\Git\usr\bin\bash.exe"):
            if os.path.exists(candidate):
                return candidate
    found = shutil.which("bash")
    if found is None:
        raise unittest.SkipTest("no bash on this machine")
    return found


def function(text, name):
    """One `name() { ... }` function out of the script, up to its closing brace at
    the start of a line."""
    m = re.search(r"^%s\(\) \{.*?^\}" % re.escape(name), text, re.S | re.M)
    if m is None:
        raise AssertionError("coverage.sh has no %s() function" % name)
    return m.group(0)


with open(SCRIPT, encoding="utf-8") as f:
    _TEXT = f.read()
FUNCTIONS = function(_TEXT, "failing_names") + "\n" + function(_TEXT, "run_suite")


class CoverageShFunctions(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="cb-coverage-sh-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def sh(self, body):
        """Runs the two functions plus `body` with OUT pointed at the scratch dir."""
        script = "set -euo pipefail\nOUT=%s\nRED=()\nLOGS=()\n%s\n%s\n" % (
            self.posix(self.tmp), FUNCTIONS, body)
        path = os.path.join(self.tmp, "t.sh")
        with open(path, "w", newline="\n") as f:
            f.write(script)
        r = subprocess.run([bash(), self.posix(path)], capture_output=True)
        return r.returncode, r.stdout.decode("utf-8", "replace"), r.stderr.decode("utf-8", "replace")

    def posix(self, path):
        # Git Bash takes C:/... paths as they are; forward slashes avoid the
        # backslashes being read as escapes inside the generated script.
        return path.replace("\\", "/")

    def names_in(self, log_bytes):
        log = os.path.join(self.tmp, "suite.log")
        with open(log, "wb") as f:
            f.write(log_bytes)
        rc, out, err = self.sh('failing_names "%s"' % self.posix(log))
        self.assertEqual(0, rc, err)
        return out.splitlines()

    def test_vstest_fail_lines_give_the_test_names(self):
        log = (b"[xUnit.net 00:00:00.00] xUnit.net VSTest Adapter v2.8.2\n"
               b"[xUnit.net 00:00:04.59]     ClaudeBuddy.Tests.PersonaRealFileTests.APictureOneByteOverTheCap [FAIL]\n"
               b"  Failed ClaudeBuddy.Tests.PersonaRealFileTests.APictureOneByteOverTheCap [12 ms]\n"
               b"[xUnit.net 00:00:05.10]     ClaudeBuddy.Tests.X.ATheory(value: \"a b\") [FAIL]\n"
               b"Failed!  - Failed:     2, Passed:   799, Skipped:    14, Total:   815\n")
        self.assertEqual(["ClaudeBuddy.Tests.PersonaRealFileTests.APictureOneByteOverTheCap",
                          'ClaudeBuddy.Tests.X.ATheory(value: "a b")'], self.names_in(log))

    def report(self, xml):
        path = os.path.join(self.tmp, "ui.xunit.xml")
        with open(path, "w", encoding="utf-8") as f:
            f.write(xml)
        return path

    def names_from(self, *paths):
        rc, out, err = self.sh("failing_names " + " ".join('"%s"' % self.posix(p) for p in paths))
        self.assertEqual(0, rc, err)
        return out.splitlines()

    def test_an_mtp_suite_names_its_failures_from_the_xunit_report_theories_decoded(self):
        # `dotnet test` prints no per-test line for these suites, so the
        # report is the only place the names are. Shape copied from a real
        # report: id before name, result after it.
        path = self.report(
            '<assembly name="x"><collection>'
            '<test id="1" name="ClaudeBuddy.Tests.ZzForcedRed.ZzThis" result="Fail" time="0.007" type="T" method="M">'
            '<failure><message>forced</message></failure></test>'
            '<test id="2" name="ClaudeBuddy.Tests.Y.ATheory(value: &quot;a &amp; b&quot;)" result="Fail" time="0.1">'
            '</test>'
            '<test id="3" name="ClaudeBuddy.Tests.Y.Fine" result="Pass" time="0.1"></test>'
            '</collection></assembly>')
        self.assertEqual(['ClaudeBuddy.Tests.Y.ATheory(value: "a & b")',
                          "ClaudeBuddy.Tests.ZzForcedRed.ZzThis"], self.names_from(path))

    def test_a_green_report_and_a_missing_one_name_nothing(self):
        # The control for the case above, and the no-report case a suite that
        # died before its tests leaves: neither may invent a name, and neither
        # may stop the script (grep's "no match" is exit 1 under pipefail).
        path = self.report('<assembly><collection><test id="1" name="A.B" result="Pass"></test>'
                           '</collection></assembly>')
        self.assertEqual([], self.names_from(path, os.path.join(self.tmp, "absent.xunit.xml")))

    def test_crlf_output_from_windows_gives_the_same_names_without_carriage_returns(self):
        log = (b"[xUnit.net 00:00:04.59]     ClaudeBuddy.Tests.A.B [FAIL]\r\n"
               b"[xUnit.net 00:00:04.61]     ClaudeBuddy.Tests.C.D [FAIL]\r\n")
        self.assertEqual(["ClaudeBuddy.Tests.A.B", "ClaudeBuddy.Tests.C.D"], self.names_in(log))

    def test_a_green_log_names_nothing_not_even_the_summary_counts(self):
        # The control: summary lines say "failed" and "Failed" too, and must
        # not be read as a test called ": 0".
        log = (b"Passed!  - Failed:     0, Passed:  5899, Skipped:     0, Total:  5899\n"
               b"  Failed! - Failed: 1, Passed: 1673, Skipped: 0, Total: 1674\n")
        self.assertEqual([], self.names_in(log))

    def test_a_red_suite_is_recorded_with_its_log_and_only_two_lines_reach_the_terminal(self):
        rc, out, err = self.sh(
            "run_suite tests/Fake fake bash -c 'for i in 1 2 3 4 5; do echo line$i; done; echo oops >&2; exit 3'\n"
            "printf 'RED=%s\\n' \"${RED[*]}\"\n"
            "printf 'LOGS=%s\\n' \"${LOGS[*]}\"\n")
        self.assertEqual(0, rc, err)
        lines = out.splitlines()
        self.assertEqual(["line5", "oops"], lines[:2])
        self.assertEqual("RED=tests/Fake", lines[2])
        self.assertEqual("LOGS=tests/Fake|%s/fake.log" % self.posix(self.tmp), lines[3])
        with open(os.path.join(self.tmp, "fake.log")) as f:
            self.assertEqual("line1\nline2\nline3\nline4\nline5\noops\n", f.read())

    def test_a_green_suite_records_nothing_and_prints_the_same_two_lines(self):
        rc, out, err = self.sh(
            "run_suite tests/Fake fake bash -c 'echo one; echo two; echo three'\n"
            "printf 'RED=%s LOGS=%s\\n' \"${RED[*]-}\" \"${LOGS[*]-}\"\n")
        self.assertEqual(0, rc, err)
        self.assertEqual(["two", "three", "RED= LOGS="], out.splitlines())


if __name__ == "__main__":
    unittest.main()
