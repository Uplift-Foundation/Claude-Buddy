@echo off
rem Runs the Windows verification brief (docs/windows-verification.md) unattended.
rem
rem Must be started by a scheduled task created with /IT, not directly over SSH.
rem An SSH-launched process gets a non-interactive window station: GUI programs
rem start but are invisible, so a notification-area icon and floating orbs can't
rem be observed. /IT puts this in the logged-on user's interactive session, where
rem the real desktop is.

setlocal
set LOG=C:\Users\warre\cbverify.log
set REPO=C:\cb
set BASE=origin/claude-desktop-profile-switcher

rem Claude Code's Bash tool must not fall through to WSL's System32\bash.exe,
rem where C:\ paths don't resolve.
set CLAUDE_CODE_GIT_BASH_PATH=C:\Program Files\Git\bin\bash.exe

cd /d %REPO% || exit /b 1

echo ==== run started >> %LOG%
git fetch origin >> %LOG% 2>&1

rem Create the working branch once; never reset it, or a re-run discards the
rem findings and fixes from the previous one.
git rev-parse --verify windows-verification >nul 2>&1 || git branch windows-verification %BASE% >> %LOG% 2>&1
git checkout windows-verification >> %LOG% 2>&1

"C:\Users\warre\.local\bin\claude.exe" -p "Read docs/windows-verification.md and carry out the entire brief. You are running unattended: nobody will answer questions, so make judgement calls and record them rather than waiting for input." --allowedTools "Bash,Read,Write,Edit,Glob,Grep,TodoWrite" --output-format text >> %LOG% 2>&1

echo ==== run finished with exit %errorlevel% >> %LOG%
