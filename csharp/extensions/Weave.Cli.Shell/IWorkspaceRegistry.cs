namespace Weave.Cli.Shell;

internal interface IWorkspaceRegistry
{
    void Register(string name, string absolutePath);

    void Unregister(string name);

    string? Resolve(string name);

    IReadOnlyDictionary<string, string> GetAll();

    IEnumerable<string> GetNames();
}
