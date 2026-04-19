using Orleans;
using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

/// <summary>
/// Marker used with <c>AddSerializer(s =&gt; s.AddAssembly(typeof(SerializationMarker).Assembly))</c>
/// so Orleans definitively scans this assembly for <c>[RegisterConverter]</c>
/// types. Not strictly required once <c>Microsoft.Orleans.Sdk</c> emits the
/// <c>[ApplicationPart]</c> attribute on the host assembly, but it's cheap
/// insurance.
/// </summary>
public static class SerializationMarker { }

// ── WorkspaceId ─────────────────────────────────────────────────

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

// ── AgentId ─────────────────────────────────────────────────────

[GenerateSerializer]
public struct AgentIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class AgentIdSurrogateConverter : IConverter<AgentId, AgentIdSurrogate>
{
    public AgentId ConvertFromSurrogate(in AgentIdSurrogate s)
        => AgentId.From(s.Value ?? string.Empty);

    public AgentIdSurrogate ConvertToSurrogate(in AgentId v)
        => new() { Value = v.Value ?? string.Empty };
}

// ── AgentTaskId ─────────────────────────────────────────────────

[GenerateSerializer]
public struct AgentTaskIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class AgentTaskIdSurrogateConverter : IConverter<AgentTaskId, AgentTaskIdSurrogate>
{
    public AgentTaskId ConvertFromSurrogate(in AgentTaskIdSurrogate s)
        => AgentTaskId.From(s.Value ?? string.Empty);

    public AgentTaskIdSurrogate ConvertToSurrogate(in AgentTaskId v)
        => new() { Value = v.Value ?? string.Empty };
}

// ── ContainerId ─────────────────────────────────────────────────

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

// ── NetworkId ───────────────────────────────────────────────────

[GenerateSerializer]
public struct NetworkIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class NetworkIdSurrogateConverter : IConverter<NetworkId, NetworkIdSurrogate>
{
    public NetworkId ConvertFromSurrogate(in NetworkIdSurrogate s)
        => NetworkId.From(s.Value ?? string.Empty);

    public NetworkIdSurrogate ConvertToSurrogate(in NetworkId v)
        => new() { Value = v.Value ?? string.Empty };
}

// ── SkillId ─────────────────────────────────────────────────────

[GenerateSerializer]
public struct SkillIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class SkillIdSurrogateConverter : IConverter<SkillId, SkillIdSurrogate>
{
    public SkillId ConvertFromSurrogate(in SkillIdSurrogate s)
        => SkillId.From(s.Value ?? string.Empty);

    public SkillIdSurrogate ConvertToSurrogate(in SkillId v)
        => new() { Value = v.Value ?? string.Empty };
}

// ── ChannelId ───────────────────────────────────────────────────

[GenerateSerializer]
public struct ChannelIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class ChannelIdSurrogateConverter : IConverter<ChannelId, ChannelIdSurrogate>
{
    public ChannelId ConvertFromSurrogate(in ChannelIdSurrogate s)
        => ChannelId.From(s.Value ?? string.Empty);

    public ChannelIdSurrogate ConvertToSurrogate(in ChannelId v)
        => new() { Value = v.Value ?? string.Empty };
}

// ── UserId ──────────────────────────────────────────────────────

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

// ── MarketplaceItemId ───────────────────────────────────────────

[GenerateSerializer]
public struct MarketplaceItemIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class MarketplaceItemIdSurrogateConverter : IConverter<MarketplaceItemId, MarketplaceItemIdSurrogate>
{
    public MarketplaceItemId ConvertFromSurrogate(in MarketplaceItemIdSurrogate s)
        => MarketplaceItemId.From(s.Value ?? string.Empty);

    public MarketplaceItemIdSurrogate ConvertToSurrogate(in MarketplaceItemId v)
        => new() { Value = v.Value ?? string.Empty };
}

// ── TemplateId ──────────────────────────────────────────────────

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
