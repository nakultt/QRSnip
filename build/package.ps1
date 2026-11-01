#Requires -Version 7
<#
.SYNOPSIS
    Builds QRSnip release artifacts.

.DESCRIPTION
    - Portable zips (unpackaged, self-contained, trimmed) for x64 and ARM64.
    - The GitHub installer and its update feed (Velopack), per architecture in
      <Output>/velopack/win-<arch>: QRSnipApp-win-<arch>-Setup.exe, full (and, when the
      previous release was downloaded into that folder first, delta) .nupkg packages and
      releases.win-<arch>.json. The installer is per-user, needs no admin rights, adds a Start
      menu entry and shows up in Installed apps; the app then updates itself from GitHub.
    - A Microsoft Store upload bundle (.msixupload, x64 + ARM64).

    The version comes from Directory.Build.props; Package.appxmanifest must match it. The
    installer's release notes are this version's section of CHANGELOG.md.

.EXAMPLE
    ./build/package.ps1
    ./build/package.ps1 -SkipStore -Architectures x64
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..' 'artifacts'),
    [ValidateSet('x64', 'arm64')]
    [string[]]$Architectures = @('x64', 'arm64'),
    [switch]$SkipPortable,
    [switch]$SkipInstaller,
    [switch]$SkipStore,
    # Optional signing command for the installer and the files it installs, run by vpk with
    # {{file}} replaced by the file(s) to sign. See docs/PUBLISHING.md.
    [string]$SignTemplate
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src' 'QRSnip' 'QRSnip.csproj'

# The installer's id decides its folder: %LOCALAPPDATA%\QRSnipApp. It must not be "QRSnip",
# because the installer empties an existing folder and %LOCALAPPDATA%\QRSnip holds the
# settings and history that portable copies also use.
$packId = 'QRSnipApp'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
[xml]$manifest = Get-Content (Join-Path $root 'src' 'QRSnip' 'Package.appxmanifest')
$packageVersion = $manifest.Package.Identity.Version
if ($packageVersion -ne "$version.0") {
    throw "Package.appxmanifest has version $packageVersion but Directory.Build.props has $version. Set the manifest to $version.0."
}

# MSBuild resolves relative paths against the project, so always pass absolute ones.
$Output = (New-Item -ItemType Directory -Force $Output).FullName
Write-Host "QRSnip $version -> $Output" -ForegroundColor Cyan

function Get-ChangelogSection([string]$Version) {
    $lines = Get-Content (Join-Path $root 'CHANGELOG.md')
    $start = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match "^## \[$([regex]::Escape($Version))\]" })
    if ($start -lt 0) { return $null }
    $section = foreach ($line in $lines[($start + 1)..($lines.Count - 1)]) {
        if ($line -match '^## \[' -or $line -match '^\[[^\]]+\]: ') { break }
        $line
    }
    return ($section -join "`n").Trim()
}

if (-not $SkipInstaller -or -not $SkipPortable) {
    if (-not $SkipInstaller) {
        dotnet tool restore
        if ($LASTEXITCODE) { throw 'dotnet tool restore failed (vpk).' }

        $notes = Join-Path $Output "release-notes-$version.md"
        $section = Get-ChangelogSection $version
        if ($section) { Set-Content -Path $notes -Value $section -Encoding utf8 }
        else { Write-Warning "CHANGELOG.md has no '## [$version]' section; the installer will have no release notes." }
    }

    foreach ($arch in $Architectures) {
        $publish = Join-Path $Output "publish-$arch"
        dotnet publish $project -c Release -r "win-$arch" -p:Platform=$arch -p:Portable=true -o $publish
        if ($LASTEXITCODE) { throw "Portable $arch build failed." }

        if (-not $SkipInstaller) {
            # Packs (and signs, if asked) the published files, so do it before zipping them.
            $channel = "win-$arch"
            $feed = (New-Item -ItemType Directory -Force (Join-Path $Output 'velopack' $channel)).FullName
            $vpk = @(
                'vpk', 'pack',
                '--packId', $packId, '--packVersion', $version,
                '--packTitle', 'QRSnip', '--packAuthors', 'Nakul T',
                '--packDir', $publish, '--mainExe', 'QRSnip.exe',
                '--icon', (Join-Path $root 'src' 'QRSnip' 'Assets' 'AppIcon.ico'),
                '--channel', $channel, '--runtime', "win-$arch",
                '--shortcuts', 'StartMenuRoot', '--noPortable',
                '--outputDir', $feed
            )
            if (Test-Path $notes) { $vpk += @('--releaseNotes', $notes) }
            if ($SignTemplate) { $vpk += @('--signTemplate', $SignTemplate) }
            dotnet @vpk
            if ($LASTEXITCODE) { throw "Installer $arch build failed." }
            Get-ChildItem $feed -Filter '*Setup.exe' | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Green }
        }

        if (-not $SkipPortable) {
            $zip = Join-Path $Output "QRSnip-$version-win-$arch-portable.zip"
            if (Test-Path $zip) { Remove-Item $zip }
            Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
            Write-Host "  $zip" -ForegroundColor Green
        }

        Remove-Item $publish -Recurse -Force
    }
}

if (-not $SkipStore) {
    $store = Join-Path $Output 'store'
    dotnet build $project -c Release -p:Platform=x64 `
        -p:UapAppxPackageBuildMode=StoreUpload -p:AppxBundle=Always '-p:AppxBundlePlatforms=x64|arm64' `
        -p:GenerateAppxPackageOnBuild=true "-p:AppxPackageDir=$store\"
    if ($LASTEXITCODE) { throw 'Store package build failed.' }

    $upload = Get-ChildItem $store -Filter '*.msixupload' | Select-Object -First 1
    $target = Join-Path $Output "QRSnip-$version-store.msixupload"
    Move-Item $upload.FullName $target -Force
    Remove-Item $store -Recurse -Force
    Write-Host "  $target" -ForegroundColor Green
}
