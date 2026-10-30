using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
using CliConfig = Weave.Cli.Shell.CliConfig;

namespace Weave.Cli.Tests;

internal static class SiloLifecycleConfiguration
{
    public static async Task VerifyAsync(string root, string backend, bool workspaceOverride)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        await using var worker = new SiloLifecycleWorker(Path.Join(root, "worker"), "healthy");
        var globalConnection = "Server=global-host;Database=global db;Password=fixture-only";
        var workspaceConnection = backend switch
        {
            "postgresql" => "Host=localhost;Database=tenant db;Username=fixture;Password=fixture-only",
            "sqlite" => "Data Source=" + Path.Join(root, "tenant db.sqlite"),
            "redis" => "localhost:6379,password=fixture-only",
            _ => "unused"
        };
        var config = new CliConfig
        {
            Storage = workspaceOverride ? "sqlserver" : backend,
            ConnectionString = backend == "memory" ? null : "file:global-reference",
            AuthMode = workspaceOverride ? "none" : "bearer",
            AuthSecret = workspaceOverride ? null : "env:fixture-auth",
            RequireHttps = !workspaceOverride
        };
        var secrets = new RecordingSecrets(globalConnection, workspaceConnection);
        var service = new SiloProcessService(new FixedConfig(config), secrets);
        var storage = workspaceOverride ? new StorageConfig
        {
            Backend = backend,
            ConnectionString = "env:workspace-reference",
            Schema = backend == "postgresql" ? "tenant schema" : null,
            Database = backend == "postgresql" ? "tenant-db" : null
        } : null;
        worker.ReleasePort();
        var process = service.StartSilo(worker.Executable, worker.Port, storage).ShouldNotBeNull();
        worker.Attach(process);
        await worker.ListeningAsync(deadline.Token);

