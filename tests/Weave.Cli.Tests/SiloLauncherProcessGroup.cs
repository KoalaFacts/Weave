using System.Diagnostics;
using System.Globalization;

namespace Weave.Cli.Tests;

internal static class SiloLauncherProcessGroup
{
    public static async Task AssertOwnGroupAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var path = Path.Join(root, "group-owner");
        while (!File.Exists(path))
            await Task.Delay(10, timeout.Token);
        var expected = int.Parse(await File.ReadAllTextAsync(path, timeout.Token), CultureInfo.InvariantCulture);
        Environment.ProcessId.ShouldBe(expected, "setsid must execute the isolated test child as the owned group leader.");
        var fields = ReadProcessFields(Environment.ProcessId);
        fields.ShouldNotBeNull();
        int.Parse(fields[2], CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    public static async Task StopAsync(int group)
    {
        if (!HasLiveMembers(group))
            return;
        File.Exists("/bin/kill").ShouldBeTrue("Group cleanup requires Linux kill.");
        var start = new ProcessStartInfo("/bin/kill")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-KILL");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("-" + group.ToString(CultureInfo.InvariantCulture));
        using var kill = Process.Start(start);
        kill.ShouldNotBeNull();
        var output = SiloLauncherProcessHarness.DrainAsync(kill.StandardOutput);
        var error = SiloLauncherProcessHarness.DrainAsync(kill.StandardError);
        try
        {
            await kill.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!kill.HasExited)
                kill.Kill();
            await Task.WhenAll(kill.WaitForExitAsync(CancellationToken.None), output, error)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (HasLiveMembers(group) && !timeout.IsCancellationRequested)
            await Task.Delay(10, CancellationToken.None);
        HasLiveMembers(group).ShouldBeFalse(
            $"Owned process group {group} still has a live process; preserve its temporary root. {await output}{await error}");
    }

    private static bool HasLiveMembers(int group)
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            var fields = ReadProcessFields(pid);
            if (fields is not null && fields[0] is not "Z" and not "X"
                && int.Parse(fields[2], CultureInfo.InvariantCulture) == group)
                return true;
        }
        return false;
    }

    private static string[]? ReadProcessFields(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
            return stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
