# Saves UI-entered config where the elevated deferred CA can read it (ProgramData).
# MSI UI properties often do not cross the UAC elevation boundary reliably.
param(
    [string] $Endpoint = '',
    [string] $BearerToken = '',
    [string] $ApiKey = ''
)

$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:ProgramData 'AssetBee\Drone'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$pending = @{
    Endpoint = $Endpoint
    BearerToken = $BearerToken
    ApiKey = $ApiKey
} | ConvertTo-Json -Compress

$pendingPath = Join-Path $dir 'msi-pending.json'
[IO.File]::WriteAllText($pendingPath, $pending)
exit 0
