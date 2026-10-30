using System.Globalization;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(LocalHostProcessGroup))]
public sealed class LocalCliInitializationProcessTests
{
    [Fact]
    public Task InitializeAsync_PreparationRejectedThenOwnedHostReady_ReportsOnlyConfirmedSetupAndNextSteps() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalCliInitializationProcessTests), async root =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            await using var worker = new LocalHostOwnedWorker(Path.Join(root, "worker"), "healthy");
            var directory = Path.Join(root, "deployment with spaces");
            var directoryArgument = Path.Join(directory, ".");
            var documents = Directory.CreateDirectory(Path.Join(root, "documents")).FullName;
            var launcher = new LocalCliRecordingLauncher();
            var review = new LocalReviewWorkflow(new LocalReview(new LocalCliNoninteractiveReview()), launcher);
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            Task<int>? initializing = null;
            try
            {
                Console.SetOut(TextWriter.Synchronized(output));
                Console.SetError(TextWriter.Synchronized(error));
                worker.ReleasePort();
                var deniedStore = new LocalCliInitializationStore("fixture preparation refused");
                var denied = new LocalCliCommand(deniedStore, new LocalHostRunner(deniedStore, TimeProvider.System), launcher, review, TimeProvider.System);
                (await denied.InitializeAsync(directoryArgument, documents, worker.Executable, "fixture-team", worker.Port, deadline.Token)).ShouldBe(1);
                deniedStore.Preparations.ShouldHaveSingleItem().ShouldBe((Path.GetFullPath(directoryArgument), documents,
                    (string?)worker.Executable, "fixture-team", worker.Port));
                deniedStore.Completions.ShouldBeEmpty();
                Directory.Exists(directory).ShouldBeFalse();
                error.ToString().ShouldContain("fixture preparation refused");
                output.ToString().ShouldNotContain("Next:");

                var store = new LocalCliInitializationStore(null);
                var command = new LocalCliCommand(store, new LocalHostRunner(store, TimeProvider.System), launcher, review, TimeProvider.System);
                initializing = command.InitializeAsync(directoryArgument, documents, worker.Executable, "fixture-team", worker.Port, deadline.Token);
                await worker.ObserveStartedAsync(initializing, deadline.Token);
                (await initializing.WaitAsync(deadline.Token)).ShouldBe(0);
                worker.HasExited.ShouldBeTrue();
                store.Preparations.ShouldHaveSingleItem().ShouldBe((Path.GetFullPath(directoryArgument), documents,
                    (string?)worker.Executable, "fixture-team", worker.Port));
                store.Completions.ShouldHaveSingleItem().ShouldBe((Path.GetFullPath(directoryArgument),
                    new LocalDeployment(worker.Executable, documents, "fixture-team", worker.Port)));
                output.ToString().ShouldContain("Next: weave local serve, then weave local codex in another terminal.");
                (await worker.ReadAsync("request.json", deadline.Token))["url"].ShouldNotBeNull()
                    .GetValue<string>().ShouldBe("/api/operator/tools/files/connect");
                launcher.Calls.ShouldBeEmpty();
            }
            finally
            {
                deadline.Cancel();
                try
                {
                    if (initializing is not null)
                    {
                        var failure = await Record.ExceptionAsync(async () => await initializing.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
                        if (failure is not null)
                            failure.ShouldBeAssignableTo<OperationCanceledException>();
                    }
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalError);
                }
            }
        });
}
