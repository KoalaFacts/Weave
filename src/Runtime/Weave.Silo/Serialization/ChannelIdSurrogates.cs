namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct ChannelIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
