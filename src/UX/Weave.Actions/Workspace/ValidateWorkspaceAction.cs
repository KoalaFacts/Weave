using System.Net;
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

        try
        {
            var json = await File.ReadAllTextAsync(input.ManifestPath, cancellationToken);

            using var response = await _httpClient.PostAsJsonAsync(
                "/api/workspaces/validate",
                new ValidateWorkspaceWire { ManifestJson = json },
                ValidateWorkspaceJsonContext.Default.ValidateWorkspaceWire,
                cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => await ReadSuccessAsync(response, cancellationToken),
                HttpStatusCode.BadRequest => await ReadValidationFailureAsync(response, cancellationToken),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ActionResult.Failed<ValidateWorkspaceResult>(
                    ActionFailure.Unauthorized($"Silo refused the validate request ({(int)response.StatusCode}).")),
                _ when (int)response.StatusCode >= 500 => ActionResult.Failed<ValidateWorkspaceResult>(
                    ActionFailure.Internal($"Silo error validating manifest ({(int)response.StatusCode}).")),
                _ => ActionResult.Failed<ValidateWorkspaceResult>(
                    ActionFailure.Internal($"Unexpected silo response ({(int)response.StatusCode}).")),
            };
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
    }

    private static async Task<ActionResult<ValidateWorkspaceResult>> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
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
            Errors: wire.Errors));
    }

    private static async Task<ActionResult<ValidateWorkspaceResult>> ReadValidationFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var problem = await response.Content.ReadFromJsonAsync(
            ValidateWorkspaceJsonContext.Default.ProblemWire,
            cancellationToken);
        var message = ManifestJsonError(problem) ?? "Manifest is invalid JSON.";
        return ActionResult.Failed<ValidateWorkspaceResult>(
            ActionFailure.ValidationFailed(message));
    }

    // The silo's only 400-emitting code path on /validate writes a single
    // "manifestJson" key into the standard ProblemDetails errors map. Pull
    // that first message; everything else is over-engineering for a private
    // CLI/silo contract.
    private static string? ManifestJsonError(ProblemWire? problem)
        => problem?.Errors is { } errors
            && errors.TryGetValue("manifestJson", out var messages)
            && messages.Length > 0
                ? messages[0]
                : null;
}
