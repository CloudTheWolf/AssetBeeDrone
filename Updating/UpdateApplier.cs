using System.Diagnostics;
using System.Security.Cryptography;
using AssetBeeDrone.Infrastructure;

namespace AssetBeeDrone.Updating;

public interface IUpdateApplier
{
    Task ApplyAsync(UpdateManifest manifest, UpdatePackage package, CancellationToken cancellationToken);

    Task<string> DownloadAndVerifyAsync(
        UpdateManifest manifest,
        UpdatePackage package,
        CancellationToken cancellationToken);

    Task InstallDownloadedAsync(string packagePath, CancellationToken cancellationToken);
}

public sealed class UpdateApplier(
    HttpClient httpClient,
    IProcessRunner processRunner,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateApplier> logger) : IUpdateApplier
{
    public const string HttpClientName = "AssetBeeDrone.AutoUpdate.Download";

    public async Task ApplyAsync(
        UpdateManifest manifest,
        UpdatePackage package,
        CancellationToken cancellationToken)
    {
        string packagePath = await DownloadAndVerifyAsync(manifest, package, cancellationToken);
        await InstallDownloadedAsync(packagePath, cancellationToken);
    }

    public async Task<string> DownloadAndVerifyAsync(
        UpdateManifest manifest,
        UpdatePackage package,
        CancellationToken cancellationToken)
    {
        Uri downloadUri = ResolvePackageUri(manifest, package);
        string workDirectory = Path.Combine(
            Path.GetTempPath(),
            "AssetBeeDrone",
            "updates",
            manifest.Version);
        Directory.CreateDirectory(workDirectory);
        string packagePath = Path.Combine(workDirectory, package.FileName);

        logger.LogInformation(
            "Downloading update {Version} package {FileName} from {Url}",
            manifest.Version,
            package.FileName,
            downloadUri);

        await DownloadAsync(downloadUri, packagePath, cancellationToken);
        VerifySha256(packagePath, package.Sha256);
        return packagePath;
    }

    public async Task InstallDownloadedAsync(string packagePath, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Applying update via {FileName}; the service will restart after install",
            Path.GetFileName(packagePath));

        await InstallAsync(packagePath, cancellationToken);

        // Installer scripts stop/replace this process. Request a clean host shutdown
        // in case the package manager leaves us running briefly.
        lifetime.StopApplication();
    }

    internal static Uri ResolvePackageUri(UpdateManifest manifest, UpdatePackage package)
    {
        if (!string.IsNullOrWhiteSpace(package.Url))
        {
            if (!Uri.TryCreate(package.Url, UriKind.Absolute, out Uri? absolute))
            {
                throw new InvalidOperationException(
                    $"Package URL for {package.FileName} is not an absolute URI.");
            }

            return absolute;
        }

        string feedUrl = BuildConstants.UpdateFeedUrl;
        Uri manifestUri = UpdateFeedClient.ResolveManifestUri(feedUrl);
        return new Uri(manifestUri, package.FileName);
    }

    private async Task DownloadAsync(
        Uri downloadUri,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream destination = new(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 82_000,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private void VerifySha256(string packagePath, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
        {
            throw new InvalidOperationException("Update package is missing a SHA-256 checksum.");
        }

        string normalizedExpected = expectedHex.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        using FileStream stream = File.OpenRead(packagePath);
        byte[] hash = SHA256.HashData(stream);
        string actual = Convert.ToHexString(hash);

        if (!actual.Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SHA-256 mismatch for {Path.GetFileName(packagePath)}: " +
                $"expected {normalizedExpected}, actual {actual}.");
        }

        logger.LogDebug("SHA-256 verified for {PackagePath}", packagePath);
    }

    private async Task InstallAsync(string packagePath, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(packagePath);

        if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            // Detach so msiexec can stop/replace this Windows Service.
            StartDetached("msiexec", $"/i \"{packagePath}\" /qn /norestart");
            return;
        }

        if (extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            ProcessResult result = await processRunner.RunAsync(
                "installer",
                ["-pkg", packagePath, "-target", "/"],
                TimeSpan.FromMinutes(15),
                cancellationToken);
            EnsureSucceeded("installer", result);
            return;
        }

        if (extension.Equals(".deb", StringComparison.OrdinalIgnoreCase))
        {
            ProcessResult result = await processRunner.RunAsync(
                "dpkg",
                ["-i", packagePath],
                TimeSpan.FromMinutes(15),
                cancellationToken);
            EnsureSucceeded("dpkg", result);
            return;
        }

        if (extension.Equals(".rpm", StringComparison.OrdinalIgnoreCase))
        {
            ProcessResult result = await processRunner.RunAsync(
                "rpm",
                ["-Uvh", packagePath],
                TimeSpan.FromMinutes(15),
                cancellationToken);
            EnsureSucceeded("rpm", result);
            return;
        }

        throw new PlatformNotSupportedException(
            $"Unsupported update package type: {extension}");
    }

    private static void EnsureSucceeded(string tool, ProcessResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        string detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        throw new InvalidOperationException(
            $"{tool} failed with exit code {result.ExitCode}: {detail.Trim()}");
    }

    private static void StartDetached(string fileName, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }
    }
}
