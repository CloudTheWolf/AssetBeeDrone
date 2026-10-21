using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBeeDrone.Reporting;

public sealed record DroneDeviceIdentityRequest(string? SerialNumber, string? DeviceName);

public sealed record DroneCommandStatusRequest(
    string Status,
    DateTimeOffset? ScheduledFor,
    JsonElement? Result,
    string? Error);

public sealed record PackageInstallStatusResult(int? ExitCode, string? Message);

public sealed record PackageInstallItemResult(string Name, bool Succeeded, int? ExitCode, string? Message);

public sealed record MultiPackageInstallStatusResult(
    bool Succeeded,
    IReadOnlyList<PackageInstallItemResult> Packages);

public sealed record ProbeUnavailableResult(string Status, string? Detail);

public sealed record BitLockerVolumeStatusResult(
    string Volume,
    string? Technology,
    string? State,
    string? ProtectionStatus,
    bool HasRecoveryKey);

public sealed record BitLockerStatusResult(IReadOnlyList<BitLockerVolumeStatusResult> Volumes);

public sealed record WindowsUpdateItemStatusResult(
    string? Id,
    string? Title,
    string? Category,
    string? KbArticle);

public sealed record WindowsUpdateStatusResult(
    int InstalledCount,
    int AvailableCount,
    IReadOnlyList<WindowsUpdateItemStatusResult> Available);

public sealed record UnsupportedCommandResult(string Detail);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DroneDeviceIdentityRequest))]
[JsonSerializable(typeof(DroneCommandStatusRequest))]
[JsonSerializable(typeof(PackageInstallStatusResult))]
[JsonSerializable(typeof(PackageInstallItemResult))]
[JsonSerializable(typeof(MultiPackageInstallStatusResult))]
[JsonSerializable(typeof(IReadOnlyList<PackageInstallItemResult>))]
[JsonSerializable(typeof(ProbeUnavailableResult))]
[JsonSerializable(typeof(BitLockerVolumeStatusResult))]
[JsonSerializable(typeof(BitLockerStatusResult))]
[JsonSerializable(typeof(IReadOnlyList<BitLockerVolumeStatusResult>))]
[JsonSerializable(typeof(WindowsUpdateItemStatusResult))]
[JsonSerializable(typeof(WindowsUpdateStatusResult))]
[JsonSerializable(typeof(IReadOnlyList<WindowsUpdateItemStatusResult>))]
[JsonSerializable(typeof(UnsupportedCommandResult))]
internal sealed partial class DroneCommandJsonContext : JsonSerializerContext;
