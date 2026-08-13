using AssetBeeDrone.Collectors;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Models;
using AssetBeeDrone.Reporting;
using AssetBeeDrone.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Tests;

public sealed class InventoryWorkerSyncTests
{
    [Fact]
    public async Task RequestSync_updates_last_success_and_clears_error()
    {
        StubTimeProvider time = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        FakeCollector collector = new();
        FakeReporter reporter = new();
        InventoryWorker worker = CreateWorker(collector, reporter, time);

        InventorySyncStatus status = await worker.RequestSyncAsync(CancellationToken.None);

        Assert.False(status.Busy);
        Assert.Null(status.LastError);
        Assert.Equal(time.GetUtcNow(), status.LastSuccessUtc);
        Assert.Equal(1, collector.Calls);
        Assert.Equal(1, reporter.Calls);
    }

    [Fact]
    public async Task RequestSync_records_error_when_reporter_fails()
    {
        StubTimeProvider time = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        FakeCollector collector = new();
        FakeReporter reporter = new() { Exception = new InvalidOperationException("boom") };
        InventoryWorker worker = CreateWorker(collector, reporter, time);

        InventorySyncStatus status = await worker.RequestSyncAsync(CancellationToken.None);

        Assert.Equal("boom", status.LastError);
        Assert.NotNull(status.LastRunUtc);
        Assert.Null(status.LastSuccessUtc);
    }

    [Fact]
    public async Task RequestSync_returns_busy_when_run_already_in_progress()
    {
        StubTimeProvider time = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeCollector collector = new() { Block = gate.Task };
        FakeReporter reporter = new();
        InventoryWorker worker = CreateWorker(collector, reporter, time);

        Task<InventorySyncStatus> first = worker.RequestSyncAsync(CancellationToken.None);
        await WaitUntilAsync(() => collector.Calls == 1);

        InventorySyncStatus busy = await worker.RequestSyncAsync(CancellationToken.None);
        Assert.True(busy.Busy);
        Assert.True(busy.Running);

        gate.SetResult();
        InventorySyncStatus completed = await first;
        Assert.False(completed.Busy);
        Assert.Null(completed.LastError);
    }

    [Fact]
    public void Tray_protocol_round_trips_camel_case_json()
    {
        TrayStatusResponse response = new(
            DateTimeOffset.Parse("2026-01-01T12:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T12:01:00Z"),
            null,
            Running: false,
            Busy: false,
            Message: "Sync completed.");

        string json = System.Text.Json.JsonSerializer.Serialize(
            response,
            TrayJsonContext.Default.TrayStatusResponse);
        Assert.Contains("\"lastSuccessUtc\"", json);

        TrayCommand? command = System.Text.Json.JsonSerializer.Deserialize(
            """{"op":"sync"}""",
            TrayJsonContext.Default.TrayCommand);
        Assert.Equal("sync", command!.Op);
    }

    private static InventoryWorker CreateWorker(
        IDeviceInventoryCollector collector,
        IInventoryReporter reporter,
        TimeProvider time) =>
        new(
            collector,
            new FakeClassifier(),
            reporter,
            Options.Create(new DroneOptions
            {
                Endpoint = new Uri("https://inventory.example.test/v1"),
                CollectionIntervalMinutes = 60
            }),
            time,
            NullLogger<InventoryWorker>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met.");
    }

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeClassifier : IAssetClassificationService
    {
        public Task<AssetClassification> ClassifyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AssetClassification("hardware", ProbeValue<string>.Available("laptop")));
    }

    private sealed class FakeCollector : IDeviceInventoryCollector
    {
        public int Calls { get; private set; }
        public Task? Block { get; init; }

        public async Task<DeviceInventory> CollectAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Block is not null)
            {
                await Block.WaitAsync(cancellationToken);
            }

            return new DeviceInventory(
                "1.0",
                DateTimeOffset.UnixEpoch,
                "windows",
                ProbeValue<string>.Available("WIN-TEST"),
                ProbeValue<string>.Available("ABC123"),
                ProbeValue<string>.Available("dellInc"),
                ProbeValue<string>.Available("latitude5520"),
                ProbeValue<OperatingSystemInfo>.Unavailable("n/a"),
                ProbeValue<CpuInfo>.Unavailable("n/a"),
                ProbeValue<MemoryInfo>.Unavailable("n/a"),
                ProbeValue<IReadOnlyList<DiskInfo>>.Available([]),
                ProbeValue<IReadOnlyList<EncryptionVolume>>.Available([]),
                ProbeValue<DomainWorkspaceInfo>.Unavailable("n/a"),
                ProbeValue<IReadOnlyList<LoginProviderInfo>>.Available([]),
                ProbeValue<IReadOnlyList<AntivirusInfo>>.Available([]),
                ProbeValue<UpdateInventory>.Unavailable("n/a"),
                ProbeValue<SbomInventory>.Unavailable("n/a"));
        }
    }

    private sealed class FakeReporter : IInventoryReporter
    {
        public int Calls { get; private set; }
        public Exception? Exception { get; init; }

        public Task ReportAsync(DeviceInventory inventory, CancellationToken cancellationToken)
        {
            Calls++;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.CompletedTask;
        }
    }
}
