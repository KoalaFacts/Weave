using Weave.Deploy;
using Weave.Deploy.Translators;

namespace Weave.Cli.Commands;

internal static class WorkspacePublisherFactory
{
    public static IPublisher Resolve(string target) => target switch
    {
        "docker-compose" => new DockerComposePublisher(),
        "kubernetes" or "k8s" => new KubernetesPublisher(),
        "nomad" => new NomadPublisher(),
        "fly-io" or "fly" => new FlyIoPublisher(),
        "github-actions" or "gh-actions" => new GitHubActionsPublisher(),
        _ => throw new ArgumentException($"Unknown target: {target}")
    };
}
