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
& icacls.exe $InstallDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
