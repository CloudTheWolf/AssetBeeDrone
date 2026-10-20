namespace AssetBeeDrone.Reporting;

public interface IDroneCommandClient
{
    Task<IReadOnlyList<DroneCommand>> ListCommandsAsync(
        string? serialNumber,
        string? deviceName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Confirms with AssetBee that this target is still assigned to this device and
    /// executable. Returns a fresh command payload that must be used for execution.
    /// </summary>
    Task<DroneCommandVerification> VerifyCommandAsync(
        long targetId,
        string? serialNumber,
        string? deviceName,
        CancellationToken cancellationToken);

    Task ReportStatusAsync(
        long targetId,
        DroneCommandStatusUpdate update,
        CancellationToken cancellationToken);
}
