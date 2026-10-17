using System.Text.Json.Serialization;

namespace W154Stream;

public readonly record struct PlayRequest(string Name);

public readonly record struct StatusDto(
    string? File,
    bool Joined,
    int FrameSent,
    uint FreeBytes,
    bool Paused,
    bool Underrun,
    uint FileToken);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FileItem[]))]
[JsonSerializable(typeof(PlayRequest))]
[JsonSerializable(typeof(StatusDto))]
sealed partial class AppJsonContext : JsonSerializerContext;
