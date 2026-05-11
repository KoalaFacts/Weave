using Microsoft.Extensions.DependencyInjection;

namespace Weave.Cli.Tui;

/// <summary>
/// Interactive REPL over the CLI primitives. Persistent prompt at the
/// bottom of the terminal, slash commands for workspace actions,
/// free-form text is sent to the currently-selected agent.
/// </summary>
internal static class TuiApp
{
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        return await services.GetRequiredService<TuiShell>().RunAsync(cancellationToken);
    }
}
