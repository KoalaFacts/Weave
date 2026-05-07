namespace Weave.Actions.Config;

/// <summary>
/// Frontend-supplied seam that <c>SetConfigAction</c> uses to persist a
/// validated key/value pair. The action validates the key against
/// <see cref="ConfigKeys.Writable"/> and parses any value-type constraints
/// (e.g. <c>defaultPort</c> must be an integer); the writer's job is to
/// apply the validated pair to its frontend-shaped storage and surface an
/// I/O failure as an exception (which the action translates to
/// <c>ActionFailureReason.Internal</c>).
/// </summary>
public interface ISystemConfigWriter
{
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}
