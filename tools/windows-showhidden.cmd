@echo off
rem Runs the Windows show-if-hidden verification (docs/windows-showhidden.md).
rem
rem Must be started by a scheduled task created with /IT. Two reasons, not one:
rem an SSH-launched process gets a non-interactive window station where GUI
rem programs are invisible, and IApplicationActivationManager::ActivateApplication
rem — the whole basis of this feature — returns E_ACCESSDENIED outside an
rem interactive session.

setlocal
set LOG=C:\Users\warre\cbshowhidden.log
set REPO=C:\cb
set BASE=origin/main

rem Keep Claude Code's Bash tool off WSL's System32\bash.exe, where C:\ paths
rem don't resolve.
set CLAUDE_CODE_GIT_BASH_PATH=C:\Program Files\Git\bin\bash.exe

cd /d %REPO% || exit /b 1

echo ==== run started >> %LOG%
git fetch origin >> %LOG% 2>&1

rem Create the working branch once; never reset it, or a re-run discards the
rem previous one's work.
git rev-parse --verify windows-showhidden >nul 2>&1 || git branch windows-showhidden %BASE% >> %LOG% 2>&1
git checkout windows-showhidden >> %LOG% 2>&1

"C:\Users\warre\.local\bin\claude.exe" -p "Read docs/windows-showhidden.md and do it. You are running unattended: nobody will answer questions, so make judgement calls and record them rather than waiting for input." --allowedTools "Bash,Read,Write,Edit,Glob,Grep,TodoWrite" --output-format text >> %LOG% 2>&1

echo ==== run finished with exit %errorlevel% >> %LOG%
