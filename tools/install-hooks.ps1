<#
.SYNOPSIS
Wires Claude Buddy into every agent CLI on this machine.

.DESCRIPTION
The one thing an install should run, and the Windows twin of
tools/install-hooks.sh. Orbs appear because a CLI calls the hook, so an app with
no hooks wired doesn't error — it sits there showing nothing — and asking
someone to know which of two installers to run for which CLI is a way of
arranging for that to happen.

A CLI that isn't here is skipped and said so, not treated as a failure. Most
people have one of the two, and "Codex: not installed" is information; an error
would be a lie.

Only -Uninstall is accepted, because it is the only flag both sub-installers
understand. The Claude Code one takes -SettingsPath and -ProfileDir, the Codex
one takes -CodexHome; forwarding either to the other makes it fail partway
through a run that has already changed something. For those, run the
sub-installer directly — that is what they are still there for.

.PARAMETER Uninstall
Remove just our entries, from every CLI, leaving other tools' hooks alone.

.PARAMETER Wsl
Passed through to the Claude Code installer, which then also wires hooks inside
each WSL distribution. Opt-in there and opt-in here, because discovering
distributions can take a while and most people do not need it. Codex has no WSL
equivalent to forward it to.
#>
param(
    [switch]$Uninstall,
    [switch]$Wsl
)

$ErrorActionPreference = 'Continue'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Resolve-Installer([string]$name) {
    @(
        (Join-Path $here $name),
        (Join-Path (Join-Path $here 'tools') $name)
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Test-ClaudeCode {
    if (Test-Path (Join-Path $env:USERPROFILE '.claude')) { return $true }
    return [bool](Get-Command claude -ErrorAction SilentlyContinue)
}

function Test-Codex {
    $home_ = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    if (Test-Path $home_) { return $true }
    return [bool](Get-Command codex -ErrorAction SilentlyContinue)
}

$wired = 0
$skipped = @()
$failed = @()

function Invoke-One([string]$label, [string]$script, [hashtable]$extra) {
    $path = Resolve-Installer $script
    if (-not $path) { $script:failed += "$label (couldn't find $script)"; return }

    Write-Host "=== $label"
    $args = @{}
    if ($Uninstall) { $args['Uninstall'] = $true }
    foreach ($key in $extra.Keys) { $args[$key] = $extra[$key] }

    try {
        & $path @args
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { $script:failed += $label }
        else { $script:wired++ }
    }
    catch {
        $script:failed += "$label ($($_.Exception.Message))"
    }
    Write-Host ''
}

if (Test-ClaudeCode) {
    $extra = @{}
    if ($Wsl) { $extra['Wsl'] = $true }
    Invoke-One 'Claude Code' 'install-windows-hooks.ps1' $extra
}
else { $skipped += 'Claude Code' }

if (Test-Codex) { Invoke-One 'Codex' 'install-codex-hooks.ps1' @{} }
else { $skipped += 'Codex' }

foreach ($one in $skipped) {
    Write-Host "=== ${one}: not installed on this machine, nothing to wire."
    Write-Host '    Install it and run this again - that is all it takes.'
    Write-Host ''
}

if ($failed.Count -gt 0) {
    Write-Host 'Finished with problems:'
    foreach ($one in $failed) { Write-Host "  - $one" }
    exit 1
}

if ($wired -eq 0) {
    Write-Host 'Neither Claude Code nor Codex was found, so nothing was wired.'
    Write-Host 'Claude Buddy will show no orbs until one of them is installed.'
    exit 0
}

Write-Host 'Done.'
