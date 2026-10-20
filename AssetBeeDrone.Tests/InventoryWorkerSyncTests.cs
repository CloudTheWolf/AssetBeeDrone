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
        RecordingSystemEventLogger events = new();
        InventoryWorker worker = CreateWorker(collector, reporter, time, events);

        InventorySyncStatus status = await worker.RequestSyncAsync(CancellationToken.None);

        Assert.Equal("boom", status.LastError);
        Assert.NotNull(status.LastRunUtc);
        Assert.Null(status.LastSuccessUtc);
        Assert.Single(events.Entries);
        Assert.Contains("Inventory sync failed", events.Entries[0].Title);
        Assert.Contains("boom", events.Entries[0].Details);
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
    public void Tray_status_json_round_trips_camel_case()
    {
        TrayStatusResponse response = new(
            DateTimeOffset.Parse("2026-01-01T12:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T12:01:00Z"),
            null,
            Running: false,
            Busy: false,
            Message: "Sync completed.",
            ServiceAliveUtc: DateTimeOffset.Parse("2026-01-01T12:02:00Z"));

        string json = System.Text.Json.JsonSerializer.Serialize(
            response,
            TrayJsonContext.Default.TrayStatusResponse);
        Assert.Contains("\"lastSuccessUtc\"", json);
        Assert.Contains("\"serviceAliveUtc\"", json);

        TrayStatusResponse? parsed = System.Text.Json.JsonSerializer.Deserialize(
            json,
            TrayJsonContext.Default.TrayStatusResponse);
        Assert.Equal(response.ServiceAliveUtc, parsed!.ServiceAliveUtc);
    }

    [Fact]
    public void Tray_status_json_includes_update_fields()
    {
        TrayStatusResponse response = new(
            DateTimeOffset.Parse("2026-01-01T12:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T12:01:00Z"),
            null,
            Running: true,
            Busy: false,
            Message: null,
            ServiceAliveUtc: DateTimeOffset.Parse("2026-01-01T12:02:00Z"),
            UpdateAvailable: true,
            UpdateVersion: "1.2.3",
            UpdateState: "Available",
            UpdateError: null,
            QuitTray: false,
            ServiceVersion: "1.0.0",
            LastUpdateCheckUtc: DateTimeOffset.Parse("2026-01-01T12:03:00Z"),
            LastUpdateCheckMessage: "You're up to date (1.0.0).");

        string json = System.Text.Json.JsonSerializer.Serialize(
            response,
            TrayJsonContext.Default.TrayStatusResponse);
        Assert.Contains("\"updateAvailable\":true", json);
        Assert.Contains("\"updateVersion\":\"1.2.3\"", json);
        Assert.Contains("\"updateState\":\"Available\"", json);
        Assert.Contains("\"serviceVersion\":\"1.0.0\"", json);
        Assert.Contains("\"lastUpdateCheckMessage\"", json);

        TrayStatusResponse? parsed = System.Text.Json.JsonSerializer.Deserialize(
            json,
            TrayJsonContext.Default.TrayStatusResponse);
        Assert.True(parsed!.UpdateAvailable);
        Assert.Equal("1.2.3", parsed.UpdateVersion);
        Assert.Equal("Available", parsed.UpdateState);
        Assert.Equal("1.0.0", parsed.ServiceVersion);
        Assert.Equal("You're up to date (1.0.0).", parsed.LastUpdateCheckMessage);
        Assert.False(parsed.QuitTray);
    }

    [Fact]
    public void Tray_status_json_includes_bitlocker_fields()
    {
        TrayStatusResponse response = new(
            DateTimeOffset.Parse("2026-01-01T12:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T12:01:00Z"),
            null,
            Running: false,
            Busy: false,
            Message: null,
            ServiceAliveUtc: DateTimeOffset.Parse("2026-01-01T12:02:00Z"),
            BitLockerVolumes:
            [
                new TrayBitLockerVolume("C:", "FullyEncrypted", "On", true),
                new TrayBitLockerVolume("D:", "TPM Only (No Key)", "On", false)
            ],
            BitLockerStatusUtc: DateTimeOffset.Parse("2026-01-01T12:04:00Z"),
            BitLockerStatusDetail: null,
            BitLockerRefreshing: false);

        string json = System.Text.Json.JsonSerializer.Serialize(
            response,
            TrayJsonContext.Default.TrayStatusResponse);
        Assert.Contains("\"bitLockerVolumes\"", json);
        Assert.Contains("\"TPM Only (No Key)\"", json);
        Assert.Contains("\"hasRecoveryKey\":true", json);
        Assert.Contains("\"bitLockerStatusUtc\"", json);

        TrayStatusResponse? parsed = System.Text.Json.JsonSerializer.Deserialize(
            json,
            TrayJsonContext.Default.TrayStatusResponse);
        Assert.Equal(2, parsed!.BitLockerVolumes!.Count);
        Assert.Equal("C:", parsed.BitLockerVolumes[0].Volume);
        Assert.True(parsed.BitLockerVolumes[0].HasRecoveryKey);
        Assert.Equal("TPM Only (No Key)", parsed.BitLockerVolumes[1].State);
        Assert.False(parsed.BitLockerVolumes[1].HasRecoveryKey);
    }

    [Fact]
    public void BitLocker_status_service_maps_inventory_without_recovery_secrets()
    {
        WindowsBitLockerStatusService service = new(
            new ThrowingProcessRunner(),
            new StubTimeProvider(DateTimeOffset.Parse("2026-01-01T12:00:00Z")));

        service.UpdateFromInventory(ProbeValue<IReadOnlyList<EncryptionVolume>>.Available(
        [
            new EncryptionVolume(
                "C:",
                "BitLocker",
                "FullyEncrypted",
                ["111111-222222-333333-444444-555555-666666-777777-888888"],
                [new BitLockerKeyProtector("{id}", "111111-222222-333333-444444-555555-666666-777777-888888")],
                "On"),
            new EncryptionVolume(
                "D:",
                "BitLocker",
                "TPM Only (No Key)",
                [],
                [],
                "On")
        ]));

        BitLockerStatusSnapshot snapshot = service.GetSnapshot();
        Assert.Equal(2, snapshot.Volumes!.Count);
        Assert.True(snapshot.Volumes[0].HasRecoveryKey);
        Assert.False(snapshot.Volumes[1].HasRecoveryKey);
        string json = System.Text.Json.JsonSerializer.Serialize(snapshot.Volumes);
        Assert.DoesNotContain("111111", json);
    }

    private static InventoryWorker CreateWorker(
        IDeviceInventoryCollector collector,
        IInventoryReporter reporter,
        TimeProvider time,
        ISystemEventLogger? systemEventLogger = null,
        IDroneSettingsClient? settingsClient = null,
        IBitLockerRemediationService? remediation = null,
        IAssetClassificationService? classifier = null) =>
        new(
            collector,
            classifier ?? new FakeClassifier(),
            reporter,
            settingsClient ?? new FakeSettingsClient(enableDriveEncryption: false),
            remediation ?? new UnsupportedBitLockerRemediationService(),
            Options.Create(new DroneOptions
            {
                Endpoint = new Uri("https://inventory.example.test/v1"),
                CollectionIntervalMinutes = 60
            }),
            time,
            systemEventLogger ?? new NoOpSystemEventLogger(),
            new UnsupportedBitLockerStatusService(),
            NullLogger<InventoryWorker>.Instance);

    private sealed class NoOpSystemEventLogger : ISystemEventLogger
    {
        public void WriteError(string title, string details)
        {
        }
    }

    private sealed class RecordingSystemEventLogger : ISystemEventLogger
    {
        public List<(string Title, string Details)> Entries { get; } = [];

        public void WriteError(string title, string details) =>
            Entries.Add((title, details));
    }

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

    private sealed class FakeSettingsClient(bool enableDriveEncryption) : IDroneSettingsClient
    {
        public int Calls { get; private set; }

        public Task<DroneRemoteSettings> GetSettingsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new DroneRemoteSettings(enableDriveEncryption));
        }
    }

    private sealed class FakeRemediationService : IBitLockerRemediationService
    {
        public int Calls { get; private set; }

        public Task<BitLockerRemediationResult> EnsureFixedDrivesEncryptedAsync(
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new BitLockerRemediationResult(true, []));
        }
    }

    private sealed class ConfigurableClassifier(string hardwareType) : IAssetClassificationService
    {
        public Task<AssetClassification> ClassifyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AssetClassification(
                "hardware",
                ProbeValue<string>.Available(hardwareType)));
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Process runner should not be used for inventory mapping.");
    }

    [Fact]
    public async Task RequestSync_remediates_bitlocker_when_org_enabled_and_laptop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        StubTimeProvider time = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        FakeCollector collector = new();
        FakeReporter reporter = new();
        FakeSettingsClient settings = new(enableDriveEncryption: true);
        FakeRemediationService remediation = new();
        InventoryWorker worker = CreateWorker(
            collector,
            reporter,
            time,
            settingsClient: settings,
            remediation: remediation,
            classifier: new ConfigurableClassifier("laptop"));

        await worker.RequestSyncAsync(CancellationToken.None);

        Assert.Equal(1, settings.Calls);
        Assert.Equal(1, remediation.Calls);
        Assert.Equal(1, collector.Calls);
        Assert.Equal(1, reporter.Calls);
    }

    [Fact]
    public async Task RequestSync_skips_bitlocker_remediation_when_org_disabled()
    {
        StubTimeProvider time = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        FakeCollector collector = new();
        FakeReporter reporter = new();
        FakeSettingsClient settings = new(enableDriveEncryption: false);
        FakeRemediationService remediation = new();
        InventoryWorker worker = CreateWorker(
            collector,
            reporter,
            time,
            settingsClient: settings,
            remediation: remediation,
            classifier: new ConfigurableClassifier("laptop"));

        await worker.RequestSyncAsync(CancellationToken.None);

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, settings.Calls);
        Assert.Equal(0, remediation.Calls);
    }

    [Fact]
    public async Task RequestSync_skips_bitlocker_remediation_for_servers()
    {
        StubTimeProvider time = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));
        FakeCollector collector = new();
        FakeReporter reporter = new();
        FakeSettingsClient settings = new(enableDriveEncryption: true);
        FakeRemediationService remediation = new();
        InventoryWorker worker = CreateWorker(
            collector,
            reporter,
            time,
            settingsClient: settings,
            remediation: remediation,
            classifier: new ConfigurableClassifier("server"));

        await worker.RequestSyncAsync(CancellationToken.None);

        Assert.Equal(0, settings.Calls);
        Assert.Equal(0, remediation.Calls);
    }
}
