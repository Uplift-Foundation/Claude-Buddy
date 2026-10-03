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
    """From the running_installed definition to the esac closing the report."""
    m = re.search(r"^  running_installed\(\) \{.*?^  esac$", text, re.S | re.M)
    if m is None:
        raise AssertionError("build-macos-app.sh has no running_installed ... esac install tail")
    return m.group(0)


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

    def run_tail(self, processes):
        """processes: {pid: executable path}. pgrep behaves like the real one —
        lists the pids, or prints nothing and exits 1 when there are none."""
        if processes:
            self.stub("pgrep", "".join("echo %d\n" % pid for pid in processes))
        else:
            self.stub("pgrep", "exit 1\n")
        cases = "".join('  %d) echo "%s" ;;\n' % (pid, exe) for pid, exe in processes.items())
        self.stub("ps", 'pid=""\nwhile [ $# -gt 0 ]; do [ "$1" = "-p" ] && pid="$2"; shift; done\n'
                        'case "$pid" in\n%s  *) exit 1 ;;\nesac\n' % cases)
        script = ("set -euo pipefail\nPATH=%s:$PATH\nSTOPPED=\"\"\nAPP_NAME=\"Claude Buddy\"\n"
                  "INSTALLED_EXE=\"%s\"\n%s\necho TAIL-COMPLETED\n") % (self.posix(self.bin), INSTALLED, TAIL)
        path = os.path.join(self.tmp, "tail.sh")
        with open(path, "w", newline="\n") as f:
            f.write(script)
        r = subprocess.run([bash(), self.posix(path)], capture_output=True, text=True)
        return r.returncode, r.stdout, r.stderr

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
        rc, out, err = self.run_tail({4242: INSTALLED})
        self.assertEqual(0, rc, out + err)
        self.assertIn("==> Running: pid 4242", out)

    def test_two_installed_copies_are_warned_about(self):
        rc, out, err = self.run_tail({4242: INSTALLED, 4343: INSTALLED})
        self.assertEqual(0, rc, out + err)
        self.assertIn("more than one Claude Buddy is running: 4242 4343", err)


if __name__ == "__main__":
    unittest.main()
