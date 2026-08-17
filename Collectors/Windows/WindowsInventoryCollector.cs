using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Collectors.Windows;

public sealed partial class WindowsInventoryCollector(
    IProcessRunner processRunner,
    TimeProvider timeProvider,
    IOptions<DroneOptions> options) : InventoryCollectorBase, IDeviceInventoryCollector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(120);
    private readonly DroneOptions _options = options.Value;

    public async Task<DeviceInventory> CollectAsync(CancellationToken cancellationToken)
    {
        Task<ProcessResult> inventoryTask = processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", InventoryScript],
            ProbeTimeout,
            cancellationToken);
        Task<ProbeValue<SbomInventory>> sbomTask = SbomCollector.CollectWindowsAsync(
            processRunner, timeProvider, _options.IncludeSbom, cancellationToken);

        ProcessResult result = await inventoryTask;
        ProbeValue<SbomInventory> sbom = await sbomTask;

        if (!result.Succeeded)
        {
            string detail = result.TimedOut
                ? "The Windows inventory probe timed out."
                : "The Windows inventory probe could not be completed.";
            return FailedInventory(detail, sbom);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            return new DeviceInventory(
                "1.0",
                timeProvider.GetUtcNow(),
                "windows",
                AvailableString(root, "DeviceName"),
                AvailableString(root, "SerialNumber"),
                AvailableCamelCaseIdentifier(
                    GetString(root, "SystemManufacturer"),
                    "System manufacturer was not returned."),
                AvailableCamelCaseIdentifier(
                    GetString(root, "SystemSku") ?? GetString(root, "SystemModel"),
                    "System SKU was not returned."),
                ParseOperatingSystem(root),
                ParseCpu(root),
                ParseMemory(root),
                ParseDisks(root),
                ParseEncryption(root),
                ParseDomain(root),
                ParseLoginProviders(root),
                ParseAntivirus(root),
                ParseUpdates(root),
                sbom);
        }
        catch (JsonException)
        {
            Console.WriteLine("The Windows inventory probe returned invalid JSON.");
            return FailedInventory("The Windows inventory probe returned invalid data.", sbom);
        }
    }

    private DeviceInventory FailedInventory(string detail, ProbeValue<SbomInventory> sbom) => new(
        "1.0",
        timeProvider.GetUtcNow(),
        "windows",
        ProbeValue<string>.Available(Environment.MachineName),
        ProbeValue<string>.Error(detail),
        ProbeValue<string>.Error(detail),
        ProbeValue<string>.Error(detail),
        ProbeValue<OperatingSystemInfo>.Error(detail),
        ProbeValue<CpuInfo>.Error(detail),
        ProbeValue<MemoryInfo>.Error(detail),
        CollectMountedDisks(),
        ProbeValue<IReadOnlyList<EncryptionVolume>>.Error(detail),
        ProbeValue<DomainWorkspaceInfo>.Error(detail),
        ProbeValue<IReadOnlyList<LoginProviderInfo>>.Error(detail),
        ProbeValue<IReadOnlyList<AntivirusInfo>>.Error(detail),
        ProbeValue<UpdateInventory>.Error(detail),
        sbom);
    private static ProbeValue<string> AvailableString(JsonElement root, string name)
    {
        string? value = GetString(root, name);
        return string.IsNullOrWhiteSpace(value)
            ? ProbeValue<string>.Unavailable($"{name} was not returned.")
            : ProbeValue<string>.Available(value);
    }

    private static ProbeValue<OperatingSystemInfo> ParseOperatingSystem(JsonElement root)
    {
        string? name = GetString(root, "OsName");
        string? version = GetString(root, "OsVersion");
        string? displayVersion = GetString(root, "OsDisplayVersion");
        string? build = GetString(root, "OsBuild");
        if (string.IsNullOrWhiteSpace(name) &&
            string.IsNullOrWhiteSpace(version) &&
            string.IsNullOrWhiteSpace(displayVersion))
        {
            return ProbeValue<OperatingSystemInfo>.Unavailable(
                "Operating system information was not returned.");
        }

        return ProbeValue<OperatingSystemInfo>.Available(new OperatingSystemInfo(
            name ?? "Windows",
            version ?? displayVersion ?? "Unknown",
            displayVersion,
            build));
    }

    private static ProbeValue<UpdateInventory> ParseUpdates(JsonElement root)
    {
        if (!root.TryGetProperty("Updates", out JsonElement updates))
        {
            return ProbeValue<UpdateInventory>.Unavailable(
                "Windows Update inventory was not returned.");
        }

        List<SoftwareUpdate> installed = ParseUpdateList(updates, "Installed");
        List<SoftwareUpdate> available = ParseUpdateList(updates, "Available");
        return ProbeValue<UpdateInventory>.Available(new UpdateInventory(installed, available));
    }

    private static List<SoftwareUpdate> ParseUpdateList(JsonElement root, string property)
    {
        List<SoftwareUpdate> updates = [];
        if (!root.TryGetProperty(property, out JsonElement values))
        {
            return updates;
        }

        foreach (JsonElement update in AsArray(values))
        {
            string? id = GetString(update, "Id");
            string? title = GetString(update, "Title");
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            updates.Add(new SoftwareUpdate(
                id ?? title!,
                title ?? id!,
                GetString(update, "Category"),
                GetDateTimeOffset(update, "InstalledAtUtc"),
                GetString(update, "KbArticle")));
        }

        return updates;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement value, string property)
    {
        string? text = GetString(value, property);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset result)
            ? result
            : null;
    }

    private static ProbeValue<CpuInfo> ParseCpu(JsonElement root)
    {
        if (!root.TryGetProperty("Cpu", out JsonElement cpu))
        {
            return ProbeValue<CpuInfo>.Unavailable("CPU information was not returned.");
        }

        return ProbeValue<CpuInfo>.Available(new CpuInfo(
            GetString(cpu, "Name") ?? "Unknown",
            GetInt32(cpu, "LogicalProcessors") ?? Environment.ProcessorCount,
            GetInt32(cpu, "Cores")));
    }

    private static ProbeValue<MemoryInfo> ParseMemory(JsonElement root)
    {
        ulong? total = GetUInt64(root, "MemoryBytes");
        return total is null
            ? ProbeValue<MemoryInfo>.Unavailable("Memory information was not returned.")
            : ProbeValue<MemoryInfo>.Available(new MemoryInfo(total.Value));
    }

    private static ProbeValue<IReadOnlyList<DiskInfo>> ParseDisks(JsonElement root)
    {
        if (!root.TryGetProperty("Disks", out JsonElement values))
        {
            return CollectMountedDisks();
        }

        List<DiskInfo> disks = [];
        foreach (JsonElement disk in AsArray(values))
        {
            disks.Add(new DiskInfo(
                GetString(disk, "DeviceId") ?? "Unknown",
                GetString(disk, "DeviceId"),
                GetUInt64(disk, "Size") ?? 0,
                GetUInt64(disk, "FreeSpace"),
                GetString(disk, "FileSystem")));
        }

        return ProbeValue<IReadOnlyList<DiskInfo>>.Available(disks);
    }

    private static ProbeValue<IReadOnlyList<EncryptionVolume>> ParseEncryption(JsonElement root)
    {
        if (GetBoolean(root, "BitLockerAvailable") != true)
        {
            return ProbeValue<IReadOnlyList<EncryptionVolume>>.Unavailable(
                "BitLocker state or recovery protectors could not be read.");
        }

        if (!root.TryGetProperty("Encryption", out JsonElement values))
        {
            return ProbeValue<IReadOnlyList<EncryptionVolume>>.Unavailable(
                "BitLocker information was not returned.");
        }

        List<EncryptionVolume> volumes = [];
        foreach (JsonElement volume in AsArray(values))
        {
            List<string> recoveryKeys = [];
            List<BitLockerKeyProtector> keyProtectors = [];
            if (volume.TryGetProperty("RecoveryKeys", out JsonElement keys))
            {
                foreach (JsonElement key in AsArray(keys))
                {
                    string? value = key.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        recoveryKeys.Add(value);
                    }
                }
            }

            if (volume.TryGetProperty("KeyProtectors", out JsonElement protectors))
            {
                foreach (JsonElement protector in AsArray(protectors))
                {
                    string? protectorId = GetString(protector, "KeyProtectorId");
                    if (!string.IsNullOrWhiteSpace(protectorId))
                    {
                        keyProtectors.Add(new BitLockerKeyProtector(
                            protectorId,
                            GetString(protector, "RecoveryKey")));
                    }
                }
            }

            volumes.Add(new EncryptionVolume(
                GetString(volume, "MountPoint") ?? "Unknown",
                "BitLocker",
                GetString(volume, "Status") ?? "Unknown",
                recoveryKeys,
                keyProtectors));
        }

        return ProbeValue<IReadOnlyList<EncryptionVolume>>.Available(volumes);
    }

    private static ProbeValue<DomainWorkspaceInfo> ParseDomain(JsonElement root)
    {
        string? joinStatus = GetString(root, "JoinStatus");
        bool? workspaceJoined = joinStatus is null
            ? null
            : AzureJoinRegex().IsMatch(joinStatus) || WorkplaceJoinRegex().IsMatch(joinStatus);
        string? workspace = TenantNameRegex().Match(joinStatus ?? string.Empty) is { Success: true } match
            ? match.Groups[1].Value.Trim()
            : null;

        return ProbeValue<DomainWorkspaceInfo>.Available(new DomainWorkspaceInfo(
            GetString(root, "Domain"),
            GetBoolean(root, "DomainJoined"),
            workspace,
            workspaceJoined));
    }

    private static ProbeValue<IReadOnlyList<LoginProviderInfo>> ParseLoginProviders(JsonElement root)
    {
        if (!root.TryGetProperty("LoginProviders", out JsonElement values))
        {
            return ProbeValue<IReadOnlyList<LoginProviderInfo>>.Unavailable(
                "Windows credential providers were not returned.");
        }

        List<LoginProviderInfo> providers = [];
        foreach (JsonElement provider in AsArray(values))
        {
            string? name = GetString(provider, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            bool disabled = GetBoolean(provider, "Disabled") == true;
            providers.Add(new LoginProviderInfo(
                name,
                disabled ? "disabled" : "enabled",
                GetString(provider, "CLSID")));
        }

        return ProbeValue<IReadOnlyList<LoginProviderInfo>>.Available(providers);
    }

    private static ProbeValue<IReadOnlyList<AntivirusInfo>> ParseAntivirus(JsonElement root)
    {
        if (!root.TryGetProperty("Antivirus", out JsonElement values))
        {
            return ProbeValue<IReadOnlyList<AntivirusInfo>>.Unavailable(
                "Windows Security Center did not return antivirus state.");
        }

        List<AntivirusInfo> products = [];
        foreach (JsonElement product in AsArray(values))
        {
            int state = GetInt32(product, "ProductState") ?? 0;
            bool enabled = (state & 0x1000) != 0;
            bool upToDate = (state & 0x10) == 0;
            products.Add(new AntivirusInfo(
                GetString(product, "Name") ?? "Unknown",
                enabled ? "enabled" : "disabled",
                enabled,
                upToDate,
                $"Security Center state 0x{state:X6}"));
        }

        return ProbeValue<IReadOnlyList<AntivirusInfo>>.Available(products);
    }

    private static IEnumerable<JsonElement> AsArray(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [value];

    private static string? GetString(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement element) &&
        element.ValueKind is not JsonValueKind.Null
            ? element.ToString()
            : null;

    private static int? GetInt32(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement element) &&
        element.TryGetInt32(out int result) ? result : null;

    private static ulong? GetUInt64(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement element) &&
        element.TryGetUInt64(out ulong result) ? result : null;

    private static bool? GetBoolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement element) &&
        element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    [GeneratedRegex(@"AzureAdJoined\s*:\s*YES", RegexOptions.IgnoreCase)]
    private static partial Regex AzureJoinRegex();

    [GeneratedRegex(@"WorkplaceJoined\s*:\s*YES", RegexOptions.IgnoreCase)]
    private static partial Regex WorkplaceJoinRegex();

    [GeneratedRegex(@"TenantName\s*:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex TenantNameRegex();

    private const string InventoryScript = """
        $ErrorActionPreference = 'Stop'
        $bios = Get-CimInstance Win32_BIOS
        $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
        $computer = Get-CimInstance Win32_ComputerSystem
        $disks = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
            [pscustomobject]@{
                DeviceId = $_.DeviceID
                Size = [uint64]$_.Size
                FreeSpace = [uint64]$_.FreeSpace
                FileSystem = $_.FileSystem
            }
        })
        $encryption = @()
        $bitLockerAvailable = $false
        try {
            $encryption = @(Get-BitLockerVolume | ForEach-Object {
                $recoveryProtectors = @($_.KeyProtector |
                    Where-Object KeyProtectorType -eq 'RecoveryPassword')
                $keys = @($recoveryProtectors |
                    ForEach-Object { $_.RecoveryPassword } | Where-Object { $_ })
                $protectors = @($recoveryProtectors | ForEach-Object {
                    [pscustomobject]@{
                        KeyProtectorId = $_.KeyProtectorId
                        RecoveryKey = $_.RecoveryPassword
                    }
                })
                [pscustomobject]@{
                    MountPoint = $_.MountPoint
                    Status = [string]$_.VolumeStatus
                    RecoveryKeys = $keys
                    KeyProtectors = $protectors
                }
            })
            $bitLockerAvailable = $true
        } catch {}
        $antivirus = @()
        try {
            $antivirus = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntivirusProduct |
                ForEach-Object {
                    [pscustomobject]@{
                        Name = $_.displayName
                        ProductState = [int]$_.productState
                    }
                })
        } catch {}
        $loginProviders = @()
        try {
            $providerPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers'
            $friendlyNames = @{
                '{60B78E88-EAD8-445C-9CFD-0B87F74EA6CD}' = 'Windows Password'
                '{D6886603-9D2F-4EB2-B667-1971041FA96B}' = 'Windows PIN'
                '{8AF662BF-65A0-4D0A-A540-A338A999D36F}' = 'Windows Picture Password'
                '{BEC09223-B018-416D-A0AC-523971B639F5}' = 'Windows Smart Card'
            }
            $loginProviders = @(Get-ChildItem $providerPath | ForEach-Object {
                $clsid = $_.PSChildName
                $registryName = (Get-ItemProperty `
                    "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid" `
                    -ErrorAction SilentlyContinue).'(default)'
                $provider = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                $name = if ($friendlyNames.ContainsKey($clsid)) {
                    $friendlyNames[$clsid]
                }
                elseif ($registryName) {
                    $registryName `
                        -replace '^Microsoft ', '' `
                        -replace 'Credential Provider$', '' `
                        -replace 'CredentialProvider$', '' `
                        -replace '\s+$', ''
                }
                else {
                    'Unknown Credential Provider'
                }
                [pscustomobject]@{
                    Name = $name
                    CLSID = $clsid
                    Disabled = ($provider.Disabled -eq 1)
                }
            })
        } catch {}
        $join = ''
        try { $join = (& "$env:SystemRoot\System32\dsregcmd.exe" /status | Out-String) } catch {}
        $os = Get-CimInstance Win32_OperatingSystem
        $displayVersion = $null
        try {
            $displayVersion = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name DisplayVersion -ErrorAction Stop).DisplayVersion
        } catch {}
        $installedUpdates = @()
        $availableUpdates = @()
        try {
            $session = New-Object -ComObject Microsoft.Update.Session
            $searcher = $session.CreateUpdateSearcher()
            try {
                $historyCount = $searcher.GetTotalHistoryCount()
                if ($historyCount -gt 0) {
                    $installedUpdates = @($searcher.QueryHistory(0, $historyCount) |
                        Where-Object { $_.ResultCode -eq 2 -and $_.Title -match '(KB\d+)' } |
                        ForEach-Object {
                            if ($_.Title -match '(KB\d+)') {
                                [pscustomobject]@{
                                    KB = $matches[1]
                                    Title = $_.Title
                                    InstalledOn = $_.Date
                                }
                            }
                        } |
                        Group-Object KB |
                        ForEach-Object {
                            $_.Group |
                                Sort-Object InstalledOn -Descending |
                                Select-Object -First 1
                        } |
                        Sort-Object InstalledOn -Descending |
                        ForEach-Object {
                            $installedOn = $null
                            if ($_.InstalledOn) {
                                $installedOn = ([datetime]$_.InstalledOn).ToUniversalTime().ToString('o')
                            }
                            [pscustomobject]@{
                                Id = $_.KB
                                Title = $_.Title
                                Category = 'Windows Update'
                                InstalledAtUtc = $installedOn
                                KbArticle = $_.KB
                            }
                        })
                }
            } catch {}
            try {
                $result = $searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0")
                $availableUpdates = @(0..($result.Updates.Count - 1) | ForEach-Object {
                    $update = $result.Updates.Item($_)
                    $kb = @($update.KBArticleIDs | ForEach-Object { "KB$_" }) -join ','
                    if ([string]::IsNullOrWhiteSpace($kb)) { $kb = $null }
                    [pscustomobject]@{
                        Id = $update.Identity.UpdateID
                        Title = $update.Title
                        Category = (@($update.Categories | ForEach-Object { $_.Name }) -join ', ')
                        InstalledAtUtc = $null
                        KbArticle = $kb
                    }
                })
            } catch {}
        } catch {}
        [pscustomobject]@{
            DeviceName = $env:COMPUTERNAME
            SerialNumber = $bios.SerialNumber
            SystemManufacturer = [string]$computer.Manufacturer
            SystemSku = [string]$computer.SystemSKUNumber
            SystemModel = [string]$computer.Model
            OsName = $os.Caption
            OsVersion = [string]$os.Version
            OsDisplayVersion = $displayVersion
            OsBuild = [string]$os.BuildNumber
            Cpu = [pscustomobject]@{
                Name = $cpu.Name
                Cores = [int]$cpu.NumberOfCores
                LogicalProcessors = [int]$cpu.NumberOfLogicalProcessors
            }
            MemoryBytes = [uint64]$computer.TotalPhysicalMemory
            Domain = $computer.Domain
            DomainJoined = [bool]$computer.PartOfDomain
            JoinStatus = $join
            Disks = $disks
            Encryption = $encryption
            BitLockerAvailable = $bitLockerAvailable
            LoginProviders = $loginProviders
            Antivirus = $antivirus
            Updates = [pscustomobject]@{
                Installed = $installedUpdates
                Available = $availableUpdates
            }
        } | ConvertTo-Json -Depth 8 -Compress
        """;
}
