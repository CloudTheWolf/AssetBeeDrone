namespace AssetBeeDrone.Updating;

public enum UpdateInstallState
{
    None,
    Available,
    Downloading,
    Installing,
    Failed
}

public sealed record UpdateCoordinatorSnapshot(
    UpdateInstallState State,
    string? Version,
    string? FileName,
    string? Error,
    bool QuitTray);

public interface IUpdateCoordinator
{
    UpdateCoordinatorSnapshot GetSnapshot();

    bool TrySetPending(UpdateManifest manifest, UpdatePackage package);

    /// <summary>
    /// Download + verify, signal tray quit, then install. No-op if nothing pending
    /// or an install is already in progress.
    /// </summary>
    Task RequestInstallAsync(CancellationToken cancellationToken);
}

public sealed class UpdateCoordinator(
    IUpdateApplier applier,
    TimeProvider timeProvider,
    ILogger<UpdateCoordinator> logger) : IUpdateCoordinator
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);

    private UpdateInstallState _state = UpdateInstallState.None;
    private UpdateManifest? _manifest;
    private UpdatePackage? _package;
    private string? _error;
    private bool _quitTray;

    public UpdateCoordinatorSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new UpdateCoordinatorSnapshot(
                _state,
                _manifest?.Version,
                _package?.FileName,
                _error,
                _quitTray);
        }
    }

    public bool TrySetPending(UpdateManifest manifest, UpdatePackage package)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(package);

        lock (_gate)
        {
            if (_state is UpdateInstallState.Downloading or UpdateInstallState.Installing)
            {
                return false;
            }

            bool samePending =
                _state == UpdateInstallState.Available &&
                string.Equals(_manifest?.Version, manifest.Version, StringComparison.Ordinal) &&
                string.Equals(_package?.FileName, package.FileName, StringComparison.Ordinal);
            if (samePending)
            {
                return false;
            }

            _manifest = manifest;
            _package = package;
            _state = UpdateInstallState.Available;
            _error = null;
            _quitTray = false;
            logger.LogInformation(
                "Update pending user confirmation: {Version} ({FileName})",
                manifest.Version,
                package.FileName);
            return true;
        }
    }

    public async Task RequestInstallAsync(CancellationToken cancellationToken)
    {
        if (!await _installGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        UpdateManifest manifest;
        UpdatePackage package;
        try
        {
            lock (_gate)
            {
                if (_state is not (UpdateInstallState.Available or UpdateInstallState.Failed) ||
                    _manifest is null ||
                    _package is null)
                {
                    return;
                }

                manifest = _manifest;
                package = _package;
                _state = UpdateInstallState.Downloading;
                _error = null;
                _quitTray = false;
            }

            string packagePath;
            try
            {
                packagePath = await applier.DownloadAndVerifyAsync(manifest, package, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_gate)
                {
                    _state = UpdateInstallState.Failed;
                    _error = exception.Message;
                    _quitTray = false;
                }

                logger.LogError(exception, "Update download failed for {Version}", manifest.Version);
                return;
            }

            lock (_gate)
            {
                _state = UpdateInstallState.Installing;
                _quitTray = true;
            }

            // Give the tray a moment to see QuitTray and exit before msiexec replaces files.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            try
            {
                await applier.InstallDownloadedAsync(packagePath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_gate)
                {
                    _state = UpdateInstallState.Failed;
                    _error = exception.Message;
                    _quitTray = false;
                }

                logger.LogError(exception, "Update install failed for {Version}", manifest.Version);
            }
        }
        finally
        {
            _installGate.Release();
        }
    }
}
