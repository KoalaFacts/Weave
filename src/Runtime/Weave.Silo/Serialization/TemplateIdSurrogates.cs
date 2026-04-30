using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct TemplateIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class TemplateIdSurrogateConverter : IConverter<TemplateId, TemplateIdSurrogate>
{
    public TemplateId ConvertFromSurrogate(in TemplateIdSurrogate s)
        => TemplateId.From(s.Value ?? string.Empty);

    public TemplateIdSurrogate ConvertToSurrogate(in TemplateId v)
        => new() { Value = v.Value ?? string.Empty };
}
