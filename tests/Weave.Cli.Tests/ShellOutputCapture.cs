using System.Globalization;
using Spectre.Console;

namespace Weave.Cli.Tests;

// Callers belong to ShellConsoleGroup so the process-global console cannot overlap other tests.
internal sealed class ShellOutputCapture : IDisposable
{
    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly StringWriter _writer = new(CultureInfo.InvariantCulture);
    public string Text => _writer.ToString();

    public ShellOutputCapture()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(_writer)
        });
        console.Profile.Capabilities.Ansi = false;
        console.Profile.Width = 240;
        AnsiConsole.Console = console;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _original;
        _writer.Dispose();
    }
}
