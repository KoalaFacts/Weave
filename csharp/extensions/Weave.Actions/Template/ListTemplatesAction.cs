using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Template;

/// <summary>
/// Phase 4b verb. Lists silo-wide capability templates as opaque JSON, used
/// by workspace export to roundtrip the silo's current template catalog.
/// </summary>
public sealed class ListTemplatesAction(HttpClient httpClient)
{
    public async Task<ActionResult<ListTemplatesResult>> ExecuteAsync(
        ListTemplatesInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var templates = await httpClient.GetFromJsonAsync(
                "/api/templates",
                TemplateJsonContext.Default.ListJsonElement,
                cancellationToken) ?? [];

            return ActionResult.Success(new ListTemplatesResult(templates));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ListTemplatesResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<ListTemplatesResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
