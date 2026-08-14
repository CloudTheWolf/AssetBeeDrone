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
$runKeyPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'AssetBeeDroneTray'

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

Get-Process -Name 'AssetBee.Drone.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $Destination -Recurse -Force

$settings = @{
    Drone = @{
        Endpoint = $Endpoint.AbsoluteUri
        CollectionIntervalMinutes = 3600
        RequestTimeoutSeconds = 30
        MaxRetryAttempts = 3
        BearerToken = if ($BearerToken) { $BearerToken } else { $null }
        ApiKey = if ($ApiKey) { $ApiKey } else { $null }
    }
} | ConvertTo-Json -Depth 4
$settingsPath = Join-Path $Destination 'appsettings.json'
[IO.File]::WriteAllText($settingsPath, $settings)

# Allow interactive users to run the tray; keep secrets admin/SYSTEM-only.
& icacls.exe $Destination /inheritance:r `
    /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'Users:(OI)(CI)RX' | Out-Null
& icacls.exe $settingsPath /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' | Out-Null

# Mirror into registry so the next upgrade/reinstall can pre-fill the MSI UI.
function Write-DroneRegMirror {
    param([Parameter(Mandatory = $true)][string] $RegPath)

    New-Item -Path $RegPath -Force | Out-Null
    Set-ItemProperty -Path $RegPath -Name 'Endpoint' -Value $Endpoint.AbsoluteUri
    if ($BearerToken) {
        Set-ItemProperty -Path $RegPath -Name 'BearerToken' -Value $BearerToken
        Remove-ItemProperty -Path $RegPath -Name 'ApiKey' -ErrorAction SilentlyContinue
    } elseif ($ApiKey) {
        Set-ItemProperty -Path $RegPath -Name 'ApiKey' -Value $ApiKey
        Remove-ItemProperty -Path $RegPath -Name 'BearerToken' -ErrorAction SilentlyContinue
    }
}

Write-DroneRegMirror -RegPath 'HKLM:\SOFTWARE\AssetBee\Drone'
try {
    Write-DroneRegMirror -RegPath 'HKCU:\SOFTWARE\AssetBee\Drone'
} catch {
}


$binary = Join-Path $Destination 'AssetBee.Drone.exe'
& sc.exe create $serviceName binPath= "`"$binary`"" start= auto obj= LocalSystem `
    DisplayName= 'AssetBee Drone' | Out-Null
& sc.exe description $serviceName 'Collects and securely reports device inventory.' | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/15000/restart/30000/restart/60000 | Out-Null
Start-Service -Name $serviceName

$tray = Join-Path $Destination 'AssetBee.Drone.Tray.exe'
if (Test-Path -LiteralPath $tray) {
    New-Item -Path $runKeyPath -Force | Out-Null
    Set-ItemProperty -Path $runKeyPath -Name $runValueName -Value "`"$tray`""
    Start-Process -FilePath $tray
}

Write-Host "AssetBee Drone installed at $Destination and started."
