using System.Net.Http.Headers;
using System.Text.Json;
using AssetBeeDrone.Configuration;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Reporting;

public sealed class HttpDroneSettingsClient(
    HttpClient httpClient,
    IOptions<DroneOptions> options,
    ILogger<HttpDroneSettingsClient> logger) : IDroneSettingsClient
{
    private static readonly DroneRemoteSettings Disabled = new(false);
    private readonly DroneOptions _options = options.Value;

    public async Task<DroneRemoteSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = CreateRequest();
            using HttpResponseMessage response =
                await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Drone settings request returned HTTP {StatusCode}; treating enableDriveEncryption as false",
                    (int)response.StatusCode);
                return Disabled;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("enableDriveEncryption", out JsonElement value))
            {
                logger.LogWarning(
                    "Drone settings response omitted enableDriveEncryption; treating as false");
                return Disabled;
            }

            bool enabled = value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false
            };

            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                logger.LogWarning(
                    "Drone settings enableDriveEncryption was not a boolean; treating as false");
            }

            return new DroneRemoteSettings(enabled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Drone settings could not be fetched; treating enableDriveEncryption as false");
            return Disabled;
        }
    }

    private HttpRequestMessage CreateRequest()
    {
        HttpRequestMessage request = new(HttpMethod.Get, ResolveSettingsUri(_options.Endpoint));

        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
        }

        return request;
    }

    /// <summary>
    /// Maps .../api/v1/inventory → .../api/v1/drone/settings via normal URI resolution.
    /// </summary>
    internal static Uri ResolveSettingsUri(Uri inventoryEndpoint) =>
        new(inventoryEndpoint, "drone/settings");
}
