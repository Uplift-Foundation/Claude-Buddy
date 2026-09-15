<#
.SYNOPSIS
    CB-119: surface a short xUnit v3 (Microsoft.Testing.Platform) run that
    otherwise exits 0.

.DESCRIPTION
    tests/UiTests -c Release has reported 1009 once and 1010 on surrounding
    runs, with the process itself exiting 0 both times. Root cause: an
    [AvaloniaFact] in [Collection("Settings")] runs and passes, then its
    *cleanup* throws InvalidOperationException inside
    Avalonia.Headless.HeadlessUnitTestSession.EnsureIsolatedApplication().
    xUnit v3 files that as an assembly error rather than a test failure, and
    it REPLACES the test's own result — so the assembly's XML carries
    errors="1" and the total test count drops by one, but "Failed" stays 0
    and the process exit code stays 0. That is unsurfaced, not invisible:
    the runner writes it to the report; nothing prints it to the console.

    This script is that surfacing. It reads the plain xUnit XML report
    (`--report-xunit`, not `--report-xunit-trx` — the assembly-level
    `errors`/`total` attributes and the `<error type=… name=…>` elements
    are direct, undocumented-schema-free reads off that format; the TRX
    schema buries the same information behind a `Counters` node with no
    per-error name at all) and fails loudly on two independent conditions:

      1. `errors != "0"` on any <assembly> — the primary check. A direct
         read of a signal the runner already emits, no upkeep required.
      2. total tests below -MinimumExpectedTests — the secondary belt.
         Deliberately set BELOW the real current total by whoever calls
         this script (see the callers in ci.yml for the current numbers and
         why they're not pinned exactly), so it catches a large accidental
         regression without needing a bump every time a suite grows by one.

    ci.yml wraps the UI suites in a 3-attempt retry loop, and critically,
    `dotnet test`'s own exit code is 0 for the exact failure this guards
    against — so the retry loop's existing `$LASTEXITCODE -eq 0` check
    cannot be the thing that decides whether to retry. This script's exit
    code is what the loop must check instead: 0 only when the report is
    clean AND at/above the floor. On every attempt (not just the last) it
    prints a `::warning::`/`::error::` GitHub Actions annotation naming the
    specific offending test, because "a count was wrong" without a name is
    the problem this ticket exists to fix.

.PARAMETER ReportPath
    Path to the plain xUnit XML report (from --report-xunit).

.PARAMETER SuiteName
    Short label used in printed messages, e.g. "UiTests" or "UiScreenshots".

.PARAMETER MinimumExpectedTests
    Floor for the total test count. Intentionally set below the real total
    by the caller.

.PARAMETER Attempt
    1-based index of the current attempt, for wording ("retrying" vs the
    final failure).

.PARAMETER MaxAttempts
    Total attempts the caller's retry loop allows.

.OUTPUTS
    Exit code 0 when the report is healthy (errors="0" and total >=
    MinimumExpectedTests); exit code 1 otherwise. Never throws for a
    well-formed report — a missing or unparsable report is itself reported
    as unhealthy rather than crashing the build with a stack trace.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportPath,

    [Parameter(Mandatory = $true)]
    [string]$SuiteName,

    [Parameter(Mandatory = $true)]
    [int]$MinimumExpectedTests,

    [Parameter(Mandatory = $true)]
    [int]$Attempt,

    [Parameter(Mandatory = $true)]
    [int]$MaxAttempts
)

$isFinal = $Attempt -ge $MaxAttempts
$levelPrefix = if ($isFinal) { '::error::' } else { '::warning::' }
$fateSuffix = if ($isFinal) { ' — failing the step' } else { ' — retrying' }

function Write-Problem {
    param([string]$Message)
    Write-Host "$levelPrefix$SuiteName $Message (attempt $Attempt of $MaxAttempts)$fateSuffix"
}

if (-not (Test-Path -LiteralPath $ReportPath)) {
    Write-Problem "produced no xUnit report at $ReportPath — treating as a short run"
    exit 1
}

try {
    [xml]$report = Get-Content -LiteralPath $ReportPath -Raw
}
catch {
    Write-Problem "xUnit report at $ReportPath could not be parsed: $($_.Exception.Message)"
    exit 1
}

# SelectNodes, not $report.assemblies.assembly: PowerShell's XML adapter
# turns both attributes AND child elements into dot-properties, and this
# schema has both an `errors` ATTRIBUTE on <assembly> (the count) and an
# `errors` CHILD ELEMENT on <assembly> (the container for <error> details) —
# the same collision exists for the assemblies/assembly relationship in
# principle, so every read below goes through XmlElement's own
# GetAttribute()/SelectNodes() rather than a dot-property, which silently
# resolves to whichever of the two `errors` a first pass picked and made
# this exact check report "healthy" against a report with errors="1" in it.
$assemblies = @($report.SelectNodes('//assemblies/assembly'))
if ($assemblies.Count -eq 0) {
    Write-Problem "xUnit report at $ReportPath has no <assembly> element"
    exit 1
}

$totalErrors = 0
$totalTests = 0
$problems = @()

foreach ($assembly in $assemblies) {
    $assemblyErrors = [int]$assembly.GetAttribute('errors')
    $assemblyTotal = [int]$assembly.GetAttribute('total')
    $totalErrors += $assemblyErrors
    $totalTests += $assemblyTotal

    if ($assemblyErrors -gt 0) {
        $errorNodes = @($assembly.SelectNodes('errors/error'))
        if ($errorNodes.Count -eq 0) {
            # errors="N" with no <error> children would itself be a report
            # shape we've never seen — name that rather than saying nothing.
            $problems += "reported errors=$assemblyErrors with no <error> element naming them"
        }
        else {
            foreach ($errorNode in $errorNodes) {
                $type = if ($errorNode.GetAttribute('type')) { $errorNode.GetAttribute('type') } else { '(no type)' }
                $name = if ($errorNode.GetAttribute('name')) { $errorNode.GetAttribute('name') } else { '(no name)' }
                $problems += "reported errors=$assemblyErrors ($type`: $name)"
            }
        }
    }
}

if ($totalTests -lt $MinimumExpectedTests) {
    $problems += "ran $totalTests tests, below the expected floor of $MinimumExpectedTests"
}

if ($problems.Count -eq 0) {
    Write-Host "$SuiteName report healthy — total=$totalTests, errors=$totalErrors (attempt $Attempt of $MaxAttempts)"
    exit 0
}

foreach ($problem in $problems) {
    Write-Problem $problem
}
exit 1
