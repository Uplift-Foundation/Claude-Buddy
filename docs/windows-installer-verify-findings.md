# Windows installer: findings

**Result: the wizard works.** Verified by Kawika on a real Windows machine, running
the installer the way a user would rather than silently.

This closes the gap `docs/windows-installer-verify.md` was written to cover. The
mechanical half was already covered by
`.github/workflows/verify-windows-installer.yml`; what needed a person was the
wizard itself and whether orbs actually appear afterwards. Both are now confirmed.

## What this does and doesn't tell us

Recorded second-hand, and deliberately not dressed up: the report was that it
worked, without a step-by-step account. So treat the following as **confirmed
working in aggregate** rather than individually attested:

- the wizard runs and completes
- the app installs and works afterwards, i.e. orbs appear

And treat these as **not captured**, because nobody wrote them down:

- SmartScreen's exact wording and how many clicks it takes to get past it. This
  was the main thing the brief asked for, since it was meant to go into the README
  verbatim — it's the first thing every Windows user meets, and the installer is
  unsigned so the warning is unavoidable.
- Whether the generated `ClaudeBuddy.ico` renders as the orb or falls back to a
  generic placeholder. The `.ico` had never been seen rendered at the time.
- Whether the hook step flashes a console window (it runs `SW_HIDE`, so it
  shouldn't).
- Sign-out/sign-in startup, and whether exactly one instance starts.
- Uninstalling through Settings → Apps, as opposed to the `unins000.exe` path CI
  already covers.

None of those are known to be broken. They are simply unverified, which is a
different thing, and worth keeping straight so nobody later reads "the wizard was
tested" as covering all of it.

## Worth picking up if anyone revisits

The SmartScreen wording is the one with real user-facing value — a warning users
don't expect generates support questions, and quoting it exactly in the README
turns a scary dialog into an expected step. Grabbing a screenshot next time
someone installs on a fresh machine would be enough.
