namespace Weave.Agents.Heartbeat;

internal sealed class HeartbeatSchedule
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from HeartbeatActor.")]
    public int ParseMinutes(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
            return 30;

        var minutePart = parts[0];
        if (minutePart.StartsWith("*/", StringComparison.Ordinal) && int.TryParse(minutePart[2..], out var interval))
            return interval;

        return 30;
    }
}
