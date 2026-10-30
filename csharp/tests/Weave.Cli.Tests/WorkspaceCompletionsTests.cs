using System.CommandLine.Completions;
using System.CommandLine.Parsing;
using Weave.Cli.Commands;
using Weave.Cli.Shell;

namespace Weave.Cli.Tests;

public sealed class WorkspaceCompletionsTests
{
    [Fact]
    public void CompleteWorkspaceNames_ReturnsRegisteredNames()
    {
        var registry = new StubRegistry { ["alpha"] = "/x/alpha", ["beta"] = "/x/beta" };
        var completions = new WorkspaceCompletions(registry);

        var items = completions.CompleteWorkspaceNames(MakeContext()).ToArray();

        items.Select(i => i.Label).ShouldBe(["alpha", "beta"]);
    }

    [Fact]
    public void CompleteWorkspaceNames_EmptyRegistry_ReturnsEmpty()
    {
        var completions = new WorkspaceCompletions(new StubRegistry());

        completions.CompleteWorkspaceNames(MakeContext()).ShouldBeEmpty();
    }

    private static CompletionContext MakeContext() =>
        CompletionContext.Empty;

    private sealed class StubRegistry : IWorkspaceRegistry
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public string this[string name] { set => _entries[name] = value; }

        public void Register(string name, string absolutePath) => _entries[name] = absolutePath;
        public void Unregister(string name) => _entries.Remove(name);
        public string? Resolve(string name) => _entries.TryGetValue(name, out var path) ? path : null;
        public IReadOnlyDictionary<string, string> GetAll() => _entries;
        public IEnumerable<string> GetNames() => _entries.Keys;
    }
}
