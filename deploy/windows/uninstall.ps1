param(
    [string] $Destination = "$env:ProgramFiles\AssetBee\Drone"
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AssetBeeDrone'
$runKeyPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'AssetBeeDroneTray'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

Get-Process -Name 'AssetBee.Drone.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if (Get-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
}

if (Test-Path $Destination) {
    Remove-Item -Path $Destination -Recurse -Force
}

Write-Host 'AssetBee Drone uninstalled.'
