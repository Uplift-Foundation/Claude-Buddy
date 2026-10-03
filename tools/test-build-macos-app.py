#!/usr/bin/env python3
"""Runs the tail of tools/build-macos-app.sh's --install block as it ships.

That tail reports how many installed Buddies are running afterwards — none
("launch it with"), one ("Running: pid N"), or more (a warning). It used to
exit 1 instead, straight after a successful install, whenever no Buddy was
running: `running_installed` failed inside `$(...)` under `set -euo pipefail`
(pgrep exits 1 when it finds nothing), so the assignment ended the script
before the report. Found installing develop at 0e09981d; fixed under CB-245.

The text from `running_installed() {` to the `esac` that ends the report is
cut out of the script and run under the script's own shell options, with
stub `pgrep` and `ps` on PATH so the number of running copies is chosen by
the test rather than by whatever happens to be running on the machine — a
developer's Mac usually has a real Buddy up. STOPPED is empty, so the
relaunch loop between the two is skipped exactly as on a first install, and
nothing is ever launched.

Set BUILD_SCRIPT_UNDER_TEST to run it against another copy of the script.
"""
import os
import re
import shutil
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.environ.get("BUILD_SCRIPT_UNDER_TEST", os.path.join(HERE, "build-macos-app.sh"))
INSTALLED = "/Applications/Claude Buddy.app/Contents/MacOS/ClaudeBuddy"


def tail(text):
    """The script's install tail, as it would run: the helper functions it
    calls, wherever the script defines them, then the text from the CB-206
    "one Buddy" comment to the esac closing the 0/1/many report."""
    m = re.search(r"^  # CB-206: one Buddy, running the new binary.*?^  esac$", text, re.S | re.M)
    if m is None:
        raise AssertionError("build-macos-app.sh has no CB-206 install tail")
    body = m.group(0)
    prelude = ""
    if "running_installed() {" not in body:
        f = re.search(r"^  running_installed\(\) \{.*?(?:^  \}$|done; \}$)", text, re.S | re.M)
        if f is None:
            raise AssertionError("build-macos-app.sh has no running_installed")
        prelude += f.group(0) + "\n"
    k = re.search(r"^  keepalive_loaded\(\) \{.*?\}$", text, re.M)
    if k is not None:
        prelude += k.group(0) + "\n"
    return prelude + body


def bash():
    if os.name == "nt":
        for candidate in (r"C:\Program Files\Git\bin\bash.exe", r"C:\Program Files\Git\usr\bin\bash.exe"):
            if os.path.exists(candidate):
                return candidate
    found = shutil.which("bash")
    if found is None:
        raise unittest.SkipTest("no bash on this machine")
    return found


with open(SCRIPT, encoding="utf-8") as f:
    TAIL = tail(f.read())


