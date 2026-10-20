using System.Net;
using System.Text;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Tests;

public sealed class DroneSettingsClientTests
{
    [Fact]
    public void ResolveSettingsUri_replaces_inventory_segment()
    {
        Uri settings = HttpDroneSettingsClient.ResolveSettingsUri(
            new Uri("https://inventory.example.test/api/v1/inventory"));

        Assert.Equal(
            new Uri("https://inventory.example.test/api/v1/drone/settings"),
            settings);
    }

    [Theory]
    [InlineData("{\"enableDriveEncryption\":true}", true)]
    [InlineData("{\"enableDriveEncryption\":false}", false)]
    [InlineData("{\"enableDriveEncryption\":\"yes\"}", false)]
    [InlineData("{}", false)]
    [InlineData("not-json", false)]
    public async Task GetSettingsAsync_parses_or_fails_closed(string body, bool expected)
    {
        SettingsHandler handler = new(HttpStatusCode.OK, body);
        using HttpClient client = new(handler);
        HttpDroneSettingsClient settingsClient = new(
            client,
            Options.Create(new DroneOptions
            {
                Endpoint = new Uri("https://inventory.example.test/api/v1/inventory"),
                ApiKey = "secret"
            }),
            NullLogger<HttpDroneSettingsClient>.Instance);

        DroneRemoteSettings settings = await settingsClient.GetSettingsAsync(CancellationToken.None);

        Assert.Equal(expected, settings.EnableDriveEncryption);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(
            new Uri("https://inventory.example.test/api/v1/drone/settings"),
            handler.RequestUri);
        Assert.True(handler.Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task GetSettingsAsync_returns_false_on_http_error()
    {
        SettingsHandler handler = new(HttpStatusCode.InternalServerError, "{}");
        using HttpClient client = new(handler);
        HttpDroneSettingsClient settingsClient = new(
            client,
            Options.Create(new DroneOptions
            {
                Endpoint = new Uri("https://inventory.example.test/api/v1/inventory")
            }),
            NullLogger<HttpDroneSettingsClient>.Instance);

        DroneRemoteSettings settings = await settingsClient.GetSettingsAsync(CancellationToken.None);

        Assert.False(settings.EnableDriveEncryption);
    }

    private sealed class SettingsHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public System.Net.Http.Headers.HttpRequestHeaders Headers { get; private set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Headers = request.Headers;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
