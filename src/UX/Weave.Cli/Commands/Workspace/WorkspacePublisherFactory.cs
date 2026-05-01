using Weave.Deploy;
using Weave.Deploy.Translators;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class WorkspacePublisherFactory
{
    public IPublisher Resolve(string target) => target switch
    {
        "docker-compose" => new DockerComposePublisher(),
        "kubernetes" or "k8s" => new KubernetesPublisher(),
        "nomad" => new NomadPublisher(),
        "fly-io" or "fly" => new FlyIoPublisher(),
        "github-actions" or "gh-actions" => new GitHubActionsPublisher(),
        _ => throw new ArgumentException($"Unknown target: {target}")
    };
}
