using System.Globalization;
using System.Text.Json;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Collectors.MacOS;

public sealed class MacOsInventoryCollector(
    IProcessRunner processRunner,
    TimeProvider timeProvider,
    IOptions<DroneOptions> options) : InventoryCollectorBase, IDeviceInventoryCollector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private readonly DroneOptions _options = options.Value;

    public async Task<DeviceInventory> CollectAsync(CancellationToken cancellationToken)
    {
        Task<ProcessResult> hardwareTask = processRunner.RunAsync(
            "system_profiler", ["SPHardwareDataType", "-json"], ProbeTimeout, cancellationToken);
        Task<ProbeValue<OperatingSystemInfo>> operatingSystemTask =
            CollectOperatingSystemAsync(cancellationToken);
        Task<ProbeValue<MemoryInfo>> memoryTask = CollectMemoryAsync(cancellationToken);
        Task<ProbeValue<IReadOnlyList<EncryptionVolume>>> encryptionTask =
            CollectEncryptionAsync(cancellationToken);
        Task<ProbeValue<DomainWorkspaceInfo>> domainTask = CollectDomainAsync(cancellationToken);
        Task<ProbeValue<IReadOnlyList<AntivirusInfo>>> antivirusTask =
            CollectAntivirusAsync(cancellationToken);
        Task<ProbeValue<UpdateInventory>> updatesTask = CollectUpdatesAsync(cancellationToken);
        Task<ProbeValue<SbomInventory>> sbomTask = SbomCollector.CollectMacOsAsync(
            processRunner, timeProvider, _options.IncludeSbom, cancellationToken);

        await Task.WhenAll(
            hardwareTask,
            operatingSystemTask,
            memoryTask,
            encryptionTask,
            domainTask,
            antivirusTask,
            updatesTask,
            sbomTask);
        (ProbeValue<string> serial, ProbeValue<CpuInfo> cpu, ProbeValue<string> model) =
            ParseHardware(await hardwareTask);

        return new DeviceInventory(
            "1.0",
            timeProvider.GetUtcNow(),
            "macos",
            ProbeValue<string>.Available(Environment.MachineName),
            serial,
            AvailableCamelCaseIdentifier("Apple Inc.", "System manufacturer was unavailable."),
            model,
            await operatingSystemTask,
            cpu,
            await memoryTask,
            CollectMountedDisks(),
            await encryptionTask,
            await domainTask,
            CollectLoginProviders(),
            await antivirusTask,
            await updatesTask,
            await sbomTask);
    }

    private async Task<ProbeValue<OperatingSystemInfo>> CollectOperatingSystemAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult nameResult = await processRunner.RunAsync(
            "sw_vers", ["-productName"], ProbeTimeout, cancellationToken);
        ProcessResult versionResult = await processRunner.RunAsync(
            "sw_vers", ["-productVersion"], ProbeTimeout, cancellationToken);
        ProcessResult buildResult = await processRunner.RunAsync(
            "sw_vers", ["-buildVersion"], ProbeTimeout, cancellationToken);
        ProcessResult kernelResult = await processRunner.RunAsync(
            "uname", ["-r"], ProbeTimeout, cancellationToken);

        if (!nameResult.Succeeded && !versionResult.Succeeded)
        {
            return ProbeValue<OperatingSystemInfo>.Unavailable("sw_vers is unavailable.");
        }

        string name = nameResult.Succeeded ? nameResult.StandardOutput.Trim() : "macOS";
        string version = versionResult.Succeeded ? versionResult.StandardOutput.Trim() : "Unknown";
        string? build = buildResult.Succeeded ? buildResult.StandardOutput.Trim() : null;
        string? kernel = kernelResult.Succeeded ? kernelResult.StandardOutput.Trim() : null;

        return ProbeValue<OperatingSystemInfo>.Available(new OperatingSystemInfo(
            name,
            version,
            DisplayVersion: version,
            Build: build,
            Kernel: kernel));
    }

    private async Task<ProbeValue<UpdateInventory>> CollectUpdatesAsync(
        CancellationToken cancellationToken)
    {
        List<SoftwareUpdate> available = await CollectAvailableSoftwareUpdatesAsync(cancellationToken);
        List<SoftwareUpdate> installed = await CollectInstalledSoftwareUpdatesAsync(cancellationToken);

        if (available.Count == 0 && installed.Count == 0)
        {
            return ProbeValue<UpdateInventory>.Unavailable(
                "softwareupdate history/list was unavailable.");
        }

        return ProbeValue<UpdateInventory>.Available(new UpdateInventory(installed, available));
    }

    private async Task<List<SoftwareUpdate>> CollectAvailableSoftwareUpdatesAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "softwareupdate", ["-l"], TimeSpan.FromSeconds(90), cancellationToken);
        if (!result.Succeeded && string.IsNullOrWhiteSpace(result.StandardOutput) &&
            string.IsNullOrWhiteSpace(result.StandardError))
        {
            return [];
        }

        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
        List<SoftwareUpdate> updates = [];
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            const string labelPrefix = "* Label:";
            const string labelPrefixAlt = "Label:";
            string? label = null;
            if (trimmed.StartsWith(labelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                label = trimmed[labelPrefix.Length..].Trim();
            }
            else if (trimmed.StartsWith(labelPrefixAlt, StringComparison.OrdinalIgnoreCase))
            {
                label = trimmed[labelPrefixAlt.Length..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                updates.Add(new SoftwareUpdate(label, label, "softwareupdate"));
            }
        }

        return updates;
    }

    private async Task<List<SoftwareUpdate>> CollectInstalledSoftwareUpdatesAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "softwareupdate", ["--history"], ProbeTimeout, cancellationToken);
        if (!result.Succeeded)
        {
            return [];
        }

        List<SoftwareUpdate> updates = [];
        foreach (string line in result.StandardOutput.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Display Name", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            DateTimeOffset? installedAt = null;
            string title = line;
            if (parts.Length >= 3 &&
                DateTime.TryParse($"{parts[^2]} {parts[^1]}", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
                    out DateTime parsed))
            {
                installedAt = parsed;
                title = string.Join(' ', parts[..^2]);
            }

            updates.Add(new SoftwareUpdate(title, title, "softwareupdate", installedAt));
        }

        return updates.Take(50).ToList();
    }

    private static (ProbeValue<string> Serial, ProbeValue<CpuInfo> Cpu, ProbeValue<string> Model)
        ParseHardware(ProcessResult result)
    {
        if (!result.Succeeded)
        {
            return (
                ProbeValue<string>.Unavailable("system_profiler is unavailable."),
                ProbeValue<CpuInfo>.Unavailable("system_profiler is unavailable."),
                ProbeValue<string>.Unavailable("system_profiler is unavailable."));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement item = document.RootElement.GetProperty("SPHardwareDataType")[0];
            string? serial = GetString(item, "serial_number");
            string cpuModel = GetString(item, "chip_type") ??
                              GetString(item, "cpu_type") ??
                              GetString(item, "machine_model") ??
                              "Unknown";
            string? sku = GetString(item, "machine_model") ?? GetString(item, "model_number");
            int logical = GetInt32(item, "number_processors") ?? Environment.ProcessorCount;
            int? cores = GetInt32(item, "total_number_cores");
            return (
                string.IsNullOrWhiteSpace(serial)
                    ? ProbeValue<string>.Unavailable("A hardware serial number was not returned.")
                    : ProbeValue<string>.Available(serial),
                ProbeValue<CpuInfo>.Available(new CpuInfo(cpuModel, logical, cores)),
                AvailableCamelCaseIdentifier(sku, "System SKU / machine model was not returned."));
        }
        catch (JsonException)
        {
            return (
                ProbeValue<string>.Error("system_profiler returned invalid JSON."),
                ProbeValue<CpuInfo>.Error("system_profiler returned invalid JSON."),
                ProbeValue<string>.Error("system_profiler returned invalid JSON."));
        }
    }

    private async Task<ProbeValue<MemoryInfo>> CollectMemoryAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "sysctl", ["-n", "hw.memsize"], ProbeTimeout, cancellationToken);
        return result.Succeeded &&
               ulong.TryParse(result.StandardOutput.Trim(), NumberStyles.None,
                   CultureInfo.InvariantCulture, out ulong bytes)
            ? ProbeValue<MemoryInfo>.Available(new MemoryInfo(bytes))
            : ProbeValue<MemoryInfo>.Unavailable("hw.memsize was not returned.");
    }

    private async Task<ProbeValue<IReadOnlyList<EncryptionVolume>>> CollectEncryptionAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "fdesetup", ["status"], ProbeTimeout, cancellationToken);
        if (!result.Succeeded)
        {
            return ProbeValue<IReadOnlyList<EncryptionVolume>>.Unavailable(
                "FileVault status could not be read.");
        }

        string state = result.StandardOutput.Contains("FileVault is On",
            StringComparison.OrdinalIgnoreCase) ? "encrypted" :
            result.StandardOutput.Contains("FileVault is Off",
                StringComparison.OrdinalIgnoreCase) ? "not encrypted" : "unknown";
        return ProbeValue<IReadOnlyList<EncryptionVolume>>.Available(
            [new EncryptionVolume("/", "FileVault", state)]);
    }

    private async Task<ProbeValue<DomainWorkspaceInfo>> CollectDomainAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult activeDirectory = await processRunner.RunAsync(
            "dsconfigad", ["-show"], ProbeTimeout, cancellationToken);
        string? domain = activeDirectory.Succeeded
            ? ExtractValue(activeDirectory.StandardOutput, "Active Directory Domain")
            : null;

        ProcessResult enrollment = await processRunner.RunAsync(
            "profiles", ["status", "-type", "enrollment"], ProbeTimeout, cancellationToken);
        bool enrolled = enrollment.Succeeded &&
                        (enrollment.StandardOutput.Contains("Enrolled via DEP: Yes",
                             StringComparison.OrdinalIgnoreCase) ||
                         enrollment.StandardOutput.Contains("MDM enrollment: Yes",
                             StringComparison.OrdinalIgnoreCase));
        string? workspace = enrolled ? "Apple MDM" : null;
        return ProbeValue<DomainWorkspaceInfo>.Available(
            new DomainWorkspaceInfo(domain, domain is not null, workspace, enrolled));
    }

    private static ProbeValue<IReadOnlyList<LoginProviderInfo>> CollectLoginProviders()
    {
        List<LoginProviderInfo> providers =
            [new("macOS LoginWindow", "available", "Built-in macOS sign-in")];
        AddIfExists(providers, "Jamf Connect", "/Applications/Jamf Connect.app");
        AddIfExists(providers, "XCreds", "/Applications/XCreds.app");
        AddIfExists(providers, "NoMAD Login", "/Library/Security/SecurityAgentPlugins/NoMADLoginAD.bundle");
        return ProbeValue<IReadOnlyList<LoginProviderInfo>>.Available(providers);
    }

    private async Task<ProbeValue<IReadOnlyList<AntivirusInfo>>> CollectAntivirusAsync(
        CancellationToken cancellationToken)
    {
        List<AntivirusInfo> products = [];
        ProcessResult gatekeeper = await processRunner.RunAsync(
            "spctl", ["--status"], ProbeTimeout, cancellationToken);
        if (gatekeeper.Succeeded)
        {
            bool enabled = gatekeeper.StandardOutput.Contains("enabled",
                StringComparison.OrdinalIgnoreCase);
            products.Add(new AntivirusInfo(
                "Gatekeeper", enabled ? "enabled" : "disabled", enabled));
        }

        ProcessResult xprotect = await processRunner.RunAsync(
            "pkgutil", ["--pkg-info", "com.apple.pkg.XProtectPlistConfigData"],
            ProbeTimeout, cancellationToken);
        if (xprotect.Succeeded)
        {
            products.Add(new AntivirusInfo(
                "XProtect",
                "installed",
                true,
                Detail: ExtractValue(xprotect.StandardOutput, "version")));
        }

        AddSecurityApp(products, "Microsoft Defender for Endpoint",
            "/Applications/Microsoft Defender.app");
        AddSecurityApp(products, "CrowdStrike Falcon", "/Applications/Falcon.app");
        AddSecurityApp(products, "SentinelOne", "/Applications/SentinelOne");

        return new ProbeValue<IReadOnlyList<AntivirusInfo>>(
            ProbeStatus.Available,
            products,
            products.Count == 0 ? "No recognized security product was detected." : null);
    }

    private static void AddIfExists(List<LoginProviderInfo> providers, string name, string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            providers.Add(new LoginProviderInfo(name, "installed", path));
        }
    }

    private static void AddSecurityApp(List<AntivirusInfo> products, string name, string path)
    {
        if (Directory.Exists(path))
        {
            products.Add(new AntivirusInfo(name, "installed", Detail: path));
        }
    }

    private static string? ExtractValue(string content, string key)
    {
        string? line = content.Split('\n')
            .FirstOrDefault(value => value.TrimStart()
                .StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
        return line?.Split(':', 2)[1].Trim();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) ? value.ToString() : null;

    private static int? GetInt32(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int result)
            ? result
            : null;
}
