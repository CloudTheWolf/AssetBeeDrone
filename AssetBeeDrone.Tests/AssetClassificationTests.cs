using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using AssetBeeDrone.Services;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Tests;

public sealed class AssetClassificationTests
{
    [Fact]
    public async Task Configured_virtualware_type_overrides_detection()
    {
        AssetClassificationService service = new(
            new FakeProcessRunner((_, _) => throw new InvalidOperationException(
                "No platform probe should run for an explicit virtualware type.")),
            Options.Create(new DroneOptions { Type = "virtualware" }));

        AssetClassification classification =
            await service.ClassifyAsync(CancellationToken.None);

        Assert.Equal("virtualware", classification.Type);
        Assert.Equal(ProbeStatus.Unsupported, classification.HardwareType.Status);
    }

    [Fact]
    public async Task Linux_virtualization_signal_selects_virtualware()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        AssetClassificationService service = new(
            new FakeProcessRunner((file, _) =>
                file == "systemd-detect-virt"
                    ? new ProcessResult(0, "kvm\n", string.Empty)
                    : new ProcessResult(-1, string.Empty, "unavailable")),
            Options.Create(new DroneOptions()));

        AssetClassification classification =
            await service.ClassifyAsync(CancellationToken.None);

        Assert.Equal("virtualware", classification.Type);
        Assert.Contains("kvm", classification.HardwareType.Detail);
    }

    [Theory]
    [InlineData(8, "laptop")]
    [InlineData(9, "laptop")]
    [InlineData(10, "laptop")]
    [InlineData(11, "laptop")]
    [InlineData(12, "laptop")]
    [InlineData(14, "laptop")]
    [InlineData(18, "laptop")]
    [InlineData(21, "laptop")]
    [InlineData(30, "laptop")]
    [InlineData(31, "laptop")]
    [InlineData(32, "laptop")]
    [InlineData(17, "server")]
    [InlineData(23, "server")]
    [InlineData(28, "server")]
    [InlineData(29, "server")]
    [InlineData(3, "desktop")]
    [InlineData(13, "desktop")]
    [InlineData(1, "desktop")]
    public void Smbios_chassis_types_map_to_hardware_form_factor(int chassisType, string expected) =>
        Assert.Equal(expected, AssetClassificationService.MapSmbiosChassisType(chassisType));

    [Theory]
    [InlineData(1, "desktop")]
    [InlineData(2, "laptop")]
    [InlineData(3, "desktop")]
    [InlineData(4, "server")]
    [InlineData(5, "server")]
    [InlineData(6, "desktop")]
    [InlineData(7, "server")]
    [InlineData(0, null)]
    [InlineData(8, null)]
    public void Windows_pc_system_types_map_to_hardware_form_factor(int systemType, string? expected) =>
        Assert.Equal(expected, AssetClassificationService.MapWindowsPcSystemType(systemType));

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
