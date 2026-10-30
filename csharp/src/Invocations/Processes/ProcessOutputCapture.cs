using System.Text;

namespace Weave.Invocations.Processes;

internal static class ProcessOutputCapture
{
    public const int Limit = 65_536;

    public static Task<string> Start(TextReader reader, TaskCompletionSource overflow) =>
        Task.Factory.StartNew(() => Capture(reader, overflow), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static string Capture(TextReader reader, TaskCompletionSource overflow)
    {
        try
        {
            return Drain(reader, overflow);
        }
        catch (Exception error)
        {
            overflow.TrySetException(error);
            throw;
        }
    }

    private static string Drain(TextReader reader, TaskCompletionSource overflow)
    {
        var retained = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var keep = Math.Min(count, Limit - retained.Length);
            retained.Append(buffer, 0, keep);
            if (keep < count)
                overflow.TrySetResult();
        }
        return retained.ToString();
    }
}
