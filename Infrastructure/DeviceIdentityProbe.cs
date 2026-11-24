using System.Text.Json;

namespace AssetBeeDrone.Infrastructure;

public sealed record DeviceIdentity(string? SerialNumber, string? DeviceName);

public interface IDeviceIdentityProbe
{
    Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken);
}

public sealed class WindowsDeviceIdentityProbe(
    IProcessRunner processRunner) : IDeviceIdentityProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $bios = Get-CimInstance Win32_BIOS
        $product = $null
        $board = $null
        try { $product = Get-CimInstance Win32_ComputerSystemProduct -ErrorAction Stop } catch {}
        try { $board = Get-CimInstance Win32_BaseBoard -ErrorAction Stop } catch {}
        $machineGuid = $null
        try {
            $machineGuid = [string](Get-ItemProperty `
                -Path 'HKLM:\SOFTWARE\Microsoft\Cryptography' `
                -Name MachineGuid `
                -ErrorAction Stop).MachineGuid
        } catch {}
        function Get-MeaningfulHardwareValue {
            param([string[]]$Candidates)
            $placeholders = @(
                'none', 'null', 'n/a', 'na', 'not specified', 'not applicable',
                'default string', 'to be filled by o.e.m.', 'to be filled by oem',
                'system serial number', 'system product name', 'system manufacturer',
                'chassis serial number', 'invalid', 'unknown', 'undefined', 'o.e.m.', '0'
            )
            foreach ($candidate in $Candidates) {
                if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
                $normalized = $candidate.Trim()
                $isPlaceholder = $false
                foreach ($placeholder in $placeholders) {
                    if ($normalized.Equals($placeholder, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $isPlaceholder = $true
                        break
                    }
                }
                if (-not $isPlaceholder) { return $normalized }
            }
            return $null
        }
        $serialNumber = Get-MeaningfulHardwareValue @(
            [string]$bios.SerialNumber,
            $(if ($null -ne $product) { [string]$product.IdentifyingNumber } else { $null }),
            $(if ($null -ne $board) { [string]$board.SerialNumber } else { $null }),
            $(if ($null -ne $product) { [string]$product.UUID } else { $null }),
            $machineGuid
        )
        [pscustomobject]@{
            DeviceName = $env:COMPUTERNAME
            SerialNumber = $serialNumber
        } | ConvertTo-Json -Compress
        """;

    public async Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DeviceIdentity(null, Environment.MachineName);
        }

        ProcessResult result = await processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", Script],
            Timeout,
            cancellationToken);

        if (!result.Succeeded)
        {
            return new DeviceIdentity(null, Environment.MachineName);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            string? serial = document.RootElement.TryGetProperty("SerialNumber", out JsonElement serialElement)
                ? serialElement.GetString()
                : null;
            string? name = document.RootElement.TryGetProperty("DeviceName", out JsonElement nameElement)
                ? nameElement.GetString()
                : Environment.MachineName;
            return new DeviceIdentity(
                string.IsNullOrWhiteSpace(serial) ? null : serial.Trim(),
                string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim());
        }
        catch (JsonException)
        {
            return new DeviceIdentity(null, Environment.MachineName);
        }
    }
}

public sealed class UnsupportedDeviceIdentityProbe : IDeviceIdentityProbe
{
    public Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DeviceIdentity(null, Environment.MachineName));
}
