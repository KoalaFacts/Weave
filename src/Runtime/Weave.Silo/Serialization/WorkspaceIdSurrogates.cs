using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct WorkspaceIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class WorkspaceIdSurrogateConverter : IConverter<WorkspaceId, WorkspaceIdSurrogate>
{
    public WorkspaceId ConvertFromSurrogate(in WorkspaceIdSurrogate s)
        => WorkspaceId.From(s.Value ?? string.Empty);

    public WorkspaceIdSurrogate ConvertToSurrogate(in WorkspaceId v)
        => new() { Value = v.Value ?? string.Empty };
}
