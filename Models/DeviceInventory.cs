using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBeeDrone.Models;

internal sealed class ProbeStatusConverter()
    : JsonStringEnumConverter<ProbeStatus>(JsonNamingPolicy.CamelCase);

[JsonConverter(typeof(ProbeStatusConverter))]
public enum ProbeStatus
{
    Available,
    Unavailable,
    Unsupported,
    AccessDenied,
    Error
}

public sealed record ProbeValue<T>(ProbeStatus Status, T? Value = default, string? Detail = null)
{
    public static ProbeValue<T> Available(T value) => new(ProbeStatus.Available, value);
    public static ProbeValue<T> Unavailable(string detail) => new(ProbeStatus.Unavailable, default, detail);
    public static ProbeValue<T> Unsupported(string detail) => new(ProbeStatus.Unsupported, default, detail);
    public static ProbeValue<T> Error(string detail) => new(ProbeStatus.Error, default, detail);
}

public sealed record CpuInfo(string Model, int LogicalProcessors, int? PhysicalCores = null);

public sealed record MemoryInfo(ulong TotalBytes, ulong? AvailableBytes = null);

public sealed record DiskInfo(
    string Name,
    string? MountPoint,
    ulong TotalBytes,
    ulong? AvailableBytes,
    string? FileSystem);

public sealed record BitLockerKeyProtector(
    string KeyProtectorId,
    string? RecoveryKey);

public sealed record EncryptionVolume(
    string Volume,
    string Technology,
    string State,
    IReadOnlyList<string>? RecoveryKeys = null,
    IReadOnlyList<BitLockerKeyProtector>? KeyProtectors = null);

public sealed record OperatingSystemInfo(
    string Name,
    string Version,
    string? DisplayVersion = null,
    string? Build = null,
    string? Kernel = null);

public sealed record SoftwareUpdate(
    string Id,
    string Title,
    string? Category = null,
    DateTimeOffset? InstalledAtUtc = null,
    string? KbArticle = null);

public sealed record UpdateInventory(
    IReadOnlyList<SoftwareUpdate> Installed,
    IReadOnlyList<SoftwareUpdate> Available);

public sealed record SbomComponent(
    string Name,
    string? Version = null,
    string Type = "library",
    string? Purl = null,
    string? Publisher = null);

public sealed record SbomTarget(
    string BomRef,
    string Kind,
    string Name,
    IReadOnlyList<SbomComponent> Components,
    string? Version = null,
    string? Image = null,
    string? ContainerId = null,
    string? Detail = null);

public sealed record SbomInventory(
    string Format,
    string SpecVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<SbomTarget> Targets);

public sealed record DomainWorkspaceInfo(
    string? Domain,
    bool? DomainJoined,
    string? Workspace,
    bool? WorkspaceJoined);

public sealed record LoginProviderInfo(string Name, string State, string? Detail = null);

public sealed record AntivirusInfo(
    string Name,
    string State,
    bool? Enabled = null,
    bool? UpToDate = null,
    string? Detail = null);

public sealed record DeviceInventory(
    string SchemaVersion,
    DateTimeOffset CollectedAtUtc,
    string Platform,
    ProbeValue<string> DeviceName,
    ProbeValue<string> SerialNumber,
    ProbeValue<string> Manufacturer,
    ProbeValue<string> Model,
    ProbeValue<OperatingSystemInfo> OperatingSystem,
    ProbeValue<CpuInfo> Cpu,
    ProbeValue<MemoryInfo> Memory,
    ProbeValue<IReadOnlyList<DiskInfo>> Disks,
    ProbeValue<IReadOnlyList<EncryptionVolume>> DiskEncryption,
    ProbeValue<DomainWorkspaceInfo> DomainWorkspace,
    ProbeValue<IReadOnlyList<LoginProviderInfo>> LoginProviders,
    ProbeValue<IReadOnlyList<AntivirusInfo>> Antivirus,
    ProbeValue<UpdateInventory> Updates,
    ProbeValue<SbomInventory> Sbom)
{
    public string Type { get; init; } = "hardware";

    public ProbeValue<string> HardwareType { get; init; } =
        ProbeValue<string>.Unavailable("Hardware form factor has not been classified.");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeviceInventory))]
internal sealed partial class InventoryJsonContext : JsonSerializerContext;
