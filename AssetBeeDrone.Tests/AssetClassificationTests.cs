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
