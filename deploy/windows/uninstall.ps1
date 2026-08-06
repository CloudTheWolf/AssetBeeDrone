param(
    [string] $Destination = "$env:ProgramFiles\AssetBee\Drone"
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AssetBeeDrone'

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
}

if (Test-Path $Destination) {
    Remove-Item -Path $Destination -Recurse -Force
}

Write-Host 'AssetBee Drone uninstalled.'
