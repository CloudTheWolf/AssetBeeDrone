using AssetBeeDrone.Collectors;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using AssetBeeDrone.Reporting;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Services;

public interface IInventorySyncController
{
    InventorySyncStatus GetStatus();

    Task<InventorySyncStatus> RequestSyncAsync(CancellationToken cancellationToken);
}

public sealed record InventorySyncStatus(
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    bool Running,
    bool Busy);

public sealed class InventoryWorker(
    IDeviceInventoryCollector collector,
    IAssetClassificationService classificationService,
    IInventoryReporter reporter,
    IDroneSettingsClient droneSettingsClient,
    IBitLockerRemediationService bitLockerRemediationService,
    IOptions<DroneOptions> options,
    TimeProvider timeProvider,
    ISystemEventLogger systemEventLogger,
    IBitLockerStatusService bitLockerStatusService,
    ILogger<InventoryWorker> logger) : BackgroundService, IInventorySyncController
{
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _statusLock = new();
    private DateTimeOffset? _lastRunUtc;
    private DateTimeOffset? _lastSuccessUtc;
    private string? _lastError;
    private int _running;

    public InventorySyncStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new InventorySyncStatus(
                _lastRunUtc,
                _lastSuccessUtc,
                _lastError,
                Volatile.Read(ref _running) != 0,
                _runGate.CurrentCount == 0);
        }
    }

    public async Task<InventorySyncStatus> RequestSyncAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
        {
            InventorySyncStatus busy = GetStatus();
            return busy with { Busy = true, Running = true };
        }

        try
        {
            await RunOnceCoreAsync(cancellationToken);
        }
        finally
        {
            _runGate.Release();
        }

        return GetStatus();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Value.CollectionInterval, timeProvider);

        do
        {
            await _runGate.WaitAsync(stoppingToken);
            try
            {
                await RunOnceCoreAsync(stoppingToken);
            }
            finally
            {
                _runGate.Release();
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override void Dispose()
    {
        _runGate.Dispose();
        base.Dispose();
    }

    private async Task RunOnceCoreAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _running, 1);
        DateTimeOffset started = timeProvider.GetUtcNow();
        try
        {
            logger.LogInformation("Collecting device inventory");
            AssetClassification classification =
                await classificationService.ClassifyAsync(cancellationToken);

            await MaybeRemediateDriveEncryptionAsync(classification, cancellationToken);

            var inventory = await collector.CollectAsync(cancellationToken);
            bitLockerStatusService.UpdateFromInventory(inventory.DiskEncryption);
            await reporter.ReportAsync(
                inventory with
                {
                    Type = classification.Type,
                    HardwareType = classification.HardwareType
                },
                cancellationToken);

            lock (_statusLock)
            {
                _lastRunUtc = started;
                _lastSuccessUtc = timeProvider.GetUtcNow();
                _lastError = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Inventory collection or delivery failed; the service will retry next interval");
            systemEventLogger.WriteError(
                "Inventory sync failed; the service will retry next interval.",
                exception.ToString());
            lock (_statusLock)
            {
                _lastRunUtc = started;
                _lastError = exception.Message;
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task MaybeRemediateDriveEncryptionAsync(
        AssetClassification classification,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!IsDesktopOrLaptop(classification))
        {
            logger.LogDebug(
                "Skipping drive-encryption remediation for hardware type {HardwareType}",
                classification.HardwareType.Value ?? classification.HardwareType.Status.ToString());
            return;
        }

        DroneRemoteSettings settings =
            await droneSettingsClient.GetSettingsAsync(cancellationToken);
        if (!settings.EnableDriveEncryption)
        {
            logger.LogDebug(
                "Org enableDriveEncryption is off or unavailable; skipping BitLocker remediation");
            return;
        }

        logger.LogInformation(
            "Org enableDriveEncryption is on; ensuring BitLocker on fixed drives");
        try
        {
            BitLockerRemediationResult result =
                await bitLockerRemediationService.EnsureFixedDrivesEncryptedAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Detail))
            {
                logger.LogWarning("BitLocker remediation finished with detail: {Detail}", result.Detail);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "BitLocker remediation failed; continuing with inventory collection");
        }
    }

    private static bool IsDesktopOrLaptop(AssetClassification classification)
    {
        if (classification.HardwareType.Status != ProbeStatus.Available ||
            string.IsNullOrWhiteSpace(classification.HardwareType.Value))
        {
            return false;
        }

        string hardwareType = classification.HardwareType.Value;
        return hardwareType.Equals("desktop", StringComparison.OrdinalIgnoreCase) ||
               hardwareType.Equals("laptop", StringComparison.OrdinalIgnoreCase);
    }
}
