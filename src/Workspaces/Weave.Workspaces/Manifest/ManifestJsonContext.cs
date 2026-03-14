using System.Text.Json;
using System.Text.Json.Serialization;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Manifest;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(WorkspaceManifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
