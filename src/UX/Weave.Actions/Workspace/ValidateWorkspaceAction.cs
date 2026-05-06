using System.Text.Json;
using Weave.Actions.Context;
using Weave.Workspaces.Manifest;

namespace Weave.Actions.Workspace;

/// <summary>
/// Phase 1 read-only verb. Local-only — no <see cref="HttpClient"/> involved
/// because manifest validation is a pure file-read + parse + structural
/// check. Reads the manifest from disk, parses with
/// <see cref="ManifestParser"/>, runs <see cref="ManifestParser.Validate"/>,
/// and returns the structural summary plus any errors. IO and parse failures
/// surface as <c>ActionFailure</c> with reason <c>ValidationFailed</c>;
/// validation-error rows ride inside the result so the frontend can render
/// the per-error list.
/// </summary>
public sealed class ValidateWorkspaceAction
{
    private readonly IManifestParser _parser;

    public ValidateWorkspaceAction(IManifestParser parser)
    {
        _parser = parser;
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
            var manifest = _parser.Parse(json);
            var errors = _parser.Validate(manifest);

            return ActionResult.Success(new ValidateWorkspaceResult(
                Name: manifest.Name,
                AgentCount: manifest.Agents?.Count ?? 0,
                ToolCount: manifest.Tools?.Count ?? 0,
                TargetCount: manifest.Targets?.Count ?? 0,
                Errors: errors));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ValidateWorkspaceResult>(ActionFailure.Cancelled());
        }
        catch (JsonException ex)
        {
            return ActionResult.Failed<ValidateWorkspaceResult>(
                ActionFailure.ValidationFailed($"Manifest is not valid JSON: {ex.Message}"));
        }
        catch (FormatException ex)
        {
            return ActionResult.Failed<ValidateWorkspaceResult>(
                ActionFailure.ValidationFailed($"Manifest format error: {ex.Message}"));
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
}
