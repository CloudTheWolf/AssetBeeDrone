using System.ComponentModel.DataAnnotations;

namespace AssetBeeDrone.Configuration;

public sealed class DroneOptions
{
    public const string SectionName = "Drone";

    [Required]
    public Uri Endpoint { get; set; } = new("https://localhost:8000/api/v1/inventory");

    [Range(1, 10080)]
    public int CollectionIntervalMinutes { get; set; } = 3600;

    [Range(5, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    public string? BearerToken { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional asset classification override: hardware or virtualware.
    /// When omitted, the service detects virtualization automatically.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// When true, writes the outbound inventory JSON to <see cref="DebugOutputPath"/>
    /// before posting. The file includes BitLocker recovery keys when present.
    /// </summary>
    public bool Debug { get; set; }

    /// <summary>
    /// Destination for the debug JSON dump. Relative paths are resolved from the
    /// process working directory. Defaults to inventory-debug.json.
    /// </summary>
    public string DebugOutputPath { get; set; } = "inventory-debug.json";

    /// <summary>
    /// When true, generates a CycloneDX-style SBOM for the host OS packages.
    /// </summary>
    public bool IncludeSbom { get; set; } = true;

    /// <summary>
    /// When true on Linux, also generates SBOMs for running Docker containers.
    /// </summary>
    public bool IncludeContainerSboms { get; set; } = true;

    public TimeSpan CollectionInterval => TimeSpan.FromMinutes(CollectionIntervalMinutes);

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);
}
