namespace Weave.Cli.Tui;

internal sealed class ChatExitConfirmation
{
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(2);
    private DateTime? _hintUntil;

    internal bool ExitRequested { get; private set; }
    internal bool IsArmed => _hintUntil is { } deadline && DateTime.UtcNow < deadline;
    internal bool HasExpiredHint => _hintUntil is { } deadline && DateTime.UtcNow >= deadline;

    internal void Reset()
    {
        _hintUntil = null;
        ExitRequested = false;
    }

    internal void ExpireHint()
    {
        _hintUntil = null;
    }

    internal bool Press()
    {
        var now = DateTime.UtcNow;
        if (_hintUntil is { } deadline && now < deadline)
        {
            ExitRequested = true;
            return true;
        }

        _hintUntil = now.Add(ConfirmWindow);
        return true;
    }
}
