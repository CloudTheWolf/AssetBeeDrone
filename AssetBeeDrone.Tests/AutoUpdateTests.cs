using System.Net;
using System.Text;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Updating;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Tests;

public sealed class AutoUpdateTests
{
    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3+git", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("not-a-version", false)]
    [InlineData("", false)]
    public void AppVersion_TryParse_handles_informational_versions(string input, bool expected)
    {
        bool parsed = AppVersion.TryParse(input, out Version version);
        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.Equal(new Version(1, 2, 3), version);
        }
    }

    [Fact]
    public void AppVersion_IsNewer_compares_against_local()
    {
        Version local = new(1, 0, 0);
        Assert.True(AppVersion.IsNewer("1.0.1", local));
        Assert.False(AppVersion.IsNewer("1.0.0", local));
        Assert.False(AppVersion.IsNewer("0.9.9", local));
    }

    [Theory]
    [InlineData(
        "https://example.test/drone/latest.json",
        "https://example.test/drone/latest.json")]
    [InlineData(
        "https://example.test/drone/",
        "https://example.test/drone/latest.json")]
    [InlineData(
        "https://example.test/drone",
        "https://example.test/drone/latest.json")]
    public void ResolveManifestUri_appends_latest_json_when_needed(string input, string expected)
    {
        Assert.Equal(new Uri(expected), UpdateFeedClient.ResolveManifestUri(input));
    }

    [Fact]
    public void UpdatePackageSelector_prefers_msi_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        UpdateManifest manifest = new(
            "2.0.0",
            [
                new UpdatePackage("win-x64", "AssetBee.Drone-2.0.0-win-x64.zip", "aa"),
                new UpdatePackage("win-x64", "AssetBee.Drone-2.0.0-win-x64.msi", "bb")
            ]);

        UpdatePackage? selected = UpdatePackageSelector.Select(manifest, "win-x64");
        Assert.NotNull(selected);
        Assert.Equal("AssetBee.Drone-2.0.0-win-x64.msi", selected.FileName);
    }

    [Fact]
    public void UpdatePackageSelector_prefers_pkg_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        UpdateManifest manifest = new(
            "2.0.0",
            [
                new UpdatePackage("osx-arm64", "AssetBee.Drone-2.0.0-osx-arm64.tar.gz", "aa"),
                new UpdatePackage("osx-arm64", "AssetBee.Drone-2.0.0-osx-arm64.pkg", "bb")
            ]);

        UpdatePackage? selected = UpdatePackageSelector.Select(manifest, "osx-arm64");
        Assert.NotNull(selected);
        Assert.Equal("AssetBee.Drone-2.0.0-osx-arm64.pkg", selected.FileName);
    }

    [Fact]
    public async Task UpdateFeedClient_fetches_and_deserializes_manifest()
    {
        const string json = """
            {
              "version": "1.2.3",
              "packages": [
                {
                  "rid": "linux-x64",
                  "fileName": "AssetBee.Drone-1.2.3-linux-x64.deb",
                  "sha256": "abc123",
                  "url": "https://example.test/AssetBee.Drone-1.2.3-linux-x64.deb"
                }
              ]
            }
            """;

        using HttpClient client = new(new StaticJsonHandler(json));
        UpdateFeedClient feed = new(
            client,
            NullLogger<UpdateFeedClient>.Instance,
            "https://example.test/drone/latest.json");

        UpdateManifest? manifest = await feed.FetchManifestAsync(CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Single(manifest.Packages);
        Assert.Equal("linux-x64", manifest.Packages[0].Rid);
        Assert.Equal("abc123", manifest.Packages[0].Sha256);
    }

    [Fact]
    public void ResolvePackageUri_uses_explicit_package_url()
    {
        UpdateManifest manifest = new("1.0.0", []);
        UpdatePackage package = new(
            "win-x64",
            "AssetBee.Drone-1.0.0-win-x64.msi",
            "deadbeef",
            "https://cdn.example.test/AssetBee.Drone-1.0.0-win-x64.msi");

        Uri uri = UpdateApplier.ResolvePackageUri(manifest, package);
        Assert.Equal(
            new Uri("https://cdn.example.test/AssetBee.Drone-1.0.0-win-x64.msi"),
            uri);
    }

    [Fact]
    public void Configuration_rejects_invalid_auto_update_interval()
    {
        DroneOptionsValidator validator = new();
        ValidateOptionsResult result = validator.Validate(null, new DroneOptions
        {
            Endpoint = new Uri("https://inventory.example.test/v1"),
            AutoUpdateIntervalHours = 0
        });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("AutoUpdateIntervalHours", StringComparison.Ordinal));
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
