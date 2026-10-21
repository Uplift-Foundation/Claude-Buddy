<#
.SYNOPSIS
Wires the Orbweaver hook into Grok Build on Windows.

.DESCRIPTION
The Windows twin of tools/install-grok-hooks.sh. Grok discovers global hooks
from $GROK_HOME\hooks\*.json and they are always trusted.

Before the rename (CB-255) our file was hooks\claude-buddy.json. Grok loads
every hooks\*.json, so leaving it beside hooks\orbweaver.json would fire each
event twice: an install removes it and an uninstall removes both. The old
claude-buddy script folder stays for sessions still running against it, with a
.superseded marker the app's LegacyHookCleanup reads.

.PARAMETER Uninstall
Remove just our hooks file (both names).
#>
param(
    [switch]$Uninstall,
    [string]$GrokHome = '',
    [string]$HookDir = '',
    [string]$HooksFile = ''
)

$ErrorActionPreference = 'Stop'

if (-not $GrokHome) {
    $GrokHome = if ($env:GROK_HOME) { $env:GROK_HOME } else { Join-Path $env:USERPROFILE '.grok' }
}
if (-not $HookDir)    { $HookDir    = Join-Path $GrokHome 'orbweaver' }
if (-not $HooksFile)  { $HooksFile  = Join-Path $GrokHome 'hooks\orbweaver.json' }
$legacyHookDir = Join-Path $GrokHome 'claude-buddy'
$legacyHooksFile = Join-Path (Split-Path -Parent $HooksFile) 'claude-buddy.json'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = @(
    (Join-Path $here 'OrbweaverHook.ps1'),
    (Join-Path (Split-Path -Parent $here) 'OrbweaverHook.ps1')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

$installed = Join-Path $HookDir 'OrbweaverHook.ps1'

# The pre-rename file goes in both modes. Guarded so a -HooksFile that is itself
# named claude-buddy.json is not deleted out from under this run.
if (($legacyHooksFile -ne $HooksFile) -and (Test-Path -LiteralPath $legacyHooksFile)) {
    Remove-Item -LiteralPath $legacyHooksFile -Force
    Write-Host "Removed the pre-rename hooks file $legacyHooksFile"
}

if ($Uninstall) {
    if (Test-Path -LiteralPath $HooksFile) { Remove-Item -LiteralPath $HooksFile -Force }
    Write-Host "Removed Orbweaver hooks from $HooksFile."
    exit 0
}

if (-not $source) {
    Write-Error "Can't find OrbweaverHook.ps1 next to $here or one level up."
    exit 1
}

New-Item -ItemType Directory -Path $HookDir -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $installed -Force
Write-Host "Hook installed: $installed"

# Mark, never delete, and only when absent: a refreshed mtime on every re-run
# would keep the app's 14-day retirement clock from ever running out.
$marker = Join-Path $legacyHookDir '.superseded'
if ((Test-Path -LiteralPath $legacyHookDir -PathType Container) -and -not (Test-Path -LiteralPath $marker)) {
    [System.IO.File]::WriteAllText($marker, '')
    Write-Host "Marked $legacyHookDir as superseded; Orbweaver retires it once nothing calls it."
}

New-Item -ItemType Directory -Path (Split-Path $HooksFile) -Force | Out-Null

$command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installed`" -Agent grok -State "
$handler = {
    param($state)
    @{ type = 'command'; command = ($command + $state); timeout = 15 }
}

$config = @{
    hooks = @{
        SessionStart = @(
            @{ hooks = @(& $handler 'idle') }
        )
        UserPromptSubmit = @(
            @{ hooks = @(& $handler 'generating') }
        )
        PreToolUse = @(
            @{ matcher = '.*'; hooks = @(& $handler 'generating') }
        )
        Notification = @(
            @{ matcher = 'permission_prompt'; hooks = @(& $handler 'waiting') }
        )
        Stop = @(
            @{ hooks = @(& $handler 'idle') }
        )
        SessionEnd = @(
            @{ hooks = @(& $handler 'ended') }
        )
    }
}

$json = $config | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($HooksFile, $json, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Wired Orbweaver hooks into $HooksFile"
Write-Host "Restart any running Grok sessions: hooks are read at session start."
