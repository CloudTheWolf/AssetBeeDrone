using AssetBeeDrone.Collectors;
using AssetBeeDrone.Collectors.Linux;
using AssetBeeDrone.Collectors.MacOS;
using AssetBeeDrone.Collectors.Windows;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Reporting;
using AssetBeeDrone.Services;
using AssetBeeDrone.Updating;
using Microsoft.Extensions.Options;

string[] hostArgs = ApplyDebugFlag(args);
HostApplicationBuilder builder = Host.CreateApplicationBuilder(hostArgs);

builder.Services
    .AddOptions<DroneOptions>()
    .Bind(builder.Configuration.GetSection(DroneOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<DroneOptions>, DroneOptionsValidator>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IAssetClassificationService, AssetClassificationService>();
builder.Services.AddSingleton<IDeviceInventoryCollector>(_ =>
{
    if (OperatingSystem.IsWindows())
    {
        return ActivatorUtilities.CreateInstance<WindowsInventoryCollector>(_);
    }

    if (OperatingSystem.IsLinux())
    {
        return ActivatorUtilities.CreateInstance<LinuxInventoryCollector>(_);
    }

    if (OperatingSystem.IsMacOS())
    {
        return ActivatorUtilities.CreateInstance<MacOsInventoryCollector>(_);
    }

    throw new PlatformNotSupportedException("AssetBee Drone supports Windows, Linux, and macOS.");
});

builder.Services.AddHttpClient<IInventoryReporter, HttpInventoryReporter>((services, client) =>
{
    DroneOptions options = services.GetRequiredService<IOptions<DroneOptions>>().Value;
    client.BaseAddress = options.Endpoint;
    client.Timeout = options.RequestTimeout;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AssetBee-Drone/1.0");
});
builder.Services.AddHttpClient(CloudDiskEncryptionProbe.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AssetBee-Drone/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    UseProxy = false,
    ConnectTimeout = TimeSpan.FromSeconds(1)
});
builder.Services.AddSingleton<CloudDiskEncryptionProbe>(services =>
{
    IHttpClientFactory factory = services.GetRequiredService<IHttpClientFactory>();
    return new CloudDiskEncryptionProbe(
        factory.CreateClient(CloudDiskEncryptionProbe.HttpClientName),
        services.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<InventoryWorker>();
builder.Services.AddSingleton<IInventorySyncController>(services =>
    services.GetRequiredService<InventoryWorker>());
builder.Services.AddHostedService(services => services.GetRequiredService<InventoryWorker>());

if (!string.IsNullOrWhiteSpace(BuildConstants.UpdateFeedUrl))
{
    builder.Services.AddHttpClient(UpdateFeedClient.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AssetBee-Drone/1.0");
    });
    builder.Services.AddHttpClient(UpdateApplier.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AssetBee-Drone/1.0");
    });
    builder.Services.AddSingleton<IUpdateFeedClient>(services =>
    {
        IHttpClientFactory factory = services.GetRequiredService<IHttpClientFactory>();
        return new UpdateFeedClient(
            factory.CreateClient(UpdateFeedClient.HttpClientName),
            services.GetRequiredService<ILogger<UpdateFeedClient>>());
    });
    builder.Services.AddSingleton<IUpdateApplier>(services =>
    {
        IHttpClientFactory factory = services.GetRequiredService<IHttpClientFactory>();
        return new UpdateApplier(
            factory.CreateClient(UpdateApplier.HttpClientName),
            services.GetRequiredService<IProcessRunner>(),
            services.GetRequiredService<IHostApplicationLifetime>(),
            services.GetRequiredService<ILogger<UpdateApplier>>());
    });
    builder.Services.AddSingleton<IUpdateCoordinator, UpdateCoordinator>();
    builder.Services.AddSingleton<UpdateWorker>();
    builder.Services.AddSingleton<IUpdateCheckController>(services =>
        services.GetRequiredService<UpdateWorker>());
    builder.Services.AddHostedService(services => services.GetRequiredService<UpdateWorker>());
}

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "AssetBee Drone");
    builder.Services.AddHostedService(services =>
        new TrayFileIpcServer(
            services.GetRequiredService<IInventorySyncController>(),
            services.GetService<IUpdateCoordinator>(),
            services.GetService<IUpdateCheckController>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<TrayFileIpcServer>>()));
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSystemd();
}

await builder.Build().RunAsync();

static string[] ApplyDebugFlag(string[] args)
{
    if (!args.Any(argument =>
            argument.Equals("--debug", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("-debug", StringComparison.OrdinalIgnoreCase)))
    {
        return args;
    }

    List<string> rewritten =
    [
        .. args.Where(argument =>
            !argument.Equals("--debug", StringComparison.OrdinalIgnoreCase) &&
            !argument.Equals("-debug", StringComparison.OrdinalIgnoreCase)),
        "--Drone:Debug=true"
    ];
    return [.. rewritten];
}