class InstallTail(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="cb-install-tail-")
        self.bin = os.path.join(self.tmp, "bin")
        os.makedirs(self.bin)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def posix(self, path):
        return path.replace("\\", "/")

    def stub(self, name, body):
        path = os.path.join(self.bin, name)
        with open(path, "w", newline="\n") as f:
            f.write("#!/bin/sh\n" + body)
        os.chmod(path, 0o755)

    def run_tail(self, processes, appear_on_call=1, stopped="", was_running="", keepalive=False):
        """processes: {pid: executable path}, visible to pgrep only from its
        `appear_on_call`-th call onwards — how launchd's restart looks from the
        script, counted rather than timed so the case cannot pass or fail on
        how fast the machine is. pgrep behaves like the real one: prints
        nothing and exits 1 while there is nothing. `open` is stubbed to record
        a relaunch, so nothing is ever launched; launchctl answers `list` per
        `keepalive`."""
        calls = os.path.join(self.tmp, "pgrep-calls")
        listing = "".join("echo %d\n" % pid for pid in processes)
        self.stub("pgrep", ('n=$(( $(cat "%s" 2>/dev/null || echo 0) + 1 )); echo $n > "%s"\n'
                            '[ $n -ge %d ] || exit 1\n%s%s')
                  % (self.posix(calls), self.posix(calls), appear_on_call, listing,
                     "" if processes else "exit 1\n"))
        cases = "".join('  %d) echo "%s" ;;\n' % (pid, exe) for pid, exe in processes.items())
        self.stub("ps", 'pid=""\nwhile [ $# -gt 0 ]; do [ "$1" = "-p" ] && pid="$2"; shift; done\n'
                        'case "$pid" in\n%s  *) exit 1 ;;\nesac\n' % cases)
        self.stub("launchctl", "exit %d\n" % (0 if keepalive else 1))
        self.opened = os.path.join(self.tmp, "opened")
        self.stub("open", 'echo "$*" >> "%s"\n' % self.posix(self.opened))
        script = ("set -euo pipefail\nPATH=%s:$PATH\nSTOPPED=\"%s\"\nWAS_RUNNING=\"%s\"\n"
                  "APP_NAME=\"Claude Buddy\"\nBUNDLE_ID=\"io.github.wtvamp.claudebuddy\"\n"
                  "INSTALLED_EXE=\"%s\"\n%s\necho TAIL-COMPLETED\n") % (
                      self.posix(self.bin), stopped, was_running, INSTALLED, TAIL)
        path = os.path.join(self.tmp, "tail.sh")
        with open(path, "w", newline="\n") as f:
            f.write(script)
        r = subprocess.run([bash(), self.posix(path)], capture_output=True, text=True)
        return r.returncode, r.stdout, r.stderr

    def relaunched(self):
        return os.path.exists(self.opened)

    def test_with_nothing_running_it_says_how_to_launch_rather_than_failing(self):
        rc, out, err = self.run_tail({})
        self.assertEqual(0, rc, "the install tail failed with no Buddy running:\n" + out + err)
        self.assertIn('Launch it with: open -a "Claude Buddy"', out)
        self.assertIn("TAIL-COMPLETED", out)

    def test_a_copy_running_from_another_path_does_not_count_and_does_not_fail(self):
        # Also the arm that returned 1 on its own: the loop's last test was
        # false, so the function's status was the failed [[ ]].
        rc, out, err = self.run_tail({4242: "/Users/dev/Claude Buddy dev.app/Contents/MacOS/ClaudeBuddy"})
        self.assertEqual(0, rc, out + err)
        self.assertIn("Launch it with", out)

    def test_one_installed_copy_is_reported_by_pid(self):
        rc, out, err = self.run_tail({4242: INSTALLED}, stopped="4242", was_running="4242")
        self.assertEqual(0, rc, out + err)
        self.assertIn("==> Running: pid 4242", out)

    def test_two_installed_copies_are_warned_about(self):
        rc, out, err = self.run_tail({4242: INSTALLED, 4343: INSTALLED})
        self.assertEqual(0, rc, out + err)
        self.assertIn("more than one Claude Buddy is running: 4242 4343", err)

    # The lead's install, exactly: on a machine opted in to the keep-alive,
    # unloading it is what stopped the old Buddy, so stop-installed-buddy.sh
    # found nothing and STOPPED was empty. The tail must still wait, because a
    # Buddy was running before, and report the copy launchd starts a moment
    # later — not "launch it with" moments before it appears.
    def test_a_keepalive_install_waits_for_the_copy_launchd_starts(self):
        rc, out, err = self.run_tail({42282: INSTALLED}, appear_on_call=2, stopped="",
                                     was_running="40928", keepalive=True)
        self.assertEqual(0, rc, out + err)
        self.assertIn("==> Running: pid 42282", out)
        self.assertFalse(self.relaunched(), "relaunched by hand although launchd was starting it")

    # Opted in to the keep-alive, but nothing running before: launchd starts
    # it on load anyway, so the tail waits for that too.
    def test_a_loaded_keepalive_is_waited_for_even_with_nothing_running_before(self):
        rc, out, err = self.run_tail({42282: INSTALLED}, appear_on_call=2, keepalive=True)
        self.assertEqual(0, rc, out + err)
        self.assertIn("==> Running: pid 42282", out)
        self.assertFalse(self.relaunched())

    # A Buddy was running, no keep-alive to bring it back: relaunched by hand,
    # once, through open.
    def test_a_running_buddy_with_no_keepalive_is_relaunched(self):
        rc, out, err = self.run_tail({}, stopped="40928", was_running="40928")
        self.assertEqual(0, rc, out + err)
        self.assertTrue(self.relaunched(), "a Buddy that was running was not brought back")

    # The control for the case above: nothing was running and nothing is
    # loaded, so nothing is launched — a first install leaves that to the user.
    def test_nothing_is_launched_when_nothing_was_running(self):
        rc, out, err = self.run_tail({})
        self.assertEqual(0, rc, out + err)
        self.assertFalse(self.relaunched())
        self.assertIn("Launch it with", out)


if __name__ == "__main__":
    unittest.main()
