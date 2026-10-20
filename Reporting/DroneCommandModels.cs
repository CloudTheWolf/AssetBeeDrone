using System.Text.Json;

namespace AssetBeeDrone.Reporting;

public sealed record DroneCommandPackage(
    long Id,
    string Name,
    string Source,
    string? MsiUrl,
    string? MsiArgs,
    string? WingetId,
    string? ChocoId,
    string? SilentArgs,
    string? Sha256);

public sealed record DroneCommand(
    long Id,
    long PushId,
    string Type,
    string Status,
    bool RequiresUserConsent,
    DateTimeOffset? FirstNotifiedAt,
    DateTimeOffset? PostponeDeadlineAt,
    DateTimeOffset? ScheduledFor,
    DroneCommandPackage? Package);

public sealed record DroneCommandStatusUpdate(
    string Status,
    DateTimeOffset? ScheduledFor = null,
    JsonElement? Result = null,
    string? Error = null);

public sealed record DroneCommandVerification(
    bool Authorized,
    string? Reason,
    DroneCommand? Command);
