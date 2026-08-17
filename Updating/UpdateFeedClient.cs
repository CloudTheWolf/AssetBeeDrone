using System.Runtime.InteropServices;
using System.Text.Json;

namespace AssetBeeDrone.Updating;

public interface IUpdateFeedClient
{
    Task<UpdateManifest?> FetchManifestAsync(CancellationToken cancellationToken);
}

public sealed class UpdateFeedClient : IUpdateFeedClient
{
    public const string HttpClientName = "AssetBeeDrone.AutoUpdate.Feed";

    private readonly HttpClient _httpClient;
    private readonly ILogger<UpdateFeedClient> _logger;
    private readonly string _feedUrl;

    public UpdateFeedClient(HttpClient httpClient, ILogger<UpdateFeedClient> logger)
        : this(httpClient, logger, BuildConstants.UpdateFeedUrl)
    {
    }

    public UpdateFeedClient(
        HttpClient httpClient,
        ILogger<UpdateFeedClient> logger,
        string feedUrl)
    {
        _httpClient = httpClient;
        _logger = logger;
        _feedUrl = feedUrl;
    }

    public async Task<UpdateManifest?> FetchManifestAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_feedUrl))
        {
            return null;
        }

        Uri manifestUri = ResolveManifestUri(_feedUrl);
        _logger.LogDebug("Fetching update manifest from {ManifestUrl}", manifestUri);

        using HttpResponseMessage response = await _httpClient.GetAsync(
            manifestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        UpdateManifest? manifest = await JsonSerializer.DeserializeAsync(
            stream,
            UpdateJsonContext.Default.UpdateManifest,
            cancellationToken);

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException(
                $"Update manifest at {manifestUri} is missing a version.");
        }

        if (manifest.Packages is null || manifest.Packages.Count == 0)
        {
            throw new InvalidOperationException(
                $"Update manifest at {manifestUri} contains no packages.");
        }

        return manifest;
    }

    internal static Uri ResolveManifestUri(string feedUrl)
    {
        string trimmed = feedUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                "UpdateFeedUrl must be an absolute http(s) URL.");
        }

        if (uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        string basePath = uri.AbsolutePath.EndsWith('/')
            ? uri.AbsolutePath
            : uri.AbsolutePath + "/";
        return new Uri(uri, basePath + "latest.json");
    }
}

public static class UpdatePackageSelector
{
    public static UpdatePackage? Select(UpdateManifest manifest, string? runtimeIdentifier = null)
    {
        string rid = runtimeIdentifier ?? ResolveRuntimeIdentifier();
        IEnumerable<UpdatePackage> matching = manifest.Packages
            .Where(package => package.Rid.Equals(rid, StringComparison.OrdinalIgnoreCase));

        if (OperatingSystem.IsWindows())
        {
            return matching.FirstOrDefault(package =>
                package.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsMacOS())
        {
            return matching.FirstOrDefault(package =>
                package.FileName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsLinux())
        {
            bool preferDeb = HasExecutable("dpkg");
            bool preferRpm = HasExecutable("rpm");
            if (preferDeb)
            {
                UpdatePackage? deb = matching.FirstOrDefault(package =>
                    package.FileName.EndsWith(".deb", StringComparison.OrdinalIgnoreCase));
                if (deb is not null)
                {
                    return deb;
                }
            }

            if (preferRpm)
            {
                UpdatePackage? rpm = matching.FirstOrDefault(package =>
                    package.FileName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase));
                if (rpm is not null)
                {
                    return rpm;
                }
            }

            return matching.FirstOrDefault(package =>
                package.FileName.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) ||
                package.FileName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    public static string ResolveRuntimeIdentifier()
    {
        string? rid = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrWhiteSpace(rid))
        {
            return rid;
        }

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        if (OperatingSystem.IsWindows())
        {
            return $"win-{arch}";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"osx-{arch}";
        }

        if (OperatingSystem.IsLinux())
        {
            return $"linux-{arch}";
        }

        throw new PlatformNotSupportedException(
            "Cannot resolve runtime identifier for auto-update.");
    }

    private static bool HasExecutable(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return File.Exists($"/usr/bin/{name}") || File.Exists($"/bin/{name}");
        }

        foreach (string directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return File.Exists($"/usr/bin/{name}") || File.Exists($"/bin/{name}");
    }
}
