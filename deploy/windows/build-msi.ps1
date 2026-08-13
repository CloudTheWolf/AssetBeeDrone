param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,
    [string] $Version,
    [string] $OutputDirectory
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

$savePendingPath = Join-Path $scriptDir 'SavePendingSettings.ps1'
$loadExistingPath = Join-Path $scriptDir 'LoadExistingSettings.ps1'
$preservePath = Join-Path $scriptDir 'PreserveExistingSettings.ps1'
$uninstallRelatedPath = Join-Path $scriptDir 'UninstallRelatedProducts.ps1'
foreach ($p in @($savePendingPath, $loadExistingPath, $preservePath, $uninstallRelatedPath)) {
    if (-not (Test-Path -LiteralPath $p)) {
        throw "Missing $p"
    }
}

# Embed scripts as base64; ExtractMsiScripts decodes with certutil (no [Type] literals in MSI strings).
$savePendingB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($savePendingPath))
$loadExistingB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($loadExistingPath))
$preserveSettingsB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($preservePath))
$uninstallRelatedB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($uninstallRelatedPath))

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
    -d "SavePendingB64=$savePendingB64" `
    -d "LoadExistingB64=$loadExistingB64" `
    -d "PreserveSettingsB64=$preserveSettingsB64" `
    -d "UninstallRelatedB64=$uninstallRelatedB64" `
    -arch x64 `
    -o $outputMsi

if ($LASTEXITCODE -ne 0) {
    throw "wix build failed with exit code $LASTEXITCODE"
}

Write-Host "Created $outputMsi"
