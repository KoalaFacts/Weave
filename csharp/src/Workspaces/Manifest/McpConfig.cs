namespace Weave.Workspaces.Manifest;

public sealed record McpConfig
{
    public string? Server { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = [];
    public string? Url { get; init; }

    // Security / resource limits for HTTP transport. Defaults err on the
    // side of "the peer is hostile" — call sites that need looser bounds
    // (long-running streaming tools, local-dev loopback) must opt in.
    public int RequestTimeoutSeconds { get; init; } = 300;
    public int IdleTimeoutSeconds { get; init; } = 30;
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxFrameBytes { get; init; } = 1 * 1024 * 1024;
    public int MaxQueuedFrames { get; init; } = 1024;
    public bool AllowPrivateEndpoints { get; init; }
}
