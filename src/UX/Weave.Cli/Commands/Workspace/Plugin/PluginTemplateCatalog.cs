using System.Collections.Frozen;

namespace Weave.Cli.Commands;

internal sealed class PluginTemplateCatalog
{
    private readonly FrozenDictionary<string, PluginTemplate> _templates =
        new Dictionary<string, PluginTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            ["dapr"] = new("dapr", "Dapr sidecar for pub/sub events and service invocation", new Dictionary<string, string> { ["port"] = "3500" }),
            ["vault"] = new("vault", "HashiCorp Vault for production secret management", new Dictionary<string, string> { ["address"] = "http://localhost:8200" }),
            ["http"] = new("http", "Generic HTTP/REST endpoint", new Dictionary<string, string> { ["base_url"] = "http://localhost:8080" }),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> PluginTypes { get; } = ["dapr", "vault", "http", "custom"];

    public bool TryGet(string type, out PluginTemplate template) => _templates.TryGetValue(type, out template!);
}
