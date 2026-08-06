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
            [pscustomobject]@{
                Manufacturer = [string]$computer.Manufacturer
                Model = [string]$computer.Model
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

            int systemType = root.TryGetProperty("PcSystemType", out JsonElement value) &&
                             value.TryGetInt32(out int parsed)
                ? parsed
                : 0;
            string? hardwareType = systemType switch
            {
                2 or 4 => "desktop",
                3 => "laptop",
                5 or 6 or 7 or 8 => "server",
                _ => null
            };
            return Hardware(hardwareType, "Windows PCSystemType did not identify the form factor.");
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

        string? hardwareType = chassisType switch
        {
            >= 8 and <= 14 or >= 30 and <= 32 => "laptop",
            >= 3 and <= 7 or 15 or 16 => "desktop",
            17 or 23 => "server",
            _ => null
        };
        return Hardware(hardwareType, $"Unrecognized DMI chassis type: {chassisType}.");
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
}
