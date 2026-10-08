namespace Weave.Mailboxes;

public sealed record MailboxResult<T> where T : class
{
    public MailboxResult(T value) { ArgumentNullException.ThrowIfNull(value); Value = value; }
    public MailboxResult(MailboxError error) { Error = error; }
    public T? Value { get; }
    public MailboxError? Error { get; }
    public bool IsSuccess => Error is null;
}
