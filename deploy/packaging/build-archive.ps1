param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,
    [Parameter(Mandatory = $true)]
    [string] $Rid,
    [string] $Version,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..\..')).Path

if (-not $Version) {
    [xml] $csproj = Get-Content -LiteralPath (Join-Path $repoRoot 'AssetBeeDrone.csproj')
    $Version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) {
        throw 'Unable to determine Version from AssetBeeDrone.csproj. Pass -Version.'
    }
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'dist'
}

if ($Rid -notlike 'win-*') {
    throw "build-archive.ps1 only builds Windows archives. Use build-archive.sh for $Rid."
}

$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ("assetbee-archive-" + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stageRoot "AssetBee.Drone-$Version-$Rid"
try {
    New-Item -ItemType Directory -Path (Join-Path $payload 'bin') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $payload 'deploy\windows') -Force | Out-Null
    Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination (Join-Path $payload 'bin') -Recurse -Force

    $template = @'
{
  "Drone": {
    "Endpoint": "https://inventory.example.com/api/v1/inventory",
    "CollectionIntervalMinutes": 360,
    "RequestTimeoutSeconds": 30,
    "MaxRetryAttempts": 3,
    "BearerToken": null,
    "ApiKey": null,
    "Type": null,
    "Debug": false,
    "DebugOutputPath": "inventory-debug.json"
  }
}
'@
    Set-Content -LiteralPath (Join-Path $payload 'bin\appsettings.json') -Value $template -Encoding utf8

    Copy-Item (Join-Path $repoRoot 'deploy\windows\install.ps1') (Join-Path $payload 'deploy\windows\')
    Copy-Item (Join-Path $repoRoot 'deploy\windows\uninstall.ps1') (Join-Path $payload 'deploy\windows\')

    @"
AssetBee Drone $Version ($Rid)

From an elevated PowerShell session:

  .\deploy\windows\install.ps1 ``
    -PublishDirectory .\bin ``
    -Endpoint https://inventory.example.com/api/v1/inventory ``
    -BearerToken 'secret'

Uninstall:

  .\deploy\windows\uninstall.ps1
"@ | Set-Content -LiteralPath (Join-Path $payload 'INSTALL.txt') -Encoding utf8

    $output = Join-Path $OutputDirectory "AssetBee.Drone-$Version-$Rid.zip"
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Force
    }
    Compress-Archive -Path $payload -DestinationPath $output -Force
    Write-Host "Created $output"
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
