namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct WorkspaceIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
