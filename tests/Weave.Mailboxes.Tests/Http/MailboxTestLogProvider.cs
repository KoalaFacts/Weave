using Microsoft.Extensions.Logging;

namespace Weave.Mailboxes.Tests.Http;

internal sealed class MailboxTestLogProvider(List<string> messages) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TestLogger(messages);
    public void Dispose() { }
    private sealed class TestLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages)
                messages.Add(formatter(state, exception));
        }
    }
}
