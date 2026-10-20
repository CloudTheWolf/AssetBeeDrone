using System.Text.Json;
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
                _awaitingUser.Remove(decision.TargetId);
                RemoteInstallIpc.ClearPending();
                await RunInstallAsync(
                    new DroneCommand(
                        decision.TargetId,
                        0,
                        "install_package",
                        "awaiting_user",
                        true,
                        null,
                        null,
                        null,
                        null),
                    cancellationToken);
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
        DroneCommand? verified = await VerifyBeforeExecuteAsync(command.Id, cancellationToken);
        if (verified is null)
        {
            return;
        }

        if (verified.Package is null)
        {
            await commandClient.ReportStatusAsync(
                verified.Id,
                new DroneCommandStatusUpdate("failed", Error: "Package metadata missing."),
                cancellationToken);
            return;
        }

        _awaitingUser.Remove(verified.Id);
        RemoteInstallIpc.ClearPending();
        await commandClient.ReportStatusAsync(
            verified.Id,
            new DroneCommandStatusUpdate("running"),
            cancellationToken);

        logger.LogInformation(
            "Installing remote package {Package} for target {TargetId}",
            verified.Package.Name,
            verified.Id);

        PackageInstallResult result =
            await packageInstaller.InstallAsync(verified.Package, cancellationToken);

        await commandClient.ReportStatusAsync(
            verified.Id,
            new DroneCommandStatusUpdate(
                result.Succeeded ? "succeeded" : "failed",
                Result: JsonSerializer.SerializeToElement(
                    new PackageInstallStatusResult(result.ExitCode, result.Message),
                    DroneCommandJsonContext.Default.PackageInstallStatusResult),
                Error: result.Succeeded ? null : result.Message),
            cancellationToken);
    }

    private async Task RunProbeAsync(DroneCommand command, CancellationToken cancellationToken)
    {
        DroneCommand? verified = await VerifyBeforeExecuteAsync(command.Id, cancellationToken);
        if (verified is null)
        {
            return;
        }

        await commandClient.ReportStatusAsync(
            verified.Id,
            new DroneCommandStatusUpdate("running"),
            cancellationToken);

        try
        {
            DeviceInventory inventory = await inventoryCollector.CollectAsync(cancellationToken);
            bool unsupported = verified.Type is not ("bitlocker_status" or "windows_update_status");
            JsonElement result = verified.Type switch
            {
                "bitlocker_status" => BuildBitLockerResult(inventory),
                "windows_update_status" => BuildWindowsUpdateResult(inventory),
                _ => JsonSerializer.SerializeToElement(
                    new UnsupportedCommandResult($"Unsupported command type '{verified.Type}'."),
                    DroneCommandJsonContext.Default.UnsupportedCommandResult)
            };

            await commandClient.ReportStatusAsync(
                verified.Id,
                new DroneCommandStatusUpdate(
                    unsupported ? "unsupported" : "succeeded",
                    Result: result,
                    Error: unsupported ? $"Unsupported command type '{verified.Type}'." : null),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await commandClient.ReportStatusAsync(
                verified.Id,
                new DroneCommandStatusUpdate("failed", Error: exception.Message),
                cancellationToken);
        }
    }

    private async Task<DroneCommand?> VerifyBeforeExecuteAsync(
        long targetId,
        CancellationToken cancellationToken)
    {
        DeviceIdentity identity = await identityProbe.GetAsync(cancellationToken);
        DroneCommandVerification verification = await commandClient.VerifyCommandAsync(
            targetId,
            identity.SerialNumber,
            identity.DeviceName,
            cancellationToken);

        if (!verification.Authorized || verification.Command is null)
        {
            logger.LogWarning(
                "Refusing to execute command {TargetId}: {Reason}",
                targetId,
                verification.Reason ?? "not authorized");
            _awaitingUser.Remove(targetId);
            PendingRemoteInstall? pending = RemoteInstallIpc.ReadPending();
            if (pending?.TargetId == targetId)
            {
                RemoteInstallIpc.ClearPending();
            }

            return null;
        }

        return verification.Command;
    }

    private static JsonElement BuildBitLockerResult(DeviceInventory inventory)
    {
        if (inventory.DiskEncryption.Status != ProbeStatus.Available ||
            inventory.DiskEncryption.Value is null)
        {
            return JsonSerializer.SerializeToElement(
                new ProbeUnavailableResult(
                    inventory.DiskEncryption.Status.ToString(),
                    inventory.DiskEncryption.Detail),
                DroneCommandJsonContext.Default.ProbeUnavailableResult);
        }

        BitLockerStatusResult payload = new(
            inventory.DiskEncryption.Value.Select(volume => new BitLockerVolumeStatusResult(
                volume.Volume,
                volume.Technology,
                volume.State,
                volume.ProtectionStatus,
                (volume.RecoveryKeys?.Count ?? 0) > 0 ||
                (volume.KeyProtectors?.Any(p => !string.IsNullOrWhiteSpace(p.RecoveryKey)) ?? false)
            )).ToList());

        return JsonSerializer.SerializeToElement(
            payload,
            DroneCommandJsonContext.Default.BitLockerStatusResult);
    }

    private static JsonElement BuildWindowsUpdateResult(DeviceInventory inventory)
    {
        if (inventory.Updates.Status != ProbeStatus.Available || inventory.Updates.Value is null)
        {
            return JsonSerializer.SerializeToElement(
                new ProbeUnavailableResult(
                    inventory.Updates.Status.ToString(),
                    inventory.Updates.Detail),
                DroneCommandJsonContext.Default.ProbeUnavailableResult);
        }

        WindowsUpdateStatusResult payload = new(
            inventory.Updates.Value.Installed.Count,
            inventory.Updates.Value.Available.Count,
            inventory.Updates.Value.Available.Take(25).Select(update => new WindowsUpdateItemStatusResult(
                update.Id,
                update.Title,
                update.Category,
                update.KbArticle)).ToList());

        return JsonSerializer.SerializeToElement(
            payload,
            DroneCommandJsonContext.Default.WindowsUpdateStatusResult);
    }
}
