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

    public static string BitLockerRequestPath => Path.Combine(GetDirectoryPath(), "bitlocker.request");

    public static string PendingInstallPath => Path.Combine(GetDirectoryPath(), "pending-install.json");

    public static string InstallDecisionPath => Path.Combine(GetDirectoryPath(), "install-decision.json");

    public static TrayPendingInstall? ReadPendingInstall()
    {
        try
        {
            if (!File.Exists(PendingInstallPath))
            {
                return null;
            }

            string json = File.ReadAllText(PendingInstallPath);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<TrayPendingInstall>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void WriteInstallDecision(long targetId, string action, DateTimeOffset? scheduledFor)
    {
        Directory.CreateDirectory(GetDirectoryPath());
        string json = JsonSerializer.Serialize(
            new TrayInstallDecision(targetId, action, scheduledFor, DateTimeOffset.UtcNow),
            JsonOptions);
        File.WriteAllText(InstallDecisionPath, json);
    }

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

    public static void RequestBitLockerStatus()
    {
        WriteRequest(BitLockerRequestPath);
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

internal sealed record TrayPendingInstall(
    long TargetId,
    string PackageName,
    string Source,
    DateTimeOffset? PostponeDeadlineAt,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset WrittenAtUtc,
    DateTimeOffset? AutoInstallAtUtc = null);

internal sealed record TrayInstallDecision(
    long TargetId,
    string Action,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset WrittenAtUtc);

internal sealed record TrayBitLockerVolume(
    string Volume,
    string State,
    string? ProtectionStatus,
    bool HasRecoveryKey);

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
    string? LastUpdateCheckMessage = null,
    IReadOnlyList<TrayBitLockerVolume>? BitLockerVolumes = null,
    DateTimeOffset? BitLockerStatusUtc = null,
    string? BitLockerStatusDetail = null,
    bool BitLockerRefreshing = false);
