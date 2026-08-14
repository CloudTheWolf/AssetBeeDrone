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

# Build -EncodedCommand payloads that materialize helper scripts.
# Avoids certutil -decode (a common AV/PUP heuristic) and keeps '[' out of MSI formatted strings.
function New-ExtractEncodedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $DestinationInit,
        [Parameter(Mandatory = $true)]
        [hashtable] $Files
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('$ErrorActionPreference = ''Stop''')
    foreach ($line in ($DestinationInit -split '\r?\n')) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $lines.Add($line)
        }
    }

    foreach ($entry in $Files.GetEnumerator()) {
        $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($entry.Value))
        $lines.Add(
            ("[IO.File]::WriteAllBytes((Join-Path `$d '{0}'), [Convert]::FromBase64String('{1}'))" -f $entry.Key, $b64)
        )
    }

    $lines.Add('exit 0')
    $script = ($lines -join "`n")
    return [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
}

$uiFiles = @{
    'AssetBee-SavePending.ps1'      = $savePendingPath
    'AssetBee-LoadExisting.ps1'     = $loadExistingPath
    'AssetBee-PreserveSettings.ps1' = $preservePath
    'AssetBee-UninstallRelated.ps1' = $uninstallRelatedPath
}

$elevatedFiles = @{
    'AssetBee-SavePending.ps1'      = $savePendingPath
    'AssetBee-LoadExisting.ps1'     = $loadExistingPath
    'AssetBee-PreserveSettings.ps1' = $preservePath
}

$elevatedInit = @'
$d = Join-Path $env:ProgramData 'AssetBee\Drone\msi-scripts'
New-Item -ItemType Directory -Force -Path $d | Out-Null
'@

$extractUiEncoded = New-ExtractEncodedCommand -DestinationInit '$d = $env:TEMP' -Files $uiFiles
$extractElevatedEncoded = New-ExtractEncodedCommand -DestinationInit $elevatedInit -Files $elevatedFiles

# Write defines to an include file so huge -EncodedCommand payloads are not
# passed on the wix.exe command line (CreateProcess length limits).
$generatedDir = Join-Path $scriptDir 'obj'
New-Item -ItemType Directory -Path $generatedDir -Force | Out-Null
$definesPath = Join-Path $generatedDir 'ExtractScriptDefines.wxi'
$definesXml = @"
<?xml version="1.0" encoding="utf-8"?>
<Include xmlns="http://wixtoolset.org/schemas/v4/wxs">
	<?define ExtractScriptsUiEncoded="$extractUiEncoded" ?>
	<?define ExtractScriptsElevatedEncoded="$extractElevatedEncoded" ?>
</Include>
"@
[IO.File]::WriteAllText($definesPath, $definesXml)

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
    Invoke-AuthenticodeSign -Paths @($exe, $tray)
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
    -arch x64 `
    -o $outputMsi

if ($LASTEXITCODE -ne 0) {
    throw "wix build failed with exit code $LASTEXITCODE"
}

if ($SignThumbprint) {
    Invoke-AuthenticodeSign -Paths @($outputMsi)
}

Write-Host "Created $outputMsi"
