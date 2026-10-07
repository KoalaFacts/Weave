namespace Weave.Mailboxes.Tests.Storage;

internal sealed class MailboxTestClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero).UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}
