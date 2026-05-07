using Weave.Actions.SystemInfo;

namespace Weave.Cli.Commands;

/// <summary>
/// Pilot consumer of the Shape C action layer. Delegates to
/// <see cref="GetSystemInfoAction"/> for the data, then renders the typed
/// result through the shared <see cref="SystemInfoRenderer"/>.
/// </summary>
internal sealed class SystemCliCommand : ICliCommand<NoCliOptions>
{
    private readonly GetSystemInfoAction _action;

    public SystemCliCommand(GetSystemInfoAction action)
    {
        _action = action;
    }

    public string Name => "system";

    public IReadOnlyList<string> Aliases => ["sys"];

    public string Description => "Show silo and CLI config info";

    public async Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        var result = await _action.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!result.IsSuccess)
        {
            CliTheme.WriteError(result.Failure.Message);
            return 1;
        }

        SystemInfoRenderer.Render(result.Value);
        return 0;
    }
}
