using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;

namespace Weave.Agents.Tests;

public sealed class ProofValidatorGrainTests
{
    private static ProofValidatorGrain CreateValidator(IChatClient? chatClient = null)
    {
        var factory = Substitute.For<IAgentChatClientFactory>();
        var client = chatClient ?? CreateAcceptingChatClient();
        factory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(client);
        var logger = Substitute.For<ILogger<ProofValidatorGrain>>();
        return new ProofValidatorGrain(factory, logger);
    }

    private static ProofValidatorGrain CreateValidatorWithFactory(out IAgentChatClientFactory factory)
    {
        factory = Substitute.For<IAgentChatClientFactory>();
        var client = CreateAcceptingChatClient();
        factory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(client);
        var logger = Substitute.For<ILogger<ProofValidatorGrain>>();
        return new ProofValidatorGrain(factory, logger);
    }

    private static List<VerificationCondition> DefaultConditions() =>
    [
        new() { Name = "ci-passing", Description = "The CI/build status must indicate the build passed successfully." },
        new() { Name = "tests-passing", Description = "Test results must not indicate any failures." }
    ];

    private static IChatClient CreateAcceptingChatClient(List<ConditionResult>? results = null)
    {
        var client = Substitute.For<IChatClient>();
        results ??=
        [
            new() { ConditionName = "ci-passing", Passed = true, Detail = "CI status shows passing" },
            new() { ConditionName = "tests-passing", Passed = true, Detail = "No test failures detected" }
        ];
        var json = System.Text.Json.JsonSerializer.Serialize(results.Select(r => new
        {
            conditionName = r.ConditionName,
            passed = r.Passed,
            detail = r.Detail
        }));
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, json)]);
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(response);
        return client;
    }

    private static IChatClient CreateRejectingChatClient(string conditionName = "ci-passing", string detail = "CI status shows failure")
    {
        var results = new List<ConditionResult>
        {
            new() { ConditionName = conditionName, Passed = false, Detail = detail }
        };
        return CreateAcceptingChatClient(results);
    }

    [Fact]
    public async Task ValidateAsync_AiAccepts_ReturnsAccepted()
    {
        var validator = CreateValidator();
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        var vote = await validator.ValidateAsync("validator-0", proof, DefaultConditions());

        vote.Accepted.ShouldBeTrue();
        vote.ValidatorId.ShouldBe("validator-0");
        vote.ConditionResults.ShouldContain(r => r.ConditionName == "ci-passing" && r.Passed);
    }

    [Fact]
    public async Task ValidateAsync_AiRejects_ReturnsRejected()
    {
        var chatClient = CreateRejectingChatClient();
        var validator = CreateValidator(chatClient);
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "Build", Value = "red" }]
        };

        var vote = await validator.ValidateAsync("validator-0", proof, DefaultConditions());

        vote.Accepted.ShouldBeFalse();
        vote.Reason.ShouldContain("ci-passing");
    }

    [Fact]
    public async Task ValidateAsync_EmptyProofItems_RejectsWithoutCallingAi()
    {
        var chatClient = Substitute.For<IChatClient>();
        var validator = CreateValidator(chatClient);
        var proof = new ProofOfWork { Items = [] };

        var vote = await validator.ValidateAsync("validator-0", proof, DefaultConditions());

        vote.Accepted.ShouldBeFalse();
        vote.Reason.ShouldBe("No proof items provided.");
        await chatClient.DidNotReceive()
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_EmptyValue_RejectsForNonEmptyCheck()
    {
        var validator = CreateValidator();
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "" }]
        };

        var vote = await validator.ValidateAsync("validator-0", proof, DefaultConditions());

        vote.Accepted.ShouldBeFalse();
        vote.Reason.ShouldContain("empty value");
        vote.ConditionResults.ShouldContain(r => r.ConditionName == "non-empty-value" && !r.Passed);
    }

    [Fact]
    public async Task ValidateAsync_ChatClientThrows_ReturnsRejection()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new InvalidOperationException("API unavailable"));
        var validator = CreateValidator(chatClient);
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        var vote = await validator.ValidateAsync("validator-0", proof, DefaultConditions());

        vote.Accepted.ShouldBeFalse();
        vote.Reason.ShouldContain("API unavailable");
    }

    [Fact]
    public async Task ValidateAsync_PassesModelIdToFactory()
    {
        var validator = CreateValidatorWithFactory(out var factory);
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        await validator.ValidateAsync("validator-0", proof, DefaultConditions(), "gpt-4o");

        factory.Received(1).Create("validator-validator-0", "gpt-4o");
    }

    [Fact]
    public async Task ValidateAsync_NullModelId_PassesNullToFactory()
    {
        var validator = CreateValidatorWithFactory(out var factory);
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        await validator.ValidateAsync("validator-0", proof, DefaultConditions());

        factory.Received(1).Create("validator-validator-0", null);
    }

    [Fact]
    public void BuildUserMessage_IncludesProofItemsAndConditions()
    {
        var proof = new ProofOfWork
        {
            Items =
            [
                new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed", Uri = "https://ci.example.com/1" },
                new ProofItem { Type = ProofType.TestResults, Label = "Tests", Value = "42 passed" }
            ]
        };
        var conditions = DefaultConditions();

        var message = ProofValidatorGrain.BuildUserMessage(proof, conditions);

        message.ShouldContain("CI");
        message.ShouldContain("passed");
        message.ShouldContain("https://ci.example.com/1");
        message.ShouldContain("42 passed");
        message.ShouldContain("ci-passing");
        message.ShouldContain("tests-passing");
    }

    [Fact]
    public void ParseConditionResults_ValidJson_ReturnsResults()
    {
        var json = """[{"conditionName":"ci-passing","passed":true,"detail":"looks good"}]""";

        var results = ProofValidatorGrain.ParseConditionResults(json);

        results.Count.ShouldBe(1);
        results[0].ConditionName.ShouldBe("ci-passing");
        results[0].Passed.ShouldBeTrue();
        results[0].Detail.ShouldBe("looks good");
    }

    [Fact]
    public void ParseConditionResults_JsonInMarkdown_ExtractsArray()
    {
        var wrapped = """
            Here is my analysis:
            ```json
            [{"conditionName":"ci-passing","passed":false,"detail":"build failed"}]
            ```
            """;

        var results = ProofValidatorGrain.ParseConditionResults(wrapped);

        results.Count.ShouldBe(1);
        results[0].Passed.ShouldBeFalse();
    }

    [Fact]
    public void ParseConditionResults_InvalidJson_ReturnsEmpty()
    {
        var results = ProofValidatorGrain.ParseConditionResults("this is not json");

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_RecordsConditionResults()
    {
        var validator = CreateValidator();
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "success" }]
        };

        var vote = await validator.ValidateAsync("validator-0", proof, DefaultConditions());

        vote.ConditionResults.Count.ShouldBeGreaterThan(0);
        foreach (var result in vote.ConditionResults)
        {
            result.ConditionName.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
