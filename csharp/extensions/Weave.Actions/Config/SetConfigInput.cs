namespace Weave.Actions.Config;

/// <summary>
/// Input for <see cref="SetConfigAction"/>. Frontends pass a key from
/// <see cref="ConfigKeys.Writable"/> and the value as user-entered text;
/// the action validates the key + value shape before delegating to
/// <see cref="ISystemConfigWriter"/>.
/// </summary>
public sealed record SetConfigInput(string Key, string Value);
