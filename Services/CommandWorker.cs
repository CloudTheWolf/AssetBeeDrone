using AssetBeeDrone.Collectors;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using AssetBeeDrone.Reporting;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Services;

public sealed class CommandWorker(
    IDroneCommandClient commandClient,
    IDeviceIdentityProbe identityProbe,
    IDeviceInventoryCollector inventoryCollector,
    IPackageInstaller packageInstaller,
    IOptions<DroneOptions> options,
    TimeProvider timeProvider,
    ILogger<CommandWorker> logger) : BackgroundService
{
    private readonly HashSet<long> _awaitingUser = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PeriodicTimer timer = new(options.Value.CommandPollInterval, timeProvider);
        do
        {
            try
            {
                await ProcessDecisionAsync(stoppingToken);
                await PollAndProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Remote command poll failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAndProcessAsync(CancellationToken cancellationToken)
    {
        DeviceIdentity identity = await identityProbe.GetAsync(cancellationToken);
        IReadOnlyList<DroneCommand> commands = await commandClient.ListCommandsAsync(
            identity.SerialNumber,
            identity.DeviceName,
            cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (DroneCommand command in commands)
        {
            await ProcessCommandAsync(command, now, cancellationToken);
        }
    }

    private async Task ProcessCommandAsync(
        DroneCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (command.RequiresUserConsent)
        {
            await ProcessInstallCommandAsync(command, now, cancellationToken);
            return;
        }

        await RunProbeAsync(command, cancellationToken);
    }

    private async Task ProcessInstallCommandAsync(
        DroneCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (command.Package is null)
        {
            await commandClient.ReportStatusAsync(
                command.Id,
                new DroneCommandStatusUpdate("failed", Error: "Package metadata missing."),
                cancellationToken);
            return;
        }

        DateTimeOffset? deadline = command.PostponeDeadlineAt;
        if (string.Equals(command.Status, "scheduled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command.Status, "postponed", StringComparison.OrdinalIgnoreCase))
        {
            if (command.ScheduledFor is not null && command.ScheduledFor <= now)
            {
                await RunInstallAsync(command, cancellationToken);
                return;
            }

            if (deadline is not null && deadline <= now)
            {
                logger.LogInformation(
                    "Install deadline reached for target {TargetId}; running automatically",
                    command.Id);
                await RunInstallAsync(command, cancellationToken);
                return;
            }

            EnsurePendingNotice(command);
            return;
        }

        if (deadline is not null && deadline <= now &&
            !_awaitingUser.Contains(command.Id))
        {
            await RunInstallAsync(command, cancellationToken);
            return;
        }

        if (_awaitingUser.Contains(command.Id) ||
            string.Equals(command.Status, "awaiting_user", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command.Status, "delivered", StringComparison.OrdinalIgnoreCase))
        {
            EnsurePendingNotice(command);
            if (deadline is not null && deadline <= now)
            {
                await RunInstallAsync(command, cancellationToken);
            }

            return;
        }

        await commandClient.ReportStatusAsync(
            command.Id,
            new DroneCommandStatusUpdate("awaiting_user"),
            cancellationToken);
        _awaitingUser.Add(command.Id);
        EnsurePendingNotice(command);
    }

    private void EnsurePendingNotice(DroneCommand command)
    {
        PendingRemoteInstall? existing = RemoteInstallIpc.ReadPending();
        if (existing?.TargetId == command.Id)
        {
            return;
        }

        RemoteInstallIpc.WritePending(new PendingRemoteInstall(
            command.Id,
            command.Package?.Name ?? "Package",
            command.Package?.Source ?? string.Empty,
            command.PostponeDeadlineAt,
            command.ScheduledFor,
            timeProvider.GetUtcNow()));
    }

    private async Task ProcessDecisionAsync(CancellationToken cancellationToken)
    {
        RemoteInstallUserDecision? decision = RemoteInstallIpc.ReadDecision();
        if (decision is null)
        {
            return;
        }

        RemoteInstallIpc.ClearDecision();
        string action = decision.Action.Trim().ToLowerInvariant();
        switch (action)
        {
            case "install_now":
                await commandClient.ReportStatusAsync(
                    decision.TargetId,
                    new DroneCommandStatusUpdate("running"),
                    cancellationToken);
                _awaitingUser.Remove(decision.TargetId);
                RemoteInstallIpc.ClearPending();
                // Package will be loaded on next poll; run by re-fetching.
                DeviceIdentity identity = await identityProbe.GetAsync(cancellationToken);
                IReadOnlyList<DroneCommand> commands = await commandClient.ListCommandsAsync(
                    identity.SerialNumber,
                    identity.DeviceName,
                    cancellationToken);
                DroneCommand? match = commands.FirstOrDefault(c => c.Id == decision.TargetId);
                if (match is not null)
                {
                    await RunInstallAsync(match, cancellationToken);
                }

                break;
            case "postpone":
            case "schedule":
                if (decision.ScheduledFor is null)
                {
                    break;
                }

                await commandClient.ReportStatusAsync(
                    decision.TargetId,
                    new DroneCommandStatusUpdate(
                        action == "postpone" ? "postponed" : "scheduled",
                        ScheduledFor: decision.ScheduledFor),
                    cancellationToken);
                PendingRemoteInstall? pending = RemoteInstallIpc.ReadPending();
                if (pending?.TargetId == decision.TargetId)
                {
                    RemoteInstallIpc.WritePending(pending with
                    {
                        ScheduledFor = decision.ScheduledFor,
                        WrittenAtUtc = timeProvider.GetUtcNow()
                    });
                }

                break;
        }
    }

    private async Task RunInstallAsync(DroneCommand command, CancellationToken cancellationToken)
    {
        if (command.Package is null)
        {
            await commandClient.ReportStatusAsync(
                command.Id,
                new DroneCommandStatusUpdate("failed", Error: "Package metadata missing."),
                cancellationToken);
            return;
        }

        _awaitingUser.Remove(command.Id);
        RemoteInstallIpc.ClearPending();
        await commandClient.ReportStatusAsync(
            command.Id,
            new DroneCommandStatusUpdate("running"),
            cancellationToken);

        logger.LogInformation(
            "Installing remote package {Package} for target {TargetId}",
            command.Package.Name,
            command.Id);

        PackageInstallResult result =
            await packageInstaller.InstallAsync(command.Package, cancellationToken);

        await commandClient.ReportStatusAsync(
            command.Id,
            new DroneCommandStatusUpdate(
                result.Succeeded ? "succeeded" : "failed",
                Result: new { exitCode = result.ExitCode, message = result.Message },
                Error: result.Succeeded ? null : result.Message),
            cancellationToken);
    }

    private async Task RunProbeAsync(DroneCommand command, CancellationToken cancellationToken)
    {
        await commandClient.ReportStatusAsync(
            command.Id,
            new DroneCommandStatusUpdate("running"),
            cancellationToken);

        try
        {
            DeviceInventory inventory = await inventoryCollector.CollectAsync(cancellationToken);
            object result = command.Type switch
            {
                "bitlocker_status" => BuildBitLockerResult(inventory),
                "windows_update_status" => BuildWindowsUpdateResult(inventory),
                _ => new { detail = $"Unsupported command type '{command.Type}'." }
            };

            bool unsupported = command.Type is not ("bitlocker_status" or "windows_update_status");
            await commandClient.ReportStatusAsync(
                command.Id,
                new DroneCommandStatusUpdate(
                    unsupported ? "unsupported" : "succeeded",
                    Result: result,
                    Error: unsupported ? $"Unsupported command type '{command.Type}'." : null),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await commandClient.ReportStatusAsync(
                command.Id,
                new DroneCommandStatusUpdate("failed", Error: exception.Message),
                cancellationToken);
        }
    }

    private static object BuildBitLockerResult(DeviceInventory inventory)
    {
        if (inventory.DiskEncryption.Status != ProbeStatus.Available ||
            inventory.DiskEncryption.Value is null)
        {
            return new
            {
                status = inventory.DiskEncryption.Status.ToString(),
                detail = inventory.DiskEncryption.Detail
            };
        }

        return new
        {
            volumes = inventory.DiskEncryption.Value.Select(volume => new
            {
                volume = volume.Volume,
                technology = volume.Technology,
                state = volume.State,
                protectionStatus = volume.ProtectionStatus,
                hasRecoveryKey = (volume.RecoveryKeys?.Count ?? 0) > 0 ||
                                 (volume.KeyProtectors?.Any(p => !string.IsNullOrWhiteSpace(p.RecoveryKey)) ?? false)
            }).ToList()
        };
    }

    private static object BuildWindowsUpdateResult(DeviceInventory inventory)
    {
        if (inventory.Updates.Status != ProbeStatus.Available || inventory.Updates.Value is null)
        {
            return new
            {
                status = inventory.Updates.Status.ToString(),
                detail = inventory.Updates.Detail
            };
        }

        return new
        {
            installedCount = inventory.Updates.Value.Installed.Count,
            availableCount = inventory.Updates.Value.Available.Count,
            available = inventory.Updates.Value.Available.Take(25).Select(update => new
            {
                id = update.Id,
                title = update.Title,
                category = update.Category,
                kbArticle = update.KbArticle
            }).ToList()
        };
    }
}
