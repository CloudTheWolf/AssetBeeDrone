using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using AssetBeeDrone.Models;

namespace AssetBeeDrone.Collectors.Linux;

/// <summary>
/// Best-effort cloud disk encryption probe via AWS/GCP/Azure metadata APIs.
/// Returns null when no cloud provider responds; otherwise a volume list (may include
/// unencrypted EBS volumes).
/// </summary>
public sealed class CloudDiskEncryptionProbe(HttpClient httpClient, TimeProvider timeProvider)
{
    public const string HttpClientName = "cloud-metadata";

    private static readonly Uri AwsImdsBase = new("http://169.254.169.254/");
    private static readonly Uri GcpMetadataDisks = new(
        "http://metadata.google.internal/computeMetadata/v1/instance/disks/?recursive=true");
    private static readonly Uri GcpMetadataDisksLinkLocal = new(
        "http://169.254.169.254/computeMetadata/v1/instance/disks/?recursive=true");
    private static readonly Uri AzureStorageProfile = new(
        "http://169.254.169.254/metadata/instance/compute/storageProfile?api-version=2021-12-13");

    public async Task<IReadOnlyList<EncryptionVolume>?> TryCollectAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<EncryptionVolume>? aws = await TryAwsAsync(cancellationToken);
        if (aws is { Count: > 0 })
        {
            return aws;
        }

        IReadOnlyList<EncryptionVolume>? azure = await TryAzureAsync(cancellationToken);
        if (azure is { Count: > 0 })
        {
            return azure;
        }

