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
        $bios = Get-CimInstance Win32_BIOS
        [pscustomobject]@{
            DeviceName = $env:COMPUTERNAME
            SerialNumber = [string]$bios.SerialNumber
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
