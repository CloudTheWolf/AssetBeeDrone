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
    [xml] $csproj = Get-Content -LiteralPath (Join-Path $repoRoot 'AssetBeeDrone.csproj')
    $Version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) {
        throw 'Unable to determine Version from AssetBeeDrone.csproj. Pass -Version.'
    }
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

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host 'WiX CLI not found on PATH; installing via dotnet tool...'
    & dotnet tool update --global wix --version 5.0.2
    $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
}

& wix --version | Out-Host
& wix extension add -g WixToolset.Util.wixext/5.0.2 2>$null

$outputMsi = Join-Path $OutputDirectory "AssetBee.Drone-$Version-win-x64.msi"

& wix build `
    (Join-Path $scriptDir 'AssetBeeDrone.wxs') `
    -ext WixToolset.Util.wixext `
    -d "Version=$Version" `
    -d "PublishDir=$PublishDirectory" `
    -d "SourceDir=$scriptDir" `
    -arch x64 `
    -o $outputMsi

if ($LASTEXITCODE -ne 0) {
    throw "wix build failed with exit code $LASTEXITCODE"
}

Write-Host "Created $outputMsi"
