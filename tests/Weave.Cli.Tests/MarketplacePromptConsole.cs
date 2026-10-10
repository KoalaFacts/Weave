using System.Globalization;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Weave.Cli.Tests;

// Script only the input boundary; prompts, validation and rendering remain Spectre's real implementation.
internal sealed partial class MarketplacePromptConsole : IAnsiConsole, IAnsiConsoleInput, IDisposable
{
    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly StringWriter _writer = new(CultureInfo.InvariantCulture);
    private readonly IAnsiConsole _console;
    private readonly Queue<ConsoleKeyInfo> _keys = new();
    public string RawText => _writer.ToString();
    // Remove only ESC-prefixed CSI controls; literal brackets and message content are preserved.
    public string Text => AnsiCsiSequence().Replace(RawText, string.Empty);
    public int RemainingKeys => _keys.Count;
    public Profile Profile => _console.Profile;
    public IAnsiConsoleCursor Cursor => _console.Cursor;
    public IAnsiConsoleInput Input => this;
    public IExclusivityMode ExclusivityMode => _console.ExclusivityMode;
    public RenderPipeline Pipeline => _console.Pipeline;

    public MarketplacePromptConsole()
    {
        _console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(_writer)
        });
        // SelectionPrompt requires ANSI even when the writer is an in-memory capture.
        Profile.Capabilities.Ansi = true;
        Profile.Capabilities.Interactive = true;
        Profile.Width = 240;
        Profile.Height = 60;
        AnsiConsole.Console = this;
    }

    public void Line(string value)
    {
        foreach (var character in value)
            _keys.Enqueue(new ConsoleKeyInfo(character, (ConsoleKey)0, false, false, false));
        Key(ConsoleKey.Enter, '\r');
    }

    public void Key(ConsoleKey key, char character = '\0') =>
        _keys.Enqueue(new ConsoleKeyInfo(character, key, false, false, false));

    public bool IsKeyAvailable() => _keys.Count > 0;
    public ConsoleKeyInfo? ReadKey(bool intercept) => _keys.Count > 0
        ? _keys.Dequeue() : throw new InvalidOperationException("Prompt requested unexpected input.");
    public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadKey(intercept));
    }
    public void Clear(bool home) => _console.Clear(home);
    public void Write(IRenderable renderable) => _console.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action) => _console.WriteAnsi(action);
    [GeneratedRegex("\u001b\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiCsiSequence();

    public void Dispose()
    {
        AnsiConsole.Console = _original;
        _writer.Dispose();
    }
}
