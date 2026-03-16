using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;

namespace Weave.Agents.Grains;

public sealed class ProofValidatorGrain(
    IAgentChatClientFactory chatClientFactory,
    ILogger<ProofValidatorGrain> logger) : Grain, IProofValidatorGrain
{
    private const string SystemPrompt = """
        You are an independent proof-of-work validator in a multi-agent system.
        Your job is to evaluate whether submitted proof items satisfy a set of verification conditions.

        For each condition, determine if the proof satisfies it based on the condition's plain-language description.

        Respond ONLY with a JSON array. Each element must have:
        - "conditionName": the condition's name
        - "passed": true or false
        - "detail": brief explanation of your reasoning

        Example:
        [{"conditionName":"ci-passing","passed":true,"detail":"CI status shows 'passed' which indicates success"}]

        Be strict but fair. Evaluate based on the evidence provided.
        """;

    public async Task<VerificationVote> ValidateAsync(
        string validatorId,
        ProofOfWork proof,
        List<VerificationCondition> conditions,
        string? modelId = null)
    {
        if (proof.Items.Count == 0)
        {
            return new VerificationVote
            {
                ValidatorId = validatorId,
                Accepted = false,
                Reason = "No proof items provided.",
                ConditionResults = []
            };
        }

        try
        {
            var chatClient = chatClientFactory.Create($"validator-{validatorId}", modelId);
            var userMessage = BuildUserMessage(proof, conditions);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, userMessage)
            };

            var response = await chatClient.GetResponseAsync(messages);
            var results = ParseConditionResults(response.Text ?? "[]");

            // Enforce non-empty values for all proof items
            foreach (var item in proof.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Value))
                {
                    results.Add(new ConditionResult
                    {
                        ConditionName = "non-empty-value",
                        Passed = false,
                        Detail = $"{item.Label}: empty value"
                    });
                }
            }

            var failures = results.Where(r => !r.Passed).ToList();
            var accepted = failures.Count == 0;
            var reason = accepted
                ? "All conditions satisfied."
                : string.Join("; ", failures.Select(f => $"{f.ConditionName}: {f.Detail}"));

            logger.LogInformation(
                "Validator {ValidatorId} voted {Accepted} with {ConditionCount} conditions evaluated",
                validatorId,
                accepted,
                results.Count);

            return new VerificationVote
            {
                ValidatorId = validatorId,
                Accepted = accepted,
                Reason = reason,
                ConditionResults = results
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Validator {ValidatorId} failed to evaluate proof", validatorId);

            return new VerificationVote
            {
                ValidatorId = validatorId,
                Accepted = false,
                Reason = $"Validation error: {ex.Message}",
                ConditionResults = []
            };
        }
    }

    internal static string BuildUserMessage(ProofOfWork proof, List<VerificationCondition> conditions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Proof Items");
        foreach (var item in proof.Items)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **{item.Label}** (Type: {item.Type}): {item.Value}");
            if (!string.IsNullOrWhiteSpace(item.Uri))
                sb.AppendLine(CultureInfo.InvariantCulture, $"  URI: {item.Uri}");
        }

        sb.AppendLine();
        sb.AppendLine("## Conditions to Evaluate");
        foreach (var condition in conditions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **{condition.Name}**: {condition.Description}");
        }

        return sb.ToString();
    }

    internal static List<ConditionResult> ParseConditionResults(string responseText)
    {
        try
        {
            var json = responseText;
            var startIdx = json.IndexOf('[');
            var endIdx = json.LastIndexOf(']');
            if (startIdx >= 0 && endIdx > startIdx)
                json = json[startIdx..(endIdx + 1)];

            var parsed = JsonSerializer.Deserialize(json, ProofValidatorJsonContext.Default.ListProofConditionResultDto);
            if (parsed is null)
                return [];

            return parsed.Select(dto => new ConditionResult
            {
                ConditionName = dto.ConditionName ?? "unknown",
                Passed = dto.Passed,
                Detail = dto.Detail
            }).ToList();
        }
        catch
        {
            return [];
        }
    }
}
