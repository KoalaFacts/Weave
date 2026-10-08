using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Weave.Mailboxes.Tests.Acceptance;

[Trait("Category", "Integration")]
public sealed class IndependentAgentsTests
{
    private static readonly string[] PublicCards = ["bob-public-pending", "bob-public-reject", "bob-public-auto"];
    private static readonly string[] StateDirectories = ["alice", "bob"];
    private static readonly string[] PolicyStates = ["pending", "rejected", "accepted", "needsAction", "accepted"];
    [Fact]
    public async Task Demo_IndependentProcesses_ContactDeliveryRestartAndDirectTransport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "weave-independent-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var report = await RunDemo(directory);
            var root = report.RootElement;
            var processes = root.GetProperty("processes");
            processes.GetArrayLength().ShouldBe(5); // relay, Alice, Bob, restarted Bob and relay
            var pids = processes.EnumerateArray().Select(p => p.GetProperty("pid").GetInt32()).ToArray();
            pids.Distinct().Count().ShouldBe(5);
            pids.ShouldAllBe(p => p > 0);
            await RequireStopped(pids);
            root.GetProperty("terminatedPids").EnumerateArray().Select(p => p.GetInt32()).ShouldBe(pids, ignoreOrder: true);
            root.GetProperty("credentialScopes").GetProperty("aliceForeignAckStatus").GetInt32().ShouldBe(404);
            root.GetProperty("credentialScopes").GetProperty("bobForeignAckStatus").GetInt32().ShouldBe(404);

            var cards = root.GetProperty("cards");
            cards.GetProperty("public").EnumerateArray().Select(c => c.GetProperty("cardId").GetString())
                .ShouldBe(PublicCards, ignoreOrder: true);
            cards.GetProperty("owned").GetArrayLength().ShouldBe(5);
            cards.GetProperty("privateDiscoveryStatus").GetInt32().ShouldBe(404);
            cards.GetProperty("privateExport").GetProperty("audienceHint").GetString().ShouldBe("intranet");
            cards.GetProperty("owned").EnumerateArray().Count(c => c.GetProperty("expiresAt").ValueKind == JsonValueKind.Null).ShouldBe(3);
            var policies = root.GetProperty("policies").EnumerateArray().ToArray();
            policies.Length.ShouldBe(5);
            foreach (var policy in policies)
            {
                policy.GetProperty("initial").GetProperty("request").GetProperty("status").GetString().ShouldBe("pending");
                var expected = policy.GetProperty("expectedStatus").GetString();
                policy.GetProperty("requesterView").GetProperty("status").GetString().ShouldBe(expected);
                policy.GetProperty("recipientView").GetProperty("status").GetString().ShouldBe(expected);
                policy.GetProperty("requesterView").TryGetProperty("payload", out _).ShouldBeFalse();
            }
            policies.Select(p => p.GetProperty("expectedStatus").GetString())
                .ShouldBe(PolicyStates);
            root.GetProperty("reply").GetProperty("senderMailboxId").GetString().ShouldBe("bob/team%2F界");
            Decode(root.GetProperty("reply").GetProperty("envelope").GetProperty("payload")).ShouldBe("synthetic optional reply");

