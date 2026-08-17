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

    public static string InstallRequestPath => Path.Combine(GetDirectoryPath(), "install.request");

    public static string CheckUpdateRequestPath => Path.Combine(GetDirectoryPath(), "checkupdate.request");

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
        WriteRequest(SyncRequestPath);
    }

    public static void RequestInstall()
    {
        WriteRequest(InstallRequestPath);
    }

    public static void RequestCheckUpdate()
    {
        WriteRequest(CheckUpdateRequestPath);
    }

    public static bool IsServiceAlive(TrayStatusResponse status, TimeSpan maxAge)
    {
        if (status.ServiceAliveUtc is null)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - status.ServiceAliveUtc.Value <= maxAge;
    }

    private static void WriteRequest(string path)
    {
        string directory = GetDirectoryPath();
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
    }
}

internal sealed record TrayStatusResponse(
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    bool Running,
    bool Busy,
    string? Message,
    DateTimeOffset? ServiceAliveUtc,
    bool UpdateAvailable = false,
    string? UpdateVersion = null,
    string? UpdateState = null,
    string? UpdateError = null,
    bool QuitTray = false,
    string? ServiceVersion = null,
    DateTimeOffset? LastUpdateCheckUtc = null,
    string? LastUpdateCheckMessage = null);
