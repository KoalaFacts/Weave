using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Workspace;

/// <summary>
/// Phase 1 read-only verb. Reads the manifest from disk (frontend
/// orchestration: the CLI has the path; the Dashboard has the upload), then
/// POSTs the JSON(C) text to the silo's <c>/api/workspaces/validate</c>
/// endpoint, which owns parsing + structural validation. The silo is the
/// single source of truth for what counts as a valid manifest; CLI / TUI /
/// Dashboard / LSP all converge here without duplicating the parser.
/// </summary>
public sealed class ValidateWorkspaceAction
{
    private readonly HttpClient _httpClient;

    public ValidateWorkspaceAction(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ActionResult<ValidateWorkspaceResult>> ExecuteAsync(
        ValidateWorkspaceInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ManifestPath);

        if (!File.Exists(input.ManifestPath))
        {
            return ActionResult.Failed<ValidateWorkspaceResult>(
                ActionFailure.NotFound($"Manifest not found at '{input.ManifestPath}'."));
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(input.ManifestPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ValidateWorkspaceResult>(ActionFailure.Cancelled());
        }
        catch (IOException ex)
        {
            return ActionResult.Failed<ValidateWorkspaceResult>(
                ActionFailure.Internal($"Could not read manifest: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return ActionResult.Failed<ValidateWorkspaceResult>(
                ActionFailure.Unauthorized($"Could not read manifest: {ex.Message}"));
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/workspaces/validate",
                new ValidateWorkspaceWire(json),
                ValidateWorkspaceJsonContext.Default.ValidateWorkspaceWire,
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var problem = await response.Content.ReadFromJsonAsync(
                    ValidateWorkspaceJsonContext.Default.ProblemWire,
                    cancellationToken);
                var message = ExtractProblemMessage(problem) ?? "Manifest is not valid JSON.";
                return ActionResult.Failed<ValidateWorkspaceResult>(
                    ActionFailure.ValidationFailed(message));
            }

            response.EnsureSuccessStatusCode();

            var wire = await response.Content.ReadFromJsonAsync(
                ValidateWorkspaceJsonContext.Default.ValidateWorkspaceResultWire,
                cancellationToken);

            if (wire is null)
            {
                return ActionResult.Failed<ValidateWorkspaceResult>(
                    ActionFailure.Internal("Silo returned an empty validation payload."));
            }

            return ActionResult.Success(new ValidateWorkspaceResult(
                Name: wire.Name,
                AgentCount: wire.AgentCount,
                ToolCount: wire.ToolCount,
                TargetCount: wire.TargetCount,
                Errors: wire.Errors ?? []));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ValidateWorkspaceResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<ValidateWorkspaceResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }

    private static string? ExtractProblemMessage(ProblemWire? problem)
    {
        if (problem?.Errors is { Count: > 0 } errors)
        {
            foreach (var entry in errors.Values)
            {
                if (entry is { Length: > 0 })
                    return entry[0];
            }
        }
        return problem?.Detail ?? problem?.Title;
    }
}
