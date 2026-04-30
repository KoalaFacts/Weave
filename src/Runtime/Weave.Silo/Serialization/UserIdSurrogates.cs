using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct UserIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class UserIdSurrogateConverter : IConverter<UserId, UserIdSurrogate>
{
    public UserId ConvertFromSurrogate(in UserIdSurrogate s)
        => UserId.From(s.Value ?? string.Empty);

    public UserIdSurrogate ConvertToSurrogate(in UserId v)
        => new() { Value = v.Value ?? string.Empty };
}
