using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Models;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Reporting;

public sealed class HttpInventoryReporter(
    HttpClient httpClient,
    IOptions<DroneOptions> options,
    ILogger<HttpInventoryReporter> logger) : IInventoryReporter
{
    private readonly DroneOptions _options = options.Value;

    public async Task ReportAsync(DeviceInventory inventory, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            inventory, InventoryJsonContext.Default.DeviceInventory);

        if (_options.Debug)
        {
            await WriteDebugPayloadAsync(payload, cancellationToken);
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using HttpRequestMessage request = CreateRequest(payload);
                using HttpResponseMessage response =
                    await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "Inventory report accepted for {DeviceName} with HTTP {StatusCode}",
                        inventory.DeviceName.Value,
                        (int)response.StatusCode);
                    return;
                }

                if (!IsTransient(response.StatusCode) || attempt >= _options.MaxRetryAttempts)
                {
                    throw new HttpRequestException(
                        $"Inventory endpoint returned HTTP {(int)response.StatusCode}.",
                        null,
                        response.StatusCode);
                }

                logger.LogWarning(
                    "Inventory endpoint returned HTTP {StatusCode}; retrying attempt {Attempt}",
                    (int)response.StatusCode,
                    attempt + 1);
            }
            catch (HttpRequestException exception) when (
                attempt < _options.MaxRetryAttempts &&
                (exception.StatusCode is null || IsTransient(exception.StatusCode.Value)))
            {
                logger.LogWarning(
                    "Inventory delivery failed ({Message}); retrying attempt {Attempt}",
                    exception.Message,
                    attempt + 1);
            }

            await Task.Delay(RetryDelay(attempt), cancellationToken);
        }
    }

    private async Task WriteDebugPayloadAsync(byte[] payload, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(
            string.IsNullOrWhiteSpace(_options.DebugOutputPath)
                ? "inventory-debug.json"
                : _options.DebugOutputPath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, payload, cancellationToken);
        logger.LogWarning(
            "Debug mode wrote the outbound inventory payload to {Path}. " +
            "This file may contain BitLocker recovery keys and other secrets.",
            path);
    }

    private HttpRequestMessage CreateRequest(byte[] payload)
    {
        HttpRequestMessage request = new(HttpMethod.Put, _options.Endpoint)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

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

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
}
