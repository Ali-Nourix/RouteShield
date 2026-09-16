<#
.SYNOPSIS
    Builds a distributable RouteShield package.

.DESCRIPTION
    Fetches the official sing-box release for the target runtime, verifies it against the
    digest GitHub publishes for the asset, publishes the app as a self-contained single file,
    and writes artifacts\RouteShield-<version>-<runtime>.zip.

    Needs nothing installed beforehand: when the machine has no .NET 8 SDK, a private copy
    is installed under .tools and used only for this build.
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

function Get-HostArchitecture {
    if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') {
        return 'arm64'
    }

    return 'x64'
}

<#
.SYNOPSIS
    Returns a dotnet that can build this project, installing one if the machine has none.
.DESCRIPTION
    An SDK of major version 8 or newer can target net8.0, so an existing install is reused
    whenever possible. Otherwise Microsoft's own installer puts a private copy under .tools,
    which keeps a one-step build from having to change anything else on the machine.
#>
function Resolve-Dotnet {
    $existing = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($existing) {
        $sdks = & $existing.Source --list-sdks 2>$null
        $usable = $sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 }

        if ($usable) {
            Step "Using the .NET SDK already on this machine ($(($usable | Select-Object -Last 1) -split ' ' | Select-Object -First 1))"
            return $existing.Source
        }
    }

    $private = Join-Path $Tools 'dotnet\dotnet.exe'
    if (Test-Path $private) {
        Step 'Using the private .NET SDK under .tools'
        return $private
    }

    Step 'No .NET 8 SDK was found; installing a private copy under .tools (this happens once)'

    New-Item -ItemType Directory -Force -Path $Tools | Out-Null
    $installer = Join-Path $Tools 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer `
        -Channel 8.0 -Architecture (Get-HostArchitecture) -InstallDir (Join-Path $Tools 'dotnet') -NoPath

    if (-not (Test-Path $private)) {
        throw 'The .NET SDK could not be installed. Install it by hand from https://dotnet.microsoft.com/download/dotnet/8.0 and run build.cmd again.'
    }

    return $private
}

function Get-ProjectVersion {
    $declared = ([xml](Get-Content $Project -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } |
        Select-Object -First 1

    if (-not $declared) {
        throw "RouteShield.csproj declares no <Version>. Pass -Version to name the package."
    }

    return [string]$declared
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

    & $Dotnet @arguments
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

    foreach ($document in 'README.md', 'README.fa.md', 'CHANGELOG.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'SECURITY.md') {
        $path = Join-Path $Root $document
        if (Test-Path $path) {
            Copy-Item $path $Dist -Force
        }
    }
}

<#
.SYNOPSIS
    Stages both browser extensions next to the app and zips each for the release.
.DESCRIPTION
    Each browser gets the shared popup and bridge client plus its own manifest and
    background script. The manifest version is rewritten to the package version so the
    add-on and the app it talks to always report the same number.
#>
function Copy-Extensions {
    Step 'Packaging the browser extensions'

    $source = Join-Path $Root 'extension'
    $target = Join-Path $Dist 'extensions'
    New-Item -ItemType Directory -Force -Path $Artifacts, $target | Out-Null

    foreach ($browser in 'firefox', 'chrome') {
        $staging = Join-Path $target $browser
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $staging | Out-Null

        Copy-Item (Join-Path $source 'shared') (Join-Path $staging 'shared') -Recurse -Force
        Copy-Item (Join-Path $source "$browser\*") $staging -Recurse -Force
        Copy-Item (Join-Path $source 'README.md') $staging -Force

        $manifestPath = Join-Path $staging 'manifest.json'
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $manifest.version = ($PackageVersion -split '-')[0]
        $manifest | ConvertTo-Json -Depth 10 | Set-Content $manifestPath -Encoding utf8

        $package = Join-Path $Artifacts "RouteShield-Extension-$browser-$PackageVersion.zip"
        Remove-Item $package -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $package -CompressionLevel Optimal
        Write-Host "  $package"
    }
}

function Compress-Package {
    Step 'Writing the package'

    New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
    $package = Join-Path $Artifacts "RouteShield-$PackageVersion-$Runtime.zip"
    Remove-Item $package -Force -ErrorAction SilentlyContinue

    Compress-Archive -Path (Join-Path $Dist '*') -DestinationPath $package -CompressionLevel Optimal

    $hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$package.sha256" -Value "$hash  $(Split-Path $package -Leaf)" -Encoding ascii

    Get-ChildItem $Artifacts -Filter 'RouteShield-Extension-*.zip' | ForEach-Object {
        $extensionHash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -Path "$($_.FullName).sha256" -Value "$extensionHash  $($_.Name)" -Encoding ascii
    }

    Write-Host ''
    Write-Host "Package: $package" -ForegroundColor Green
    Write-Host "SHA-256: $hash" -ForegroundColor Green
}

$Dotnet = Resolve-Dotnet
$PackageVersion = if ($Version) { $Version } else { Get-ProjectVersion }
Install-SingBox
Publish-App
Copy-Core
Copy-Extensions
Compress-Package
