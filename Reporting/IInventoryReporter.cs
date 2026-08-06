using AssetBeeDrone.Models;

namespace AssetBeeDrone.Reporting;

public interface IInventoryReporter
{
    Task ReportAsync(DeviceInventory inventory, CancellationToken cancellationToken);
}
