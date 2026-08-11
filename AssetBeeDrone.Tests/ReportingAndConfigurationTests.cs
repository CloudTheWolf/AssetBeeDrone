using System.Net;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Models;
using AssetBeeDrone.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBeeDrone.Tests;

public sealed class ReportingAndConfigurationTests
{
    private const string RecoveryKey =
        "111111-222222-333333-444444-555555-666666-777777-888888";

    [Fact]
    public async Task Reporter_retries_transient_status_and_never_logs_payload()
    {
        SequenceHandler handler = new(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.Accepted);
        using HttpClient client = new(handler);
        ListLogger<HttpInventoryReporter> logger = new();
        DroneOptions options = new()
        {
            Endpoint = new Uri("https://inventory.example.test/v1/devices"),
            MaxRetryAttempts = 2
        };
        HttpInventoryReporter reporter =
            new(client, Options.Create(options), logger);

        await reporter.ReportAsync(CreateInventory(), CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Contains(RecoveryKey, handler.LastBody);
        Assert.Contains("\"type\":\"hardware\"", handler.LastBody);
        Assert.Contains("\"hardwareType\"", handler.LastBody);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(RecoveryKey));
    }

    [Fact]
    public async Task Reporter_writes_debug_json_when_enabled()
    {
        string path = Path.Combine(Path.GetTempPath(), $"assetbee-debug-{Guid.NewGuid():N}.json");
        SequenceHandler handler = new(HttpStatusCode.Accepted);
        using HttpClient client = new(handler);
        ListLogger<HttpInventoryReporter> logger = new();
        DroneOptions options = new()
        {
            Endpoint = new Uri("https://inventory.example.test/v1/devices"),
            Debug = true,
            DebugOutputPath = path
        };

        try
        {
            await new HttpInventoryReporter(client, Options.Create(options), logger)
                .ReportAsync(CreateInventory(), CancellationToken.None);

            Assert.True(File.Exists(path));
            string body = await File.ReadAllTextAsync(path);
            Assert.Equal(handler.LastBody, body);
            Assert.Contains(RecoveryKey, body);
            Assert.Contains(logger.Messages,
                message => message.Contains(path, StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Configuration_allows_http_only_for_debug_loopback_endpoints()
    {
        DroneOptionsValidator validator = new();

        ValidateOptionsResult localhostDebug = validator.Validate(null, new DroneOptions
        {
            Endpoint = new Uri("http://localhost:8080/inventory"),
            Debug = true
        });
        ValidateOptionsResult loopbackDebug = validator.Validate(null, new DroneOptions
        {
            Endpoint = new Uri("http://127.0.0.1:8080/inventory"),
            Debug = true
        });
        ValidateOptionsResult localhostWithoutDebug = validator.Validate(null, new DroneOptions
        {
            Endpoint = new Uri("http://localhost:8080/inventory")
        });
        ValidateOptionsResult remoteDebug = validator.Validate(null, new DroneOptions
        {
            Endpoint = new Uri("http://inventory.example.test"),
            Debug = true
        });
        ValidateOptionsResult invalidType = validator.Validate(null, new DroneOptions
        {
            Endpoint = new Uri("https://inventory.example.test"),
            Type = "spaceship"
        });
        ValidateOptionsResult ambiguous = validator.Validate(null, new DroneOptions
        {
            Endpoint = new Uri("https://inventory.example.test"),
            BearerToken = "token",
            ApiKey = "key"
        });

        Assert.True(localhostDebug.Succeeded);
        Assert.True(loopbackDebug.Succeeded);
        Assert.True(localhostWithoutDebug.Failed);
        Assert.True(remoteDebug.Failed);
        Assert.True(invalidType.Failed);
        Assert.True(ambiguous.Failed);
    }

    private static DeviceInventory CreateInventory() => new(
        "1.0",
        DateTimeOffset.UnixEpoch,
        "windows",
        ProbeValue<string>.Available("WIN-TEST"),
        ProbeValue<string>.Available("ABC123"),
        ProbeValue<string>.Available("dellInc"),
        ProbeValue<string>.Available("latitude5520"),
        ProbeValue<OperatingSystemInfo>.Available(
            new OperatingSystemInfo("Microsoft Windows 11 Pro", "10.0.26100", "24H2", "26100")),
        ProbeValue<CpuInfo>.Available(new CpuInfo("CPU", 4)),
        ProbeValue<MemoryInfo>.Available(new MemoryInfo(1024)),
        ProbeValue<IReadOnlyList<DiskInfo>>.Available([]),
        ProbeValue<IReadOnlyList<EncryptionVolume>>.Available(
        [
            new EncryptionVolume(
                "C:",
                "BitLocker",
                "encrypted",
                [RecoveryKey],
                [new BitLockerKeyProtector("{11111111-2222-3333-4444-555555555555}", RecoveryKey)])
        ]),
        ProbeValue<DomainWorkspaceInfo>.Available(
            new DomainWorkspaceInfo(null, false, null, false)),
        ProbeValue<IReadOnlyList<LoginProviderInfo>>.Available([]),
        ProbeValue<IReadOnlyList<AntivirusInfo>>.Available([]),
        ProbeValue<UpdateInventory>.Available(new UpdateInventory([], [])),
        ProbeValue<SbomInventory>.Available(new SbomInventory(
            "CycloneDX",
            "1.6",
            DateTimeOffset.UnixEpoch,
            [
                new SbomTarget(
                    "host",
                    "host",
                    "WIN-TEST",
                    [new SbomComponent("Example.Package", "1.0.0", "application")])
            ])));

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _index;

        public int RequestCount { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            HttpStatusCode status = statuses[Math.Min(_index, statuses.Length - 1)];
            _index++;
            return new HttpResponseMessage(status);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

}
