# Saves UI-entered config where the elevated deferred CA can read it.
# Windows Temp is writable before elevation and readable by LocalSystem afterwards.
param(
    [Parameter(Mandatory = $true)]
    [string] $PendingPath,
    [string] $Endpoint = '',
    [string] $BearerToken = '',
    [string] $ApiKey = ''
)

$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $PendingPath
if (-not [string]::IsNullOrWhiteSpace($dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$pending = @{
    Endpoint = $Endpoint
    BearerToken = $BearerToken
    ApiKey = $ApiKey
} | ConvertTo-Json -Compress

[IO.File]::WriteAllText($PendingPath, $pending)
exit 0
