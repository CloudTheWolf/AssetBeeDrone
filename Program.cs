using AssetBeeDrone.Collectors;
using AssetBeeDrone.Collectors.Linux;
using AssetBeeDrone.Collectors.MacOS;
using AssetBeeDrone.Collectors.Windows;
using AssetBeeDrone.Configuration;
using AssetBeeDrone.Infrastructure;
using AssetBeeDrone.Reporting;
using AssetBeeDrone.Services;
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
builder.Services.AddHostedService<InventoryWorker>();

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "AssetBee Drone");
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
