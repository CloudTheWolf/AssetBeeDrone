namespace AssetBeeDrone.Reporting;

public interface IDroneCommandClient
{
    Task<IReadOnlyList<DroneCommand>> ListCommandsAsync(
        string? serialNumber,
        string? deviceName,
        CancellationToken cancellationToken);

    Task ReportStatusAsync(
        long targetId,
        DroneCommandStatusUpdate update,
        CancellationToken cancellationToken);
}
