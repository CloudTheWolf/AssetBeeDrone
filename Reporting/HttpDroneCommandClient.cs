using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AssetBeeDrone.Configuration;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Reporting;

public sealed class HttpDroneCommandClient(
    HttpClient httpClient,
    IOptions<DroneOptions> options,
    ILogger<HttpDroneCommandClient> logger) : IDroneCommandClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DroneOptions _options = options.Value;

    public async Task<IReadOnlyList<DroneCommand>> ListCommandsAsync(
        string? serialNumber,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        Uri uri = BuildListUri(serialNumber, deviceName);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, uri);
        using HttpResponseMessage response =
            await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Drone commands list returned HTTP {StatusCode}",
                (int)response.StatusCode);
            return [];
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<DroneCommand> commands = [];
        foreach (JsonElement element in data.EnumerateArray())
        {
            DroneCommand? command = ParseCommand(element);
            if (command is not null)
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    public async Task ReportStatusAsync(
        long targetId,
        DroneCommandStatusUpdate update,
        CancellationToken cancellationToken)
    {
        Uri uri = new(_options.Endpoint, $"drone/commands/{targetId}/status");
        string body = JsonSerializer.Serialize(new
        {
            status = update.Status,
            scheduledFor = update.ScheduledFor,
            result = update.Result,
            error = update.Error
        }, JsonOptions);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, uri);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response =
            await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string preview = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Drone command status update for {TargetId} returned HTTP {StatusCode}: {Body}",
                targetId,
                (int)response.StatusCode,
                preview.Length > 500 ? preview[..500] : preview);
        }
    }

    private Uri BuildListUri(string? serialNumber, string? deviceName)
    {
        Uri baseUri = new(_options.Endpoint, "drone/commands");
        List<string> query = [];
        if (!string.IsNullOrWhiteSpace(serialNumber))
        {
            query.Add("serialNumber=" + Uri.EscapeDataString(serialNumber));
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            query.Add("deviceName=" + Uri.EscapeDataString(deviceName));
        }

        return query.Count == 0
            ? baseUri
            : new Uri(baseUri + "?" + string.Join('&', query));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        HttpRequestMessage request = new(method, uri);
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

    private static DroneCommand? ParseCommand(JsonElement element)
    {
        if (!element.TryGetProperty("id", out JsonElement idElement) ||
            !idElement.TryGetInt64(out long id))
        {
            return null;
        }

        long pushId = element.TryGetProperty("pushId", out JsonElement pushElement) &&
                      pushElement.TryGetInt64(out long parsedPush)
            ? parsedPush
            : 0;
        string type = GetString(element, "type") ?? string.Empty;
        string status = GetString(element, "status") ?? "pending";
        bool consent = element.TryGetProperty("requiresUserConsent", out JsonElement consentElement) &&
                       consentElement.ValueKind == JsonValueKind.True;

        DroneCommandPackage? package = null;
        if (element.TryGetProperty("package", out JsonElement packageElement) &&
            packageElement.ValueKind == JsonValueKind.Object)
        {
            package = new DroneCommandPackage(
                packageElement.TryGetProperty("id", out JsonElement pkgId) && pkgId.TryGetInt64(out long pid)
                    ? pid
                    : 0,
                GetString(packageElement, "name") ?? "Package",
                GetString(packageElement, "source") ?? string.Empty,
                GetString(packageElement, "msiUrl"),
                GetString(packageElement, "msiArgs"),
                GetString(packageElement, "wingetId"),
                GetString(packageElement, "chocoId"),
                GetString(packageElement, "silentArgs"),
                GetString(packageElement, "sha256"));
        }

        return new DroneCommand(
            id,
            pushId,
            type,
            status,
            consent,
            GetDate(element, "firstNotifiedAt"),
            GetDate(element, "postponeDeadlineAt"),
            GetDate(element, "scheduledFor"),
            package);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetDate(JsonElement element, string name)
    {
        string? text = GetString(element, name);
        return DateTimeOffset.TryParse(text, out DateTimeOffset parsed) ? parsed : null;
    }
}
