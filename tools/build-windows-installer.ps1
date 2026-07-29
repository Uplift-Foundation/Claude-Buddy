# Builds the Claude Buddy Windows installer.
#
#   .\tools\build-windows-installer.ps1
#   .\tools\build-windows-installer.ps1 -SkipPublish   # reuse an existing publish
#
# Produces dist\ClaudeBuddy-<version>-win-x64-setup.exe.
#
# Requires the .NET SDK and Inno Setup 6 (iscc.exe). Install Inno with either:
#   winget install -e --id JRSoftware.InnoSetup
#   choco install innosetup
#
# Signing is optional and off unless WINDOWS_CERT_THUMBPRINT names a code
# signing certificate in the current user's store. Unsigned is workable for a
# beta — SmartScreen shows a "More info -> Run anyway" warning rather than
# refusing outright — but signed is obviously better if a certificate exists.

[CmdletBinding()]
param(
    [switch] $SkipPublish,
    [string] $Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'

# signtool.exe ships with the Windows SDK and is not on PATH by default, so it
# has to be hunted down under the versioned SDK bin directories. Newest version
# first, because an old SDK's signtool may not understand modern timestamp URLs.
function Resolve-SignTool {
    $found = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }

    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $candidate = Get-ChildItem -LiteralPath $root -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    throw 'signtool.exe not found. Install the Windows SDK, or clear WINDOWS_CERT_THUMBPRINT to skip signing.'
}

function Invoke-SignTool {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Thumbprint
    )

    $signtool = Resolve-SignTool
    # /fd and /td sha256: SHA-1 signatures are rejected by current Windows.
    # /tr countersigns via a timestamp authority so the signature stays valid
    # after the certificate itself expires.
    & $signtool sign /sha1 $Thumbprint /fd sha256 `
        /tr http://timestamp.digicert.com /td sha256 `
        /d 'Claude Buddy' $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $Path ($LASTEXITCODE)" }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    # The csproj owns the version; parsing it here keeps the installer filename,
    # the Add/Remove Programs entry and the compiled assembly from drifting apart.
    $csproj = Join-Path $repoRoot 'ClaudeBuddy.csproj'
    # -Raw so the XML cast gets one string rather than an array of lines.
    # PropertyGroup may be a collection, hence filtering for the one that
    # actually carries a Version.
    $version = ([xml](Get-Content -LiteralPath $csproj -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "Could not read <Version> from $csproj" }
    Write-Host "==> Version $version"

    $dist = Join-Path $repoRoot 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null

    # Inno's LicenseFile picks text vs RTF by extension and the repo's LICENSE
    # has none, so hand it a .txt copy.
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') `
              -Destination (Join-Path $dist 'LICENSE.txt') -Force

    if (-not $SkipPublish) {
        Write-Host "==> Publishing ($Rid)"
        # Single-file here, unlike the macOS bundle: the installer lays down one
        # ClaudeBuddy.exe, and self-extraction on launch is an acceptable trade
        # for not scattering 200 runtime files through the install directory.
        & dotnet publish $csproj -c Release -r $Rid `
            -p:DebugType=none `
            --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    }

    $exe = Join-Path $repoRoot "bin\Release\net8.0\$Rid\publish\ClaudeBuddy.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Published executable not found at $exe"
    }

    # Sign the app before packaging, so the signature ends up inside the installer
    # rather than only on it.
    $thumbprint = $env:WINDOWS_CERT_THUMBPRINT
    if ($thumbprint) {
        Write-Host "==> Signing ClaudeBuddy.exe"
        Invoke-SignTool -Path $exe -Thumbprint $thumbprint
    }

    $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if (-not $iscc) {
        foreach ($candidate in @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
            if (Test-Path -LiteralPath $candidate) { $iscc = $candidate; break }
        }
    } else {
        $iscc = $iscc.Source
    }
    if (-not $iscc) {
        throw "Inno Setup's ISCC.exe not found. Install it with: winget install -e --id JRSoftware.InnoSetup"
    }

    Write-Host "==> Compiling installer"
    # ISCC wants its options ahead of the script filename.
    & $iscc "/DVersion=$version" (Join-Path $PSScriptRoot 'ClaudeBuddy.iss') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }

    $setup = Join-Path $repoRoot "dist\ClaudeBuddy-$version-win-x64-setup.exe"
    if (-not (Test-Path -LiteralPath $setup)) { throw "Installer not produced at $setup" }

    if ($thumbprint) {
        Write-Host "==> Signing installer"
        Invoke-SignTool -Path $setup -Thumbprint $thumbprint
    } else {
        Write-Host "==> Not signed (WINDOWS_CERT_THUMBPRINT unset) — SmartScreen will warn"
    }

    Write-Host "==> Built $setup"
}
finally {
    Pop-Location
}
