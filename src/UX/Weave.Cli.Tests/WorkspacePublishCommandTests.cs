using Weave.Cli.Commands;
using Weave.Deploy.Translators;

namespace Weave.Cli.Tests;

public sealed class WorkspacePublishCommandTests
{
    // ── Target resolution ──────────────────────────────────────────

    [Theory]
    [InlineData("docker-compose", typeof(DockerComposePublisher))]
    [InlineData("kubernetes", typeof(KubernetesPublisher))]
    [InlineData("k8s", typeof(KubernetesPublisher))]
    [InlineData("nomad", typeof(NomadPublisher))]
    [InlineData("fly-io", typeof(FlyIoPublisher))]
    [InlineData("fly", typeof(FlyIoPublisher))]
    [InlineData("github-actions", typeof(GitHubActionsPublisher))]
    [InlineData("gh-actions", typeof(GitHubActionsPublisher))]
    public void ResolvePublisher_ValidTarget_ReturnsCorrectType(string target, Type expectedType)
    {
        var publisher = WorkspacePublisherFactory.Resolve(target);
        publisher.ShouldBeOfType(expectedType);
    }

    [Theory]
    [InlineData("docker-compose", "docker-compose")]
    [InlineData("kubernetes", "kubernetes")]
    [InlineData("k8s", "kubernetes")]
    [InlineData("nomad", "nomad")]
    [InlineData("fly-io", "fly-io")]
    [InlineData("fly", "fly-io")]
    [InlineData("github-actions", "github-actions")]
    [InlineData("gh-actions", "github-actions")]
    public void ResolvePublisher_TargetName_MatchesExpected(string target, string expectedName)
    {
        var publisher = WorkspacePublisherFactory.Resolve(target);
        publisher.TargetName.ShouldBe(expectedName);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("docker")]
    [InlineData("aws")]
    [InlineData("")]
    public void ResolvePublisher_UnknownTarget_ThrowsArgumentException(string target)
    {
        var ex = Should.Throw<ArgumentException>(() => WorkspacePublisherFactory.Resolve(target));
        ex.Message.ShouldContain("Unknown target");
    }

    // ── Command structure ──────────────────────────────────────────

    [Fact]
    public void Create_ReturnsCommandWithExpectedName()
    {
        var cmd = WorkspacePublishCommand.Create();
        cmd.Name.ShouldBe("publish");
    }
}
