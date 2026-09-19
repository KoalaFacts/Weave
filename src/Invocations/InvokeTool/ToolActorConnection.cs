using Weave.Tools.Connectors;

namespace Weave.Tools.Tool;

internal sealed record ToolActorConnection(IToolConnector Connector, ToolHandle Handle);
