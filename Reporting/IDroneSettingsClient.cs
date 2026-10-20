namespace AssetBeeDrone.Reporting;

public sealed record DroneRemoteSettings(bool EnableDriveEncryption);

public interface IDroneSettingsClient
{
    /// <summary>
    /// Fetches org-controlled Drone settings from AssetBee.
    /// Returns <see cref="DroneRemoteSettings.EnableDriveEncryption"/> as false when the
    /// response is missing, invalid, or unreachable.
    /// </summary>
    Task<DroneRemoteSettings> GetSettingsAsync(CancellationToken cancellationToken);
}
