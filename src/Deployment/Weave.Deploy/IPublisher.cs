using Weave.Workspaces.Manifest;
namespace Weave.Deploy;

public interface IPublisher
{
    string TargetName { get; }
    Task<PublishResult> PublishAsync(WorkspaceManifest manifest, PublishOptions options, CancellationToken ct = default);
}
