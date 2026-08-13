# Writes appsettings.json during MSI install.
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
