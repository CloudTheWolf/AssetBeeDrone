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
    public async Task Linux_collector_reports_expanded_antivirus_units()
    {
        string lsblk = await ReadFixtureAsync("linux-lsblk.txt");
        string lsblkJson = await ReadFixtureAsync("linux-lsblk.json");
        FakeProcessRunner runner = new((file, arguments) => (file, string.Join(' ', arguments)) switch
        {
            ("lsblk", string value) when value.Contains("-J", StringComparison.Ordinal) =>
                new ProcessResult(0, lsblkJson, string.Empty),
            ("lsblk", _) => new ProcessResult(0, lsblk, string.Empty),
            ("uname", _) => new ProcessResult(0, "6.8.0-test\n", string.Empty),
            ("apt", _) => new ProcessResult(0, "Listing...\n", string.Empty),
            ("realm", _) => new ProcessResult(1, string.Empty, "not found"),
            ("hostname", _) => new ProcessResult(0, string.Empty, string.Empty),
            ("systemctl", string value) when value.Contains("falcon-sensor") =>
                new ProcessResult(0, "loaded\nactive\n", string.Empty),
            ("systemctl", string value) when value.Contains("cbagentd") =>
                new ProcessResult(0, "loaded\ninactive\n", string.Empty),
            ("systemctl", _) => new ProcessResult(0, "not-found\ninactive\n", string.Empty),
            ("hostnamectl", _) => new ProcessResult(0,
                """{"HardwareVendor":"Dell Inc.","HardwareModel":"Latitude 5520","HardwareSerial":"LINUXSERIAL"}""",
                string.Empty),
            ("dpkg-query", _) => new ProcessResult(0, "bash\t5.2.21-2\n", string.Empty),
            ("docker", _) => new ProcessResult(1, string.Empty, "unavailable"),
            _ => Missing(file)
        });

        DeviceInventory inventory =
            await new LinuxInventoryCollector(runner, TimeProvider.System, DefaultOptions)
                .CollectAsync(CancellationToken.None);

        Assert.Contains(inventory.Antivirus.Value!,
            item => item.Name == "CrowdStrike Falcon" && item.Enabled == true);
        Assert.Contains(inventory.Antivirus.Value!,
            item => item.Name == "Carbon Black" && item.Enabled == false &&
                    item.Detail == "cbagentd.service");
        Assert.Equal(2, inventory.Antivirus.Value!.Count);
    }

    [Fact]
    public async Task Linux_collector_falls_back_to_aws_ebs_encryption_when_no_luks()
    {
        string lsblk = await ReadFixtureAsync("linux-lsblk-no-luks.txt");
        string lsblkJson = await ReadFixtureAsync("linux-lsblk-no-luks.json");
        string describeVolumes = await ReadFixtureAsync("aws-describe-volumes.xml");
        FakeHttpMessageHandler http = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path.EndsWith("/latest/api/token", StringComparison.Ordinal))
            {
                return TextResponse("imds-token");
            }

            if (path.Contains("instance-identity/document", StringComparison.Ordinal))
            {
                return TextResponse(
                    """{"region":"us-east-1","instanceId":"i-1234567890abcdef0"}""");
            }

            if (path.EndsWith("/latest/meta-data/iam/security-credentials/", StringComparison.Ordinal))
            {
                return TextResponse("TestRole\n");
            }

            if (path.Contains("/security-credentials/TestRole", StringComparison.Ordinal))
            {
                return TextResponse(
                    """{"AccessKeyId":"AKIATEST","SecretAccessKey":"secret","Token":"session"}""");
            }

            if (request.RequestUri.Host.StartsWith("ec2.", StringComparison.Ordinal) &&
                request.RequestUri.Query.Contains("DescribeVolumes", StringComparison.Ordinal))
            {
                return TextResponse(describeVolumes, "application/xml");
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        using HttpClient client = new(http) { Timeout = TimeSpan.FromSeconds(2) };
        CloudDiskEncryptionProbe probe = new(client, TimeProvider.System);
        FakeProcessRunner runner = LinuxRunnerWithoutLuks(lsblk, lsblkJson);

        DeviceInventory inventory =
            await new LinuxInventoryCollector(runner, TimeProvider.System, DefaultOptions, probe)
                .CollectAsync(CancellationToken.None);

        Assert.Equal(ProbeStatus.Available, inventory.DiskEncryption.Status);
        Assert.Equal(2, inventory.DiskEncryption.Value!.Count);
        Assert.Contains(inventory.DiskEncryption.Value!,
            volume => volume is { Technology: "AWS EBS", State: "encrypted", Volume: "/dev/xvda" });
        Assert.Contains(inventory.DiskEncryption.Value!,
            volume => volume is { Technology: "AWS EBS", State: "not encrypted", Volume: "/dev/xvdb" });
        Assert.DoesNotContain(inventory.DiskEncryption.Value!,
            volume => volume.Technology == "LUKS/dm-crypt");
    }

    [Fact]
    public async Task Cloud_probe_reports_azure_and_gcp_disk_encryption()
    {
        string azure = await ReadFixtureAsync("azure-storage-profile.json");
        string gcp = await ReadFixtureAsync("gcp-disks.json");

        FakeHttpMessageHandler azureHandler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("storageProfile", StringComparison.Ordinal))
            {
                return TextResponse(azure);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        using HttpClient azureClient = new(azureHandler);
        IReadOnlyList<EncryptionVolume>? azureVolumes =
            await new CloudDiskEncryptionProbe(azureClient, TimeProvider.System)
                .TryCollectAsync(CancellationToken.None);
        Assert.NotNull(azureVolumes);
        Assert.Contains(azureVolumes,
            volume => volume is { Technology: "Azure Disk (SSE)", State: "encrypted", Volume: "osdisk-linux" });
        Assert.Contains(azureVolumes,
            volume => volume is { Technology: "Azure Disk (ADE)", State: "encrypted", Volume: "datadisk-1" });

        FakeHttpMessageHandler gcpHandler = new((request, _) =>
        {
            if (request.Headers.Contains("Metadata-Flavor") &&
                request.RequestUri!.AbsolutePath.Contains("/instance/disks/", StringComparison.Ordinal))
            {
                return TextResponse(gcp);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        using HttpClient gcpClient = new(gcpHandler);
        IReadOnlyList<EncryptionVolume>? gcpVolumes =
            await new CloudDiskEncryptionProbe(gcpClient, TimeProvider.System)
                .TryCollectAsync(CancellationToken.None);
        Assert.NotNull(gcpVolumes);
        Assert.Contains(gcpVolumes, volume => volume.Volume == "boot" && volume.Technology == "GCP Persistent Disk");
        Assert.Contains(gcpVolumes, volume => volume.Volume == "persistent-disk-1");
        Assert.DoesNotContain(gcpVolumes, volume => volume.Volume == "local-ssd-0");
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

    private static FakeProcessRunner LinuxRunnerWithoutLuks(string lsblk, string lsblkJson) =>
        new((file, arguments) => (file, string.Join(' ', arguments)) switch
        {
            ("lsblk", string value) when value.Contains("-J", StringComparison.Ordinal) =>
                new ProcessResult(0, lsblkJson, string.Empty),
            ("lsblk", _) => new ProcessResult(0, lsblk, string.Empty),
            ("uname", _) => new ProcessResult(0, "6.8.0-test\n", string.Empty),
            ("apt", _) => new ProcessResult(0, "Listing...\n", string.Empty),
            ("realm", _) => new ProcessResult(1, string.Empty, "not found"),
            ("hostname", _) => new ProcessResult(0, string.Empty, string.Empty),
            ("systemctl", _) => new ProcessResult(0, "not-found\ninactive\n", string.Empty),
            ("hostnamectl", _) => new ProcessResult(0,
                """{"HardwareVendor":"Amazon EC2","HardwareModel":"t3.micro","HardwareSerial":"ec2-serial"}""",
                string.Empty),
            ("dpkg-query", _) => new ProcessResult(0, "bash\t5.2.21-2\n", string.Empty),
            ("docker", _) => new ProcessResult(1, string.Empty, "unavailable"),
            _ => Missing(file)
        });

    private static HttpResponseMessage TextResponse(string content, string mediaType = "application/json") =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, mediaType)
        };

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

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request, cancellationToken));
    }
}
