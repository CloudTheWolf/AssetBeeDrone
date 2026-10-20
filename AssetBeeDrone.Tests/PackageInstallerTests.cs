using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Reporting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBeeDrone.Tests;

public sealed class PackageInstallerTests
{
    [Fact]
    public void BuildArgumentList_winget_includes_silent_flags()
    {
        WindowsPackageInstaller installer = CreateInstaller();
        IReadOnlyList<string> args = installer.BuildArgumentList(new DroneCommandPackage(
            1, "GCPW", "winget", null, null, "Google.GCPW", null, null, null));

        Assert.Contains("--silent", args);
        Assert.Contains("Google.GCPW", args);
        Assert.Equal("install", args[0]);
    }

    [Fact]
    public void BuildArgumentList_choco_includes_yes()
    {
        WindowsPackageInstaller installer = CreateInstaller();
        IReadOnlyList<string> args = installer.BuildArgumentList(new DroneCommandPackage(
            1, "WatchGuard", "choco", null, null, null, "watchguard", null, null));

        Assert.Equal(["install", "watchguard", "-y"], args.ToArray());
    }

    [Fact]
    public void BuildArgumentList_msi_uses_quiet_flags()
    {
        WindowsPackageInstaller installer = CreateInstaller();
        IReadOnlyList<string> args = installer.BuildArgumentList(
            new DroneCommandPackage(
                1, "Agent", "msi", "https://example.com/a.msi", "ALLUSERS=1", null, null, null, null),
            @"C:\Temp\a.msi");

        Assert.Equal("/i", args[0]);
        Assert.Equal(@"C:\Temp\a.msi", args[1]);
        Assert.Contains("/qn", args);
        Assert.Contains("ALLUSERS=1", args);
    }

    private static WindowsPackageInstaller CreateInstaller() =>
        new(
            new ThrowingProcessRunner(),
            new NullHttpClientFactory(),
            NullLogger<WindowsPackageInstaller>.Instance);

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used.");
    }
}
