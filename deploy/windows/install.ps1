param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,
    [Parameter(Mandatory = $true)]
    [uri] $Endpoint,
    [string] $BearerToken,
    [string] $ApiKey,
    [string] $Destination = "$env:ProgramFiles\AssetBee\Drone"
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AssetBeeDrone'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

if ($Endpoint.Scheme -ne 'https') {
    throw 'The inventory endpoint must use HTTPS.'
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $Destination -Recurse -Force

$settings = @{
    Drone = @{
        Endpoint = $Endpoint.AbsoluteUri
        CollectionIntervalMinutes = 360
        RequestTimeoutSeconds = 30
        MaxRetryAttempts = 3
        BearerToken = if ($BearerToken) { $BearerToken } else { $null }
        ApiKey = if ($ApiKey) { $ApiKey } else { $null }
    }
} | ConvertTo-Json -Depth 4
$settingsPath = Join-Path $Destination 'appsettings.json'
[IO.File]::WriteAllText($settingsPath, $settings)

& icacls.exe $Destination /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
$binary = Join-Path $Destination 'AssetBee.Drone.exe'
& sc.exe create $serviceName binPath= "`"$binary`"" start= auto obj= LocalSystem `
    DisplayName= 'AssetBee Drone' | Out-Null
& sc.exe description $serviceName 'Collects and securely reports device inventory.' | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/15000/restart/30000/restart/60000 | Out-Null
Start-Service -Name $serviceName

Write-Host "AssetBee Drone installed at $Destination and started."
