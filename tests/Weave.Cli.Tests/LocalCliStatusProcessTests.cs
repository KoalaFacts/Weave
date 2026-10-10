using System.Text.Json.Nodes;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class LocalCliStatusProcessTests
{
    [Fact]
    public Task StatusAsync_RecordedPendingMissingAndDenied_PreservesOriginalEvidenceAndSeparatesCredentials() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalCliStatusProcessTests), async root =>
        {
            const string id = "82c07b3d2a3646e88f2f0b8db07c452b";
            const string route = "/api/workspaces/onboarding/tools/files/invocations/" + id;
            string[] states = ["recorded", "pending", "missing", "denied"];
            foreach (var state in states)
            {
                var invocationStatus = state == "recorded" ? 200 : state == "denied" ? 403 : 404;
                var invocation = state == "recorded"
                    ? new JsonObject
                    {
                        ["invocationId"] = id,
                        ["toolName"] = "files",
                        ["attemptId"] = "f2a6d14f43f24f52a6e7b649f891c186",
                        ["outcome"] = "OutcomeUnknown",
                        ["outcomeRecorded"] = false,
                        ["success"] = false
                    }
                    : new JsonObject { ["errorCode"] = state == "denied" ? "forbidden" : "invocation-not-found", ["marker"] = state };
                var approval = state == "pending"
                    ? new JsonObject { ["invocationId"] = id, ["approvalState"] = "Pending", ["marker"] = "retained-approval" }
                    : new JsonObject { ["errorCode"] = "approval-not-found" };
                List<LocalCliHttpStep> steps =
                [
                    new LocalCliHttpStep("POST", "/api/operator/credentials/agent/issue", 200, LocalCliBoundaryFixture.Capability, true),
                    new("GET", route, invocationStatus, invocation.ToJsonString())
                ];
                if (invocationStatus == 404)
                    steps.Add(new("GET", route + "/approval", state == "pending" ? 200 : 404, approval.ToJsonString()));
                await using var server = new LocalCliLoopbackServer([.. steps]);
                using var fixture = new LocalCliBoundaryFixture(Path.Join(root, state), server.Port);
                var result = await fixture.Command.StatusAsync(fixture.DirectoryPath,
                    "82C07B3D-2A36-46E8-8F2F-0B8DB07C452B", TestContext.Current.CancellationToken);
                await server.CompleteAsync();

                result.ShouldBe(state is "recorded" or "pending" ? 0 : 1);
                var output = JsonNode.Parse(fixture.Output.ToString()).ShouldNotBeNull();
                output["invocation_id"].ShouldNotBeNull().GetValue<string>().ShouldBe(id);
                output["execution_state"].ShouldNotBeNull().GetValue<string>()
                    .ShouldBe(state == "recorded" ? "OutcomeUnknown" : state == "pending" ? "NotStarted" : "Unconfirmed");
                output["invocation"].ShouldNotBeNull()["http_status"].ShouldNotBeNull().GetValue<int>().ShouldBe(invocationStatus);
                JsonNode.DeepEquals(output["invocation"].ShouldNotBeNull()["result"], invocation).ShouldBeTrue();
                if (invocationStatus == 404)
                    JsonNode.DeepEquals(output["approval"].ShouldNotBeNull()["result"], approval).ShouldBeTrue();
                else
                    output["approval"].ShouldBeNull();
                server.Headers[0]["X-Weave-Operator-Key"].ShouldBe(LocalCliBoundaryFixture.OperatorKey);
                server.Headers[0].ContainsKey("X-Weave-Capability").ShouldBeFalse();
                foreach (var query in server.Headers.Skip(1))
                {
                    query["X-Weave-Capability"].ShouldBe(LocalCliBoundaryFixture.Capability);
                    query.ContainsKey("X-Weave-Operator-Key").ShouldBeFalse();
                    query.ContainsKey("Authorization").ShouldBeFalse();
                }
                fixture.Output.ToString().ShouldNotContain(LocalCliBoundaryFixture.Capability);
                fixture.Output.ToString().ShouldNotContain(LocalCliBoundaryFixture.OperatorKey);
                fixture.AssertPreserved();
            }
        });
}
