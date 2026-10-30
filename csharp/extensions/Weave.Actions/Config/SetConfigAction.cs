using System.Globalization;
using Weave.Actions.Context;

namespace Weave.Actions.Config;

/// <summary>
/// Phase 2 write verb. Validates a config key/value pair against
/// <see cref="ConfigKeys.Writable"/> and the per-key value-type constraints
/// (<c>defaultPort</c> must parse as a positive int, <c>siloPath</c>
/// accepts any non-blank string), then persists via
/// <see cref="ISystemConfigWriter"/>.
/// </summary>
public sealed class SetConfigAction
{
    private readonly ISystemConfigWriter _writer;

    public SetConfigAction(ISystemConfigWriter writer)
    {
        _writer = writer;
    }

    public async Task<ActionResult<SetConfigResult>> ExecuteAsync(
        SetConfigInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Key);
        ArgumentNullException.ThrowIfNull(input.Value);

        var canonical = ConfigKeys.Writable.FirstOrDefault(k =>
            string.Equals(k, input.Key, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
        {
            return ActionResult.Failed<SetConfigResult>(ActionFailure.ValidationFailed(
                $"Unknown config key '{input.Key}'. Writable keys: {string.Join(", ", ConfigKeys.Writable)}."));
        }

        if (canonical == "defaultPort"
            && (!int.TryParse(input.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port <= 0
                || port > 65535))
        {
            return ActionResult.Failed<SetConfigResult>(ActionFailure.ValidationFailed(
                $"'{input.Value}' is not a valid port (expected an integer between 1 and 65535)."));
        }

        if (canonical == "siloPath" && string.IsNullOrWhiteSpace(input.Value))
        {
            return ActionResult.Failed<SetConfigResult>(ActionFailure.ValidationFailed(
                "siloPath cannot be blank."));
        }

        try
        {
            await _writer.SetAsync(canonical, input.Value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<SetConfigResult>(ActionFailure.Cancelled());
        }
        catch (IOException ex)
        {
            return ActionResult.Failed<SetConfigResult>(ActionFailure.Internal(
                $"Could not save config: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return ActionResult.Failed<SetConfigResult>(ActionFailure.Unauthorized(
                $"Could not save config: {ex.Message}"));
        }

        return ActionResult.Success(new SetConfigResult(canonical, input.Value));
    }
}
