namespace Weave.Actions.SystemInfo;

/// <summary>
/// Frontend-supplied seam that returns the static snapshot of local config
/// the system-info view stitches together with a silo probe. The CLI
/// implementation reads <c>~/.weave/config.json</c>; tests inject a fake.
/// </summary>
public interface ISystemConfigSource
{
    SystemConfigSnapshot Load();
}
