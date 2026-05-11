using System.Text.Json;

namespace Weave.Actions.Channel;

public sealed record PostChannelInput(string WorkspaceId, JsonElement Channel);
