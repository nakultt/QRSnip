#Requires -Version 7
<#
.SYNOPSIS
    Builds QRSnip release artifacts.

.DESCRIPTION
    - Portable zips (unpackaged, self-contained, trimmed) for x64 and ARM64.
    - A Microsoft Store upload bundle (.msixupload, x64 + ARM64).

    The version comes from Directory.Build.props; Package.appxmanifest must match it.

.EXAMPLE
    ./build/package.ps1
    ./build/package.ps1 -SkipStore
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..' 'artifacts'),
    [switch]$SkipPortable,
    [switch]$SkipStore
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src' 'QRSnip' 'QRSnip.csproj'

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

if (-not $SkipPortable) {
    foreach ($arch in 'x64', 'arm64') {
        $publish = Join-Path $Output "publish-$arch"
        dotnet publish $project -c Release -r "win-$arch" -p:Platform=$arch -p:Portable=true -o $publish
        if ($LASTEXITCODE) { throw "Portable $arch build failed." }

        $zip = Join-Path $Output "QRSnip-$version-win-$arch-portable.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
        Remove-Item $publish -Recurse -Force
        Write-Host "  $zip" -ForegroundColor Green
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
