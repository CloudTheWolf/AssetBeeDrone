# Writes appsettings.json during MSI install (and mirrors values for upgrade pre-fill).
# Resolves config from: parameters → the pre-elevation pending file → the
# legacy ProgramData pending file → existing appsettings.json → HKLM registry.
param(
    [string] $Endpoint = '',
    [string] $BearerToken = '',
    [string] $ApiKey = '',
    [string] $PendingPath = '',
    [Parameter(Mandatory = $true)]
    [string] $InstallDir
)

$ErrorActionPreference = 'Stop'

# MSI directory properties end with '\'. Quoting that as "C:\Path\" escapes the
# closing quote, so WiX passes "[INSTALLFOLDER]." and we strip the marker here.
$InstallDir = $InstallDir.Trim().TrimEnd('.').TrimEnd('\')

$legacyPendingPath = Join-Path $env:ProgramData 'AssetBee\Drone\msi-pending.json'
$settingsPath = Join-Path $InstallDir 'appsettings.json'

function Read-Pending {
    foreach ($path in @($PendingPath, $legacyPendingPath)) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) {
            continue
        }
        try {
            return Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json
        } catch {
            continue
        }
    }
    return $null
}

function Read-ExistingSettings {
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return $null
    }
    try {
        $json = Get-Content -LiteralPath $settingsPath -Raw -ErrorAction Stop | ConvertFrom-Json
        return $json.Drone
    } catch {
        return $null
    }
}

function Read-RegistryConfig {
    $regPath = 'HKLM:\SOFTWARE\AssetBee\Drone'
    if (-not (Test-Path -LiteralPath $regPath)) {
        return $null
    }
    try {
        $item = Get-ItemProperty -LiteralPath $regPath -ErrorAction Stop
        return [pscustomobject]@{
            Endpoint = [string]$item.Endpoint
            BearerToken = [string]$item.BearerToken
            ApiKey = [string]$item.ApiKey
        }
    } catch {
        return $null
    }
}

if ([string]::IsNullOrWhiteSpace($Endpoint)) {
    $pending = Read-Pending
    if ($pending -and -not [string]::IsNullOrWhiteSpace([string]$pending.Endpoint)) {
        $Endpoint = [string]$pending.Endpoint
        if ([string]::IsNullOrWhiteSpace($BearerToken)) {
            $BearerToken = [string]$pending.BearerToken
        }
        if ([string]::IsNullOrWhiteSpace($ApiKey)) {
            $ApiKey = [string]$pending.ApiKey
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Endpoint) -or (
        [string]::IsNullOrWhiteSpace($BearerToken) -and [string]::IsNullOrWhiteSpace($ApiKey))) {
    $existing = Read-ExistingSettings
    if ($existing) {
        if ([string]::IsNullOrWhiteSpace($Endpoint)) {
            $Endpoint = [string]$existing.Endpoint
        }
        if ([string]::IsNullOrWhiteSpace($BearerToken) -and [string]::IsNullOrWhiteSpace($ApiKey)) {
            $BearerToken = [string]$existing.BearerToken
            $ApiKey = [string]$existing.ApiKey
            if ($BearerToken -eq 'null') { $BearerToken = '' }
            if ($ApiKey -eq 'null') { $ApiKey = '' }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Endpoint) -or (
        [string]::IsNullOrWhiteSpace($BearerToken) -and [string]::IsNullOrWhiteSpace($ApiKey))) {
    $reg = Read-RegistryConfig
    if ($reg) {
        if ([string]::IsNullOrWhiteSpace($Endpoint)) {
            $Endpoint = [string]$reg.Endpoint
        }
        if ([string]::IsNullOrWhiteSpace($BearerToken) -and [string]::IsNullOrWhiteSpace($ApiKey)) {
            $BearerToken = [string]$reg.BearerToken
            $ApiKey = [string]$reg.ApiKey
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Endpoint)) {
    throw 'The inventory endpoint is missing from both the installer UI and existing settings.'
}

try {
    $endpointUri = [uri]$Endpoint
} catch {
    throw "ENDPOINT is not a valid URL: $Endpoint"
}

if ($endpointUri.Scheme -ne 'https') {
    throw 'The inventory endpoint must use HTTPS.'
}

$hasBearer = -not [string]::IsNullOrWhiteSpace($BearerToken) -and $BearerToken -ne 'null'
$hasApiKey = -not [string]::IsNullOrWhiteSpace($ApiKey) -and $ApiKey -ne 'null'
if ($hasBearer -eq $hasApiKey) {
    throw 'Provide exactly one of BearerToken or ApiKey.'
}

if (-not (Test-Path -LiteralPath $InstallDir)) {
    throw "Install directory not found: $InstallDir"
}

$settings = @{
    Drone = @{
        Endpoint = $endpointUri.AbsoluteUri
        CollectionIntervalMinutes = 360
        RequestTimeoutSeconds = 30
        MaxRetryAttempts = 3
        BearerToken = if ($hasBearer) { $BearerToken } else { $null }
        ApiKey = if ($hasApiKey) { $ApiKey } else { $null }
    }
} | ConvertTo-Json -Depth 4

[IO.File]::WriteAllText($settingsPath, $settings)
& icacls.exe $InstallDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'Users:(OI)(CI)RX' | Out-Null
& icacls.exe $settingsPath /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' | Out-Null

# Mirror into registry so the next upgrade/reinstall can pre-fill the MSI UI.
function Write-DroneRegMirror {
    param([Parameter(Mandatory = $true)][string] $RegPath)

    New-Item -Path $RegPath -Force | Out-Null
    Set-ItemProperty -Path $RegPath -Name 'Endpoint' -Value $endpointUri.AbsoluteUri
    if ($hasBearer) {
        Set-ItemProperty -Path $RegPath -Name 'BearerToken' -Value $BearerToken
        Remove-ItemProperty -Path $RegPath -Name 'ApiKey' -ErrorAction SilentlyContinue
    } else {
        Set-ItemProperty -Path $RegPath -Name 'ApiKey' -Value $ApiKey
        Remove-ItemProperty -Path $RegPath -Name 'BearerToken' -ErrorAction SilentlyContinue
    }
}

Write-DroneRegMirror -RegPath 'HKLM:\SOFTWARE\AssetBee\Drone'
try {
    Write-DroneRegMirror -RegPath 'HKCU:\SOFTWARE\AssetBee\Drone'
} catch {
}

foreach ($path in @($PendingPath, $legacyPendingPath)) {
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}
