namespace Weave.Actions.Context;

/// <summary>
/// Reports progress and outcome messages from inside an action. Frontend-supplied:
/// the CLI implementation routes through Spectre/CliTheme; a TUI implementation
/// can drive a status spinner; test fakes record into a list for assertions.
/// </summary>
public interface IActionReporter
{
    void Info(string message);
    void Success(string message);
    void Warning(string message);
    void Error(string message);
}
