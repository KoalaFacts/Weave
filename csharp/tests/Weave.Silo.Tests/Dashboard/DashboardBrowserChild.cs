using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Weave.Silo.Tests.Dashboard;

internal sealed class DashboardBrowserChild : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _log = new();
    private readonly Lock _gate = new();
    private readonly Task _stdout;
    private readonly Task _stderr;

    public DashboardBrowserChild(ProcessStartInfo start)
    {
        _process = Process.Start(start).ShouldNotBeNull();
        _process.StandardInput.Close();
        _stdout = DrainAsync(_process.StandardOutput);
        _stderr = DrainAsync(_process.StandardError);
    }

    public string Log { get { lock (_gate) return _log.ToString(); } }
    public bool HasExited => _process.HasExited;

    public static ProcessStartInfo StartInfo(string executable, string home)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = home,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Retain the inherited profiler/coverage environment; isolate only the child profile.
        start.Environment["HOME"] = home;
        start.Environment["USERPROFILE"] = home;
        start.Environment["XDG_CONFIG_HOME"] = Path.Join(home, "config");
        start.Environment["XDG_DATA_HOME"] = Path.Join(home, "data");
        start.Environment["XDG_CACHE_HOME"] = Path.Join(home, "cache");
        start.Environment["DOTNET_CLI_HOME"] = home;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return start;
    }

    private async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            lock (_gate)
            {
                var retained = Math.Min(count, 65536 - _log.Length);
                if (retained > 0)
                    _log.Append(buffer, 0, retained);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                var signalStart = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                signalStart.ArgumentList.Add("-TERM");
                signalStart.ArgumentList.Add(_process.Id.ToString(CultureInfo.InvariantCulture));
                using var signal = Process.Start(signalStart).ShouldNotBeNull();
                await signal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
        catch (TimeoutException)
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        finally
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                await Task.WhenAll(_process.WaitForExitAsync(), _stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
