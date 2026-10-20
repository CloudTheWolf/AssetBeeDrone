using System.Text.Json;

namespace AssetBeeDrone.Infrastructure;

public sealed record PendingRemoteInstall(
    long TargetId,
    string PackageName,
    string Source,
    DateTimeOffset? PostponeDeadlineAt,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset WrittenAtUtc);

public sealed record RemoteInstallUserDecision(
    long TargetId,
    string Action,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset WrittenAtUtc);

public static class RemoteInstallIpc
{
    public const string PendingFileName = "pending-install.json";
    public const string DecisionFileName = "install-decision.json";

    public static string PendingPath => Path.Combine(TrayIpcPaths.GetDirectoryPath(), PendingFileName);

    public static string DecisionPath => Path.Combine(TrayIpcPaths.GetDirectoryPath(), DecisionFileName);

    public static void WritePending(PendingRemoteInstall pending)
    {
        Directory.CreateDirectory(TrayIpcPaths.GetDirectoryPath());
        File.WriteAllText(
            PendingPath,
            JsonSerializer.Serialize(pending, RemoteInstallJsonContext.Default.PendingRemoteInstall));
    }

    public static PendingRemoteInstall? ReadPending()
    {
        try
        {
            if (!File.Exists(PendingPath))
            {
                return null;
            }

            string json = File.ReadAllText(PendingPath);
            return JsonSerializer.Deserialize(json, RemoteInstallJsonContext.Default.PendingRemoteInstall);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void ClearPending()
    {
        TryDelete(PendingPath);
    }

    public static void WriteDecision(RemoteInstallUserDecision decision)
    {
        Directory.CreateDirectory(TrayIpcPaths.GetDirectoryPath());
        File.WriteAllText(
            DecisionPath,
            JsonSerializer.Serialize(decision, RemoteInstallJsonContext.Default.RemoteInstallUserDecision));
    }

    public static RemoteInstallUserDecision? ReadDecision()
    {
        try
        {
            if (!File.Exists(DecisionPath))
            {
                return null;
            }

            string json = File.ReadAllText(DecisionPath);
            return JsonSerializer.Deserialize(json, RemoteInstallJsonContext.Default.RemoteInstallUserDecision);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void ClearDecision()
    {
        TryDelete(DecisionPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
