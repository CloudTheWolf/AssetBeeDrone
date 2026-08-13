using AssetBeeDrone.Collectors.Linux;
using AssetBeeDrone.Collectors.MacOS;
using AssetBeeDrone.Collectors.Windows;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Tests;

public sealed class CollectorTests
{
    private static IOptions<DroneOptions> DefaultOptions { get; } =
        Options.Create(new DroneOptions());

    [Fact]
    public async Task Host_platform_collection_smoke_test()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ProcessRunner runner = new(NullLogger<ProcessRunner>.Instance);
        DeviceInventory inventory =
            await new LinuxInventoryCollector(runner, TimeProvider.System, DefaultOptions)
                .CollectAsync(CancellationToken.None);

        Assert.Equal("linux", inventory.Platform);
        Assert.Equal(Environment.MachineName, inventory.DeviceName.Value);
        Assert.Equal(ProbeStatus.Available, inventory.OperatingSystem.Status);
        Assert.False(string.IsNullOrWhiteSpace(inventory.OperatingSystem.Value?.Name));
        Assert.False(string.IsNullOrWhiteSpace(inventory.OperatingSystem.Value?.Version));
        Assert.False(string.IsNullOrWhiteSpace(inventory.OperatingSystem.Value?.Kernel));
        Assert.Equal(ProbeStatus.Available, inventory.Updates.Status);
        Assert.NotNull(inventory.Cpu.Value);
        Assert.NotNull(inventory.Memory.Value);
        Assert.NotEqual(ProbeStatus.Error, inventory.Sbom.Status);
    }

    [Fact]
    public async Task Windows_collector_parses_sensitive_and_security_fields()
    {
        string fixture = await ReadFixtureAsync("windows-inventory.json");
        FakeProcessRunner runner = new((file, arguments) =>
        {
            if (file != "powershell.exe")
            {
                return Missing(file);
            }

            string command = string.Join(' ', arguments);
            if (command.Contains("Uninstall", StringComparison.Ordinal) ||
                command.Contains("Get-AppxPackage", StringComparison.Ordinal))
            {
                return new ProcessResult(0,
                    """[{"Name":"Example.Package","Version":"1.2.3","Publisher":"Programs"}]""",
                    string.Empty);
            }

            return new ProcessResult(0, fixture, string.Empty);
        });

        DeviceInventory inventory =
            await new WindowsInventoryCollector(runner, TimeProvider.System, DefaultOptions)
                .CollectAsync(CancellationToken.None);

        Assert.Equal("ABC123", inventory.SerialNumber.Value);
        Assert.Equal("dellInc", inventory.Manufacturer.Value);
        Assert.Equal("latitude5520", inventory.Model.Value);
        Assert.Equal("Microsoft Windows 11 Pro", inventory.OperatingSystem.Value?.Name);
        Assert.Equal("10.0.26100", inventory.OperatingSystem.Value?.Version);
        Assert.Equal("24H2", inventory.OperatingSystem.Value?.DisplayVersion);
        Assert.Equal("26100", inventory.OperatingSystem.Value?.Build);
        Assert.Equal(8, inventory.Cpu.Value?.LogicalProcessors);
        Assert.Equal("Example Workspace", inventory.DomainWorkspace.Value?.Workspace);
        Assert.Contains(inventory.LoginProviders.Value!,
            provider => provider.Name == "Windows Password" && provider.State == "enabled");
        Assert.Contains(inventory.LoginProviders.Value!,
            provider => provider.Name.Contains("Google Credential Provider") &&
                        provider.Detail == "{F8A1793B-7873-4046-B2A7-1F318747F427}");
        Assert.Equal(3, inventory.LoginProviders.Value!.Count);
        Assert.Equal(
            "111111-222222-333333-444444-555555-666666-777777-888888",
            inventory.DiskEncryption.Value![0].RecoveryKeys![0]);
        Assert.Equal(
            "{11111111-2222-3333-4444-555555555555}",
            inventory.DiskEncryption.Value[0].KeyProtectors![0].KeyProtectorId);
        Assert.Equal(
            inventory.DiskEncryption.Value[0].RecoveryKeys![0],
            inventory.DiskEncryption.Value[0].KeyProtectors![0].RecoveryKey);
        Assert.Equal("KB5034441", inventory.Updates.Value!.Installed[0].Id);
        Assert.Equal("update-1", inventory.Updates.Value.Available[0].Id);
        Assert.Equal(ProbeStatus.Available, inventory.Sbom.Status);
        Assert.Equal("CycloneDX", inventory.Sbom.Value!.Format);
        Assert.Contains(inventory.Sbom.Value.Targets.Single(target => target.Kind == "host").Components,
            component => component.Name == "Example.Package" && component.Version == "1.2.3");
    }

    [Fact]
    public async Task Linux_collector_reports_luks_realm_security_service_and_sbom()
    {
        string lsblk = await ReadFixtureAsync("linux-lsblk.txt");
        string lsblkJson = await ReadFixtureAsync("linux-lsblk.json");
        FakeProcessRunner runner = new((file, arguments) => (file, string.Join(' ', arguments)) switch
        {
            ("lsblk", string value) when value.Contains("-J", StringComparison.Ordinal) =>
                new ProcessResult(0, lsblkJson, string.Empty),
            ("lsblk", _) => new ProcessResult(0, lsblk, string.Empty),
            ("uname", _) => new ProcessResult(0, "6.8.0-test\n", string.Empty),
            ("apt", _) => new ProcessResult(0,
                "Listing...\nlinux-image-generic/noble-updates 6.8.0-50 amd64 [upgradable from: 6.8.0-49]\n",
                string.Empty),
            ("realm", _) => new ProcessResult(0,
                "example.test\n  domain-name: example.test\n", string.Empty),
            ("systemctl", string value) when value.Contains("clamav-daemon") =>
                new ProcessResult(0, "loaded\nactive\n", string.Empty),
            ("systemctl", _) => new ProcessResult(0, "not-found\ninactive\n", string.Empty),
            ("hostnamectl", _) => new ProcessResult(0,
                """{"HardwareVendor":"Dell Inc.","HardwareModel":"Latitude 5520","HardwareSerial":"LINUXSERIAL"}""",
                string.Empty),
            ("dpkg-query", _) => new ProcessResult(0, "bash\t5.2.21-2\nopenssl\t3.0.13-0\n", string.Empty),
            ("docker", string value) when value.StartsWith("ps ", StringComparison.Ordinal) =>
                new ProcessResult(0,
                    """{"ID":"abc123","Names":"web","Image":"nginx:1.25"}""" + "\n",
                    string.Empty),
            ("docker", string value) when value.Contains("exec abc123 dpkg-query", StringComparison.Ordinal) =>
                new ProcessResult(0, "nginx\t1.25.0-1\n", string.Empty),
            _ => Missing(file)
        });

        DeviceInventory inventory =
            await new LinuxInventoryCollector(runner, TimeProvider.System, DefaultOptions)
                .CollectAsync(CancellationToken.None);

        Assert.Equal("LINUXSERIAL", inventory.SerialNumber.Value);
        Assert.Equal("dellInc", inventory.Manufacturer.Value);
        Assert.Equal("latitude5520", inventory.Model.Value);
        Assert.Equal(ProbeStatus.Available, inventory.Disks.Status);
        Assert.Contains(inventory.Disks.Value!, disk => disk.Name == "nvme0n1p1" && disk.MountPoint == "/boot/efi");
        Assert.Contains(inventory.Disks.Value!, disk => disk.Name == "nvme0n1p2" && disk.MountPoint == "/");
        Assert.Contains(inventory.Disks.Value!, disk => disk.Name == "nvme0n1p3" && disk.FileSystem == "crypto_LUKS");
        Assert.Contains(inventory.Disks.Value!, disk => disk.Name == "dm-0" && disk.MountPoint == "/secure");
        Assert.DoesNotContain(inventory.Disks.Value!, disk => disk.Name.StartsWith("loop", StringComparison.Ordinal));
        Assert.DoesNotContain(inventory.Disks.Value!, disk => disk.FileSystem == "tmpfs");
        Assert.DoesNotContain(inventory.Disks.Value!, disk => disk.FileSystem == "squashfs");
        Assert.DoesNotContain(inventory.Disks.Value!, disk => disk.Name == "nvme0n1");
        Assert.Contains(inventory.DiskEncryption.Value!,
            volume => volume.Technology == "LUKS/dm-crypt");
        Assert.Equal("example.test", inventory.DomainWorkspace.Value?.Domain);
        AntivirusInfo antivirus = Assert.Single(inventory.Antivirus.Value!);
        Assert.Equal("ClamAV", antivirus.Name);
        Assert.True(antivirus.Enabled);
        Assert.Contains(inventory.Updates.Value!.Available,
            update => update.Id == "linux-image-generic");
        Assert.Equal(ProbeStatus.Available, inventory.Sbom.Status);
        Assert.Contains(inventory.Sbom.Value!.Targets,
            target => target.Kind == "host" &&
                      target.Components.Any(component => component.Name == "bash"));
        Assert.Contains(inventory.Sbom.Value.Targets,
            target => target.Kind == "container" &&
                      target.Name == "web" &&
                      target.Image == "nginx:1.25" &&
                      target.Components.Any(component => component.Name == "nginx"));
    }

    [Fact]
    public async Task Mac_collector_parses_hardware_filevault_workspace_and_xprotect()
    {
        string hardware = await ReadFixtureAsync("macos-hardware.json");
        FakeProcessRunner runner = new((file, arguments) => (file, string.Join(' ', arguments)) switch
        {
            ("system_profiler", _) => new ProcessResult(0, hardware, string.Empty),
            ("sw_vers", "-productName") => new ProcessResult(0, "macOS\n", string.Empty),
            ("sw_vers", "-productVersion") => new ProcessResult(0, "15.1\n", string.Empty),
            ("sw_vers", "-buildVersion") => new ProcessResult(0, "24B83\n", string.Empty),
            ("uname", _) => new ProcessResult(0, "24.1.0\n", string.Empty),
            ("softwareupdate", "-l") => new ProcessResult(0,
                "* Label: macOS Sequoia 15.2-24C101\n\tTitle: macOS Sequoia 15.2\n", string.Empty),
            ("softwareupdate", "--history") => new ProcessResult(0,
                "Display Name                   Version    Date\nmacOS Sequoia 15.1             15.1       01/10/2026\n",
                string.Empty),
            ("sysctl", _) => new ProcessResult(0, "17179869184\n", string.Empty),
            ("fdesetup", _) => new ProcessResult(0, "FileVault is On.\n", string.Empty),
            ("dsconfigad", _) => new ProcessResult(0,
                "Active Directory Domain: example.test\n", string.Empty),
            ("profiles", _) => new ProcessResult(0,
                "Enrolled via DEP: Yes\nMDM enrollment: Yes\n", string.Empty),
            ("spctl", _) => new ProcessResult(0, "assessments enabled\n", string.Empty),
            ("pkgutil", "--pkgs") => new ProcessResult(0, "com.example.app\n", string.Empty),
            ("pkgutil", string value) when value.StartsWith("--pkg-info ", StringComparison.Ordinal) =>
                new ProcessResult(0, "version: 2.1.0\n", string.Empty),
            ("pkgutil", _) => new ProcessResult(0, "version: 2169\n", string.Empty),
            _ => Missing(file)
        });

        DeviceInventory inventory =
            await new MacOsInventoryCollector(runner, TimeProvider.System, DefaultOptions)
                .CollectAsync(CancellationToken.None);

        Assert.Equal("MAC123", inventory.SerialNumber.Value);
        Assert.Equal("macOS", inventory.OperatingSystem.Value?.Name);
        Assert.Equal("15.1", inventory.OperatingSystem.Value?.Version);
        Assert.Equal("24B83", inventory.OperatingSystem.Value?.Build);
        Assert.Equal("Apple M Test", inventory.Cpu.Value?.Model);
        Assert.Equal("encrypted", inventory.DiskEncryption.Value![0].State);
        Assert.True(inventory.DomainWorkspace.Value?.WorkspaceJoined);
        Assert.Contains(inventory.Antivirus.Value!, item => item.Name == "XProtect");
        Assert.Contains(inventory.Updates.Value!.Available,
            update => update.Id.Contains("15.2"));
        Assert.Contains(inventory.Updates.Value.Installed,
            update => update.Title.Contains("15.1"));
        Assert.Equal(ProbeStatus.Available, inventory.Sbom.Status);
        Assert.Contains(inventory.Sbom.Value!.Targets.Single().Components,
            component => component.Name == "com.example.app" && component.Version == "2.1.0");
    }

    private static async Task<string> ReadFixtureAsync(string name) =>
        await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", name),
            CancellationToken.None);

    private static ProcessResult Missing(string file) =>
        new(-1, string.Empty, $"{file} unavailable");

    private sealed class FakeProcessRunner(
        Func<string, IReadOnlyList<string>, ProcessResult> handler) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(fileName, arguments.ToArray()));
    }
}
