using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Weave.Cli.Tests;

internal sealed class ScriptedSelectionConsole : IAnsiConsole, IAnsiConsoleInput, IDisposable
{
    private readonly IAnsiConsole _previous = AnsiConsole.Console;
    private readonly StringWriter _output = new(CultureInfo.InvariantCulture);
    private readonly IAnsiConsole _console;
    private readonly Queue<ConsoleKey> _keys;

    public ScriptedSelectionConsole(params ConsoleKey[] keys)
    {
        _keys = new Queue<ConsoleKey>(keys);
        _console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_output),
            Ansi = AnsiSupport.Yes,
            Interactive = InteractionSupport.Yes
        });
        // CI profile enrichment can override factory settings for redirected streams.
        // This controlled console models an interactive terminal for the real selection prompt.
        _console.Profile.Capabilities.Ansi = true;
        _console.Profile.Capabilities.Interactive = true;
        _console.Profile.Width = 240;
        _console.Profile.Height = 60;
        AnsiConsole.Console = this;
    }

    public string Output => _output.ToString();
    public int RemainingKeys => _keys.Count;
    public Profile Profile => _console.Profile;
    public IAnsiConsoleCursor Cursor => _console.Cursor;
    public IAnsiConsoleInput Input => this;
    public IExclusivityMode ExclusivityMode => _console.ExclusivityMode;
    public RenderPipeline Pipeline => _console.Pipeline;
    public void Clear(bool home) => _console.Clear(home);
    public void Write(IRenderable renderable) => _console.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action) => _console.WriteAnsi(action);
    public bool IsKeyAvailable() => _keys.Count != 0;
    public ConsoleKeyInfo? ReadKey(bool intercept)
    {
        _keys.Count.ShouldBeGreaterThan(0, "The real prompt requested unexpected additional input.");
        var key = _keys.Dequeue();
        return new ConsoleKeyInfo(key == ConsoleKey.Enter ? '\r' : '\0', key, false, false, false);
    }

    public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadKey(intercept));
    }

    public void Dispose()
    {
        AnsiConsole.Console = _previous;
        _output.Dispose();
    }
}
