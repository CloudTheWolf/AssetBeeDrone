using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Collectors.Linux;

public sealed partial class LinuxInventoryCollector(
    IProcessRunner processRunner,
    TimeProvider timeProvider,
    IOptions<DroneOptions> options) : InventoryCollectorBase, IDeviceInventoryCollector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateProbeTimeout = TimeSpan.FromSeconds(45);
    private readonly DroneOptions _options = options.Value;

    public async Task<DeviceInventory> CollectAsync(CancellationToken cancellationToken)
    {
        Task<ProbeValue<OperatingSystemInfo>> operatingSystemTask =
            CollectOperatingSystemAsync(cancellationToken);
        Task<ProbeValue<IReadOnlyList<DiskInfo>>> disksTask = CollectDisksAsync(cancellationToken);
        Task<ProbeValue<IReadOnlyList<EncryptionVolume>>> encryptionTask =
            CollectEncryptionAsync(cancellationToken);
        Task<ProbeValue<DomainWorkspaceInfo>> domainTask = CollectDomainAsync(cancellationToken);
        Task<ProbeValue<IReadOnlyList<AntivirusInfo>>> antivirusTask =
            CollectAntivirusAsync(cancellationToken);
        Task<ProbeValue<UpdateInventory>> updatesTask = CollectUpdatesAsync(cancellationToken);
        Task<ProbeValue<SbomInventory>> sbomTask = SbomCollector.CollectLinuxAsync(
            processRunner,
            timeProvider,
            _options.IncludeSbom,
            _options.IncludeContainerSboms,
            cancellationToken);
        Task<HostIdentity> identityTask = CollectHostIdentityAsync(cancellationToken);

        await Task.WhenAll(
            operatingSystemTask,
            disksTask,
            encryptionTask,
            domainTask,
            antivirusTask,
            updatesTask,
            sbomTask,
            identityTask);

        HostIdentity identity = await identityTask;
        return new DeviceInventory(
            "1.0",
            timeProvider.GetUtcNow(),
            "linux",
            ProbeValue<string>.Available(Environment.MachineName),
            identity.SerialNumber,
            identity.Manufacturer,
            identity.Model,
            await operatingSystemTask,
            CollectCpu(),
            CollectMemory(),
            await disksTask,
            await encryptionTask,
            await domainTask,
            CollectLoginProviders(),
            await antivirusTask,
            await updatesTask,
            await sbomTask);
    }

    private sealed record HostIdentity(
        ProbeValue<string> SerialNumber,
        ProbeValue<string> Manufacturer,
        ProbeValue<string> Model);

    private async Task<HostIdentity> CollectHostIdentityAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> hostnamectl = await ReadHostnamectlAsync(cancellationToken);

        string? serial = FirstMeaningful(
            ReadFirstExistingFile(
                "/sys/class/dmi/id/product_serial",
                "/sys/devices/virtual/dmi/id/product_serial",
                "/sys/class/dmi/id/product_uuid",
                "/sys/devices/virtual/dmi/id/product_uuid",
                "/sys/class/dmi/id/board_serial",
                "/sys/devices/virtual/dmi/id/board_serial",
                "/sys/firmware/devicetree/base/serial-number"),
            GetMapValue(hostnamectl, "HardwareSerial", "Serial"));

        string? manufacturer = FirstMeaningful(
            ReadFirstExistingFile(
                "/sys/class/dmi/id/sys_vendor",
                "/sys/devices/virtual/dmi/id/sys_vendor",
                "/sys/class/dmi/id/board_vendor",
                "/sys/devices/virtual/dmi/id/board_vendor",
                "/sys/class/dmi/id/chassis_vendor",
                "/sys/devices/virtual/dmi/id/chassis_vendor"),
            GetMapValue(hostnamectl, "HardwareVendor", "Vendor"));

        string? model = FirstMeaningful(
            ReadFirstExistingFile(
                "/sys/class/dmi/id/product_sku",
                "/sys/devices/virtual/dmi/id/product_sku",
                "/sys/class/dmi/id/product_name",
                "/sys/devices/virtual/dmi/id/product_name",
                "/sys/class/dmi/id/board_name",
                "/sys/devices/virtual/dmi/id/board_name",
                "/sys/firmware/devicetree/base/model"),
            GetMapValue(hostnamectl, "HardwareModel", "Model"));

        // Last-resort stable id when firmware DMI is absent (containers, WSL, some VMs).
        serial ??= FirstMeaningful(
            ReadFirstExistingFile("/etc/machine-id", "/var/lib/dbus/machine-id"));

        return new HostIdentity(
            string.IsNullOrWhiteSpace(serial)
                ? ProbeValue<string>.Unavailable(
                    "No firmware serial, product UUID, or machine-id was available.")
                : ProbeValue<string>.Available(serial),
            AvailableCamelCaseIdentifier(
                manufacturer,
                "System manufacturer was not exposed by DMI or hostnamectl."),
            AvailableCamelCaseIdentifier(
                model,
                "System SKU/model was not exposed by DMI or hostnamectl."));
    }

    private async Task<Dictionary<string, string>> ReadHostnamectlAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult json = await processRunner.RunAsync(
            "hostnamectl", ["--json=short"], ProbeTimeout, cancellationToken);
        if (json.Succeeded && !string.IsNullOrWhiteSpace(json.StandardOutput))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json.StandardOutput);
                Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        values[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }

                return values;
            }
            catch (JsonException)
            {
                // Fall through to text parsing.
            }
        }

        ProcessResult text = await processRunner.RunAsync(
            "hostnamectl", [], ProbeTimeout, cancellationToken);
        if (!text.Succeeded)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.StandardOutput.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            parsed[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return parsed;
    }

    private static string? GetMapValue(Dictionary<string, string> values, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FirstMeaningful(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (IsMeaningfulHardwareValue(value))
            {
                return value!.Trim();
            }
        }

        return null;
    }

    private static bool IsMeaningfulHardwareValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        string[] placeholders =
        [
            "none", "null", "n/a", "na", "not specified", "not applicable",
            "default string", "to be filled by o.e.m.", "to be filled by oem",
            "system serial number", "system product name", "system manufacturer",
            "chassis serial number", "invalid", "unknown", "undefined", "o.e.m."
        ];
        return !placeholders.Any(placeholder =>
            normalized.Equals(placeholder, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ProbeValue<OperatingSystemInfo>> CollectOperatingSystemAsync(
        CancellationToken cancellationToken)
    {
        string? release = ReadFirstExistingFile("/etc/os-release", "/usr/lib/os-release");
        if (release is null)
        {
            return ProbeValue<OperatingSystemInfo>.Unavailable(
                "/etc/os-release is unavailable.");
        }

        Dictionary<string, string> values = ParseOsRelease(release);
        string name = GetOsReleaseValue(values, "NAME") ??
                      GetOsReleaseValue(values, "PRETTY_NAME") ??
                      "Linux";
        string? versionId = GetOsReleaseValue(values, "VERSION_ID");
        string? prettyVersion = GetOsReleaseValue(values, "VERSION") ??
                                GetOsReleaseValue(values, "PRETTY_NAME");
        string version = prettyVersion ?? versionId ?? "Unknown";

        ProcessResult uname = await processRunner.RunAsync(
            "uname", ["-r"], ProbeTimeout, cancellationToken);
        string? kernel = uname.Succeeded ? uname.StandardOutput.Trim() : null;

        if (version == "Unknown" && string.IsNullOrWhiteSpace(kernel))
        {
            return ProbeValue<OperatingSystemInfo>.Unavailable("OS version was not returned.");
        }

        return ProbeValue<OperatingSystemInfo>.Available(new OperatingSystemInfo(
            name,
            version,
            versionId,
            Build: null,
            Kernel: kernel));
    }

    private async Task<ProbeValue<UpdateInventory>> CollectUpdatesAsync(
        CancellationToken cancellationToken)
    {
        List<SoftwareUpdate> available = await CollectAvailableAptUpdatesAsync(cancellationToken);
        if (available.Count == 0)
        {
            available = await CollectAvailableDnfUpdatesAsync(cancellationToken);
        }

        List<SoftwareUpdate> installed = CollectInstalledAptUpdates();
        if (installed.Count == 0)
        {
            installed = await CollectInstalledRpmUpdatesAsync(cancellationToken);
        }

        if (available.Count == 0 && installed.Count == 0)
        {
            return ProbeValue<UpdateInventory>.Unavailable(
                "No package-manager update history or pending updates were found.");
        }

        return ProbeValue<UpdateInventory>.Available(new UpdateInventory(installed, available));
    }

    private async Task<List<SoftwareUpdate>> CollectAvailableAptUpdatesAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "apt",
            ["list", "--upgradable"],
            UpdateProbeTimeout,
            cancellationToken);
        if (!result.Succeeded && string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        List<SoftwareUpdate> updates = [];
        foreach (string line in result.StandardOutput.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Listing", StringComparison.OrdinalIgnoreCase) ||
                !line.Contains('/'))
            {
                continue;
            }

            string package = line.Split('/', 2)[0].Trim();
            string? candidate = line.Split([' '], StringSplitOptions.RemoveEmptyEntries)
                .ElementAtOrDefault(1);
            updates.Add(new SoftwareUpdate(
                package,
                candidate is null ? package : $"{package} -> {candidate}",
                "apt"));
        }

        return updates;
    }

    private async Task<List<SoftwareUpdate>> CollectAvailableDnfUpdatesAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "dnf",
            ["check-update", "--quiet"],
            UpdateProbeTimeout,
            cancellationToken);
        // dnf returns exit code 100 when updates are available.
        if (result.ExitCode is not (0 or 100) || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        List<SoftwareUpdate> updates = [];
        foreach (string line in result.StandardOutput.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = WhitespaceRegex().Split(line);
            if (parts.Length < 2 || parts[0].StartsWith("Last", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            updates.Add(new SoftwareUpdate(parts[0], $"{parts[0]} {parts[1]}", "dnf"));
        }

        return updates;
    }

    private static List<SoftwareUpdate> CollectInstalledAptUpdates()
    {
        string? history = ReadFirstExistingFile("/var/log/apt/history.log");
        if (history is null)
        {
            return [];
        }

        List<SoftwareUpdate> updates = [];
        DateTimeOffset? start = null;
        foreach (string line in history.Split('\n'))
        {
            if (line.StartsWith("Start-Date:", StringComparison.Ordinal))
            {
                start = DateTimeOffset.TryParse(line["Start-Date:".Length..].Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed)
                    ? parsed
                    : null;
                continue;
            }

            if (!line.StartsWith("Upgrade:", StringComparison.Ordinal) &&
                !line.StartsWith("Install:", StringComparison.Ordinal))
            {
                continue;
            }

            string packages = line[(line.IndexOf(':') + 1)..].Trim();
            foreach (Match match in AptPackageRegex().Matches(packages))
            {
                string package = match.Groups[1].Value;
                string version = match.Groups[2].Value.Split(',', 2)[^1].Trim();
                updates.Add(new SoftwareUpdate(
                    package,
                    $"{package} {version}",
                    line.StartsWith("Upgrade:", StringComparison.Ordinal) ? "upgrade" : "install",
                    start));
            }
        }

        updates.Reverse();
        return updates.Take(50).ToList();
    }

    private async Task<List<SoftwareUpdate>> CollectInstalledRpmUpdatesAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "rpm",
            ["-qa", "--last"],
            ProbeTimeout,
            cancellationToken);
        if (!result.Succeeded)
        {
            return [];
        }

        List<SoftwareUpdate> updates = [];
        foreach (string line in result.StandardOutput.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50))
        {
            string[] parts = WhitespaceRegex().Split(line, 2);
            if (parts.Length == 0)
            {
                continue;
            }

            DateTimeOffset? installedAt = null;
            if (parts.Length == 2 &&
                DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed))
            {
                installedAt = parsed;
            }

            updates.Add(new SoftwareUpdate(parts[0], parts[0], "rpm", installedAt));
        }

        return updates;
    }

    private static Dictionary<string, string> ParseOsRelease(string content)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim().Trim('"');
            values[key] = value;
        }

        return values;
    }

    private static string? GetOsReleaseValue(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static ProbeValue<CpuInfo> CollectCpu()
    {
        string? cpuInfo = ReadFirstExistingFile("/proc/cpuinfo");
        string? model = cpuInfo?.Split('\n')
            .Select(line => line.Split(':', 2))
            .FirstOrDefault(parts => parts.Length == 2 &&
                                     parts[0].Trim() is "model name" or "Hardware")?[1]
            .Trim();

        return ProbeValue<CpuInfo>.Available(new CpuInfo(
            model ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount));
    }

    private static ProbeValue<MemoryInfo> CollectMemory()
    {
        string? memInfo = ReadFirstExistingFile("/proc/meminfo");
        if (memInfo is null)
        {
            return ProbeValue<MemoryInfo>.Unavailable("/proc/meminfo is unavailable.");
        }

        ulong? total = ParseMemInfo(memInfo, "MemTotal");
        ulong? available = ParseMemInfo(memInfo, "MemAvailable");
        return total is null
            ? ProbeValue<MemoryInfo>.Error("MemTotal could not be parsed.")
            : ProbeValue<MemoryInfo>.Available(new MemoryInfo(total.Value, available));
    }

    private async Task<ProbeValue<IReadOnlyList<DiskInfo>>> CollectDisksAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "lsblk",
            ["-J", "-b", "-o", "NAME,KNAME,TYPE,FSTYPE,SIZE,FSAVAIL,MOUNTPOINT"],
            ProbeTimeout,
            cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return ProbeValue<IReadOnlyList<DiskInfo>>.Unavailable(
                "lsblk is unavailable; block devices could not be inventoried.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            List<DiskInfo> disks = [];
            if (document.RootElement.TryGetProperty("blockdevices", out JsonElement devices))
            {
                CollectBlockDevices(devices, disks);
            }

            return ProbeValue<IReadOnlyList<DiskInfo>>.Available(disks);
        }
        catch (JsonException exception)
        {
            return ProbeValue<IReadOnlyList<DiskInfo>>.Error(
                $"lsblk returned invalid JSON: {exception.Message}");
        }
    }

    private static void CollectBlockDevices(JsonElement devices, List<DiskInfo> disks)
    {
        if (devices.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement device in devices.EnumerateArray())
        {
            string? type = GetJsonString(device, "type");
            string? name = GetJsonString(device, "kname") ?? GetJsonString(device, "name");
            string? fstype = GetJsonString(device, "fstype");
            string? mountPoint = GetJsonString(device, "mountpoint");
            bool hasChildren = device.TryGetProperty("children", out JsonElement children) &&
                               children.ValueKind == JsonValueKind.Array &&
                               children.GetArrayLength() > 0;

            if (ShouldIncludeBlockDevice(type, name, fstype, mountPoint, hasChildren) &&
                !string.IsNullOrWhiteSpace(name) &&
                TryGetUInt64(device, "size", out ulong size))
            {
                ulong? available = TryGetUInt64(device, "fsavail", out ulong fsAvail)
                    ? fsAvail
                    : null;
                disks.Add(new DiskInfo(
                    name,
                    string.IsNullOrWhiteSpace(mountPoint) ? null : mountPoint,
                    size,
                    available,
                    string.IsNullOrWhiteSpace(fstype) ? null : fstype));
            }

            if (hasChildren)
            {
                CollectBlockDevices(children, disks);
            }
        }
    }

    private static bool ShouldIncludeBlockDevice(
        string? type,
        string? name,
        string? fstype,
        string? mountPoint,
        bool hasChildren)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.StartsWith("loop", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (type is not null &&
            (type.Equals("loop", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("rom", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (IsVirtualFileSystem(fstype))
        {
            return false;
        }

        // Prefer partition/LVM/RAID/crypt names (nvme0n1p1). Skip blank parent disks
        // that only exist as containers for child partitions.
        if (type is not null &&
            type.Equals("disk", StringComparison.OrdinalIgnoreCase) &&
            hasChildren &&
            string.IsNullOrWhiteSpace(fstype) &&
            string.IsNullOrWhiteSpace(mountPoint))
        {
            return false;
        }

        return type is null ||
               type.Equals("disk", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("part", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("lvm", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("crypt", StringComparison.OrdinalIgnoreCase) ||
               type.StartsWith("raid", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("mpath", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVirtualFileSystem(string? fstype)
    {
        if (string.IsNullOrWhiteSpace(fstype))
        {
            return false;
        }

        if (fstype.StartsWith("fuse", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] virtualFileSystems =
        [
            "overlay", "squashfs", "tmpfs", "devtmpfs", "devpts", "proc", "sysfs",
            "cgroup", "cgroup2", "pstore", "securityfs", "debugfs", "tracefs",
            "fusectl", "configfs", "mqueue", "hugetlbfs", "rpc_pipefs", "nsfs",
            "bpf", "autofs", "binfmt_misc", "efivarfs", "ramfs", "rootfs",
            "iso9660", "udf"
        ];
        return virtualFileSystems.Any(candidate =>
            fstype.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetJsonString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool TryGetUInt64(JsonElement element, string property, out ulong value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out JsonElement propertyValue) ||
            propertyValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (propertyValue.ValueKind == JsonValueKind.Number &&
            propertyValue.TryGetUInt64(out value))
        {
            return true;
        }

        return propertyValue.ValueKind == JsonValueKind.String &&
               ulong.TryParse(propertyValue.GetString(), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out value);
    }

    private async Task<ProbeValue<IReadOnlyList<EncryptionVolume>>> CollectEncryptionAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "lsblk",
            ["-n", "-o", "NAME,FSTYPE,MOUNTPOINT"],
            ProbeTimeout,
            cancellationToken);
        if (!result.Succeeded)
        {
            return ProbeValue<IReadOnlyList<EncryptionVolume>>.Unavailable(
                "lsblk is unavailable; LUKS/dm-crypt state could not be inspected.");
        }

        List<EncryptionVolume> encrypted = [];
        foreach (string line in result.StandardOutput.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = WhitespaceRegex().Split(line.Trim(), 3);
            if (parts.Length >= 2 &&
                (parts[1].Equals("crypto_LUKS", StringComparison.OrdinalIgnoreCase) ||
                 parts[0].StartsWith("dm-", StringComparison.Ordinal)))
            {
                encrypted.Add(new EncryptionVolume(
                    parts.Length == 3 ? parts[2] : parts[0],
                    "LUKS/dm-crypt",
                    parts[1].Equals("crypto_LUKS", StringComparison.OrdinalIgnoreCase)
                        ? "encrypted"
                        : "active"));
            }
        }

        return new ProbeValue<IReadOnlyList<EncryptionVolume>>(
            ProbeStatus.Available,
            encrypted,
            encrypted.Count == 0 ? "No LUKS/dm-crypt volumes were detected." : null);
    }

    private async Task<ProbeValue<DomainWorkspaceInfo>> CollectDomainAsync(
        CancellationToken cancellationToken)
    {
        ProcessResult realm = await processRunner.RunAsync(
            "realm", ["list"], ProbeTimeout, cancellationToken);
        if (realm.Succeeded && !string.IsNullOrWhiteSpace(realm.StandardOutput))
        {
            string? domain = realm.StandardOutput.Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("domain-name:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1].Trim();
            domain ??= realm.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return ProbeValue<DomainWorkspaceInfo>.Available(
                new DomainWorkspaceInfo(domain, true, "realmd/SSSD", true));
        }

        ProcessResult hostname = await processRunner.RunAsync(
            "hostname", ["-d"], ProbeTimeout, cancellationToken);
        string? dnsDomain = hostname.Succeeded ? hostname.StandardOutput.Trim() : null;
        return ProbeValue<DomainWorkspaceInfo>.Available(new DomainWorkspaceInfo(
            string.IsNullOrWhiteSpace(dnsDomain) ? null : dnsDomain,
            false,
            File.Exists("/etc/sssd/sssd.conf") ? "SSSD" : null,
            File.Exists("/etc/sssd/sssd.conf")));
    }

    private static ProbeValue<IReadOnlyList<LoginProviderInfo>> CollectLoginProviders()
    {
        List<LoginProviderInfo> providers = [];
        if (Directory.Exists("/etc/pam.d"))
        {
            providers.Add(new LoginProviderInfo("PAM", "configured", "/etc/pam.d"));
        }

        if (File.Exists("/etc/sssd/sssd.conf"))
        {
            providers.Add(new LoginProviderInfo("SSSD", "configured"));
        }

        if (File.Exists("/etc/nslcd.conf"))
        {
            providers.Add(new LoginProviderInfo("LDAP/nslcd", "configured"));
        }

        return ProbeValue<IReadOnlyList<LoginProviderInfo>>.Available(providers);
    }

    private async Task<ProbeValue<IReadOnlyList<AntivirusInfo>>> CollectAntivirusAsync(
        CancellationToken cancellationToken)
    {
        (string Name, string Service)[] detectors =
        [
            ("ClamAV", "clamav-daemon.service"),
            ("Microsoft Defender for Endpoint", "mdatp.service"),
            ("CrowdStrike Falcon", "falcon-sensor.service"),
            ("SentinelOne", "sentinelone.service"),
            ("Sophos", "sav-protect.service")
        ];

        List<AntivirusInfo> products = [];
        foreach ((string name, string service) in detectors)
        {
            ProcessResult result = await processRunner.RunAsync(
                "systemctl",
                ["show", service, "--property=LoadState", "--property=ActiveState", "--value"],
                ProbeTimeout,
                cancellationToken);
            string[] states = result.StandardOutput.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (states.Length >= 2 && states[0] == "loaded")
            {
                string state = states[1];
                bool enabled = state == "active";
                products.Add(new AntivirusInfo(name, state, enabled, Detail: service));
            }
        }

        return new ProbeValue<IReadOnlyList<AntivirusInfo>>(
            ProbeStatus.Available,
            products,
            products.Count == 0 ? "No recognized antivirus/EDR service was detected." : null);
    }

    private static ulong? ParseMemInfo(string content, string key)
    {
        string? line = content.Split('\n')
            .FirstOrDefault(value => value.StartsWith(key + ":", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        Match match = DigitsRegex().Match(line);
        return match.Success &&
               ulong.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture,
                   out ulong kibibytes)
            ? kibibytes * 1024
            : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"([A-Za-z0-9.+\-]+):\w+\s+\(([^)]+)\)")]
    private static partial Regex AptPackageRegex();
}
