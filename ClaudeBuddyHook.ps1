param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('idle', 'generating', 'waiting', 'ended')]
    [string]$State,

    # Baked in as a literal by install-windows-hooks.ps1 at wiring time,
    # computed there in a normal, full environment — not re-derived here,
    # where a WSL-interop-launched invocation's environment can't be trusted
    # to have TEMP/TMP set at all (already known to omit PATH; found to omit
    # TEMP too on a real machine, which silently pointed this script at an
    # unrelated relative-path folder with no visible error). Falls back to
    # GetTempPath() for anyone who wired this by hand via the README's JSON
    # snippets, where it isn't passed.
    [string]$TempDir = ''
)

$ErrorActionPreference = 'SilentlyContinue'

$sessionId = 'unknown'
$cwd = ''
$transcript = ''
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    if ($payload.session_id) { $sessionId = $payload.session_id }
    if ($payload.cwd) { $cwd = $payload.cwd }
    if ($payload.transcript_path) { $transcript = $payload.transcript_path }
} catch {}

# What the chat is called and what color it's been given. Claude Code keeps
# all three of these in the transcript, re-emitting them as the session goes:
#   {"type":"custom-title","customTitle":"claude-buddy",...}   <- /rename
#   {"type":"ai-title","aiTitle":"Package app with a tray",...} <- auto-named
#   {"type":"agent-color","agentColor":"green",...}             <- /color
# A name set with /rename wins over the generated one regardless of which was
# written last; a session with neither falls back to the directory name.
#
# WSL sessions land here with a Linux transcript path this script can't read,
# so they keep the folder-name fallback — see the platform notes in README.
$title = ''
$color = ''
if ($State -ne 'ended' -and $transcript -and (Test-Path $transcript)) {
    try {
        # Read the tail first: transcripts reach tens of MB and this runs on
        # every tool call. Only scan the whole file when a long run of tool
        # output has pushed all three records out of that window.
        # -Encoding UTF8 is load-bearing on Windows PowerShell 5.1, which
        # otherwise reads these UTF-8 transcripts as the ANSI codepage and
        # turns a name like "café" into "cafÃ©". PowerShell 7 already defaults
        # to UTF-8; being explicit is correct on both.
        $pattern = '^\{"type":"(custom-title|ai-title|agent-color)"'
        $meta = Get-Content -Path $transcript -Tail 400 -Encoding UTF8 |
            Where-Object { $_ -match $pattern }
        if (-not $meta) {
            $meta = Get-Content -Path $transcript -Encoding UTF8 |
                Where-Object { $_ -match $pattern }
        }

        $newest = {
            param($type)
            $meta | Where-Object { $_.StartsWith('{"type":"' + $type + '"') } | Select-Object -Last 1
        }

        $line = & $newest 'custom-title'
        if ($line) { $title = ($line | ConvertFrom-Json).customTitle }
        if (-not $title) {
            $line = & $newest 'ai-title'
            if ($line) { $title = ($line | ConvertFrom-Json).aiTitle }
        }

        $line = & $newest 'agent-color'
        if ($line) { $color = ($line | ConvertFrom-Json).agentColor }
    } catch {}
}

$resolvedTempDir = if ($TempDir) { $TempDir } else { [System.IO.Path]::GetTempPath() }
$dir = Join-Path $resolvedTempDir 'claude_buddy'
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir | Out-Null
}

$file = Join-Path $dir "$sessionId.txt"

if ($State -eq 'ended') {
    Remove-Item -Path $file -Force
    exit 0
}

# Identify the terminal hosting this session so a click on the orb can jump
# to it. Windows Terminal advertises itself via WT_SESSION (which flows
# through WSL too, via WSLENV); VS Code's integrated terminal sets
# TERM_PROGRAM. For native sessions, walk up the parent process chain to
# the first process that owns a top-level window — that's the terminal
# (WindowsTerminal.exe, Code.exe, the conhost shell, ...). The walk finds
# nothing for WSL sessions (the Windows-side parent is an interop bridge,
# not the terminal), which is what the term_program fallback is for.
$termProgram = ''
if ($env:WT_SESSION) { $termProgram = 'WindowsTerminal' }
elseif ($env:TERM_PROGRAM) { $termProgram = $env:TERM_PROGRAM }

