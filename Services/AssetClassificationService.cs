using System.Globalization;
using System.Text.Json;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Services;

public sealed record AssetClassification(string Type, ProbeValue<string> HardwareType);

public interface IAssetClassificationService
{
    Task<AssetClassification> ClassifyAsync(CancellationToken cancellationToken);
}

public sealed class AssetClassificationService(
    IProcessRunner processRunner,
    IOptions<DroneOptions> options) : IAssetClassificationService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private readonly string? _configuredType = NormalizeType(options.Value.Type);

    public async Task<AssetClassification> ClassifyAsync(CancellationToken cancellationToken)
    {
        if (_configuredType == "virtualware")
        {
            return Virtualware("Asset type was configured as virtualware.");
        }

        return OperatingSystem.IsWindows()
            ? await ClassifyWindowsAsync(_configuredType, cancellationToken)
            : OperatingSystem.IsLinux()
                ? await ClassifyLinuxAsync(_configuredType, cancellationToken)
                : OperatingSystem.IsMacOS()
                    ? await ClassifyMacOsAsync(_configuredType, cancellationToken)
                    : new AssetClassification(
                        _configuredType ?? "hardware",
                        ProbeValue<string>.Unsupported("Unsupported operating system."));
    }

    private async Task<AssetClassification> ClassifyWindowsAsync(
        string? configuredType,
        CancellationToken cancellationToken)
    {
        const string script = """
            $computer = Get-CimInstance Win32_ComputerSystem
            $chassisTypes = @()
            try {
                $enclosure = Get-CimInstance Win32_SystemEnclosure | Select-Object -First 1
                if ($null -ne $enclosure -and $null -ne $enclosure.ChassisTypes) {
                    $chassisTypes = @($enclosure.ChassisTypes | ForEach-Object { [int]$_ })
                }
            } catch {}
            [pscustomobject]@{
                Manufacturer = [string]$computer.Manufacturer
                Model = [string]$computer.Model
                ChassisTypes = $chassisTypes
                PcSystemType = [int]$computer.PCSystemType
            } | ConvertTo-Json -Compress
            """;
        ProcessResult result = await processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            ProbeTimeout,
            cancellationToken);
        if (!result.Succeeded)
        {
            return HardwareUnavailable(configuredType, "Windows chassis information was unavailable.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            string identity =
                $"{GetString(root, "Manufacturer")} {GetString(root, "Model")}".Trim();
            if (configuredType is null && IsVirtualIdentity(identity))
            {
                return Virtualware($"Virtual machine platform detected: {identity}.");
            }

            // Prefer SMBIOS ChassisTypes (same enum as Linux DMI chassis_type).
            // Matches: switch on each ChassisTypes value, break on laptop/server.
            List<int> chassisTypes = ReadChassisTypes(root).ToList();
            if (chassisTypes.Count > 0)
            {
                string hardwareType = "desktop";
                foreach (int chassisType in chassisTypes)
                {
                    string mapped = MapSmbiosChassisType(chassisType);
                    if (mapped is "laptop" or "server")
                    {
                        hardwareType = mapped;
                        break;
                    }
                }

                return Hardware(hardwareType, string.Empty);
            }

            // PCSystemType: 1 Desktop, 2 Mobile, 3 Workstation, 4 Enterprise Server,
            // 5 SOHO Server, 6 Appliance PC, 7 Performance Server.
            int systemType = root.TryGetProperty("PcSystemType", out JsonElement value) &&
                             value.TryGetInt32(out int parsed)
                ? parsed
                : 0;
            string? fromPcSystemType = MapWindowsPcSystemType(systemType);
            return Hardware(
                fromPcSystemType,
                "Windows chassis type and PCSystemType did not identify the form factor.");
        }
        catch (JsonException)
        {
            return HardwareUnavailable(configuredType, "Windows chassis data was invalid.");
        }
    }

    private async Task<AssetClassification> ClassifyLinuxAsync(
        string? configuredType,
        CancellationToken cancellationToken)
    {
        if (configuredType is null)
        {
            ProcessResult virtualization = await processRunner.RunAsync(
                "systemd-detect-virt", [], ProbeTimeout, cancellationToken);
            string technology = virtualization.StandardOutput.Trim();
            if (virtualization.Succeeded &&
                !string.IsNullOrWhiteSpace(technology) &&
                !technology.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return Virtualware($"Virtualization detected: {technology}.");
            }

            string identity = string.Join(' ', new[]
            {
                ReadTrimmed("/sys/class/dmi/id/sys_vendor"),
                ReadTrimmed("/sys/class/dmi/id/product_name")
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (IsVirtualIdentity(identity))
            {
                return Virtualware($"Virtual machine platform detected: {identity}.");
            }
        }

        string? chassisText = ReadTrimmed("/sys/class/dmi/id/chassis_type");
        if (!int.TryParse(chassisText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int chassisType))
        {
            return Hardware(null, "Linux DMI chassis type was unavailable.");
        }

        return Hardware(MapSmbiosChassisType(chassisType), string.Empty);
    }

    private async Task<AssetClassification> ClassifyMacOsAsync(
        string? configuredType,
        CancellationToken cancellationToken)
    {
        ProcessResult modelResult = await processRunner.RunAsync(
            "sysctl", ["-n", "hw.model"], ProbeTimeout, cancellationToken);
        string model = modelResult.StandardOutput.Trim();
        if (configuredType is null && IsVirtualIdentity(model))
        {
            return Virtualware($"Virtual machine platform detected: {model}.");
        }

        string? hardwareType =
            model.StartsWith("MacBook", StringComparison.OrdinalIgnoreCase) ? "laptop" :
            model.StartsWith("Xserve", StringComparison.OrdinalIgnoreCase) ? "server" :
            model.StartsWith("iMac", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("Macmini", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("MacPro", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("MacStudio", StringComparison.OrdinalIgnoreCase) ? "desktop" :
            null;
        return Hardware(hardwareType, "The Mac model did not identify the form factor.");
    }

    private static AssetClassification Hardware(string? hardwareType, string unavailableDetail) =>
        new(
            "hardware",
            hardwareType is null
                ? ProbeValue<string>.Unavailable(unavailableDetail)
                : ProbeValue<string>.Available(hardwareType));

    private static AssetClassification HardwareUnavailable(string? configuredType, string detail) =>
        configuredType == "virtualware"
            ? Virtualware("Asset type was configured as virtualware.")
            : Hardware(null, detail);

    private static AssetClassification Virtualware(string detail) =>
        new("virtualware", ProbeValue<string>.Unsupported(detail));

    private static string? NormalizeType(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsVirtualIdentity(string value)
    {
        string[] markers =
        [
            "virtual", "vmware", "virtualbox", "kvm", "qemu", "xen", "hyper-v",
            "parallels", "amazon ec2", "google compute engine", "bochs", "bhyve"
        ];
        return markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) ? value.ToString() : null;

    private static IEnumerable<int> ReadChassisTypes(JsonElement root)
    {
        if (!root.TryGetProperty("ChassisTypes", out JsonElement values))
        {
            yield break;
        }

        if (values.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement value in values.EnumerateArray())
            {
                if (value.TryGetInt32(out int chassisType))
                {
                    yield return chassisType;
                }
            }

            yield break;
        }

        if (values.TryGetInt32(out int single))
        {
            yield return single;
        }
    }

    // SMBIOS chassis type codes (Linux DMI / Win32_SystemEnclosure.ChassisTypes).
    public static string MapSmbiosChassisType(int chassisType) => chassisType switch
    {
        8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32 => "laptop",
        17 or 23 or 28 or 29 => "server",
        _ => "desktop"
    };

    // Win32_ComputerSystem.PCSystemType fallback when SMBIOS chassis is missing.
    public static string? MapWindowsPcSystemType(int systemType) => systemType switch
    {
        1 or 3 or 6 => "desktop",
        2 => "laptop",
        4 or 5 or 7 => "server",
        _ => null
    };
}
