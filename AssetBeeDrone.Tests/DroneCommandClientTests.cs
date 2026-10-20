using System.Net;
using System.Text;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Tests;

public sealed class DroneCommandClientTests
{
    [Fact]
    public async Task ListCommandsAsync_parses_payload()
    {
        const string body = """
            {
              "data": [
                {
                  "id": 9,
                  "pushId": 3,
                  "type": "install_package",
                  "status": "pending",
                  "requiresUserConsent": true,
                  "package": {
                    "id": 2,
                    "name": "GCPW",
                    "source": "winget",
                    "wingetId": "Google.GCPW"
                  }
                }
              ],
              "hardwareId": 1
            }
            """;
        CommandHandler handler = new(HttpStatusCode.OK, body);
        using HttpClient client = new(handler);
        HttpDroneCommandClient commandClient = new(
            client,
            Options.Create(new DroneOptions
            {
                Endpoint = new Uri("https://inventory.example.test/api/v1/inventory"),
                ApiKey = "secret"
            }),
            NullLogger<HttpDroneCommandClient>.Instance);

        IReadOnlyList<DroneCommand> commands =
            await commandClient.ListCommandsAsync("SN-1", "PC-1", CancellationToken.None);

        Assert.Single(commands);
        Assert.Equal(9, commands[0].Id);
        Assert.True(commands[0].RequiresUserConsent);
        Assert.Equal("GCPW", commands[0].Package!.Name);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Contains("serialNumber=SN-1", handler.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("drone/commands", handler.RequestUri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListCommandsAsync_returns_empty_on_http_error()
    {
        CommandHandler handler = new(HttpStatusCode.InternalServerError, "{}");
        using HttpClient client = new(handler);
        HttpDroneCommandClient commandClient = new(
            client,
            Options.Create(new DroneOptions
            {
                Endpoint = new Uri("https://inventory.example.test/api/v1/inventory")
            }),
            NullLogger<HttpDroneCommandClient>.Instance);

        IReadOnlyList<DroneCommand> commands =
            await commandClient.ListCommandsAsync("SN-1", null, CancellationToken.None);

        Assert.Empty(commands);
    }

    [Fact]
    public async Task VerifyCommandAsync_returns_authorized_command()
    {
        const string body = """
            {
              "authorized": true,
              "reason": null,
              "command": {
                "id": 9,
                "pushId": 3,
                "type": "bitlocker_status",
                "status": "pending",
                "requiresUserConsent": false,
                "package": null
              }
            }
            """;
        CommandHandler handler = new(HttpStatusCode.OK, body);
        using HttpClient client = new(handler);
        HttpDroneCommandClient commandClient = new(
            client,
            Options.Create(new DroneOptions
            {
                Endpoint = new Uri("https://inventory.example.test/api/v1/inventory"),
                ApiKey = "secret"
            }),
            NullLogger<HttpDroneCommandClient>.Instance);

        DroneCommandVerification verification =
            await commandClient.VerifyCommandAsync(9, "SN-1", "PC-1", CancellationToken.None);

        Assert.True(verification.Authorized);
        Assert.NotNull(verification.Command);
        Assert.Equal(9, verification.Command.Id);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/drone/commands/9/verify", handler.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("SN-1", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyCommandAsync_fails_closed_when_unauthorized()
    {
        const string body = """
            { "authorized": false, "reason": "Push is no longer open.", "command": null }
            """;
        CommandHandler handler = new(HttpStatusCode.Forbidden, body);
        using HttpClient client = new(handler);
        HttpDroneCommandClient commandClient = new(
            client,
            Options.Create(new DroneOptions
            {
                Endpoint = new Uri("https://inventory.example.test/api/v1/inventory")
            }),
            NullLogger<HttpDroneCommandClient>.Instance);

        DroneCommandVerification verification =
            await commandClient.VerifyCommandAsync(9, "SN-1", null, CancellationToken.None);

        Assert.False(verification.Authorized);
        Assert.Null(verification.Command);
        Assert.Contains("no longer open", verification.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CommandHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
