# Writes appsettings.json during MSI install (and mirrors values for upgrade pre-fill).
param(
    [Parameter(Mandatory = $true)]
    [uri] $Endpoint,
    [string] $BearerToken = '',
    [string] $ApiKey = '',
    [Parameter(Mandatory = $true)]
    [string] $InstallDir
)

$ErrorActionPreference = 'Stop'

# MSI directory properties end with '\'. Quoting that as "C:\Path\" escapes the
# closing quote, so WiX passes "[INSTALLFOLDER]." and we strip the marker here.
$InstallDir = $InstallDir.Trim().TrimEnd('.').TrimEnd('\')

if ($Endpoint.Scheme -ne 'https') {
    throw 'The inventory endpoint must use HTTPS.'
}

$hasBearer = -not [string]::IsNullOrWhiteSpace($BearerToken)
$hasApiKey = -not [string]::IsNullOrWhiteSpace($ApiKey)
if ($hasBearer -eq $hasApiKey) {
    throw 'Provide exactly one of -BearerToken or -ApiKey.'
}

if (-not (Test-Path -LiteralPath $InstallDir)) {
    throw "Install directory not found: $InstallDir"
}

$settings = @{
    Drone = @{
        Endpoint = $Endpoint.AbsoluteUri
        CollectionIntervalMinutes = 360
        RequestTimeoutSeconds = 30
        MaxRetryAttempts = 3
        BearerToken = if ($hasBearer) { $BearerToken } else { $null }
        ApiKey = if ($hasApiKey) { $ApiKey } else { $null }
    }
} | ConvertTo-Json -Depth 4

$settingsPath = Join-Path $InstallDir 'appsettings.json'
[IO.File]::WriteAllText($settingsPath, $settings)
& icacls.exe $InstallDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'Users:(OI)(CI)RX' | Out-Null
& icacls.exe $settingsPath /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' | Out-Null

# Mirror into registry so the next upgrade/reinstall can pre-fill the MSI UI.
function Write-DroneRegMirror {
    param([Parameter(Mandatory = $true)][string] $RegPath)

    New-Item -Path $RegPath -Force | Out-Null
    Set-ItemProperty -Path $RegPath -Name 'Endpoint' -Value $Endpoint.AbsoluteUri
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
    # Deferred CA runs as SYSTEM; HKCU may be unavailable — HKLM is enough for the next elevate/search.
}
