using AssetBeeDrone.Configuration;
using AssetBeeDrone.Updating;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Services;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string? AvailableVersion,
    string Message);

public interface IUpdateCheckController
{
    DateTimeOffset? LastCheckUtc { get; }

    string? LastCheckMessage { get; }

    /// <summary>
    /// Fetches the update feed once. On Windows, a newer package is staged for tray
    /// confirmation; on other platforms it applies immediately (same as the timer path).
    /// </summary>
    Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken);
}

public sealed class UpdateWorker(
    IUpdateFeedClient feedClient,
    IUpdateApplier applier,
    IUpdateCoordinator coordinator,
    IOptions<DroneOptions> options,
    TimeProvider timeProvider,
    ILogger<UpdateWorker> logger) : BackgroundService, IUpdateCheckController
{
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _lastCheckLock = new();
    private DateTimeOffset? _lastCheckUtc;
    private string? _lastCheckMessage;

    public DateTimeOffset? LastCheckUtc
    {
        get
        {
            lock (_lastCheckLock)
            {
                return _lastCheckUtc;
            }
        }
    }

    public string? LastCheckMessage
    {
        get
        {
            lock (_lastCheckLock)
            {
                return _lastCheckMessage;
            }
        }
    }

    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken)
    {
        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            UpdateCheckResult result = await CheckOnceAsync(userInitiated: true, cancellationToken);
            lock (_lastCheckLock)
            {
                _lastCheckUtc = timeProvider.GetUtcNow();
                _lastCheckMessage = result.Message;
            }

            return result;
        }
        finally
        {
            _checkGate.Release();
        }
    }

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
            await _checkGate.WaitAsync(stoppingToken);
            try
            {
                await CheckOnceAsync(userInitiated: false, stoppingToken);
            }
            finally
            {
                _checkGate.Release();
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<UpdateCheckResult> CheckOnceAsync(bool userInitiated, CancellationToken cancellationToken)
    {
        try
        {
            UpdateManifest? manifest = await feedClient.FetchManifestAsync(cancellationToken);
            if (manifest is null)
            {
                string message = string.IsNullOrWhiteSpace(BuildConstants.UpdateFeedUrl)
                    ? "Updates are not configured for this build."
                    : "Could not read the update feed.";
                if (userInitiated)
                {
                    logger.LogWarning("{Message}", message);
                }

                return new UpdateCheckResult(false, null, message);
            }

            if (!AppVersion.IsNewer(manifest.Version, AppVersion.Current))
            {
                string message = $"You're up to date ({AppVersion.Current}).";
                logger.LogDebug(
                    "No update available (feed={FeedVersion}, local={LocalVersion})",
                    manifest.Version,
                    AppVersion.Current);
                return new UpdateCheckResult(false, null, message);
            }

            UpdatePackage? package = UpdatePackageSelector.Select(manifest);
            if (package is null)
            {
                string message =
                    $"Update {manifest.Version} is available but no package matches this platform.";
                logger.LogWarning(
                    "{Message} ({Rid})",
                    message,
                    UpdatePackageSelector.ResolveRuntimeIdentifier());
                return new UpdateCheckResult(false, manifest.Version, message);
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
                return new UpdateCheckResult(
                    true,
                    manifest.Version,
                    $"Update {manifest.Version} is available.");
            }

            await applier.ApplyAsync(manifest, package, cancellationToken);
            return new UpdateCheckResult(
                true,
                manifest.Version,
                $"Installing update {manifest.Version}…");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                userInitiated
                    ? "Update check failed"
                    : "Auto-update check failed; will retry next interval");
            return new UpdateCheckResult(false, null, $"Update check failed: {exception.Message}");
        }
    }
}
