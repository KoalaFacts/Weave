using System.Net;
using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Workspace;

/// <summary>
/// Phase 2 write verb. POSTs a prepared <c>WorkspaceManifest</c> to
/// <c>POST /api/workspaces</c> and translates the silo's response.
/// </summary>
/// <remarks>
/// The frontend handles file I/O and relative-path resolution before
/// calling; the action's job is the HTTP envelope and result mapping. The
/// silo is the source of truth for structural validation, so 400 responses
/// surface as <see cref="ActionFailureReason.ValidationFailed"/> with the
/// silo's per-key error list joined into the message.
/// </remarks>
public sealed class StartWorkspaceAction
{
    private readonly HttpClient _httpClient;

    public StartWorkspaceAction(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ActionResult<StartWorkspaceResult>> ExecuteAsync(
        StartWorkspaceInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Manifest);
        if (input.Capability is { } encoded && (encoded.Length is 0 or > 16_384
            || encoded.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
            return ActionResult.Failed<StartWorkspaceResult>(
                ActionFailure.ValidationFailed("The installation capability is not a single encoded token."));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspaces")
            {
                Content = JsonContent.Create(new StartWorkspaceWire { Manifest = input.Manifest },
                    WorkspaceJsonContext.Default.StartWorkspaceWire)
            };
            if (input.Capability is not null)
                request.Headers.Add("X-Weave-Capability", input.Capability);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.Created or HttpStatusCode.OK => await ReadSuccessAsync(response, cancellationToken),
                HttpStatusCode.BadRequest => await ReadValidationFailureAsync(response, cancellationToken),
                HttpStatusCode.Conflict => await ReadConflictAsync(response, cancellationToken),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ActionResult.Failed<StartWorkspaceResult>(
                    ActionFailure.Unauthorized($"Silo refused the start request ({(int)response.StatusCode}).")),
                _ when (int)response.StatusCode >= 500 => ActionResult.Failed<StartWorkspaceResult>(
                    ActionFailure.Internal($"Silo error starting workspace ({(int)response.StatusCode}).")),
                _ => ActionResult.Failed<StartWorkspaceResult>(
                    ActionFailure.Internal($"Unexpected silo response ({(int)response.StatusCode}).")),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<StartWorkspaceResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<StartWorkspaceResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }

    private static async Task<ActionResult<StartWorkspaceResult>> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var wire = await response.Content.ReadFromJsonAsync(
            WorkspaceJsonContext.Default.WorkspaceWire,
            cancellationToken);

        if (wire is null)
        {
            return ActionResult.Failed<StartWorkspaceResult>(
                ActionFailure.Internal("Silo returned an empty workspace payload."));
        }

        return ActionResult.Success(new StartWorkspaceResult(new WorkspaceStatusSummary
        {
            WorkspaceId = wire.WorkspaceId,
            Name = wire.Name,
            Status = wire.Status,
            ContainerCount = wire.ContainerCount,
            StartedAt = wire.StartedAt,
            StoppedAt = wire.StoppedAt,
            NetworkId = wire.NetworkId,
            ErrorMessage = wire.ErrorMessage
        }));
    }

    private static async Task<ActionResult<StartWorkspaceResult>> ReadValidationFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var problem = await response.Content.ReadFromJsonAsync(
            WorkspaceJsonContext.Default.WorkspaceProblemWire,
            cancellationToken);
        var message = FormatValidationErrors(problem) ?? "Manifest is invalid.";
        return ActionResult.Failed<StartWorkspaceResult>(
            ActionFailure.ValidationFailed(message));
    }

    private static async Task<ActionResult<StartWorkspaceResult>> ReadConflictAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var problem = await response.Content.ReadFromJsonAsync(
            WorkspaceJsonContext.Default.WorkspaceProblemWire,
            cancellationToken);
        var detail = problem?.Detail;
        return ActionResult.Failed<StartWorkspaceResult>(
            ActionFailure.Conflict(string.IsNullOrWhiteSpace(detail)
                ? "Workspace is already running or in a conflicting state."
                : detail));
    }

    // The silo emits ValidationProblem with a per-field errors map (e.g.
    // "manifest.name": ["Name is required."]). Surface "key: message" for
    // each, joined into one line so the frontend can render it as-is.
    private static string? FormatValidationErrors(WorkspaceProblemWire? problem)
    {
        if (problem?.Errors is not { } errors || errors.Count == 0)
            return null;

        return string.Join("; ",
            errors.SelectMany(kvp => kvp.Value.Select(message => $"{kvp.Key}: {message}")));
    }
}
