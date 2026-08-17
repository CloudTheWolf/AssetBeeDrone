using AssetBeeDrone.Configuration;
using AssetBeeDrone.Updating;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Services;

public sealed class UpdateWorker(
    IUpdateFeedClient feedClient,
    IUpdateApplier applier,
    IUpdateCoordinator coordinator,
    IOptions<DroneOptions> options,
    TimeProvider timeProvider,
    ILogger<UpdateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(BuildConstants.UpdateFeedUrl))
        {
            logger.LogDebug("Auto-update is disabled (UpdateFeedUrl was not set at build time)");
            return;
        }

        if (!options.Value.AutoUpdate)
        {
            logger.LogInformation("Auto-update is disabled by configuration");
            return;
        }

        TimeSpan interval = options.Value.AutoUpdateInterval;
        logger.LogInformation(
            "Auto-update enabled; checking {FeedUrl} every {IntervalHours} hour(s). Current version: {Version}",
            BuildConstants.UpdateFeedUrl,
            options.Value.AutoUpdateIntervalHours,
            AppVersion.Current);

        using PeriodicTimer timer = new(interval, timeProvider);

        // Delay the first check slightly so inventory can start first.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        do
        {
            await CheckOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            UpdateManifest? manifest = await feedClient.FetchManifestAsync(cancellationToken);
            if (manifest is null)
            {
                return;
            }

            if (!AppVersion.IsNewer(manifest.Version, AppVersion.Current))
            {
                logger.LogDebug(
                    "No update available (feed={FeedVersion}, local={LocalVersion})",
                    manifest.Version,
                    AppVersion.Current);
                return;
            }

            UpdatePackage? package = UpdatePackageSelector.Select(manifest);
            if (package is null)
            {
                logger.LogWarning(
                    "Update {Version} is available but no package matches this platform ({Rid})",
                    manifest.Version,
                    UpdatePackageSelector.ResolveRuntimeIdentifier());
                return;
            }

            logger.LogInformation(
                "Update available: {LocalVersion} -> {RemoteVersion} ({FileName})",
                AppVersion.Current,
                manifest.Version,
                package.FileName);

            if (OperatingSystem.IsWindows())
            {
                // Tray prompts the user; install starts only after install.request.
                coordinator.TrySetPending(manifest, package);
                return;
            }

            await applier.ApplyAsync(manifest, package, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Auto-update check failed; will retry next interval");
        }
    }
}
