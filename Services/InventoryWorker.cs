using AssetBeeDrone.Collectors;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Reporting;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Services;

public sealed class InventoryWorker(
    IDeviceInventoryCollector collector,
    IAssetClassificationService classificationService,
    IInventoryReporter reporter,
    IOptions<DroneOptions> options,
    TimeProvider timeProvider,
    ILogger<InventoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Value.CollectionInterval, timeProvider);

        do
        {
            try
            {
                logger.LogInformation("Collecting device inventory");
                AssetClassification classification =
                    await classificationService.ClassifyAsync(stoppingToken);
                var inventory = await collector.CollectAsync(stoppingToken);
                await reporter.ReportAsync(
                    inventory with
                    {
                        Type = classification.Type,
                        HardwareType = classification.HardwareType
                    },
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Inventory collection or delivery failed; the service will retry next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
