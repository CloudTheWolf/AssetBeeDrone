using System.Text.Json.Serialization;

namespace AssetBeeDrone.Infrastructure;

public sealed record TrayStatusResponse(
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    bool Running,
    bool Busy,
    string? Message = null,
    DateTimeOffset? ServiceAliveUtc = null,
    bool UpdateAvailable = false,
    string? UpdateVersion = null,
    string? UpdateState = null,
    string? UpdateError = null,
    bool QuitTray = false,
    string? ServiceVersion = null,
    DateTimeOffset? LastUpdateCheckUtc = null,
    string? LastUpdateCheckMessage = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TrayStatusResponse))]
public sealed partial class TrayJsonContext : JsonSerializerContext;