$termPid = 0
# The claude process itself, recorded so the app can tell a running session from
# a status file left behind by one that never exited cleanly (Ctrl+C fires no
# SessionEnd). See SessionManager.SessionGone.
#
# Found by walking up for the first `claude` ancestor, NOT by taking this
# script's immediate parent — which is what this did, on the assumption that
# "Claude Code spawns the hook directly". It does not, on Windows: the hook
# command runs through a short-lived shell, so the immediate parent is that
# shell, and it exits the moment the hook does. Measured on a real machine —
# successive hook writes for one live session recorded 54076, then 83076, both
# already dead, while the session's actual claude.exe sat at 36804 the whole
# time. The app reads a dead session_pid as "Ctrl+C'd without a SessionEnd" and
# suppresses the orb, so every live session on Windows went invisible while a
# three-day-old status file with no session_pid at all kept its orb.
#
# Nothing to do with the Unix hook, which asks a different question entirely
# (first ancestor owning a real tty) and was never wrong this way.
#
# The walk is the one that was already here looking for term_pid: claude.exe
# owns no top-level window of its own, so it is always passed on the way up to
# the terminal that does, and both answers come out of a single climb.
#
# Left at 0 when no claude ancestor is found, which is the safe direction: the
# app treats an unrecorded pid as "can't check" and keeps the orb on the
# lifetime rule, rather than hiding a session that is really running. That is
# also what the WSL case now gets, where the Windows-side chain dead-ends in an
# interop bridge and previously recorded a doomed pid.
$sessionPid = 0
try {
    $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
    for ($i = 0; $i -lt 10 -and $cur; $i++) {
        $parentId = $cur.ParentProcessId
        if (-not $parentId) { break }
        $proc = Get-Process -Id $parentId -ErrorAction Stop
        if (-not $proc) { break }

        $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$parentId"

        # Tested here, after advancing $cur and before the window check below,
        # because the name and command line come off the CIM object and the
        # window check can break out of the loop. Costs one extra CIM query in
        # the iteration that finds the terminal; a hook runs a handful of them
        # already.
        #
        # `node` with claude on its command line covers an npm-style install,
        # where the session is node running the CLI rather than a claude.exe.
        # `claude.exe*` rather than an exact match: Claude Code self-updates by
        # renaming the running binary aside, leaving names like
        # claude.exe.old.1786153043553 on disk. Observed on this machine — CIM's
        # Name still reported the stable "claude.exe" for the live process while
        # Get-Process reported the renamed image, so an exact match happens to
        # work today, but which API reports which name is not worth depending on
        # when a session outliving an update is entirely ordinary.
        if ($sessionPid -eq 0 -and $cur) {
            $name = "$($cur.Name)"
            if ($name -like 'claude.exe*' -or
                ($name -eq 'node.exe' -and "$($cur.CommandLine)" -match 'claude')) {
                $sessionPid = [int]$parentId
            }
        }

        if ($proc.MainWindowHandle -ne 0) { $termPid = [int]$parentId; break }
    }
} catch {}

$status = @{
    state           = $State
    cwd             = $cwd
    title           = $title
    color           = $color
    term_program    = $termProgram
    term_pid        = $termPid
    session_pid     = $sessionPid
    transcript_path = $transcript
} | ConvertTo-Json -Compress

# Not Set-Content: on Windows PowerShell 5.1 it writes the ANSI codepage and
# replaces anything outside it with "?", so a chat name with an em dash or an
# accent would reach the app corrupted. UTF-8 *without* a BOM specifically —
# System.Text.Json treats a leading BOM as an invalid start of value, which
# would make the app skip the file and drop the orb entirely.
[System.IO.File]::WriteAllText($file, $status, (New-Object System.Text.UTF8Encoding($false)))
