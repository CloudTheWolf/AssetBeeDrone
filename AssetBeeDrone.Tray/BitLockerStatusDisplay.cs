namespace AssetBeeDrone.Tray;

internal static class BitLockerStatusDisplay
{
    public static string Format(TrayStatusResponse? status, bool serviceAlive)
    {
        if (status is null || !serviceAlive)
        {
            return "Service is not running. Start AssetBee Drone to read BitLocker status.";
        }

        if (status.BitLockerRefreshing)
        {
            return "Reading BitLocker status from the AssetBee service…";
        }

        if (status.BitLockerVolumes is { Count: > 0 })
        {
            string lines = string.Join(Environment.NewLine, status.BitLockerVolumes.Select(static volume =>
            {
                string protection = string.IsNullOrWhiteSpace(volume.ProtectionStatus)
                    ? "Protection unknown"
                    : $"Protection {volume.ProtectionStatus}";
                string key = volume.HasRecoveryKey
                    ? "Recovery key available"
                    : "No recovery key";
                return $"{volume.Volume} — {volume.State} · {protection} · {key}";
            }));

            if (status.BitLockerStatusUtc is not null)
            {
                return lines + Environment.NewLine +
                       $"Updated {status.BitLockerStatusUtc.Value.ToLocalTime():g} (via AssetBee service)";
            }

            return lines;
        }

        if (!string.IsNullOrWhiteSpace(status.BitLockerStatusDetail))
        {
            return status.BitLockerStatusDetail;
        }

        return "BitLocker status has not been collected yet. Select Refresh status.";
    }
}
