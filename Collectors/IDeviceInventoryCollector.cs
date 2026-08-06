using AssetBeeDrone.Models;

namespace AssetBeeDrone.Collectors;

public interface IDeviceInventoryCollector
{
    Task<DeviceInventory> CollectAsync(CancellationToken cancellationToken);
}
