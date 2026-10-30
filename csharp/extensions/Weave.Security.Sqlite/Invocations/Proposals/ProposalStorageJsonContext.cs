using System.Text.Json.Serialization;

namespace Weave.Security.Sqlite;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredProposalInput))]
internal sealed partial class ProposalStorageJsonContext : JsonSerializerContext;
