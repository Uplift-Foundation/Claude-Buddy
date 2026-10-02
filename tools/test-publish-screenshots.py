#!/usr/bin/env python3
"""Runs publish-screenshots.yml's real publish step against a local bare repository.

CB-237: every run pushes to one `screenshots` branch at the same flat
<rid>/<name>.png paths, and the PR comment used to link to the branch tip, so a
comment showed whichever run pushed last, from any PR. The step is extracted
from the workflow text and run with a fake `gh`, so what is tested is the shell
that ships, not a copy of it.

Linux and macOS only: the workflow itself runs on ubuntu-latest.
"""
import os
import re
import shutil
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
WORKFLOW = os.path.join(HERE, "..", ".github", "workflows", "publish-screenshots.yml")
REPO = "o/r"
GIT_ENV = {"GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t", "GIT_COMMITTER_NAME": "t",
           "GIT_COMMITTER_EMAIL": "t@t"}


def publish_step(text):
    """The `run:` block of the step named 'Publish to the screenshots branch...'."""
    lines = text.splitlines()
    start = next(i for i, l in enumerate(lines) if "name: Publish to the screenshots branch" in l)
    run = next(i for i in range(start, len(lines)) if lines[i].strip() == "run: |")
    body = []
    for l in lines[run + 1:]:
        if l.strip() and not l.startswith(" " * 10):
            break
        body.append(l[10:] if l.startswith(" " * 10) else l)
    return "\n".join(body)


def substitute(script, run_id, pr):
    """Replace ${{ ... }} expressions with what a workflow_dispatch run would supply."""
    def one(m):
        expr = m.group(0)
        if "github.repository" in expr:
            return REPO
        if "run_id" in expr:
            return str(run_id)
        if "inputs.pr_number" in expr:
            return str(pr)
        return ""
    return re.sub(r"\$\{\{.*?\}\}", one, script)


def point_at_local_remote(script):
    """Older copies of the workflow have no override; patch the one line so a test can
    never reach github.com. Refuse to run if it still could."""
    if "SCREENSHOTS_REMOTE_URL" not in script:
        script = script.replace(
            'remote_url="https://x-access-token:${GH_TOKEN}@github.com/${repo}.git"',
            'remote_url="$SCREENSHOTS_REMOTE_URL"')
    assert "@github.com/${repo}.git" not in script.replace(
        "${SCREENSHOTS_REMOTE_URL:-https://x-access-token:${GH_TOKEN}@github.com/${repo}.git}", ""), \
        "the step could still reach github.com"
    return script


