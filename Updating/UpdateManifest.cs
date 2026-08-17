using System.Text.Json.Serialization;

namespace AssetBeeDrone.Updating;

public sealed record UpdateManifest(
    string Version,
    IReadOnlyList<UpdatePackage> Packages);

public sealed record UpdatePackage(
    string Rid,
    string FileName,
    string Sha256,
    string? Url = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpdateManifest))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