            var delivery = root.GetProperty("delivery");
            var plain = delivery.GetProperty("plain");
            var encrypted = delivery.GetProperty("encrypted");
            Decode(plain).ShouldBe("synthetic plaintext body");
            Decode(encrypted).ShouldNotContain("synthetic encrypted body");
            encrypted.GetProperty("payloadEncoding").GetString().ShouldBe("demo/aes-256-gcm");
            var active = delivery.GetProperty("activePayloads").EnumerateArray().ToArray();
            active.Length.ShouldBe(2);
            foreach (var payload in new[] { plain, encrypted })
            {
                var stored = active.Single(r => r.GetProperty("messageId").GetString() == payload.GetProperty("messageId").GetString());
                stored.GetProperty("state").GetInt32().ShouldBe(0);
                stored.GetProperty("bytes").GetString().ShouldBe(payload.GetProperty("bytes").GetString());
            }
            delivery.GetProperty("firstReceipt").GetProperty("state").GetString().ShouldBe("pending");
            delivery.GetProperty("retryReceipt").GetRawText().ShouldBe(delivery.GetProperty("firstReceipt").GetRawText());
            var firstPull = delivery.GetProperty("firstPull").GetProperty("items");
            firstPull.GetArrayLength().ShouldBe(2);
            var repeated = delivery.GetProperty("repeatedPull").GetProperty("items");
            repeated.GetArrayLength().ShouldBe(2);
            delivery.GetProperty("sse").GetArrayLength().ShouldBe(2);
            delivery.GetProperty("afterPullReceipt").GetProperty("state").GetString().ShouldBe("pending");
            delivery.GetProperty("afterSseReceipt").GetProperty("state").GetString().ShouldBe("pending");
            delivery.GetProperty("afterRestartPull").GetProperty("items").GetArrayLength().ShouldBe(2);
            var local = delivery.GetProperty("localAfterRestart");
            var receipts = local.GetProperty("receipts").EnumerateArray().ToArray();
            foreach (var payload in new[] { plain, encrypted })
            {
                var id = payload.GetProperty("messageId").GetString();
                var matches = receipts.Where(r => r.GetProperty("messageId").GetString() == id).ToArray();
                matches.Length.ShouldBe(1);
                matches[0].GetProperty("deliveries").GetInt32().ShouldBe(4);
            }
            receipts.Single(r => r.GetProperty("messageId").GetString() == encrypted.GetProperty("messageId").GetString())
                .GetProperty("text").GetString().ShouldBe("synthetic encrypted body");
            delivery.GetProperty("ack").GetProperty("state").GetString().ShouldBe("acknowledged");
            delivery.GetProperty("repeatedAck").GetRawText().ShouldBe(delivery.GetProperty("ack").GetRawText());
            delivery.GetProperty("afterAckPull").GetProperty("items").GetArrayLength().ShouldBe(0);

            var lifecycle = root.GetProperty("lifecycle");
            lifecycle.GetProperty("expiredReceipt").GetProperty("state").GetString().ShouldBe("expired");
            lifecycle.GetProperty("expiredPull").GetProperty("items").GetArrayLength().ShouldBe(0);
            lifecycle.GetProperty("blockedReceipt").GetProperty("state").GetString().ShouldBe("blocked");
            lifecycle.GetProperty("blockedSendStatus").GetInt32().ShouldBe(404);
            lifecycle.GetProperty("blockedContactStatus").GetInt32().ShouldBe(404);
            lifecycle.GetProperty("afterUnblockPull").GetProperty("items").GetArrayLength().ShouldBe(0);
            lifecycle.GetProperty("staleSendStatus").GetInt32().ShouldBe(409);
            lifecycle.GetProperty("freshContact").GetProperty("isConnected").GetBoolean().ShouldBeTrue();
            lifecycle.GetProperty("freshReceipt").GetProperty("state").GetString().ShouldBe("pending");
            lifecycle.GetProperty("afterRelayRestartReceipt").GetProperty("state").GetString().ShouldBe("acknowledged");

            var direct = root.GetProperty("direct");
            direct.GetProperty("first").GetProperty("accepted").GetBoolean().ShouldBeTrue();
            direct.GetProperty("retry").GetProperty("duplicate").GetBoolean().ShouldBeTrue();
            direct.GetProperty("local").GetProperty("receipts").EnumerateArray()
                .Single(r => r.GetProperty("messageId").GetString() == direct.GetProperty("payload").GetProperty("messageId").GetString())
                .GetProperty("text").GetString().ShouldBe("synthetic direct body");
            direct.GetProperty("outboxStatus").GetInt32().ShouldBe(404);
            direct.GetProperty("relayRowCount").GetInt32().ShouldBe(0);

