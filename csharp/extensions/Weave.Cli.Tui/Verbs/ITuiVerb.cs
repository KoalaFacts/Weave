namespace Weave.Cli.Tui.Verbs;

/// <summary>
/// One slash-command verb. Implementations are DI-registered as
/// <see cref="ITuiVerb"/>; the dispatcher resolves them by name+aliases
/// instead of carrying a typed field per verb.
/// </summary>
internal interface ITuiVerb
{
    string Name { get; }

    IReadOnlyList<string> Aliases { get; }

    Task DispatchAsync(TuiVerbContext context, CancellationToken ct);
}
