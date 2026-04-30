namespace Weave.Cli.Commands;

internal interface ICliCommand<in TOptions>
{
    string Name { get; }

    IReadOnlyList<string> Aliases { get; }

    string Description { get; }

    Task<int> ExecuteAsync(TOptions options, CancellationToken ct);
}
