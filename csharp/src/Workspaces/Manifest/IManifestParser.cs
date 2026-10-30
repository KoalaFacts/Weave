namespace Weave.Workspaces.Manifest;

public interface IManifestParser
{
    WorkspaceManifest Parse(string json);
    WorkspaceManifest ParseFile(string path);
    string Serialize(WorkspaceManifest manifest);
    IReadOnlyList<string> Validate(WorkspaceManifest manifest);
}
