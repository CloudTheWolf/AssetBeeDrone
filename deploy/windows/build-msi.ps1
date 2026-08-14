param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,
    [string] $Version,
    [string] $OutputDirectory,
    # Optional Authenticode signing (strongly recommended to reduce SmartScreen / PUP false positives).
    [string] $SignThumbprint,
    [string] $SignTimestampUrl = 'http://timestamp.digicert.com',
    [string] $SignToolPath
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..\..')).Path

if (-not $Version) {
    $Version = & (Join-Path $repoRoot 'deploy\packaging\get-version.ps1') -RepoRoot $repoRoot
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'dist'
}

$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$exe = Join-Path $PublishDirectory 'AssetBee.Drone.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Published binary not found: $exe"
}

Write-Host "Building MSI version $Version"
Write-Host 'Publishing tray application into publish directory...'
& dotnet publish (Join-Path $repoRoot 'AssetBeeDrone.Tray\AssetBeeDrone.Tray.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o $PublishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Tray publish failed with exit code $LASTEXITCODE"
}

$tray = Join-Path $PublishDirectory 'AssetBee.Drone.Tray.exe'
if (-not (Test-Path -LiteralPath $tray)) {
    throw "Tray binary not found after publish: $tray"
}

$helperOut = Join-Path $scriptDir 'obj\helper'
Write-Host 'Publishing MSI helper...'
& dotnet publish (Join-Path $repoRoot 'AssetBeeDrone.MsiHelper\AssetBeeDrone.MsiHelper.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishAot=true `
    -p:Version=$Version `
    -o $helperOut
if ($LASTEXITCODE -ne 0) {
    throw "MSI helper publish failed with exit code $LASTEXITCODE"
}

$helper = Join-Path $helperOut 'AssetBee.Drone.MsiHelper.exe'
if (-not (Test-Path -LiteralPath $helper)) {
    throw "MSI helper binary not found after publish: $helper"
}

$wixVersion = '7.0.0'
$wixEulaId = 'wix7'
$wixToolDir = Join-Path $env:USERPROFILE '.dotnet\tools'
$wixExe = Join-Path $wixToolDir 'wix.exe'

# Pin WiX CLI + extensions so mismatched installs cannot break the build.
Write-Host "Ensuring WiX CLI $wixVersion (dotnet tool)..."

& dotnet tool update --global wix --version $wixVersion
if ($LASTEXITCODE -ne 0) {
    & dotnet tool install --global wix --version $wixVersion

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install WiX CLI $wixVersion"
    }
}

if (-not (Test-Path -LiteralPath $wixExe)) {
    throw "WiX CLI not found at $wixExe after install"
}

$env:PATH = "$wixToolDir;$env:PATH"

& $wixExe --version | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Failed to execute WiX CLI"
}

# WiX 7 requires explicit OSMF EULA acceptance.
Write-Host "Accepting WiX $wixVersion EULA..."
& $wixExe eula accept $wixEulaId

if ($LASTEXITCODE -ne 0) {
    throw "Failed to accept WiX EULA with exit code $LASTEXITCODE"
}

Write-Host "Ensuring WiX Util extension..."
& $wixExe extension add -g "WixToolset.Util.wixext/$wixVersion"

if ($LASTEXITCODE -ne 0) {
    throw "Failed to install WixToolset.Util.wixext/$wixVersion with exit code $LASTEXITCODE"
}

Write-Host "Ensuring WiX UI extension..."

& $wixExe extension add -g "WixToolset.UI.wixext/$wixVersion"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to install WixToolset.Util.wixext/$wixVersion with exit code $LASTEXITCODE"
}

$env:PATH = "$wixToolDir;$env:PATH"


$outputMsi = Join-Path $OutputDirectory "AssetBee.Drone-$Version-win-x64.msi"

function Invoke-AuthenticodeSign {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Paths
    )

    $signtool = $SignToolPath
    if (-not $signtool) {
        $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    }
    if (-not $signtool -or -not (Test-Path -LiteralPath $signtool)) {
        throw 'signtool.exe not found. Install the Windows SDK or pass -SignToolPath.'
    }

    foreach ($path in $Paths) {
        Write-Host "Signing $path"
        & $signtool sign `
            /sha1 $SignThumbprint `
            /fd SHA256 `
            /td SHA256 `
            /tr $SignTimestampUrl `
            /v `
            $path
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for $path with exit code $LASTEXITCODE"
        }
    }
}

if ($SignThumbprint) {
    # Sign payload EXEs before they are embedded in the MSI.
    Invoke-AuthenticodeSign -Paths @($exe, $tray, $helper)
} else {
    Write-Host 'Skipping Authenticode signing (pass -SignThumbprint to enable).'
}

# -acceptEula is required for WiX v7+ (Open Source Maintenance Fee). See https://docs.firegiant.com/wix/osmf/
& $wixExe build `
    -acceptEula $wixEulaId `
    (Join-Path $scriptDir 'AssetBeeDrone.wxs') `
    (Join-Path $scriptDir 'AssetBeeDroneUI.wxs') `
    -ext "WixToolset.Util.wixext/$wixVersion" `
    -ext "WixToolset.UI.wixext/$wixVersion" `
    -d "Version=$Version" `
    -d "PublishDir=$PublishDirectory" `
    -d "SourceDir=$scriptDir" `
    -d "HelperPath=$helper" `
    -arch x64 `
    -o $outputMsi

if ($LASTEXITCODE -ne 0) {
    throw "wix build failed with exit code $LASTEXITCODE"
}

if ($SignThumbprint) {
    Invoke-AuthenticodeSign -Paths @($outputMsi)
}

Write-Host "Created $outputMsi"
