param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('idle', 'generating', 'waiting', 'ended')]
    [string]$State
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
$lead = ''
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
        $tail = Get-Content -Path $transcript -Tail 400 -Encoding UTF8
        $meta = $tail | Where-Object { $_ -match $pattern }
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

        # Agent teams. A team member runs as its own claude process, so it gets
        # its own orb with nothing to say it belongs to anyone — which is what
        # the arrow the app draws is for. The member's transcript is the only
        # thing on disk naming its team, and it names it on every message:
        #   {"parentUuid":null,...,"teamName":"session-6a6fcb43","agentName":"..."}
        # Unlike the title records this isn't a whole line and can't be
        # anchored, but message content is JSON-escaped — a transcript quoting
        # this comment holds \"teamName\":\", which the contiguous pattern below
        # can't match. No whole-file fallback: every message carries it.
        $team = ''
        foreach ($line in $tail) {
            if ($line -match '"teamName":"([A-Za-z0-9._-]+)"') { $team = $Matches[1] }
        }

        # Only members record a team; the lead records nothing, so its full
        # session id has to come from the team's config (the team *name* holds
        # only the first eight characters of it). The character class above is
        # what keeps this off any path but a team's own. A lead naming itself is
        # dropped — an orb with an arrow to itself is worse than no arrow.
        if ($team) {
            $configDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR }
                         else { Join-Path $env:USERPROFILE '.claude' }
            $teamConfig = Join-Path $configDir "teams\$team\config.json"
            if (Test-Path $teamConfig) {
                $lead = (Get-Content -Path $teamConfig -Raw -Encoding UTF8 |
                    ConvertFrom-Json).leadSessionId
            }
            if ($lead -eq $sessionId) { $lead = '' }
        }
    } catch {}
}

$dir = Join-Path $env:TEMP 'claude_buddy'
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
# SessionEnd). It is this script's immediate parent: Claude Code spawns the hook
# directly. See SessionManager.SessionGone.
$sessionPid = 0
try {
    $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
    if ($cur -and $cur.ParentProcessId) { $sessionPid = [int]$cur.ParentProcessId }
    for ($i = 0; $i -lt 10 -and $cur; $i++) {
        $parentId = $cur.ParentProcessId
        if (-not $parentId) { break }
        $proc = Get-Process -Id $parentId -ErrorAction Stop
        if (-not $proc) { break }
        if ($proc.MainWindowHandle -ne 0) { $termPid = [int]$parentId; break }
        $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$parentId"
    }
} catch {}

$status = @{
    state        = $State
    cwd          = $cwd
    title        = $title
    color        = $color
    lead         = $lead
    term_program = $termProgram
    term_pid     = $termPid
    session_pid  = $sessionPid
} | ConvertTo-Json -Compress

# Not Set-Content: on Windows PowerShell 5.1 it writes the ANSI codepage and
# replaces anything outside it with "?", so a chat name with an em dash or an
# accent would reach the app corrupted. UTF-8 *without* a BOM specifically —
# System.Text.Json treats a leading BOM as an invalid start of value, which
# would make the app skip the file and drop the orb entirely.
[System.IO.File]::WriteAllText($file, $status, (New-Object System.Text.UTF8Encoding($false)))
