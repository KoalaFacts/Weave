using System.Globalization;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

internal static class LocalHostProcessScenario
{
    public static async Task VerifyAsync(string root, string mode, bool durableStoresReady = true)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var worker = new LocalHostOwnedWorker(Path.Join(root, "worker"), mode);
        var directory = Path.Join(root, "private deployment");
        var contentRoot = Directory.CreateDirectory(Path.Join(directory, "private")).FullName;
        var documents = Directory.CreateDirectory(Path.Join(root, "documents")).FullName;
        var document = Path.Join(documents, "preserved.txt");
        await File.WriteAllTextAsync(document, "document must remain unchanged", deadline.Token);
        var store = new LocalHostRecordingStore(durableStoresReady);
        var deployment = new LocalDeployment(worker.Executable, documents, "local-fixture", worker.Port);
        var runner = new LocalHostRunner(store, TimeProvider.System);
        var environment = new Dictionary<string, string>
        {
            ["Weave__Fixture"] = "discard-weave",
            ["CapabilityTokens__Fixture"] = "discard-capability",
            ["ConnectionStrings__Fixture"] = "discard-connection",
            ["ASPNETCORE_ENVIRONMENT"] = "discard-aspnet",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["Urls"] = "http://127.0.0.1:1",
            ["LOCAL_HOST_KEEP"] = "preserved-environment"
        };
        var previous = environment.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Task<int>? running = null;
        Exception? scenarioFailure = null;
        try
        {
            foreach (var entry in environment)
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            Console.SetOut(TextWriter.Synchronized(output));
            Console.SetError(TextWriter.Synchronized(error));
            worker.ReleasePort();
            running = runner.RunAsync(directory, deployment, initialize: true, cancellation.Token);
            var started = await worker.ObserveStartedAsync(running, deadline.Token);
            var request = await worker.ReadAsync("request.json", deadline.Token);

            if (mode == "pending")
            {
                worker.HasExited.ShouldBeFalse();
                running.IsCompleted.ShouldBeFalse();
                cancellation.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(() => running.WaitAsync(deadline.Token));
            }
            else if (mode == "denied")
            {
                var failure = await Should.ThrowAsync<IOException>(() => running.WaitAsync(deadline.Token));
                failure.Message.ShouldContain("Document connection was denied (HTTP 403)");
            }
            else
            {
                (await running.WaitAsync(deadline.Token)).ShouldBe(durableStoresReady ? 0 : 1);
                store.Completions.ShouldHaveSingleItem().ShouldBe((directory, deployment));
                if (durableStoresReady)
                    output.ToString().ShouldContain("Local document approval configured.");
                else
                {
                    error.ToString().ShouldContain("Initialization did not establish both durable stores.");
                    output.ToString().ShouldNotContain("Local document approval configured.");
                }
            }

            worker.HasExited.ShouldBeTrue("the runner must finish owned-process cleanup before returning");
            if (mode != "healthy")
            {
                store.Completions.ShouldBeEmpty();
                output.ToString().ShouldNotContain("Local document approval configured.");
            }
            started["cwd"].ShouldNotBeNull().GetValue<string>().ShouldBe(contentRoot);
            started["args"].ShouldNotBeNull().AsArray()
                .Select(value => value.ShouldNotBeNull().GetValue<string>())
                .ShouldBe(["--Weave:LocalMode=true", $"--urls={deployment.Origin}", $"--contentRoot={contentRoot}"]);
            var actualEnvironment = started["environment"].ShouldNotBeNull().AsObject();
            actualEnvironment.Select(entry => entry.Key).Order().ShouldBe(["DOTNET_ENVIRONMENT", "LOCAL_HOST_KEEP"]);
            actualEnvironment["DOTNET_ENVIRONMENT"].ShouldNotBeNull().GetValue<string>().ShouldBe("Production");
            actualEnvironment["LOCAL_HOST_KEEP"].ShouldNotBeNull().GetValue<string>().ShouldBe("preserved-environment");
            request["method"].ShouldNotBeNull().GetValue<string>().ShouldBe("POST");
            request["url"].ShouldNotBeNull().GetValue<string>().ShouldBe("/api/operator/tools/files/connect");
            request["body"].ShouldNotBeNull().GetValue<string>().ShouldBeEmpty();
            var headers = request["headers"].ShouldNotBeNull().AsObject();
            headers["x-weave-operator-key"].ShouldNotBeNull().GetValue<string>().ShouldBe(LocalHostRecordingStore.OperatorSecret);
            headers.ContainsKey("x-weave-capability").ShouldBeFalse();
            headers.ContainsKey("authorization").ShouldBeFalse();
            var diagnostics = error.ToString();
            diagnostics.ShouldContain("stdout [redacted] [redacted]");
            diagnostics.ShouldContain("stderr [redacted] [redacted]");
            diagnostics.ShouldContain("stdout-drained");
            diagnostics.ShouldContain("stderr-drained");
            diagnostics.ShouldNotContain(LocalHostRecordingStore.OperatorSecret);
            diagnostics.ShouldNotContain(LocalHostRecordingStore.SigningSecret);
            diagnostics.ShouldNotContain(new string('o', 4097));
            diagnostics.ShouldNotContain(new string('e', 4097));
            (await File.ReadAllTextAsync(document, deadline.Token)).ShouldBe("document must remain unchanged");
        }
        catch (Exception failure)
        {
            scenarioFailure = failure;
            throw;
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                if (running is not null)
                {
                    var cleanupFailure = await Record.ExceptionAsync(async () =>
                        await running.WaitAsync(TimeSpan.FromSeconds(5)));
                    if (cleanupFailure is not null && !ReferenceEquals(cleanupFailure, scenarioFailure))
                    {
                        if (mode == "denied" && cleanupFailure is IOException)
                            cleanupFailure.Message.ShouldContain("Document connection was denied (HTTP 403)");
                        else
                            cleanupFailure.ShouldBeAssignableTo<OperationCanceledException>();
                    }
                }
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                foreach (var entry in previous)
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }
}
