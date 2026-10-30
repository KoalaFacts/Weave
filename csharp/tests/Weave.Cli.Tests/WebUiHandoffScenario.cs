using System.Net;
using System.Text;
using Weave.Actions.Dashboard;
using Weave.Actions.SystemInfo;

namespace Weave.Cli.Tests;

internal static class WebUiHandoffScenario
{
    private const string ExplicitUrl = "http://127.0.0.1:43123/dashboard?search=release%20notes&quote=%22fixture%22#preview";
    private const string EnvironmentUrl = "http://127.0.0.1:43124/from-env?view=fixture%20view&mode=review";
    private const string ArgumentVariable = "WEAVE_TEST_WEBUI_ARGUMENTS";

    public static async Task ExecuteAsync(string root, string mode)
    {
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ShouldBe(root);
        Environment.CurrentDirectory.ShouldBe(root);
        var executableDirectory = Directory.CreateDirectory(Path.Join(root, "owned-opener-bin")).FullName;
        var argumentPath = Path.Join(root, "opener-argv.bin");
        if (mode != "missing-opener")
        {
            File.Exists("/bin/sh").ShouldBeTrue("The owned opener fixture requires /bin/sh.");
            File.Exists("/bin/mv").ShouldBeTrue("The owned opener fixture requires /bin/mv for its atomic handshake.");
            var executablePath = Path.Join(executableDirectory, "xdg-open");
            await File.WriteAllTextAsync(executablePath, OpenerScript, new UTF8Encoding(false), TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsLinux())
                throw new InvalidOperationException("The isolated opener fixture requires Linux.");
            File.SetUnixFileMode(executablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousUrl = Environment.GetEnvironmentVariable("WEAVE_WEBUI_URL");
        var previousArgumentPath = Environment.GetEnvironmentVariable(ArgumentVariable);
        try
        {
            // Only the harness-owned child reaches this method. An empty fixture directory is the entire fallback PATH.
            Environment.SetEnvironmentVariable("PATH", executableDirectory);
            Environment.SetEnvironmentVariable("WEAVE_WEBUI_URL", mode == "configured-default" ? null : EnvironmentUrl);
            Environment.SetEnvironmentVariable(ArgumentVariable, argumentPath);
            using var output = new ShellOutputCapture();
            using var handler = new RecordingDashboardHandler();
            using var client = new HttpClient(handler);
            var config = new FixtureConfigSource();
            var command = new WebUiCliCommand(new GetDashboardStatusAction(config, client));
            var explicitUrl = mode is "explicit" or "missing-opener" ? ExplicitUrl : null;
            var expectedUrl = explicitUrl ?? (mode == "configured-default" ? "http://localhost:43126" : EnvironmentUrl);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));

            var result = await command.ExecuteAsync(new WebUiOptions(explicitUrl, NoOpen: false), deadline.Token);

            result.ShouldBe(0);
            handler.Requests.ShouldHaveSingleItem().ShouldBe(new Uri(expectedUrl));
            config.Reads.ShouldBe(mode == "configured-default" ? 1 : 0);
            output.Text.ShouldContain("Web UI: " + expectedUrl);
            output.Text.ShouldContain("Dashboard is not reachable yet.");
            if (mode == "missing-opener")
            {
                output.Text.ShouldContain("Could not open a browser automatically. Visit: " + expectedUrl);
                output.Text.ShouldNotContain("Opened in your default browser.");
                File.Exists(argumentPath).ShouldBeFalse();
                File.Exists(argumentPath + ".pending").ShouldBeFalse();
            }
            else
            {
                // The shim atomically renames only after recording every argument: this is the process handoff boundary.
                while (!File.Exists(argumentPath))
                    await Task.Delay(10, deadline.Token);
                var arguments = await File.ReadAllTextAsync(argumentPath, deadline.Token);
                arguments.ShouldBe(expectedUrl + "\0");
                output.Text.ShouldContain("Opened in your default browser.");
                output.Text.ShouldNotContain("Could not open a browser automatically.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("WEAVE_WEBUI_URL", previousUrl);
            Environment.SetEnvironmentVariable(ArgumentVariable, previousArgumentPath);
        }
    }

    private const string OpenerScript = """
        #!/bin/sh
        set -eu
        printf '%s\000' "$@" > "$WEAVE_TEST_WEBUI_ARGUMENTS.pending"
        /bin/mv -- "$WEAVE_TEST_WEBUI_ARGUMENTS.pending" "$WEAVE_TEST_WEBUI_ARGUMENTS"
        """;

    private sealed class RecordingDashboardHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Method.ShouldBe(HttpMethod.Get);
            Requests.Add(request.RequestUri.ShouldNotBeNull());
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            _responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _responses)
                    response.Dispose();
                _responses.Clear();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class FixtureConfigSource : ISystemConfigSource
    {
        public int Reads { get; private set; }
        public SystemConfigSnapshot Load()
        {
            Reads++;
            return new SystemConfigSnapshot
            {
                Version = "fixture",
                BaseUrl = "http://127.0.0.1:43125",
                DefaultPort = 43125,
                Storage = "memory",
                AuthMode = "none",
                RequireHttps = false,
                SiloPath = null,
                WeaveHome = "unused-fixture"
            };
        }
    }
}
