using System.Security.Cryptography;
using AssetBeeDrone.Reporting;

namespace AssetBeeDrone.Infrastructure;

public sealed record PackageInstallResult(bool Succeeded, int ExitCode, string Message);

public interface IPackageInstaller
{
    Task<PackageInstallResult> InstallAsync(
        DroneCommandPackage package,
        CancellationToken cancellationToken);

    IReadOnlyList<string> BuildArgumentList(DroneCommandPackage package, string? msiLocalPath = null);
}

public sealed class WindowsPackageInstaller(
    IProcessRunner processRunner,
    IHttpClientFactory httpClientFactory,
    ILogger<WindowsPackageInstaller> logger) : IPackageInstaller
{
    public const string HttpClientName = "PackageDownload";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    public IReadOnlyList<string> BuildArgumentList(
        DroneCommandPackage package,
        string? msiLocalPath = null)
    {
        return package.Source.ToLowerInvariant() switch
        {
            "winget" =>
            [
                "install", "-e", "--id", package.WingetId ?? string.Empty,
                "--silent", "--accept-package-agreements", "--accept-source-agreements",
                .. SplitArgs(package.SilentArgs)
            ],
            "choco" =>
            [
                "install", package.ChocoId ?? string.Empty, "-y",
                .. SplitArgs(package.SilentArgs)
            ],
            "msi" =>
            [
                "/i", msiLocalPath ?? "package.msi", "/qn", "/norestart",
                .. SplitArgs(package.MsiArgs),
                .. SplitArgs(package.SilentArgs)
            ],
            _ => []
        };
    }

    public async Task<PackageInstallResult> InstallAsync(
        DroneCommandPackage package,
        CancellationToken cancellationToken)
    {
        try
        {
            return package.Source.ToLowerInvariant() switch
            {
                "winget" => await RunAsync(
                    "winget",
                    BuildArgumentList(package),
                    cancellationToken),
                "choco" => await RunAsync(
                    "choco",
                    BuildArgumentList(package),
                    cancellationToken),
                "msi" => await InstallMsiAsync(package, cancellationToken),
                _ => new PackageInstallResult(false, -1, $"Unsupported package source '{package.Source}'.")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Package install failed for {Package}", package.Name);
            return new PackageInstallResult(false, -1, exception.Message);
        }
    }

    private async Task<PackageInstallResult> InstallMsiAsync(
        DroneCommandPackage package,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(package.MsiUrl) ||
            !Uri.TryCreate(package.MsiUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return new PackageInstallResult(false, -1, "MSI URL must be HTTPS.");
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"assetbee-{Guid.NewGuid():N}.msi");
        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            await using (Stream download = await client.GetStreamAsync(uri, cancellationToken))
            await using (FileStream file = File.Create(tempPath))
            {
                await download.CopyToAsync(file, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(package.Sha256))
            {
                await using FileStream hashStream = File.OpenRead(tempPath);
                byte[] hash = await SHA256.HashDataAsync(hashStream, cancellationToken);
                string hex = Convert.ToHexString(hash).ToLowerInvariant();
                if (!hex.Equals(package.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return new PackageInstallResult(false, -1, "MSI SHA-256 mismatch.");
                }
            }

            return await RunAsync("msiexec.exe", BuildArgumentList(package, tempPath), cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<PackageInstallResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            fileName,
            arguments,
            InstallTimeout,
            cancellationToken);

        string message = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        if (message.Length > 1000)
        {
            message = message[..1000];
        }

        if (result.TimedOut)
        {
            return new PackageInstallResult(false, -1, $"{fileName} timed out.");
        }

        return new PackageInstallResult(
            result.ExitCode == 0,
            result.ExitCode,
            string.IsNullOrWhiteSpace(message) ? $"Exit code {result.ExitCode}" : message);
    }

    private static IEnumerable<string> SplitArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            yield break;
        }

        foreach (string part in args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }
}

public sealed class UnsupportedPackageInstaller : IPackageInstaller
{
    public Task<PackageInstallResult> InstallAsync(
        DroneCommandPackage package,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PackageInstallResult(false, -1, "Package installs are only supported on Windows."));

    public IReadOnlyList<string> BuildArgumentList(
        DroneCommandPackage package,
        string? msiLocalPath = null) =>
        [];
}
