using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Tools.Connectors;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

public sealed class EchoMcpHttpConnectionLifecycleTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    public async Task ExampleServer_DelayedSocketClose_PreservesHandshakeAndEveryJsonSseCall(int predecessorStatus)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await using var peer = new ClosingPeer(predecessorStatus, implicitClose: false);
        var endpoint = await peer.EndpointAsync(deadline.Token);
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(Config(endpoint), deadline.Token),
            "echo-close", NullLogger.Instance);

        var error = await Record.ExceptionAsync(async () =>
        {
            await connection.InitializeAsync(deadline.Token);
            (await connection.ListToolsAsync(deadline.Token)).Count.ShouldBe(1);
            for (var index = 0; index < 10; index++)
            {
                var text = $"echo-{index}";
                var result = await connection.CallToolAsync("echo", new JsonObject
                {
                    ["text"] = text,
                    ["chunk_size"] = index % 2 == 0 ? 0 : 2
                }, deadline.Token);
                result.IsError.ShouldBeFalse();
                result.Content.Single().Text.ShouldBe(text);
            }
        });

        error.ShouldBeNull(peer.Diagnostics);
        await peer.WaitForServedAsync(13, deadline.Token);
        peer.ServedPosts.ShouldBe(13); // initialize + notification + list + ten distinct calls
        peer.LatePosts.ShouldBe(0);
        peer.HasExited.ShouldBeFalse();
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    public async Task ExampleServer_ImplicitCloseNegativeControl_ReproducesResponseEndedWithoutReplay(int predecessorStatus)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var peer = new ClosingPeer(predecessorStatus, implicitClose: true);
        var endpoint = await peer.EndpointAsync(deadline.Token);
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(Config(endpoint), deadline.Token),
            "echo-close", NullLogger.Instance);

        var error = await Record.ExceptionAsync(async () =>
        {
            await connection.InitializeAsync(deadline.Token);
            await connection.CallToolAsync("echo", new JsonObject { ["text"] = "one-call", ["chunk_size"] = 2 }, deadline.Token);
        });

        error.ShouldBeOfType<IOException>(peer.Diagnostics);
        error.GetBaseException().ShouldBeOfType<HttpIOException>().HttpRequestError.ShouldBe(HttpRequestError.ResponseEnded);
        await peer.LatePost.WaitAsync(deadline.Token);
        peer.ServedPosts.ShouldBe(predecessorStatus == 200 ? 1 : 2);
        peer.LatePosts.ShouldBe(1);
        peer.HasExited.ShouldBeFalse();
    }

    private static McpConfig Config(string endpoint) => new()
    {
        Url = endpoint,
        AllowPrivateEndpoints = true,
        RequestTimeoutSeconds = 5
    };

    private sealed class ClosingPeer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _diagnostics = new();
        private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _latePost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _servedSignal = new(0);
        private int _servedPosts;
        private int _latePosts;

        public ClosingPeer(int predecessorStatus, bool implicitClose)
        {
            var start = new ProcessStartInfo
            {
                FileName = LocatePython(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(LocateFixture());
            start.ArgumentList.Add("--delay-after");
            start.ArgumentList.Add(predecessorStatus.ToString(CultureInfo.InvariantCulture));
            if (implicitClose)
                start.ArgumentList.Add("--implicit-close");
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => Capture(e.Data);
            _process.ErrorDataReceived += (_, e) => Capture(e.Data);
            _process.Exited += (_, _) => _ready.TrySetException(new IOException("Echo close peer exited before readiness."));
            try
            {
                if (!_process.Start())
                    throw new IOException("Echo close peer could not start.");
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch
            {
                _process.Dispose();
                _servedSignal.Dispose();
                throw;
            }
        }

        public int ServedPosts => Volatile.Read(ref _servedPosts);
        public int LatePosts => Volatile.Read(ref _latePosts);
        public Task LatePost => _latePost.Task;
        public bool HasExited => _process.HasExited;
        public string Diagnostics => RuntimeInformation.FrameworkDescription + "\n" + string.Join("\n", _diagnostics);

        public async Task<string> EndpointAsync(CancellationToken ct)
        {
            var port = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return $"http://127.0.0.1:{port}/mcp";
        }

        public async Task WaitForServedAsync(int expected, CancellationToken ct)
        {
            while (ServedPosts < expected)
                await _servedSignal.WaitAsync(ct);
        }

        private void Capture(string? line)
        {
            if (line is null)
                return;
            if (_diagnostics.Count < 64)
                _diagnostics.Enqueue(line[..Math.Min(1024, line.Length)]);
            if (line.StartsWith("ready=", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535)
                _ready.TrySetResult(port);
            if (line == "served")
            {
                Interlocked.Increment(ref _servedPosts);
                _servedSignal.Release();
            }
            if (line == "late-post")
            {
                Interlocked.Increment(ref _latePosts);
                _latePost.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(deadline.Token);
            }
            finally
            {
                _process.Dispose();
                _servedSignal.Dispose();
            }
        }

        private static string LocatePython()
        {
            foreach (var name in new[] { "python3", "python" })
                foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    var candidate = Path.Join(directory, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            throw new IOException("Python is required for the real HTTP connection-lifecycle regression.");
        }

        private static string LocateFixture()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Join(directory.FullName, "tests", "Weave.Tools.Tests", "Fixtures", "echo_http_close_peer.py");
                if (File.Exists(candidate))
                    return candidate;
            }
            throw new IOException("The HTTP connection-lifecycle fixture is missing.");
        }
    }
}
