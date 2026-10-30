namespace Weave.Cli.Commands.Local;

internal sealed record LocalDeployment(string HostPath, string DocumentsPath, string Workspace, int Port)
{
    public string Origin => $"http://127.0.0.1:{Port}";
}
