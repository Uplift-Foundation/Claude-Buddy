# Builds the optional side-car speech engine and packs it for release.
#
#   .\tools\build-speech-engine.ps1
#   .\tools\build-speech-engine.ps1 -Install   # ...and drop it straight into
#                                              #    %APPDATA% for local testing
#
# Produces dist\ClaudeBuddySpeech-<version>-win-x64.zip, which NeuralSpeech
# downloads from the matching GitHub release. The app never contains this: see
# tools/ClaudeBuddySpeech/ClaudeBuddySpeech.csproj for why it is a separate
# downloaded process rather than a dependency.
#
# Windows-only, with tools/build-speech-engine.sh as its macOS twin. That split
# is not just convention: an osx-* engine has to be published on a Mac, because
# the SDK ad-hoc signs the apphost with `codesign` and Apple Silicon will not
# exec an unsigned arm64 binary, so -Rid osx-arm64 from here would produce a
# bundle that installs and then dies on launch.

[CmdletBinding()]
param(
    [switch] $Install,
    [string] $Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'ClaudeBuddySpeech\ClaudeBuddySpeech.csproj'

Push-Location $repoRoot
try {
    # Read from the *app's* csproj, not the engine's. ClaudeBuddy.csproj's
    # <Version> is the single source of truth for the shipped version — the
    # installer script and the release workflow both parse that same element — and
    # the engine ships in the app's own release under the same tag. NeuralSpeech
    # derives the version it asks for from the app assembly, so taking it from
    # anywhere else here is how the filename and the URL drift apart.
    $appProject = Join-Path $repoRoot 'ClaudeBuddy.csproj'
    $version = ([xml](Get-Content -LiteralPath $appProject -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "Could not read <Version> from $appProject" }
    Write-Host "==> Speech engine $version ($Rid)"

    Write-Host "==> Publishing"
    & dotnet publish $project -c Release -r $Rid -p:DebugType=none --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

    $publish = Join-Path $PSScriptRoot "ClaudeBuddySpeech\bin\Release\net10.0\$Rid\publish"
    if (-not (Test-Path -LiteralPath $publish)) { throw "Publish output not found at $publish" }

    # The voices are the whole reason this script exists rather than a bare
    # `dotnet publish`. KokoroSharp ships its 54 voice files by way of an MSBuild
    # Copy that runs AfterTargets="Build" and produces no items, so they land in
    # bin\ and `dotnet publish` never carries them anywhere. Measured: a published
    # engine has no voices\ directory at all, and would exit "no voices directory"
    # forever while every `dotnet run` looked perfect. Copied explicitly here so
    # what ships is what was tested.
    $voicesSource = Join-Path $PSScriptRoot "ClaudeBuddySpeech\bin\Release\net10.0\$Rid\voices"
    if (-not (Test-Path -LiteralPath $voicesSource)) {
        throw "No voices at $voicesSource — the KokoroSharp copy target did not run"
    }

    $voicesTarget = Join-Path $publish 'voices'
    New-Item -ItemType Directory -Path $voicesTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $voicesSource '*.npy') -Destination $voicesTarget -Force

    $voiceCount = (Get-ChildItem -LiteralPath $voicesTarget -Filter *.npy).Count
    Write-Host "==> Bundled $voiceCount voices"
    if ($voiceCount -eq 0) { throw "No .npy voices were copied" }

    # Apache-2.0, unlike this repo's MIT: carried alongside so the attribution
    # travels with the bytes it applies to.
    $license = Join-Path $voicesSource 'LICENSE'
    if (Test-Path -LiteralPath $license) {
        Copy-Item -LiteralPath $license -Destination (Join-Path $voicesTarget 'LICENSE') -Force
    }

    $dist = Join-Path $repoRoot 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $zip = Join-Path $dist "ClaudeBuddySpeech-$version-$Rid.zip"

    Write-Host "==> Packing"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip

    $mb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
    Write-Host "==> Built $zip ($mb MB)"

    if ($Install) {
        # Straight into the layout NeuralSpeech expects, so the feature can be
        # tested end to end before any release exists to download from.
        $target = Join-Path $env:APPDATA "ClaudeBuddy\speech-engine\$version"
        Write-Host "==> Installing to $target"
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -Path (Join-Path $publish '*') -Destination $target -Recurse -Force
        Write-Host "==> Installed. The model downloads separately on first enable."
    }
}
finally {
    Pop-Location
}
