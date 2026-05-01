using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal static class TuiRuntimeProbe
{
    public static async Task<bool> ProbeSiloAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new WorkspaceApiClient();
            return await client.IsReachableAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}
