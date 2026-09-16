<#
.SYNOPSIS
    Builds a distributable RouteShield package.

.DESCRIPTION
    Fetches the official sing-box release for the target runtime, verifies it against the
    digest GitHub publishes for the asset, publishes the app as a self-contained single file,
    and writes artifacts\RouteShield-<version>-<runtime>.zip.
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$Version,

    [string]$SingBoxVersion = '1.14.0',

    [switch]$SkipCoreDownload
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'src\RouteShield\RouteShield.csproj'
$Tools = Join-Path $Root '.tools'
$Artifacts = Join-Path $Root 'artifacts'
$Dist = Join-Path $Root "dist\RouteShield-$Runtime"
$CoreCache = Join-Path $Root 'third_party\sing-box'
$CoreExe = Join-Path $CoreCache 'sing-box.exe'
$CoreLicense = Join-Path $CoreCache 'LICENSE'

function Step([string]$Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Assert-ExitCode([string]$Name) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

function Get-SingBoxArchiveName {
    $architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'amd64' }
    return "sing-box-$SingBoxVersion-windows-$architecture.zip"
}

function Install-SingBox {
    if ($SkipCoreDownload) {
        if (-not (Test-Path $CoreExe)) {
            throw "sing-box.exe is missing at $CoreExe and -SkipCoreDownload was given."
        }

        Step 'Using the cached sing-box executable'
        return
    }

    if (Test-Path $CoreExe) {
        Step 'Using the cached sing-box executable'
        return
    }

    Step "Downloading sing-box $SingBoxVersion from the official release"

    New-Item -ItemType Directory -Force -Path $Tools, $CoreCache | Out-Null

    $headers = @{ 'User-Agent' = 'RouteShield-Builder'; 'Accept' = 'application/vnd.github+json' }
    if ($env:GITHUB_TOKEN) {
        $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN"
    }

    $release = Invoke-RestMethod -Headers $headers -Uri `
        "https://api.github.com/repos/SagerNet/sing-box/releases/tags/v$SingBoxVersion"

    $archiveName = Get-SingBoxArchiveName
    $asset = $release.assets | Where-Object { $_.name -eq $archiveName } | Select-Object -First 1
    if (-not $asset) {
        throw "The sing-box release does not publish $archiveName."
    }

    $url = [string]$asset.browser_download_url
    if (-not $url.StartsWith('https://github.com/SagerNet/sing-box/releases/download/')) {
        throw "Unexpected sing-box asset URL: $url"
    }

    $archive = Join-Path $Tools $archiveName
    Write-Host "  $url"
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing

    # GitHub reports a digest for release assets; when it does, the download must match it.
    if ($asset.digest -and $asset.digest.StartsWith('sha256:')) {
        $expected = $asset.digest.Substring(7).ToLowerInvariant()
        $actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()

        if ($expected -ne $actual) {
            Remove-Item $archive -Force
            throw "sing-box checksum mismatch. Expected $expected but got $actual."
        }

        Write-Host "  SHA-256 verified: $actual" -ForegroundColor Green
    }
    else {
        Write-Warning 'GitHub did not report a digest for this asset; the download could not be checksum-verified.'
    }

    $extract = Join-Path $Tools 'sing-box-extract'
    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $archive -DestinationPath $extract -Force

    $found = Get-ChildItem $extract -Recurse -Filter 'sing-box.exe' | Select-Object -First 1
    if (-not $found) {
        throw 'sing-box.exe was not present in the downloaded archive.'
    }

    Copy-Item $found.FullName $CoreExe -Force

    $license = Get-ChildItem $extract -Recurse -Filter 'LICENSE' | Select-Object -First 1
    if ($license) {
        Copy-Item $license.FullName $CoreLicense -Force
    }

    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
}

function Publish-App {
    Step "Publishing RouteShield ($Configuration, $Runtime)"

    Remove-Item $Dist -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $Dist | Out-Null

    $arguments = @(
        'publish', $Project,
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', 'true',
        '-o', $Dist,
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true'
    )

    if ($Version) {
        $arguments += "-p:Version=$Version"
    }

    dotnet @arguments
    Assert-ExitCode 'dotnet publish'
}

function Copy-Core {
    Step 'Bundling the sing-box core'

    $coreDirectory = Join-Path $Dist 'core'
    New-Item -ItemType Directory -Force -Path $coreDirectory | Out-Null
    Copy-Item $CoreExe (Join-Path $coreDirectory 'sing-box.exe') -Force

    if (Test-Path $CoreLicense) {
        Copy-Item $CoreLicense (Join-Path $coreDirectory 'sing-box-LICENSE.txt') -Force
    }

    foreach ($document in 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'SECURITY.md') {
        $path = Join-Path $Root $document
        if (Test-Path $path) {
            Copy-Item $path $Dist -Force
        }
    }
}

function Compress-Package {
    Step 'Writing the package'

    $resolved = $Version
    if (-not $resolved) {
        $resolved = (Get-Item (Join-Path $Dist 'RouteShield.exe')).VersionInfo.ProductVersion
        $resolved = ($resolved -split '\+')[0]
    }

    New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
    $package = Join-Path $Artifacts "RouteShield-$resolved-$Runtime.zip"
    Remove-Item $package -Force -ErrorAction SilentlyContinue

    Compress-Archive -Path (Join-Path $Dist '*') -DestinationPath $package -CompressionLevel Optimal

    $hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$package.sha256" -Value "$hash  $(Split-Path $package -Leaf)" -Encoding ascii

    Write-Host ''
    Write-Host "Package: $package" -ForegroundColor Green
    Write-Host "SHA-256: $hash" -ForegroundColor Green
}

Install-SingBox
Publish-App
Copy-Core
Compress-Package