            var alice = ReadState(directory, "alice");
            var bob = ReadState(directory, "bob");
            alice.GetProperty("mailboxId").GetString().ShouldBe("alice/group");
            bob.GetProperty("mailboxId").GetString().ShouldBe("bob/team%2F界");
            alice.GetProperty("receipts").GetArrayLength().ShouldBe(1);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "relay.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM mailbox_messages WHERE id=$id";
            command.Parameters.AddWithValue("$id", direct.GetProperty("payload").GetProperty("messageId").GetString());
            Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
            command.Parameters.Clear();
            command.CommandText = "SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL OR payload_size<>0";
            Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
            command.CommandText = "SELECT count(*) FROM mailbox_messages WHERE state=1";
            Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThanOrEqualTo(5);
            var logs = root.GetProperty("relayLog").GetString().ShouldNotBeNull();
            logs.ShouldNotContain("synthetic plaintext body");
            logs.ShouldNotContain("synthetic encrypted body");
            logs.ShouldNotContain("X-Weave-Mailbox-Control");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ProcessProbe_LiveAndStoppedChild_HasNonvacuousControls()
    {
        var start = new ProcessStartInfo("node") { UseShellExecute = false };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("setInterval(() => {}, 1000)");
        using var process = new Process { StartInfo = start };
        process.Start().ShouldBeTrue();
        try
        {
            var failure = await Record.ExceptionAsync(() => RequireStopped([process.Id]));
            failure.ShouldBeOfType<Shouldly.ShouldAssertException>();
            failure.Message.ShouldContain("[True]");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
        }
        await RequireStopped([process.Id]);
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("rpc")]
    public async Task Demo_NoncooperativeChildCancellation_ObservesBoundedExitAndDrain(string stage)
    {
        var directory = Path.Combine(Path.GetTempPath(), "weave-cancel-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var repository = RepositoryRoot();
            var start = new ProcessStartInfo("node") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(repository, "tests", "Weave.Mailboxes.Tests", "Acceptance", "Fixtures", "cancellation-probe.mjs"));
            start.ArgumentList.Add(stage);
            start.ArgumentList.Add(new Uri(Path.Combine(repository, "examples", "agent-network", "run-demo.ts")).AbsoluteUri);
            start.Environment["WEAVE_DEMO_HOST"] = Path.Combine(AppContext.BaseDirectory, "Weave.Mailbox.Host.dll");
            var output = await RunProcess(start);
            output.ExitCode.ShouldBe(0, output.Error);
            using var report = JsonDocument.Parse(output.Output);
            var result = report.RootElement;
            result.GetProperty("livePositiveControl").GetBoolean().ShouldBeTrue();
            result.GetProperty("settledWithoutWatchdog").GetBoolean().ShouldBeTrue();
            result.GetProperty("allChildrenClosed").GetBoolean().ShouldBeTrue();
            result.GetProperty("drainsObserved").GetBoolean().ShouldBeTrue();
            result.GetProperty("stoppedSignalControls").GetBoolean().ShouldBeTrue();
            result.GetProperty("elapsedAfterCancelMs").GetInt32().ShouldBeLessThan(10000);
            result.GetProperty("outcome").GetString().ShouldBe("AbortError");
            if (!OperatingSystem.IsWindows())
                result.GetProperty("noncooperativeSignal").GetString().ShouldBe("SIGKILL");
            await RequireStopped(result.GetProperty("stoppedPids").EnumerateArray().Select(p => p.GetInt32()).ToArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("cooperative-error")]
    [InlineData("cooperative-result")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public async Task Demo_CooperativeLateResponse_RecognizesOnlyOneIssuedReply(string stage)
    {
        var directory = Path.Combine(Path.GetTempPath(), "weave-cooperative-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var repository = RepositoryRoot();
            var start = new ProcessStartInfo("node") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(repository, "tests", "Weave.Mailboxes.Tests", "Acceptance", "Fixtures", "cancellation-probe.mjs"));
            start.ArgumentList.Add(stage);
            start.ArgumentList.Add(new Uri(Path.Combine(repository, "examples", "agent-network", "run-demo.ts")).AbsoluteUri);
            start.Environment["WEAVE_DEMO_HOST"] = Path.Combine(AppContext.BaseDirectory, "Weave.Mailbox.Host.dll");
            var output = await RunProcess(start);
            output.ExitCode.ShouldBe(0, output.Error);
            using var report = JsonDocument.Parse(output.Output);
            var result = report.RootElement;
            result.GetProperty("livePositiveControl").GetBoolean().ShouldBeTrue();
            result.GetProperty("settledWithoutWatchdog").GetBoolean().ShouldBeTrue();
            result.GetProperty("allChildrenClosed").GetBoolean().ShouldBeTrue();
            result.GetProperty("stoppedSignalControls").GetBoolean().ShouldBeTrue();
            result.GetProperty("selectedExitCode").GetInt32().ShouldBe(0);
            result.GetProperty("noncooperativeSignal").ValueKind.ShouldBe(JsonValueKind.Null);
            result.GetProperty("commandsAfterCancel").GetInt32().ShouldBe(0);
            result.GetProperty("elapsedAfterCancelMs").GetInt32().ShouldBeLessThan(10000);
            await RequireStopped(result.GetProperty("stoppedPids").EnumerateArray().Select(p => p.GetInt32()).ToArray());
            if (stage.StartsWith("cooperative", StringComparison.Ordinal))
            {
                result.GetProperty("outcome").GetString().ShouldBe("AbortError");
                result.GetProperty("drainsObserved").GetBoolean().ShouldBeTrue();
                result.GetProperty("defaultStateRemoved").GetBoolean().ShouldBeTrue();
            }
            else
            {
                result.GetProperty("outcome").GetString().ShouldBe("AssertionError");
                result.GetProperty("errorMessage").GetString().ShouldBe("Child process cleanup failed");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Demo_RelativeRetainedDirectory_UsesOneRootAndPreservesContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "weave-relative-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var sentinel = Path.Combine(directory, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "synthetic retained marker", TestContext.Current.CancellationToken);
            using var report = await RunDemo("retained", directory);
            await RequireStopped(report.RootElement.GetProperty("processes").EnumerateArray().Select(p => p.GetProperty("pid").GetInt32()).ToArray());
            File.ReadAllText(sentinel).ShouldBe("synthetic retained marker");
            var state = Path.Combine(directory, "retained");
            File.Exists(Path.Combine(state, "relay.db")).ShouldBeTrue();
            File.Exists(Path.Combine(state, "alice", "state.json")).ShouldBeTrue();
            File.Exists(Path.Combine(state, "bob", "state.json")).ShouldBeTrue();
            Directory.Exists(Path.Combine(state, "retained")).ShouldBeFalse();
            Directory.GetDirectories(state).Select(Path.GetFileName).Order().ShouldBe(StateDirectories);
            var start = new ProcessStartInfo("node") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "examples", "agent-network", "run-demo.ts"));
            start.ArgumentList.Add("--state-directory");
            start.ArgumentList.Add("retained");
            start.Environment["WEAVE_DEMO_HOST"] = Path.Combine(AppContext.BaseDirectory, "Weave.Mailbox.Host.dll");
            var existing = Directory.GetFiles(state, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllBytes);
            var refused = await RunProcess(start);
            refused.ExitCode.ShouldNotBe(0);
            foreach (var file in existing)
                File.ReadAllBytes(file.Key).ShouldBe(file.Value);
            File.ReadAllText(sentinel).ShouldBe("synthetic retained marker");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task RequireStopped(int[] pids)
    {
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("function alive(pid) { try { process.kill(pid, 0); return true; } catch (error) { if (error.code === 'ESRCH') return false; throw error; } } console.log(JSON.stringify({ currentLive: alive(process.pid), observed: JSON.parse(process.argv[1]).map(alive) }));");
        start.ArgumentList.Add("[" + string.Join(",", pids) + "]");
        var output = await RunProcess(start);
        output.ExitCode.ShouldBe(0, output.Error);
        using var document = JsonDocument.Parse(output.Output);
        document.RootElement.GetProperty("currentLive").GetBoolean().ShouldBeTrue();
        document.RootElement.GetProperty("observed").EnumerateArray().Select(value => value.GetBoolean()).ShouldAllBe(value => !value);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Weave.slnx")))
            directory = directory.Parent;
        return directory.ShouldNotBeNull().FullName;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcess(ProcessStartInfo start)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        using var process = new Process { StartInfo = start };
        process.Start().ShouldBeTrue();
        var stdout = Drain(process.StandardOutput, timeout.Token);
        var stderr = Drain(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token);
        }
    }

    private static string Decode(JsonElement payload) => Encoding.UTF8.GetString(Convert.FromBase64String(payload.GetProperty("bytes").GetString()!));
    private static JsonElement ReadState(string directory, string endpoint)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, endpoint, "state.json")));
        return document.RootElement.Clone();
    }
    private static async Task<JsonDocument> RunDemo(string directory, string? workingDirectory = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Weave.slnx")))
            root = root.Parent;
        root.ShouldNotBeNull();
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (workingDirectory is not null)
            start.WorkingDirectory = workingDirectory;
        start.ArgumentList.Add(Path.Combine(root.FullName, "examples", "agent-network", "run-demo.ts"));
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--state-directory");
        start.ArgumentList.Add(directory);
        start.Environment["WEAVE_DEMO_HOST"] = Path.Combine(AppContext.BaseDirectory, "Weave.Mailbox.Host.dll");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var process = new Process { StartInfo = start };
        process.Start().ShouldBeTrue();
        var stdout = Drain(process.StandardOutput, timeout.Token);
        var stderr = Drain(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            process.ExitCode.ShouldBe(0, await stderr);
            return JsonDocument.Parse(await stdout);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token);
        }
    }
    private static async Task<string> Drain(StreamReader reader, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            output.Length.ShouldBeLessThan(262144 - read);
            output.Append(buffer, 0, read);
        }
        return output.ToString();
    }
}
