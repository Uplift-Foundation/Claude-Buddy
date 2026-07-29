# Installs the Claude Buddy hook into Claude Code's Windows settings.
#
# The hook is what makes orbs appear: Claude Code runs it on session start,
# prompt submit, tool use, stop and session end, and it writes a small status
# file per session that the app watches. Without it wired into settings.json
# nothing happens at all — no error, just no orbs, which is a confusing way to
# fail and the reason this script exists rather than a README instruction to
# hand-edit JSON.
#
#   .\tools\install-windows-hooks.ps1              # install / repair
#   .\tools\install-windows-hooks.ps1 -Uninstall   # remove just our entries
#
# Safe to re-run: it strips any existing Claude Buddy entries before adding
# fresh ones, so it converges rather than accumulating duplicates.

[CmdletBinding()]
param(
    [switch] $Uninstall,

    # Where the hook script is copied to. Kept out of the repo so the hook keeps
    # working if the clone moves or is deleted.
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'ClaudeBuddy'),

    [string] $SettingsPath = (Join-Path $env:USERPROFILE '.claude\settings.json')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'ClaudeBuddyHook.ps1'
$installed = Join-Path $InstallDir 'ClaudeBuddyHook.ps1'

if (-not $Uninstall) {
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Can't find ClaudeBuddyHook.ps1 next to the repo root ($source)."
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $installed -Force
    Write-Host "Hook installed: $installed"
}

if (-not (Test-Path -LiteralPath $SettingsPath)) {
    # An absent settings file is normal on a fresh install; start one rather
    # than refusing, but never invent anything beyond the hooks themselves.
    New-Item -ItemType Directory -Path (Split-Path -Parent $SettingsPath) -Force | Out-Null
    '{}' | Set-Content -LiteralPath $SettingsPath -Encoding ASCII
    Write-Host "Created $SettingsPath"
}

# ConvertFrom-Json -AsHashtable is PowerShell 6+, and the powershell.exe Claude
# Code invokes on Windows is 5.1, so convert the object graph by hand. Recursive
# because settings.json nests: hooks -> event -> groups -> hooks -> command.
function ConvertTo-HashtableDeep($value) {
    if ($null -eq $value) { return $null }

    if ($value -is [System.Collections.IDictionary]) {
        $copy = @{}
        foreach ($key in @($value.Keys)) { $copy[$key] = ConvertTo-HashtableDeep $value[$key] }
        return $copy
    }

    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $copy = @{}
        foreach ($property in $value.PSObject.Properties) {
            $copy[$property.Name] = ConvertTo-HashtableDeep $property.Value
        }
        return $copy
    }

    # Strings are enumerable; check them before the array branch.
    if ($value -is [string]) { return $value }

    if ($value -is [System.Collections.IEnumerable]) {
        return @(foreach ($item in $value) { ConvertTo-HashtableDeep $item })
    }

    return $value
}

# Read with -Raw and parse: this preserves every existing setting, which matters
# because this file holds the user's model, permissions, status line and so on.
$json = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8
$settings = if ([string]::IsNullOrWhiteSpace($json)) { @{} } else { ConvertTo-HashtableDeep ($json | ConvertFrom-Json) }

$backup = "$SettingsPath.claudebuddy-backup"
Copy-Item -LiteralPath $SettingsPath -Destination $backup -Force
Write-Host "Backed up settings to $backup"

if (-not $settings.ContainsKey('hooks') -or $null -eq $settings['hooks']) {
    $settings['hooks'] = @{}
}

$hooks = $settings['hooks']

# One command line per state. Quoted install path: it contains no spaces today
# but %LOCALAPPDATA% can, on a redirected profile.
function New-HookCommand([string] $State) {
    $quoted = '"' + $installed + '"'
    return "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $quoted -State $State"
}

# Which Claude Code events drive which orb state. Notification carries a matcher
# because only some notifications mean "Claude needs you"; the rest would make
# every orb amber, which would make amber meaningless.
$wanted = @(
    @{ Event = 'SessionStart';     Matcher = $null;                  State = 'idle' },
    @{ Event = 'UserPromptSubmit'; Matcher = $null;                  State = 'generating' },
    @{ Event = 'PreToolUse';       Matcher = '.*';                   State = 'generating' },
    @{ Event = 'Stop';             Matcher = $null;                  State = 'idle' },
    @{ Event = 'SessionEnd';       Matcher = $null;                  State = 'ended' },
    @{ Event = 'Notification';     Matcher = 'permission_prompt';    State = 'waiting' },
    @{ Event = 'Notification';     Matcher = 'elicitation_dialog';   State = 'waiting' },
    @{ Event = 'Notification';     Matcher = 'elicitation_complete'; State = 'generating' }
)

# Strip our own entries wherever they appear, so re-running repairs rather than
# duplicating, and -Uninstall leaves other tools' hooks untouched.
# $event is an automatic variable in PowerShell; using it as a loop variable
# here would shadow it and can misbehave.
foreach ($eventName in @($hooks.Keys)) {
    $groups = @($hooks[$eventName])
    $kept = @()

    foreach ($group in $groups) {
        if ($null -eq $group) { continue }

        $inner = @(@($group['hooks']) | Where-Object {
            $_ -and ($_['command'] -notlike '*ClaudeBuddyHook.ps1*')
        })

        if ($inner.Count -gt 0) {
            $group['hooks'] = $inner
            $kept += $group
        }
    }

    if ($kept.Count -gt 0) { $hooks[$eventName] = $kept } else { $hooks.Remove($eventName) }
}

if (-not $Uninstall) {
    foreach ($entry in $wanted) {
        $group = @{ hooks = @(@{ type = 'command'; command = (New-HookCommand $entry.State) }) }
        if ($entry.Matcher) { $group['matcher'] = $entry.Matcher }

        $existing = if ($hooks.ContainsKey($entry.Event)) { @($hooks[$entry.Event]) } else { @() }
        $hooks[$entry.Event] = @($existing) + @($group)
    }
}

$settings['hooks'] = $hooks

# UTF-8 *without* a BOM. System.Text.Json — which Claude Code and this app both
# use — treats a leading BOM as an invalid start of value, and PowerShell 5.1's
# Set-Content adds one by default. This exact mistake has bitten this project
# before, in the hook itself.
$out = $settings | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($SettingsPath, $out, (New-Object System.Text.UTF8Encoding($false)))

if ($Uninstall) {
    Write-Host 'Removed Claude Buddy hooks from settings.json.'
    Write-Host 'The installed hook script was left in place; delete it if you want it gone.'
}
else {
    Write-Host "Wired $($wanted.Count) hook entries into $SettingsPath"
    Write-Host ''
    Write-Host 'Restart any running Claude Code sessions — hooks are read at session start,'
    Write-Host 'so existing sessions will not produce orbs until they are restarted.'
}