        IReadOnlyList<EncryptionVolume>? gcp = await TryGcpAsync(cancellationToken);
        return gcp is { Count: > 0 } ? gcp : null;
    }

    private async Task<IReadOnlyList<EncryptionVolume>?> TryAwsAsync(
        CancellationToken cancellationToken)
    {
        string? token = await PutStringAsync(
            new Uri(AwsImdsBase, "latest/api/token"),
            request =>
            {
                request.Method = HttpMethod.Put;
                request.Headers.TryAddWithoutValidation(
                    "X-aws-ec2-metadata-token-ttl-seconds", "60");
            },
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string? identityJson = await GetStringAsync(
            new Uri(AwsImdsBase, "latest/dynamic/instance-identity/document"),
            request => request.Headers.TryAddWithoutValidation(
                "X-aws-ec2-metadata-token", token),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(identityJson))
        {
            return null;
        }

        string? region;
        string? instanceId;
        try
        {
            using JsonDocument document = JsonDocument.Parse(identityJson);
            region = document.RootElement.TryGetProperty("region", out JsonElement regionElement)
                ? regionElement.GetString()
                : null;
            instanceId = document.RootElement.TryGetProperty("instanceId", out JsonElement idElement)
                ? idElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        string? roleName = await GetStringAsync(
            new Uri(AwsImdsBase, "latest/meta-data/iam/security-credentials/"),
            request => request.Headers.TryAddWithoutValidation(
                "X-aws-ec2-metadata-token", token),
            cancellationToken);
        roleName = roleName?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return null;
        }

        string? credentialsJson = await GetStringAsync(
            new Uri(AwsImdsBase, $"latest/meta-data/iam/security-credentials/{roleName}"),
            request => request.Headers.TryAddWithoutValidation(
                "X-aws-ec2-metadata-token", token),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return null;
        }

        string? accessKeyId;
        string? secretAccessKey;
        string? sessionToken;
        try
        {
            using JsonDocument document = JsonDocument.Parse(credentialsJson);
            accessKeyId = GetJsonString(document.RootElement, "AccessKeyId");
            secretAccessKey = GetJsonString(document.RootElement, "SecretAccessKey");
            sessionToken = GetJsonString(document.RootElement, "Token");
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(accessKeyId) ||
            string.IsNullOrWhiteSpace(secretAccessKey) ||
            string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["Action"] = "DescribeVolumes",
            ["Version"] = "2016-11-15",
            ["Filter.1.Name"] = "attachment.instance-id",
            ["Filter.1.Value.1"] = instanceId
        };
        string canonicalQuery = string.Join('&',
            query.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        string host = $"ec2.{region}.amazonaws.com";
        string authorization = AwsSigV4.BuildAuthorizationHeader(
            host,
            canonicalQuery,
            region,
            "ec2",
            accessKeyId,
            secretAccessKey,
            sessionToken,
            timeProvider.GetUtcNow(),
            out string amzDate);

        string? xml = await GetStringAsync(
            new Uri($"https://{host}/?{canonicalQuery}"),
            request =>
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
                request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
                request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);
            },
            cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            IReadOnlyList<EncryptionVolume> volumes = ParseDescribeVolumesXml(xml);
            return volumes.Count == 0 ? null : volumes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<EncryptionVolume> ParseDescribeVolumesXml(string xml)
    {
        XDocument document = XDocument.Parse(xml);
        XNamespace? ns = document.Root?.Name.Namespace;
        XElement? volumeSet = ns is null
            ? document.Root?.Element("volumeSet")
            : document.Root?.Element(ns + "volumeSet");
        if (volumeSet is null)
        {
            return [];
        }

        IEnumerable<XElement> items = ns is null
            ? volumeSet.Elements("item")
            : volumeSet.Elements(ns + "item");

        List<EncryptionVolume> volumes = [];
        foreach (XElement item in items)
        {
            XElement? volumeIdElement = ns is null
                ? item.Element("volumeId")
                : item.Element(ns + "volumeId");
            if (volumeIdElement is null)
            {
                continue;
            }

            XElement? encryptedElement = ns is null
                ? item.Element("encrypted")
                : item.Element(ns + "encrypted");
            bool encrypted = string.Equals(
                encryptedElement?.Value, "true", StringComparison.OrdinalIgnoreCase);

            string? device = null;
            XElement? attachmentSet = ns is null
                ? item.Element("attachmentSet")
                : item.Element(ns + "attachmentSet");
            XElement? attachment = attachmentSet?.Elements(ns is null ? "item" : ns + "item")
                .FirstOrDefault();
            if (attachment is not null)
            {
                device = (ns is null
                    ? attachment.Element("device")
                    : attachment.Element(ns + "device"))?.Value;
            }

            string volume = !string.IsNullOrWhiteSpace(device)
                ? device
                : volumeIdElement.Value;

            volumes.Add(new EncryptionVolume(
                volume,
                "AWS EBS",
                encrypted ? "encrypted" : "not encrypted"));
        }

        return volumes;
    }

    private async Task<IReadOnlyList<EncryptionVolume>?> TryAzureAsync(
        CancellationToken cancellationToken)
    {
        string? json = await GetStringAsync(
            AzureStorageProfile,
            request => request.Headers.TryAddWithoutValidation("Metadata", "true"),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            List<EncryptionVolume> volumes = [];

            if (document.RootElement.TryGetProperty("osDisk", out JsonElement osDisk))
            {
                AddAzureDisk(volumes, osDisk, "osDisk");
            }

            if (document.RootElement.TryGetProperty("dataDisks", out JsonElement dataDisks) &&
                dataDisks.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement dataDisk in dataDisks.EnumerateArray())
                {
                    string name = GetJsonString(dataDisk, "name") ?? "dataDisk";
                    AddAzureDisk(volumes, dataDisk, name);
                }
            }

            return volumes.Count == 0 ? null : volumes;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddAzureDisk(
        List<EncryptionVolume> volumes, JsonElement disk, string fallbackName)
    {
        string volume = GetJsonString(disk, "name") ?? fallbackName;
        bool adeEnabled = false;
        if (disk.TryGetProperty("encryptionSettings", out JsonElement settings) &&
            settings.ValueKind == JsonValueKind.Object &&
            settings.TryGetProperty("enabled", out JsonElement enabled))
        {
            adeEnabled = enabled.ValueKind == JsonValueKind.True ||
                         (enabled.ValueKind == JsonValueKind.String &&
                          string.Equals(enabled.GetString(), "true",
                              StringComparison.OrdinalIgnoreCase));
        }

        bool hasManagedDisk = disk.TryGetProperty("managedDisk", out JsonElement managed) &&
                              managed.ValueKind is JsonValueKind.Object or JsonValueKind.String;

        if (adeEnabled)
        {
            volumes.Add(new EncryptionVolume(volume, "Azure Disk (ADE)", "encrypted"));
        }
        else if (hasManagedDisk)
        {
            volumes.Add(new EncryptionVolume(volume, "Azure Disk (SSE)", "encrypted"));
        }
        else
        {
            volumes.Add(new EncryptionVolume(volume, "Azure Disk", "not encrypted"));
        }
    }

    private async Task<IReadOnlyList<EncryptionVolume>?> TryGcpAsync(
        CancellationToken cancellationToken)
    {
        string? json = await GetStringAsync(
            GcpMetadataDisks,
            request => request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google"),
            cancellationToken);
        json ??= await GetStringAsync(
            GcpMetadataDisksLinkLocal,
            request => request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google"),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<EncryptionVolume> volumes = [];
            foreach (JsonElement disk in document.RootElement.EnumerateArray())
            {
                string? type = GetJsonString(disk, "type");
                if (!string.IsNullOrEmpty(type) &&
                    !type.Equals("PERSISTENT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string volume = GetJsonString(disk, "deviceName") ??
                                GetJsonString(disk, "device-name") ??
                                $"disk-{GetJsonString(disk, "index") ?? volumes.Count.ToString(CultureInfo.InvariantCulture)}";
                volumes.Add(new EncryptionVolume(
                    volume,
                    "GCP Persistent Disk",
                    "encrypted"));
            }

            return volumes.Count == 0 ? null : volumes;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> PutStringAsync(
        Uri uri,
        Action<HttpRequestMessage> configure,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Put, uri);
            configure(request);
            using HttpResponseMessage response =
                await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> GetStringAsync(
        Uri uri,
        Action<HttpRequestMessage> configure,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            configure(request);
            using HttpResponseMessage response =
                await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? GetJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
