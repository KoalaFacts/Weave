using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[RegisterConverter]
public sealed class InvocationAttemptIdSurrogateConverter : IConverter<InvocationAttemptId, InvocationAttemptIdSurrogate>
{
    public InvocationAttemptId ConvertFromSurrogate(in InvocationAttemptIdSurrogate s)
        => InvocationAttemptId.From(s.Value ?? string.Empty);

    public InvocationAttemptIdSurrogate ConvertToSurrogate(in InvocationAttemptId v)
        => new() { Value = v.Value ?? string.Empty };
}