        (await SiloProcessService.WaitForReadyAsync(worker.Port, deadline.Token, attempts: 8)).ShouldBeTrue();
        var expected = new List<string> { "--Weave:LocalMode=true", $"--urls=http://localhost:{worker.Port}" };
        if (backend != "memory")
        {
            expected.Add("--Weave:ActorStorage:Provider=" + backend);
            var connectionName = backend switch
            {
                "postgresql" => "PostgreSql",
                "sqlserver" => "SqlServer",
                "sqlite" => "Sqlite",
                "redis" => "Redis",
                _ => throw new ArgumentException("Unexpected fixture provider.", nameof(backend))
            };
            expected.Add("--ConnectionStrings:" + connectionName + "=" + (workspaceOverride ? workspaceConnection : globalConnection));
            if (workspaceOverride && backend == "postgresql")
            {
                expected.Add("--Weave:ActorStorage:Schema=tenant schema");
                expected.Add("--Weave:ActorStorage:Database=tenant-db");
            }
        }
        if (!workspaceOverride)
        {
            expected.Add("--Weave:Auth:Mode=bearer");
            expected.Add("--Weave:Auth:Secret=fixture auth with spaces");
            expected.Add("--Weave:RequireHttps=true");
        }
        (await worker.ArgumentsAsync(deadline.Token)).ShouldBe(expected);
        string[] expectedReferences = workspaceOverride ? ["env:workspace-reference"]
            : backend == "memory" ? ["env:fixture-auth"] : ["file:global-reference", "env:fixture-auth"];
        secrets.References.ShouldBe(expectedReferences);
        await AssertLogAsync(root, deadline.Token);
        SiloProcessService.TryKill(process);
        await worker.WaitForExitAsync(deadline.Token);
        worker.HasExited.ShouldBeTrue();
        SiloProcessService.TryKill(process);
        SiloProcessService.TryKill(null);
    }

    public static async Task VerifyLogFailureAsync(string root)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var logPath = Path.Join(root, ".weave", "silo.log");
        Directory.CreateDirectory(logPath);
        await using var worker = new SiloLifecycleWorker(Path.Join(root, "worker"), "healthy");
        worker.ReleasePort();
        var service = MemoryService();
        var process = service.StartSilo(worker.Executable, worker.Port).ShouldNotBeNull();
        worker.Attach(process);
        await worker.ListeningAsync(deadline.Token);

        (await SiloProcessService.WaitForReadyAsync(worker.Port, deadline.Token, attempts: 8)).ShouldBeTrue();

        // The worker listens only after both 96 KiB streams finish writing.
        Directory.Exists(logPath).ShouldBeTrue();
        File.Exists(logPath).ShouldBeFalse();
        SiloProcessService.TryKill(process);
        await worker.WaitForExitAsync(deadline.Token);
        worker.HasExited.ShouldBeTrue();
    }

    public static async Task VerifyReadinessAsync(string root)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var worker = new SiloLifecycleWorker(Path.Join(root, "worker"), "unhealthy");
        worker.ReleasePort();
        var process = MemoryService().StartSilo(worker.Executable, worker.Port).ShouldNotBeNull();
        worker.Attach(process);
        await worker.ListeningAsync(deadline.Token);
        await worker.ArgumentsAsync(deadline.Token);

        (await SiloProcessService.WaitForReadyAsync(worker.Port, deadline.Token, attempts: 2)).ShouldBeFalse();
        (await worker.HealthObservedAsync(deadline.Token))["status"].ShouldNotBeNull().GetValue<int>().ShouldBe(503);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            SiloProcessService.WaitForReadyAsync(worker.Port, cancellation.Token, attempts: 2));
        SiloProcessService.TryKill(process);
        await worker.WaitForExitAsync(deadline.Token);
        worker.HasExited.ShouldBeTrue();
    }

    public static async Task VerifyEarlyExitAsync(string root)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var worker = new SiloLifecycleWorker(Path.Join(root, "worker"), "exit-on-probe");
        worker.ReleasePort();
        var process = MemoryService().StartSilo(worker.Executable, worker.Port).ShouldNotBeNull();
        worker.Attach(process);
        await worker.ListeningAsync(deadline.Token);
        await worker.ArgumentsAsync(deadline.Token);

        (await SiloProcessService.WaitForReadyAsync(worker.Port, deadline.Token, attempts: 2)).ShouldBeFalse();

        (await worker.HealthObservedAsync(deadline.Token))["status"].ShouldNotBeNull().GetValue<int>().ShouldBe(503);
        await worker.WaitForExitAsync(deadline.Token);
        worker.ExitCode.ShouldBe(23);
    }

    public static SiloProcessService MemoryService() => new(new FixedConfig(new CliConfig
    {
        Storage = "memory",
        AuthMode = "none",
        RequireHttps = false
    }), new RecordingSecrets("unused", "unused"));

    private static async Task AssertLogAsync(string root, CancellationToken ct)
    {
        var path = Path.Join(root, ".weave", "silo.log");
        string log;
        do
        {
            await Task.Delay(10, ct);
            log = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        } while (!log.Contains("OUT stdout-end", StringComparison.Ordinal) || !log.Contains("ERR stderr-end", StringComparison.Ordinal));
        log.ShouldContain("OUT lifecycle-stdout-marker");
        log.ShouldContain("ERR lifecycle-stderr-marker");
    }

    private sealed class FixedConfig(CliConfig config) : IConfigStore
    {
        public CliConfig Load() => config;
        public bool Exists() => true;
        public void Save(CliConfig value) => throw new InvalidOperationException("Process startup must not rewrite configuration.");
    }

    private sealed class RecordingSecrets(string global, string workspace) : ISecretResolver
    {
        public List<string> References { get; } = [];
        public string? ResolveReference(string? reference)
        {
            if (reference is null)
                return null;
            References.Add(reference);
            return reference switch
            {
                "file:global-reference" => global,
                "env:workspace-reference" => workspace,
                "env:fixture-auth" => "fixture auth with spaces",
                _ => throw new InvalidOperationException("Unexpected secret reference.")
            };
        }
        public string ToEnvReference(string storageBackend) => throw new InvalidOperationException("Startup must preserve references.");
    }
}
