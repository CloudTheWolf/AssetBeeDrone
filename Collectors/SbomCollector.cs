using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using Microsoft.Win32;

namespace AssetBeeDrone.Collectors;

public static class SbomCollector
{
    public const string Format = "CycloneDX";
    public const string SpecVersion = "1.6";

    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ContainerTimeout = TimeSpan.FromSeconds(45);

    public static async Task<ProbeValue<SbomInventory>> CollectLinuxAsync(
        IProcessRunner processRunner,
        TimeProvider timeProvider,
        bool includeHost,
        bool includeContainers,
        CancellationToken cancellationToken)
    {
        if (!includeHost && !includeContainers)
        {
            return ProbeValue<SbomInventory>.Unavailable("SBOM collection is disabled.");
        }

        List<SbomTarget> targets = [];
        if (includeHost)
        {
            SbomTarget? host = await CollectLinuxHostAsync(processRunner, cancellationToken);
            if (host is not null)
            {
                targets.Add(host);
            }
        }

        if (includeContainers)
        {
            targets.AddRange(await CollectDockerContainersAsync(processRunner, cancellationToken));
        }

        return targets.Count == 0
            ? ProbeValue<SbomInventory>.Unavailable(
                "No host packages or Docker containers were available for SBOM generation.")
            : ProbeValue<SbomInventory>.Available(new SbomInventory(
                Format,
                SpecVersion,
                timeProvider.GetUtcNow(),
                targets));
    }

    public static async Task<ProbeValue<SbomInventory>> CollectWindowsAsync(
        IProcessRunner processRunner,
        TimeProvider timeProvider,
        bool includeHost,
        CancellationToken cancellationToken)
    {
        if (!includeHost)
        {
            return ProbeValue<SbomInventory>.Unavailable("SBOM collection is disabled.");
        }

        List<SbomComponent> components = [];
        if (OperatingSystem.IsWindows())
        {
            components = CollectWindowsPackagesFromRegistry();
        }

        // Fallback for tests on non-Windows hosts, or if registry yielded nothing.
        if (components.Count == 0)
        {
            components = await CollectWindowsPackagesViaPowerShellAsync(
                processRunner, cancellationToken);
        }

        if (components.Count == 0 && OperatingSystem.IsWindows())
        {
            components = await CollectWindowsAppxViaPowerShellAsync(
                processRunner, cancellationToken);
        }

        return ProbeValue<SbomInventory>.Available(new SbomInventory(
            Format,
            SpecVersion,
            timeProvider.GetUtcNow(),
            [
                new SbomTarget(
                    "host",
                    "host",
                    Environment.MachineName,
                    components,
                    Detail: components.Count == 0
                        ? "No installed packages were returned from the Uninstall registry or AppX."
                        : null)
            ]));
    }

