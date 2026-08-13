using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBeeDrone.Tray;

internal static class TrayFileIpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetDirectoryPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = @"C:\ProgramData";
        }

        return Path.Combine(root, "AssetBee", "Drone");
    }

    public static string StatusPath => Path.Combine(GetDirectoryPath(), "status.json");

    public static string SyncRequestPath => Path.Combine(GetDirectoryPath(), "sync.request");

    public static TrayStatusResponse? ReadStatus()
    {
        string path = StatusPath;
        if (!File.Exists(path))
        {
            return null;
        }

        string json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<TrayStatusResponse>(json, JsonOptions);
    }

    public static void RequestSync()
    {
        string directory = GetDirectoryPath();
        Directory.CreateDirectory(directory);
        File.WriteAllText(SyncRequestPath, DateTimeOffset.UtcNow.ToString("O"));
    }

    public static bool IsServiceAlive(TrayStatusResponse status, TimeSpan maxAge)
    {
        if (status.ServiceAliveUtc is null)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - status.ServiceAliveUtc.Value <= maxAge;
    }
}

internal sealed record TrayStatusResponse(
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    bool Running,
    bool Busy,
    string? Message,
    DateTimeOffset? ServiceAliveUtc);
