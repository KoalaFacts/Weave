namespace Weave.Workspaces.Runtime;

public sealed class ContainerRuntimeOptions
{
    public const string PodmanEngine = "podman";
    public const string DockerEngine = "docker";

    public string Engine { get; init; } = PodmanEngine;
}
