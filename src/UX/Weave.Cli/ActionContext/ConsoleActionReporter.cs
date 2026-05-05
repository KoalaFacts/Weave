using Weave.Actions.Context;
using Weave.Cli.Commands;

namespace Weave.Cli.ActionContext;

/// <summary>
/// CLI binding for <see cref="IActionReporter"/>: routes through
/// <see cref="CliTheme"/> so action messages match the rest of the CLI palette.
/// </summary>
internal sealed class ConsoleActionReporter : IActionReporter
{
    public void Info(string message) => CliTheme.WriteInfo(message);

    public void Success(string message) => CliTheme.WriteSuccess(message);

    public void Warning(string message) => CliTheme.WriteWarning(message);

    public void Error(string message) => CliTheme.WriteError(message);
}
