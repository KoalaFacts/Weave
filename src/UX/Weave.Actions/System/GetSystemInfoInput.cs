namespace Weave.Actions.System;

/// <summary>
/// Input shape for <see cref="GetSystemInfoAction"/>. Empty today — the action
/// reads its data from <see cref="ISystemConfigSource"/> and probes the silo
/// itself. Kept as a named record so the action signature stays consistent
/// with every other typed-input action in the layer.
/// </summary>
public sealed record GetSystemInfoInput;
