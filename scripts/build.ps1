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

<#
.SYNOPSIS
    Clears the previous output, and explains the one reason that usually fails.
.DESCRIPTION
    Windows will not delete an executable that is running, and the most common thing running
    from dist\ is the RouteShield you just tested. Killing it here would leave its tunnel and
    firewall rules behind, so the build stops and asks for a clean quit instead.
#>
function Clear-Dist {
    $exe = Join-Path $Dist 'RouteShield.exe'

    $running = Get-Process -Name RouteShield -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Dist, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        throw "RouteShield is still running from $Dist. Quit it first (tray icon -> Quit), then run build.cmd again."
    }

    Remove-Item $Dist -Recurse -Force -ErrorAction SilentlyContinue

    if (Test-Path $exe) {
        throw "The previous build's RouteShield.exe in $Dist is locked - usually because it is still running, or an antivirus scan is holding it. Quit RouteShield (tray icon -> Quit), wait a moment, and run build.cmd again."
    }

    New-Item -ItemType Directory -Force -Path $Dist | Out-Null
}

function Publish-App {
    Step "Publishing RouteShield ($Configuration, $Runtime)"

    Clear-Dist

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
    Writes a zip the way browsers expect one: files at the root, entry names with forward slashes.
.DESCRIPTION
    Compress-Archive on Windows PowerShell 5.1 records entry names with backslashes
    ("shared\popup.html"), and Firefox refuses such an archive as corrupt. This walks the
    staged folder itself and names every entry with "/", which is what Mozilla's own
    web-ext build produces and what both browsers read.
#>
function New-ExtensionArchive([string]$Source, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

    Remove-Item $Destination -Force -ErrorAction SilentlyContinue

    $root = (Resolve-Path $Source).Path.TrimEnd('\', '/')
    $stream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem $root -Recurse -File | Sort-Object FullName | ForEach-Object {
                $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $_.FullName, $relative, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

<#
.SYNOPSIS
    Checks a staged add-on the way Mozilla's tooling would, when that tooling is installed.
.DESCRIPTION
    web-ext lint is the check AMO runs on upload. It is not fetched here — a local build must
    not depend on npm — but when web-ext is already on the PATH the staged Firefox add-on is
    run through it, and lint errors fail the build. CI installs web-ext and always lints.
#>
function Test-FirefoxAddon([string]$Staging) {
    $webExt = Get-Command web-ext -ErrorAction SilentlyContinue
    if (-not $webExt) {
        Write-Host '  web-ext is not installed; skipping the add-on lint (npm install -g web-ext to enable it).'
        return
    }

    & $webExt.Source lint --source-dir $Staging --self-hosted --no-config-discovery
    Assert-ExitCode 'web-ext lint'
}

<#
.SYNOPSIS
    Stages both browser extensions next to the app and packages each for the release.
.DESCRIPTION
    Each browser gets the shared popup and bridge client plus its own manifest and
    background script. The manifest version is rewritten to the package version so the
    add-on and the app it talks to always report the same number.

    Firefox's package is an .xpi: a zip of the add-on's files with manifest.json at the root,
    as https://extensionworkshop.com/documentation/publish/package-your-extension/ describes.
    Developer Edition and Nightly install it directly once xpinstall.signatures.required is
    off; release Firefox needs it signed by AMO, or loaded temporarily from about:debugging.
    Chrome's is the same layout as a .zip, for "Load unpacked" after extraction.
#>
function Copy-Extensions {
    Step 'Packaging the browser extensions'

    $source = Join-Path $Root 'extension'
    $target = Join-Path $Dist 'extensions'
    New-Item -ItemType Directory -Force -Path $Artifacts, $target | Out-Null

    $manifestVersion = ($PackageVersion -split '-')[0]

    foreach ($browser in 'firefox', 'chrome') {
        $staging = Join-Path $target $browser
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $staging | Out-Null

        Copy-Item (Join-Path $source 'shared') (Join-Path $staging 'shared') -Recurse -Force
        Copy-Item (Join-Path (Join-Path $source $browser) '*') $staging -Recurse -Force
        Copy-Item (Join-Path $source 'README.md') $staging -Force

        $manifestPath = Join-Path $staging 'manifest.json'
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $manifest.version = $manifestVersion
        $manifest | ConvertTo-Json -Depth 10 | Set-Content $manifestPath -Encoding utf8

        $extension = if ($browser -eq 'firefox') { 'xpi' } else { 'zip' }
        $package = Join-Path $Artifacts "RouteShield-Extension-$browser-$PackageVersion.$extension"

        if ($browser -eq 'firefox') {
            Test-FirefoxAddon $staging
        }

        New-ExtensionArchive -Source $staging -Destination $package
        Write-Host "  $package"

        # The installable file also sits beside the app, where "Open extension folder" leads.
        Copy-Item $package (Join-Path $target "RouteShield-$browser.$extension") -Force
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

    Get-ChildItem $Artifacts -File | Where-Object { $_.Name -like 'RouteShield-Extension-*.zip' -or $_.Name -like 'RouteShield-Extension-*.xpi' } | ForEach-Object {
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
