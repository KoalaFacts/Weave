namespace Weave.Cli.Tests;

internal sealed class RecordingWorkspaceRegistry : IWorkspaceRegistry
{
    public Dictionary<string, string> Registrations { get; } = [];

    public void Register(string name, string absolutePath) => Registrations.Add(name, absolutePath);
    public void Unregister(string name) => Registrations.Remove(name);
    public string? Resolve(string name) => Registrations.GetValueOrDefault(name);
    public IReadOnlyDictionary<string, string> GetAll() => Registrations;
    public IEnumerable<string> GetNames() => Registrations.Keys;
}
