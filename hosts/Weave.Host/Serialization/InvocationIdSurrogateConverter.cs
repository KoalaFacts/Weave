using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[RegisterConverter]
public sealed class InvocationIdSurrogateConverter : IConverter<InvocationId, InvocationIdSurrogate>
{
    public InvocationId ConvertFromSurrogate(in InvocationIdSurrogate s)
        => InvocationId.From(s.Value ?? string.Empty);

    public InvocationIdSurrogate ConvertToSurrogate(in InvocationId v)
        => new() { Value = v.Value ?? string.Empty };
}
