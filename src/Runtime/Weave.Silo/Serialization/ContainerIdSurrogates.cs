using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct ContainerIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class ContainerIdSurrogateConverter : IConverter<ContainerId, ContainerIdSurrogate>
{
    public ContainerId ConvertFromSurrogate(in ContainerIdSurrogate s)
        => ContainerId.From(s.Value ?? string.Empty);

    public ContainerIdSurrogate ConvertToSurrogate(in ContainerId v)
        => new() { Value = v.Value ?? string.Empty };
}