@unittest.skipIf(os.name == "nt", "the workflow runs on ubuntu-latest")
class PublishStep(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="cb-publish-shots-")
        self.remote = os.path.join(self.tmp, "remote.git")
        subprocess.run(["git", "init", "-q", "--bare", "-b", "screenshots", self.remote], check=True)
        self.work = os.path.join(self.tmp, "ws", "work")
        os.makedirs(self.work)
        self.bin = os.path.join(self.tmp, "bin")
        os.makedirs(self.bin)
        self.comments = os.path.join(self.tmp, "comments")
        os.makedirs(self.comments)
        with open(os.path.join(self.bin, "gh"), "w") as f:
            f.write('#!/bin/sh\n[ "$1 $2" = "pr comment" ] && cat > "%s/pr-$3.md"\nexit 0\n' % self.comments)
        os.chmod(os.path.join(self.bin, "gh"), 0o755)
        with open(self.workflow_path()) as f:
            self.script = point_at_local_remote(publish_step(f.read()))

    def workflow_path(self):
        return os.environ.get("PUBLISH_WORKFLOW_UNDER_TEST", WORKFLOW)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_step(self, pr, png, run_id):
        d = os.path.join(self.work, "downloaded", "ui-screenshots-osx-arm64")
        shutil.rmtree(os.path.join(self.work, "downloaded"), ignore_errors=True)
        os.makedirs(d)
        with open(os.path.join(d, "a.png"), "wb") as f:
            f.write(png)
        env = dict(os.environ, **GIT_ENV, PATH=self.bin + os.pathsep + os.environ["PATH"],
                   GH_TOKEN="x", GITHUB_STEP_SUMMARY=os.path.join(self.tmp, "summary"),
                   SCREENSHOTS_REMOTE_URL="file://" + self.remote)
        r = subprocess.run(["bash", "-c", substitute(self.script, run_id, pr)], cwd=self.work,
                           env=env, capture_output=True, text=True)
        self.assertEqual(0, r.returncode, r.stdout + r.stderr)
        with open(os.path.join(self.comments, "pr-%s.md" % pr)) as f:
            return r.stdout, f.read()

    def git(self, *args):
        return subprocess.run(["git", "-C", self.remote, *args], capture_output=True)

    def shown_by(self, comment):
        """What the link in a comment shows: the blob at <ref>:<path>, or None if the
        ref does not exist on the remote."""
        m = re.search(r"raw/(.*)/(osx-arm64/a\.png)\)", comment)
        self.assertIsNotNone(m, comment)
        ref, path = m.groups()
        r = self.git("show", "%s:%s" % (ref, path))
        return (r.stdout if r.returncode == 0 else None), ref

    def test_each_comment_still_shows_its_own_images_after_another_run_overwrote_them(self):
        _, a = self.run_step(135, b"IMAGE-FROM-A", 1)
        _, b = self.run_step(139, b"IMAGE-FROM-B", 2)
        self.assertEqual(b"IMAGE-FROM-A", self.shown_by(a)[0])
        self.assertEqual(b"IMAGE-FROM-B", self.shown_by(b)[0])

    def test_the_link_names_a_commit_not_a_branch(self):
        _, a = self.run_step(135, b"A", 1)
        self.assertRegex(self.shown_by(a)[1], r"^[0-9a-f]{40}$")

    def test_a_run_with_nothing_new_to_commit_still_links_to_a_commit_holding_its_images(self):
        self.run_step(135, b"SAME", 1)
        _, again = self.run_step(139, b"SAME", 2)
        shown, ref = self.shown_by(again)
        self.assertEqual(b"SAME", shown)
        self.assertRegex(ref, r"^[0-9a-f]{40}$")

    def test_a_retry_that_rebases_onto_another_runs_push_links_to_the_commit_that_landed(self):
        self.run_step(135, b"FIRST", 1)
        flag = os.path.join(self.tmp, "rejected-once")
        hook = os.path.join(self.remote, "hooks", "pre-receive")
        with open(hook, "w") as f:
            f.write("""#!/bin/sh
if [ ! -e "%s" ]; then
  touch "%s"
  unset GIT_OBJECT_DIRECTORY GIT_ALTERNATE_OBJECT_DIRECTORIES GIT_QUARANTINE_PATH
  tip=$(git rev-parse refs/heads/screenshots)
  blob=$(echo competing | git hash-object -w --stdin)
  tree=$( { git ls-tree "$tip^{tree}"; printf '100644 blob %%s\\tcompeting.txt\\n' "$blob"; } | git mktree )
  c=$(git commit-tree "$tree" -p "$tip" -m "another run pushed first")
  git update-ref refs/heads/screenshots "$c"
  exit 1
fi
""" % (flag, flag))
        os.chmod(hook, 0o755)
        out, comment = self.run_step(139, b"SECOND", 2)
        self.assertIn("Push conflict, retry 1", out)
        shown, ref = self.shown_by(comment)
        self.assertEqual(b"SECOND", shown)
        # The commit is on the remote's branch, not a pre-rebase one that never arrived.
        self.assertEqual(0, self.git("merge-base", "--is-ancestor", ref, "screenshots").returncode)

    def test_the_resolver_would_catch_a_branch_tip_link(self):
        """Control for the checks above: build the OLD link form by hand and show that
        it is the one that resolves to the wrong run."""
        _, a = self.run_step(135, b"IMAGE-FROM-A", 1)
        self.run_step(139, b"IMAGE-FROM-B", 2)
        old_form = a.replace(self.shown_by(a)[1], "refs/heads/screenshots")
        self.assertEqual(b"IMAGE-FROM-B", self.shown_by(old_form)[0])


if __name__ == "__main__":
    unittest.main()
