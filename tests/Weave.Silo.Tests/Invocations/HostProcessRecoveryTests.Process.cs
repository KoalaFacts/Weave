using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class HostProcessRecoveryTests
{
    private sealed class HostChild : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly string _key;
        public HttpClient Client { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private HostChild(Process process, string key)
        {
            _process = process;
            _key = key;
            _stdout = DrainAsync(process.StandardOutput);
            _stderr = DrainAsync(process.StandardError);
        }

        public static async Task<HostChild> StartAsync(string root, WorkerSettings settings, CancellationToken ct)
        {
            File.Delete(Path.Combine(root, "ready"));
            File.WriteAllText(Path.Combine(root, "worker.json"), JsonSerializer.Serialize(settings, JsonOptions));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add(typeof(HostProcessRecoveryTests).Assembly.Location);
            start.ArgumentList.Add("-class");
            start.ArgumentList.Add(typeof(HostProcessRecoveryTests).FullName!);
            start.Environment[ChildRootVariable] = root;
            var process = new Process { StartInfo = start };
            try
            { process.Start().ShouldBeTrue(); }
            catch { process.Dispose(); throw; }
            var child = new HostChild(process, settings.SigningKey);
            try
            {
                var address = new Uri(await child.ReadReadyFileAsync(Path.Combine(root, "ready"), ct));
                address.IsLoopback.ShouldBeTrue();
                address.Scheme.ShouldBe("http");
                child.Client.BaseAddress = address;
                return child;
            }
            catch
            {
                await child.DisposeAsync();
                throw;
            }
        }

        public async Task WaitForFileAsync(string path, CancellationToken ct)
        {
            while (!File.Exists(path))
            {
                if (_process.HasExited)
                    throw new InvalidOperationException($"Worker exited {_process.ExitCode}: {await DiagnosticsAsync()}");
                await Task.Delay(25, ct);
            }
        }

        private async Task<string> ReadReadyFileAsync(string path, CancellationToken ct)
        {
            while (true)
            {
                await WaitForFileAsync(path, ct);
                try
                {
                    return await File.ReadAllTextAsync(path, ct);
                }
                catch (IOException) when (!ct.IsCancellationRequested)
                {
                    if (_process.HasExited)
                        throw;
                    await Task.Delay(25, ct);
                }
            }
        }

        public async Task StopAsync(CancellationToken ct)
        {
            await _process.StandardInput.WriteLineAsync("stop".AsMemory(), ct);
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(ct);
            _process.ExitCode.ShouldBe(0, await DiagnosticsAsync());
        }

        public async Task KillAsync(CancellationToken ct)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(ct);
            _process.HasExited.ShouldBeTrue();
            _process.ExitCode.ShouldNotBe(0);
        }

        private async Task<string> DiagnosticsAsync() => ((await _stdout) + (await _stderr)).Replace(_key, "[fixture-key]", StringComparison.Ordinal);

        private static async Task<string> DrainAsync(StreamReader reader)
        {
            var tail = new StringBuilder();
            var buffer = new char[2048];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > 8192)
                    tail.Remove(0, tail.Length - 8192);
            }
            return tail.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(cleanup.Token);
            await Task.WhenAll(_stdout, _stderr).WaitAsync(cleanup.Token);
            _process.Dispose();
        }
    }
}