    [SupportedOSPlatform("windows")]
    private static List<SbomComponent> CollectWindowsPackagesFromRegistry()
    {
        List<SbomComponent> components = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        CollectUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry64, seen, components);
        CollectUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry32, seen, components);
        CollectUninstallHive(RegistryHive.CurrentUser, RegistryView.Default, seen, components);

        components.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return components;
    }

    [SupportedOSPlatform("windows")]
    private static void CollectUninstallHive(
        RegistryHive hive,
        RegistryView view,
        HashSet<string> seen,
        List<SbomComponent> components)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? uninstall =
                baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
            {
                return;
            }

            foreach (string subKeyName in uninstall.GetSubKeyNames())
            {
                using RegistryKey? subKey = uninstall.OpenSubKey(subKeyName);
                if (subKey is null)
                {
                    continue;
                }

                if (subKey.GetValue("DisplayName") is not string name ||
                    string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string? version = subKey.GetValue("DisplayVersion") as string;
                string? publisher = subKey.GetValue("Publisher") as string;
                string dedupeKey = $"{name}|{version}";
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                components.Add(new SbomComponent(
                    name.Trim(),
                    string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
                    "application",
                    Publisher: string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
                    Purl: BuildPurl("generic", name.Trim(), version)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException
                                       or IOException or ObjectDisposedException)
        {
            // Other hives may still succeed.
        }
    }

    private static async Task<List<SbomComponent>> CollectWindowsPackagesViaPowerShellAsync(
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        // Keep the script simple: ConvertTo-Json -InputObject avoids empty-pipeline blanks.
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $items = New-Object System.Collections.Generic.List[object]
            foreach ($path in @(
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
            )) {
                foreach ($pkg in Get-ItemProperty -Path $path) {
                    if ([string]::IsNullOrWhiteSpace($pkg.DisplayName)) { continue }
                    $items.Add([pscustomobject]@{
                        Name = [string]$pkg.DisplayName
                        Version = [string]$pkg.DisplayVersion
                        Publisher = [string]$pkg.Publisher
                    })
                }
            }
            ConvertTo-Json -InputObject @($items) -Depth 4 -Compress
            """;

        ProcessResult result = await processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-Command", script],
            HostTimeout,
            cancellationToken);

        return result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? ParseWindowsPackages(result.StandardOutput)
            : [];
    }

    private static async Task<List<SbomComponent>> CollectWindowsAppxViaPowerShellAsync(
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $items = @(Get-AppxPackage -AllUsers | ForEach-Object {
                [pscustomobject]@{
                    Name = [string]$_.Name
                    Version = [string]$_.Version
                    Publisher = [string]$_.Publisher
                }
            })
            if ($items.Count -eq 0) {
                $items = @(Get-AppxPackage | ForEach-Object {
                    [pscustomobject]@{
                        Name = [string]$_.Name
                        Version = [string]$_.Version
                        Publisher = [string]$_.Publisher
                    }
                })
            }
            ConvertTo-Json -InputObject @($items) -Depth 4 -Compress
            """;

        ProcessResult result = await processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-Command", script],
            HostTimeout,
            cancellationToken);

        return result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? ParseWindowsPackages(result.StandardOutput)
            : [];
    }

    public static async Task<ProbeValue<SbomInventory>> CollectMacOsAsync(
        IProcessRunner processRunner,
        TimeProvider timeProvider,
        bool includeHost,
        CancellationToken cancellationToken)
    {
        if (!includeHost)
        {
            return ProbeValue<SbomInventory>.Unavailable("SBOM collection is disabled.");
        }

        ProcessResult result = await processRunner.RunAsync(
            "pkgutil", ["--pkgs"], HostTimeout, cancellationToken);
        if (!result.Succeeded)
        {
            return ProbeValue<SbomInventory>.Unavailable("pkgutil is unavailable for SBOM generation.");
        }

        List<SbomComponent> components = [];
        foreach (string packageId in result.StandardOutput.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ProcessResult info = await processRunner.RunAsync(
                "pkgutil", ["--pkg-info", packageId], TimeSpan.FromSeconds(5), cancellationToken);
            string? version = null;
            if (info.Succeeded)
            {
                version = info.StandardOutput.Split('\n')
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                    ?["version:".Length..]
                    .Trim();
            }

            components.Add(new SbomComponent(
                packageId,
                version,
                "application",
                Purl: $"pkg:macports/{Uri.EscapeDataString(packageId)}" +
                      (string.IsNullOrWhiteSpace(version) ? string.Empty : $"@{version}")));
        }

        return ProbeValue<SbomInventory>.Available(new SbomInventory(
            Format,
            SpecVersion,
            timeProvider.GetUtcNow(),
            [new SbomTarget("host", "host", Environment.MachineName, components)]));
    }

    private static async Task<SbomTarget?> CollectLinuxHostAsync(
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        ProcessResult dpkg = await processRunner.RunAsync(
            "dpkg-query",
            ["-W", "-f=${Package}\t${Version}\n"],
            HostTimeout,
            cancellationToken);
        if (dpkg.Succeeded || !string.IsNullOrWhiteSpace(dpkg.StandardOutput))
        {
            List<SbomComponent> components = ParseTabularPackages(
                dpkg.StandardOutput, "deb", "library");
            return new SbomTarget("host", "host", Environment.MachineName, components);
        }

        ProcessResult rpm = await processRunner.RunAsync(
            "rpm",
            ["-qa", "--queryformat", "%{NAME}\t%{VERSION}-%{RELEASE}\n"],
            HostTimeout,
            cancellationToken);
        if (rpm.Succeeded || !string.IsNullOrWhiteSpace(rpm.StandardOutput))
        {
            List<SbomComponent> components = ParseTabularPackages(
                rpm.StandardOutput, "rpm", "library");
            return new SbomTarget("host", "host", Environment.MachineName, components);
        }

        return null;
    }

    private static async Task<IReadOnlyList<SbomTarget>> CollectDockerContainersAsync(
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        ProcessResult docker = await processRunner.RunAsync(
            "docker",
            ["ps", "--format", "{{json .}}"],
            ContainerTimeout,
            cancellationToken);
        if (!docker.Succeeded || string.IsNullOrWhiteSpace(docker.StandardOutput))
        {
            return [];
        }

        List<SbomTarget> targets = [];
        foreach (string line in docker.StandardOutput.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string? id = GetJsonString(root, "ID") ?? GetJsonString(root, "Id");
                string? name = GetJsonString(root, "Names") ?? GetJsonString(root, "Name") ?? id;
                string? image = GetJsonString(root, "Image");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                List<SbomComponent> components =
                    await CollectContainerPackagesAsync(processRunner, id, cancellationToken);
                targets.Add(new SbomTarget(
                    $"container:{id}",
                    "container",
                    name ?? id,
                    components,
                    Image: image,
                    ContainerId: id,
                    Detail: components.Count == 0
                        ? "Container package inventory was unavailable from the package manager."
                        : null));
            }
            catch (JsonException)
            {
                // Skip malformed docker ps lines.
            }
        }

        return targets;
    }

    private static async Task<List<SbomComponent>> CollectContainerPackagesAsync(
        IProcessRunner processRunner,
        string containerId,
        CancellationToken cancellationToken)
    {
        ProcessResult dpkg = await processRunner.RunAsync(
            "docker",
            ["exec", containerId, "dpkg-query", "-W", "-f=${Package}\t${Version}\n"],
            ContainerTimeout,
            cancellationToken);
        if (dpkg.Succeeded || dpkg.StandardOutput.Contains('\t'))
        {
            return ParseTabularPackages(dpkg.StandardOutput, "deb", "library");
        }

        ProcessResult rpm = await processRunner.RunAsync(
            "docker",
            [
                "exec", containerId, "rpm", "-qa", "--queryformat",
                "%{NAME}\t%{VERSION}-%{RELEASE}\n"
            ],
            ContainerTimeout,
            cancellationToken);
        if (rpm.Succeeded || rpm.StandardOutput.Contains('\t'))
        {
            return ParseTabularPackages(rpm.StandardOutput, "rpm", "library");
        }

        ProcessResult apk = await processRunner.RunAsync(
            "docker",
            ["exec", containerId, "apk", "info", "-v"],
            ContainerTimeout,
            cancellationToken);
        if (apk.Succeeded || !string.IsNullOrWhiteSpace(apk.StandardOutput))
        {
            return ParseApkPackages(apk.StandardOutput);
        }

        return [];
    }

    private static List<SbomComponent> ParseTabularPackages(
        string output,
        string purlType,
        string componentType)
    {
        List<SbomComponent> components = [];
        foreach (string line in output.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split('\t', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            string name = parts[0];
            string? version = parts.Length > 1 ? parts[1] : null;
            components.Add(new SbomComponent(
                name,
                version,
                componentType,
                Purl: BuildPurl(purlType, name, version)));
        }

        return components;
    }

    private static List<SbomComponent> ParseApkPackages(string output)
    {
        List<SbomComponent> components = [];
        foreach (string line in output.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.LastIndexOf('-');
            if (separator <= 0)
            {
                components.Add(new SbomComponent(line, Type: "library", Purl: BuildPurl("apk", line, null)));
                continue;
            }

            // apk info -v returns name-version-release; keep last two hyphen segments as version when possible.
            string[] tokens = line.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                components.Add(new SbomComponent(line, Type: "library", Purl: BuildPurl("apk", line, null)));
                continue;
            }

            string version = string.Join('-', tokens[^2..]);
            string name = string.Join('-', tokens[..^2]);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = line;
                version = null!;
            }

            components.Add(new SbomComponent(
                name,
                version,
                "library",
                Purl: BuildPurl("apk", name, version)));
        }

        return components;
    }

    private static List<SbomComponent> ParseWindowsPackages(string json)
    {
        List<SbomComponent> components = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            IEnumerable<JsonElement> items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                : [document.RootElement];
            foreach (JsonElement item in items)
            {
                string? name = GetJsonString(item, "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string? version = GetJsonString(item, "Version");
                components.Add(new SbomComponent(
                    name,
                    version,
                    "application",
                    Publisher: GetJsonString(item, "Publisher"),
                    Purl: BuildPurl("generic", name, version)));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return components;
    }

    private static string BuildPurl(string type, string name, string? version)
    {
        string encoded = Uri.EscapeDataString(name);
        return string.IsNullOrWhiteSpace(version)
            ? $"pkg:{type}/{encoded}"
            : $"pkg:{type}/{encoded}@{Uri.EscapeDataString(version)}";
    }

    private static string? GetJsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind is not JsonValueKind.Null
            ? value.ToString()
            : null;
}
