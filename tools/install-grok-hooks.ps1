<#
.SYNOPSIS
Wires the Claude Buddy hook into Grok Build on Windows.

.DESCRIPTION
The Windows twin of tools/install-grok-hooks.sh. Grok discovers global hooks
from $GROK_HOME\hooks\*.json and they are always trusted.

.PARAMETER Uninstall
Remove just our hooks file.
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
if (-not $HookDir)    { $HookDir    = Join-Path $GrokHome 'claude-buddy' }
if (-not $HooksFile)  { $HooksFile  = Join-Path $GrokHome 'hooks\claude-buddy.json' }

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = @(
    (Join-Path $here 'ClaudeBuddyHook.ps1'),
    (Join-Path (Split-Path -Parent $here) 'ClaudeBuddyHook.ps1')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

$installed = Join-Path $HookDir 'ClaudeBuddyHook.ps1'

if ($Uninstall) {
    if (Test-Path $HooksFile) { Remove-Item -LiteralPath $HooksFile -Force }
    Write-Host "Removed Claude Buddy hooks from $HooksFile."
    exit 0
}

if (-not $source) {
    Write-Error "Can't find ClaudeBuddyHook.ps1 next to $here or one level up."
    exit 1
}

New-Item -ItemType Directory -Path $HookDir -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $installed -Force
Write-Host "Hook installed: $installed"

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
Write-Host "Wired Claude Buddy hooks into $HooksFile"
Write-Host "Restart any running Grok sessions: hooks are read at session start."
