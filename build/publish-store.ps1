#Requires -Version 7
<#
.SYNOPSIS
    Submits a QRSnip update to the Microsoft Store with the Microsoft Store Developer CLI (msstore).

.DESCRIPTION
    1. Uploads the .msixupload as a new draft submission (copied from the last published one).
    2. Sets "What's new in this version" from this version's section of CHANGELOG.md.
    3. Optionally enables a gradual package rollout.
    4. Commits the submission for certification (unless -Draft).

    The app must already be live in the Store (the first submission is always made in
    Partner Center), and msstore must be signed in (`msstore reconfigure ...`).
    See docs/PUBLISHING.md.

.EXAMPLE
    ./build/publish-store.ps1 -PreviewNotes
    ./build/publish-store.ps1 -Package artifacts/QRSnip-1.0.1-store.msixupload -ProductId 9N0000000000
    ./build/publish-store.ps1 -Package ... -ProductId ... -RolloutPercentage 10 -Draft
#>
param(
    [string]$Package,
    [string]$ProductId,
    [ValidateRange(1, 100)]
    [int]$RolloutPercentage = 100,
    [switch]$Draft,
    [switch]$PreviewNotes
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version

function Get-ReleaseNotes([string]$Version) {
    # The Store shows plain text (max 1500 characters), so flatten the Markdown section.
    $lines = Get-Content (Join-Path $root 'CHANGELOG.md')
    $start = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match "^## \[$([regex]::Escape($Version))\]" })
    if ($start -lt 0) { throw "CHANGELOG.md has no '## [$Version]' section." }

    $notes = foreach ($line in $lines[($start + 1)..($lines.Count - 1)]) {
        if ($line -match '^## \[' -or $line -match '^\[[^\]]+\]: ') { break }
        $line -replace '^###\s+', '' -replace '^\s*[-*]\s+', '• ' -replace '\*\*|`', ''
    }

    $text = (($notes -join "`n") -replace "(`n){3,}", "`n`n").Trim()
    if ($text.Length -gt 1500) { $text = $text.Substring(0, 1497) + '...' }
    return $text
}

function Invoke-Store {
    $output = & msstore @args 2>&1 | Out-String
    if ($LASTEXITCODE) { throw "msstore $($args[0..1] -join ' ') failed:`n$output" }
    return $output
}

$notes = Get-ReleaseNotes $version
if ($PreviewNotes) {
    Write-Host "What's new in $version`:" -ForegroundColor Cyan
    Write-Host $notes
    return
}

if (-not $Package -or -not $ProductId) { throw 'Pass -Package and -ProductId (or -PreviewNotes).' }
if (-not (Test-Path $Package)) { throw "Package not found: $Package" }

[xml]$manifest = Get-Content (Join-Path $root 'src' 'QRSnip' 'Package.appxmanifest')
if ($manifest.Package.Identity.Publisher -eq 'CN=Nakul T') {
    throw 'Package.appxmanifest still has the placeholder identity. Copy Name/Publisher from Partner Center › Product identity first.'
}

Write-Host "Submitting QRSnip $version to the Microsoft Store (product $ProductId)" -ForegroundColor Cyan

# 1. Upload. 'publish' recreates the draft from the last published submission, so it must run first.
Invoke-Store publish (Resolve-Path $Package).Path -id $ProductId --noCommit | Write-Host

# 2-3. Release notes and rollout on the draft.
$raw = Invoke-Store submission get $ProductId
$submission = $raw.Substring($raw.IndexOf('{'), $raw.LastIndexOf('}') - $raw.IndexOf('{') + 1) | ConvertFrom-Json

foreach ($listing in $submission.Listings.PSObject.Properties) {
    $listing.Value.BaseListing.ReleaseNotes = $notes
}

if ($RolloutPercentage -lt 100) {
    $rollout = $submission.PackageDeliveryOptions.PackageRollout
    $rollout.IsPackageRollout = $true
    $rollout.PackageRolloutPercentage = $RolloutPercentage
    Write-Host "Gradual rollout: $RolloutPercentage %"
}

Invoke-Store submission update $ProductId ($submission | ConvertTo-Json -Depth 100 -Compress) | Write-Host

if ($Draft) {
    Write-Host 'Left as a draft. Review it in Partner Center, then run: msstore submission publish' $ProductId -ForegroundColor Yellow
    return
}

# 4. Send to certification. Certification itself takes hours, so don't wait for it here.
Invoke-Store submission publish $ProductId | Write-Host
Invoke-Store submission status $ProductId | Write-Host
Write-Host 'Submitted for certification.' -ForegroundColor Green
