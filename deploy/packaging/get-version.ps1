param(
    [string] $RepoRoot
)

$ErrorActionPreference = 'Stop'

if (-not [string]::IsNullOrWhiteSpace($env:VERSION)) {
    Write-Output $env:VERSION.Trim()
    return
}

$ref = $env:GITHUB_REF
if (-not [string]::IsNullOrWhiteSpace($ref) -and $ref.StartsWith('refs/tags/v')) {
    Write-Output $ref.Substring('refs/tags/v'.Length)
    return
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_NUMBER)) {
    Write-Output "1.0.$($env:GITHUB_RUN_NUMBER.Trim())"
    return
}

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

try {
    $tag = & git -C $RepoRoot describe --tags --exact-match 2>$null
    if ($LASTEXITCODE -eq 0 -and $tag -match '^v(.+)$') {
        Write-Output $Matches[1]
        return
    }
} catch {
}

try {
    $count = & git -C $RepoRoot rev-list --count HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $count -match '^\d+$' -and [int]$count -gt 0) {
        Write-Output "1.0.$count"
        return
    }
} catch {
}

Write-Output '1.0.0'
