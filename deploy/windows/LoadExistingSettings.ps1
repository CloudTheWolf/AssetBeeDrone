# Loads existing Drone settings into the registry so WiX RegistrySearch can pre-fill the UI.
# Writes HKLM when elevated; always mirrors to HKCU for unelevated UI AppSearch.
$ErrorActionPreference = 'Stop'

$settingsPath = Join-Path $env:ProgramFiles 'AssetBee\Drone\appsettings.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    exit 0
}

try {
    $json = Get-Content -LiteralPath $settingsPath -Raw -ErrorAction Stop | ConvertFrom-Json
} catch {
    exit 0
}

$drone = $json.Drone
if ($null -eq $drone) {
    exit 0
}

$endpoint = [string]$drone.Endpoint
$bearer = [string]$drone.BearerToken
$apiKey = [string]$drone.ApiKey

function Write-DroneReg {
    param([Parameter(Mandatory = $true)][string] $RegPath)

    New-Item -Path $RegPath -Force | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($endpoint)) {
        Set-ItemProperty -Path $RegPath -Name 'Endpoint' -Value $endpoint
    }

    if ([string]::IsNullOrWhiteSpace($bearer) -or $bearer -eq 'null') {
        Remove-ItemProperty -Path $RegPath -Name 'BearerToken' -ErrorAction SilentlyContinue
    } else {
        Set-ItemProperty -Path $RegPath -Name 'BearerToken' -Value $bearer
    }

    if ([string]::IsNullOrWhiteSpace($apiKey) -or $apiKey -eq 'null') {
        Remove-ItemProperty -Path $RegPath -Name 'ApiKey' -ErrorAction SilentlyContinue
    } else {
        Set-ItemProperty -Path $RegPath -Name 'ApiKey' -Value $apiKey
    }
}

foreach ($regPath in @('HKLM:\SOFTWARE\AssetBee\Drone', 'HKCU:\SOFTWARE\AssetBee\Drone')) {
    try {
        Write-DroneReg -RegPath $regPath
    } catch {
        # HKLM may fail in unelevated UI; HKCU usually succeeds.
    }
}

exit 0
