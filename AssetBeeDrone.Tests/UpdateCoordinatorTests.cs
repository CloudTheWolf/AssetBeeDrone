using AssetBeeDrone.Updating;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBeeDrone.Tests;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public void TrySetPending_sets_available_snapshot()
    {
        FakeApplier applier = new();
        UpdateCoordinator coordinator = new(
            applier,
            TimeProvider.System,
            NullLogger<UpdateCoordinator>.Instance);

        UpdateManifest manifest = new("1.2.3", []);
        UpdatePackage package = new("win-x64", "AssetBee.Drone-1.2.3-win-x64.msi", "abc");

        Assert.True(coordinator.TrySetPending(manifest, package));
        UpdateCoordinatorSnapshot snapshot = coordinator.GetSnapshot();
        Assert.Equal(UpdateInstallState.Available, snapshot.State);
        Assert.Equal("1.2.3", snapshot.Version);
        Assert.Equal("AssetBee.Drone-1.2.3-win-x64.msi", snapshot.FileName);
        Assert.False(snapshot.QuitTray);
    }

    [Fact]
    public void TrySetPending_ignores_duplicate_version()
    {
        UpdateCoordinator coordinator = new(
            new FakeApplier(),
            TimeProvider.System,
            NullLogger<UpdateCoordinator>.Instance);
        UpdateManifest manifest = new("1.2.3", []);
        UpdatePackage package = new("win-x64", "AssetBee.Drone-1.2.3-win-x64.msi", "abc");

        Assert.True(coordinator.TrySetPending(manifest, package));
        Assert.False(coordinator.TrySetPending(manifest, package));
    }

    [Fact]
    public async Task RequestInstall_downloads_sets_quit_tray_then_installs()
    {
        ImmediateTimeProvider time = new();
        FakeApplier applier = new();
        UpdateCoordinator coordinator = new(
            applier,
            time,
            NullLogger<UpdateCoordinator>.Instance);

        UpdateManifest manifest = new("2.0.0", []);
        UpdatePackage package = new("win-x64", "AssetBee.Drone-2.0.0-win-x64.msi", "deadbeef");
        Assert.True(coordinator.TrySetPending(manifest, package));

        await coordinator.RequestInstallAsync(CancellationToken.None);

        Assert.Equal(1, applier.DownloadCalls);
        Assert.Equal(1, applier.InstallCalls);
        Assert.Equal(@"C:\temp\AssetBee.Drone-2.0.0-win-x64.msi", applier.LastPackagePath);
        Assert.Equal(UpdateInstallState.Installing, coordinator.GetSnapshot().State);
        Assert.True(coordinator.GetSnapshot().QuitTray);
    }

    [Fact]
    public async Task RequestInstall_records_failure_when_download_throws()
    {
        FakeApplier applier = new()
        {
            DownloadException = new InvalidOperationException("checksum failed")
        };
        UpdateCoordinator coordinator = new(
            applier,
            TimeProvider.System,
            NullLogger<UpdateCoordinator>.Instance);
        Assert.True(coordinator.TrySetPending(
            new UpdateManifest("2.0.0", []),
            new UpdatePackage("win-x64", "pkg.msi", "aa")));

        await coordinator.RequestInstallAsync(CancellationToken.None);

        UpdateCoordinatorSnapshot snapshot = coordinator.GetSnapshot();
        Assert.Equal(UpdateInstallState.Failed, snapshot.State);
        Assert.Equal("checksum failed", snapshot.Error);
        Assert.False(snapshot.QuitTray);
        Assert.Equal(0, applier.InstallCalls);
    }

    /// <summary>
    /// Completes <see cref="TimeProvider"/> delays immediately so install tests stay fast.
    /// </summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            // Fire due callbacks on the thread pool with no delay.
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new NoOpTimer();
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeApplier : IUpdateApplier
    {
        public int DownloadCalls { get; private set; }
        public int InstallCalls { get; private set; }
        public string? LastPackagePath { get; private set; }
        public Exception? DownloadException { get; set; }

        public Task ApplyAsync(
            UpdateManifest manifest,
            UpdatePackage package,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> DownloadAndVerifyAsync(
            UpdateManifest manifest,
            UpdatePackage package,
            CancellationToken cancellationToken)
        {
            DownloadCalls++;
            if (DownloadException is not null)
            {
                throw DownloadException;
            }

            LastPackagePath = $@"C:\temp\{package.FileName}";
            return Task.FromResult(LastPackagePath);
        }

        public Task InstallDownloadedAsync(string packagePath, CancellationToken cancellationToken)
        {
            InstallCalls++;
            LastPackagePath = packagePath;
            return Task.CompletedTask;
        }
    }
}
