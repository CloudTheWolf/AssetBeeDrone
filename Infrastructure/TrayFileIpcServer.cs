using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AssetBeeDrone.Services;
using AssetBeeDrone.Updating;

namespace AssetBeeDrone.Infrastructure;

public static class TrayIpcPaths
{
    public const string StatusFileName = "status.json";
    public const string SyncRequestFileName = "sync.request";
    public const string InstallRequestFileName = "install.request";

    public static string GetDirectoryPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = OperatingSystem.IsWindows() ? @"C:\ProgramData" : "/var/lib";
        }

        return Path.Combine(root, "AssetBee", "Drone");
    }

    public static string StatusPath => Path.Combine(GetDirectoryPath(), StatusFileName);

    public static string SyncRequestPath => Path.Combine(GetDirectoryPath(), SyncRequestFileName);

    public static string InstallRequestPath => Path.Combine(GetDirectoryPath(), InstallRequestFileName);
}

/// <summary>
/// File-based IPC for the tray app. Named pipes between LocalSystem and interactive
/// sessions are unreliable; ProgramData status/sync files work consistently.
/// </summary>
public sealed class TrayFileIpcServer(
    IInventorySyncController syncController,
    IUpdateCoordinator? updateCoordinator,
    TimeProvider timeProvider,
    ILogger<TrayFileIpcServer> logger) : BackgroundService
{
    private CancellationToken _appStopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _appStopping = stoppingToken;

        try
        {
            EnsureDirectory();
            await WriteStatusAsync(syncController.GetStatus(), "Service started.", stoppingToken);
            logger.LogInformation("Tray IPC ready at {Directory}", TrayIpcPaths.GetDirectoryPath());
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to initialize tray IPC directory");
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), timeProvider);
        do
        {
            try
            {
                await ProcessSyncRequestAsync(stoppingToken);
                await ProcessInstallRequestAsync(stoppingToken);
                await WriteStatusAsync(syncController.GetStatus(), message: null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Tray IPC loop failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessSyncRequestAsync(CancellationToken cancellationToken)
    {
        string requestPath = TrayIpcPaths.SyncRequestPath;
        if (!File.Exists(requestPath))
        {
            return;
        }

        try
        {
            File.Delete(requestPath);
        }
        catch (IOException)
        {
            // Tray may still be writing; try again next tick.
            return;
        }

        InventorySyncStatus current = syncController.GetStatus();
        if (current.Busy || current.Running)
        {
            await WriteStatusAsync(current, "Sync already in progress.", cancellationToken);
            return;
        }

        await WriteStatusAsync(current with { Busy = true, Running = true }, "Sync started.", cancellationToken);
        _ = RunSyncInBackgroundAsync();
    }

    private async Task ProcessInstallRequestAsync(CancellationToken cancellationToken)
    {
        if (updateCoordinator is null)
        {
            return;
        }

        string requestPath = TrayIpcPaths.InstallRequestPath;
        if (!File.Exists(requestPath))
        {
            return;
        }

        try
        {
            File.Delete(requestPath);
        }
        catch (IOException)
        {
            return;
        }

        UpdateCoordinatorSnapshot snapshot = updateCoordinator.GetSnapshot();
        if (snapshot.State is UpdateInstallState.Downloading or UpdateInstallState.Installing)
        {
            await WriteStatusAsync(syncController.GetStatus(), "Update already in progress.", cancellationToken);
            return;
        }

        if (snapshot.State is not (UpdateInstallState.Available or UpdateInstallState.Failed))
        {
            await WriteStatusAsync(syncController.GetStatus(), "No update available to install.", cancellationToken);
            return;
        }

        await WriteStatusAsync(syncController.GetStatus(), "Downloading update…", cancellationToken);
        _ = RunInstallInBackgroundAsync();
    }

    private async Task RunSyncInBackgroundAsync()
    {
        try
        {
            InventorySyncStatus status = await syncController.RequestSyncAsync(_appStopping);
            string? message = status.Busy
                ? "Sync already in progress."
                : status.LastError is null
                    ? "Sync completed."
                    : "Sync finished with errors.";
            await WriteStatusAsync(status, message, _appStopping);
            if (status.LastError is null && !status.Busy)
            {
                logger.LogInformation("Tray-triggered inventory sync completed");
            }
            else if (!string.IsNullOrWhiteSpace(status.LastError))
            {
                logger.LogWarning("Tray-triggered inventory sync finished with error: {Error}", status.LastError);
            }
        }
        catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Tray-triggered inventory sync failed");
            try
            {
                await WriteStatusAsync(syncController.GetStatus(), exception.Message, CancellationToken.None);
            }
            catch
            {
                // Ignore secondary status write failures.
            }
        }
    }

    private async Task RunInstallInBackgroundAsync()
    {
        if (updateCoordinator is null)
        {
            return;
        }

        try
        {
            await updateCoordinator.RequestInstallAsync(_appStopping);
            UpdateCoordinatorSnapshot after = updateCoordinator.GetSnapshot();
            string message = after.State switch
            {
                UpdateInstallState.Failed => after.Error ?? "Update failed.",
                UpdateInstallState.Installing => "Installing update…",
                _ => "Update finished."
            };
            await WriteStatusAsync(syncController.GetStatus(), message, _appStopping);
        }
        catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Tray-triggered update install failed");
            try
            {
                await WriteStatusAsync(syncController.GetStatus(), exception.Message, CancellationToken.None);
            }
            catch
            {
                // Ignore secondary status write failures.
            }
        }
    }

    private async Task WriteStatusAsync(
        InventorySyncStatus status,
        string? message,
        CancellationToken cancellationToken)
    {
        UpdateCoordinatorSnapshot? update = updateCoordinator?.GetSnapshot();
        bool updateAvailable = update is not null && (
            update.State is UpdateInstallState.Downloading or UpdateInstallState.Installing ||
            (update.State is UpdateInstallState.Available or UpdateInstallState.Failed &&
             !string.IsNullOrWhiteSpace(update.Version)));

        TrayStatusResponse response = new(
            status.LastRunUtc,
            status.LastSuccessUtc,
            status.LastError,
            status.Running,
            status.Busy,
            message,
            timeProvider.GetUtcNow(),
            UpdateAvailable: updateAvailable,
            UpdateVersion: update?.Version,
            UpdateState: update?.State is null or UpdateInstallState.None
                ? null
                : update.State.ToString(),
            UpdateError: update?.Error,
            QuitTray: update?.QuitTray == true);

        string json = JsonSerializer.Serialize(response, TrayJsonContext.Default.TrayStatusResponse);
        string path = TrayIpcPaths.StatusPath;
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json + "\n", cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureDirectory()
    {
        string path = TrayIpcPaths.GetDirectoryPath();
        DirectoryInfo directory = Directory.CreateDirectory(path);

        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify,
            inherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}
