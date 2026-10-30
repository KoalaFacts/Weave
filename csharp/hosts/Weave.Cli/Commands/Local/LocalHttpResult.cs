using System.Text.Json.Nodes;

namespace Weave.Cli.Commands.Local;

internal sealed record LocalHttpResult(int Status, JsonNode? Body)
{
    public JsonObject ToNode() => new() { ["http_status"] = Status, ["result"] = Body?.DeepClone() };
}
