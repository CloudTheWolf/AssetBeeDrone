# Copies installed appsettings into ProgramData before RemoveExistingProducts
# deletes them during an upgrade/reinstall.
$ErrorActionPreference = 'Stop'

$src = Join-Path $env:ProgramFiles 'AssetBee\Drone\appsettings.json'
if (-not (Test-Path -LiteralPath $src)) {
    exit 0
}

try {
    $json = Get-Content -LiteralPath $src -Raw -ErrorAction Stop | ConvertFrom-Json
} catch {
    exit 0
}

if ($null -eq $json.Drone) {
    exit 0
}

$endpoint = [string]$json.Drone.Endpoint
$bearer = [string]$json.Drone.BearerToken
$apiKey = [string]$json.Drone.ApiKey
if ($bearer -eq 'null') { $bearer = '' }
if ($apiKey -eq 'null') { $apiKey = '' }

if ([string]::IsNullOrWhiteSpace($endpoint)) {
    exit 0
}

$dir = Join-Path $env:ProgramData 'AssetBee\Drone'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$pending = @{
    Endpoint = $endpoint
    BearerToken = $bearer
    ApiKey = $apiKey
} | ConvertTo-Json -Compress

[IO.File]::WriteAllText((Join-Path $dir 'msi-pending.json'), $pending)
exit 0
