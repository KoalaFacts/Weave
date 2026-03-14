using Weave.Workspaces.Models;

namespace Weave.Deploy;

public interface IPublisher
{
    string TargetName { get; }
    Task<PublishResult> PublishAsync(WorkspaceManifest manifest, PublishOptions options, CancellationToken ct = default);
}

public sealed record PublishOptions
{
    public string OutputPath { get; init; } = "./output";
    public string? Registry { get; init; }
    public Dictionary<string, string> Variables { get; init; } = [];
}

public sealed record PublishResult
{
    public bool Success { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public List<string> GeneratedFiles { get; init; } = [];
    public string? Error { get; init; }
}
