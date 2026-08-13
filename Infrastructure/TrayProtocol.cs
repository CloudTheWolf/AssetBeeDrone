using System.Text.Json.Serialization;

namespace AssetBeeDrone.Infrastructure;

public sealed record TrayCommand(string Op);

public sealed record TrayStatusResponse(
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    bool Running,
    bool Busy,
    string? Message = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TrayCommand))]
[JsonSerializable(typeof(TrayStatusResponse))]
public sealed partial class TrayJsonContext : JsonSerializerContext;

