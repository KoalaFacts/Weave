using Weave.Workspaces.Models;

namespace Weave.Silo.Plugins;

internal static class PluginConfigResolver
{
    public static PluginDefinition Resolve(PluginDefinition definition, PluginSchema schema)
    {
        var resolved = new Dictionary<string, string>(definition.Config, StringComparer.OrdinalIgnoreCase);

        foreach (var field in schema.Config)
        {
            if (resolved.ContainsKey(field.Name))
                continue;

            if (field.EnvVar is not null)
            {
                var envValue = Environment.GetEnvironmentVariable(field.EnvVar);
                if (envValue is not null)
                {
                    resolved[field.Name] = envValue;
                    continue;
                }
            }

            if (field.Default is not null)
                resolved[field.Name] = field.Default;
        }

        return definition with { Config = resolved };
    }

    public static string? Validate(PluginDefinition definition, PluginSchema schema)
    {
        var missing = new List<string>();
        foreach (var field in schema.Config)
        {
            if (field.Required && !definition.Config.ContainsKey(field.Name))
                missing.Add(field.EnvVar is not null
                    ? $"'{field.Name}' (or set {field.EnvVar})"
                    : $"'{field.Name}'");
        }

        return missing.Count > 0
            ? $"Missing required config: {string.Join(", ", missing)}"
            : null;
    }

    public static IReadOnlyDictionary<string, string> RedactSecrets(
        IReadOnlyDictionary<string, string> info,
        PluginSchema schema)
    {
        var secretNames = new HashSet<string>(
            schema.Config.Where(f => f.Secret).Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);

        if (secretNames.Count == 0)
            return info;

        return info.ToDictionary(
            kvp => kvp.Key,
            kvp => secretNames.Contains(kvp.Key) ? "***" : kvp.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}
