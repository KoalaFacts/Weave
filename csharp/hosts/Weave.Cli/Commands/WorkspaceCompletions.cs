using System.CommandLine.Completions;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceCompletions(IWorkspaceRegistry registry)
{
    public IEnumerable<CompletionItem> CompleteWorkspaceNames(CompletionContext _) =>
        registry.GetNames().Select(n => new CompletionItem(n));
}
