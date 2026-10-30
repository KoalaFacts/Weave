namespace Weave.Cli.Tui;

internal sealed class ChatExitConfirmation(TimeProvider timeProvider)
{
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(2);
    private DateTimeOffset? _hintUntil;

    internal bool ExitRequested { get; private set; }
    internal bool IsArmed => _hintUntil is { } deadline && timeProvider.GetUtcNow() < deadline;
    internal bool HasExpiredHint => _hintUntil is { } deadline && timeProvider.GetUtcNow() >= deadline;

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
        var now = timeProvider.GetUtcNow();
        if (_hintUntil is { } deadline && now < deadline)
        {
            ExitRequested = true;
            return true;
        }

        _hintUntil = now.Add(ConfirmWindow);
        return true;
    }
}
