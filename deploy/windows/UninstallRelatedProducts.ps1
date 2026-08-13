# Removes related MSI products before the new package starts its execute sequence.
# This is needed for legacy packages whose uninstall launch condition required ENDPOINT.
param(
    [string] $ProductCodes = '',
    [string] $Endpoint = '',
    [ValidateSet('quiet', 'basic')]
    [string] $Ui = 'quiet'
)

$ErrorActionPreference = 'Stop'
$msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'

foreach ($productCode in ($ProductCodes -split ';' | Where-Object { $_ })) {
    $uiArgument = if ($Ui -eq 'basic') { '/qb' } else { '/qn' }
    $arguments = @('/x', $productCode, $uiArgument, '/norestart')
    if (-not [string]::IsNullOrWhiteSpace($Endpoint)) {
        $arguments += "ENDPOINT=$Endpoint"
    }

    $process = Start-Process -FilePath $msiexec -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -notin @(0, 1605, 3010)) {
        exit $process.ExitCode
    }
}

exit 0
