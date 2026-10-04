using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weave.Cli.Commands.Local;

internal sealed partial class LocalDeploymentStore : ILocalDeploymentStore
{
    public static string Resolve(string directory) => Path.GetFullPath(directory);

    public static string HostConfigurationPath(string directory) => Path.Join(directory, "private", "appsettings.json");

    public LocalSetupResult Prepare(string directory, string documents, string? host, string workspace, int port)
    {
        directory = Resolve(directory);
        documents = Path.GetFullPath(documents);
        if (!Directory.Exists(documents) || (File.GetAttributes(documents) & FileAttributes.ReparsePoint) != 0)
            return new(null, "Choose an existing document directory without a symbolic link at its root.");
        if (!NamePattern().IsMatch(workspace) || port is < 1024 or > 65535)
            return new(null, "Use a workspace name of 1-64 letters, digits, underscores or hyphens, and a port from 1024 to 65535.");
        if (LocalHostRunner.HttpPortError(port) is { } portError)
            return new(null, portError);
        if (SeparateDirectoriesError(directory, documents) is { } pathError)
            return new(null, pathError);
        host = Path.GetFullPath(host ?? Path.Join(AppContext.BaseDirectory, "host",
            OperatingSystem.IsWindows() ? "Weave.Silo.exe" : "Weave.Silo"));
        if (!File.Exists(host) || host.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return new(null, "Published Host was not found. Use the distribution containing host/, or provide --host with a published executable or DLL.");
        if (Directory.Exists(directory) || File.Exists(directory))
            return new(null, "The local configuration directory already exists. Existing state was preserved.");
        var deployment = new LocalDeployment(host, documents, workspace, port);
        LocalPrivateDirectory.Create(directory);
        File.WriteAllText(Path.Join(directory, ".gitignore"), "*\n");
        Directory.CreateDirectory(Path.Join(directory, "private"));
        Directory.CreateDirectory(Path.Join(directory, "state"));
        Directory.CreateDirectory(Path.Join(directory, "receipts"));
        File.WriteAllText(HostConfigurationPath(directory), LocalHostConfiguration.Create(directory, deployment).ToJsonString());
        return new(deployment, null);
    }

    public bool CompleteInitialization(string directory, LocalDeployment deployment)
    {
        if (!File.Exists(Path.Join(directory, "state", "invocations.db"))
            || !Directory.Exists(Path.Join(directory, "state", "revocations")))
            return false;
        var configuration = ReadHostConfiguration(directory);
        configuration["CapabilityTokens"]!["RequireExistingStorage"] = true;
        configuration["Weave"]!["Invocations"]!["RequireExistingStorage"] = true;
        File.WriteAllText(HostConfigurationPath(directory), configuration.ToJsonString());
        File.WriteAllText(Path.Join(directory, "local.json"), JsonSerializer.Serialize(deployment, LocalJsonContext.Default.LocalDeployment));
        return true;
    }

    public LocalSetupResult Load(string directory)
    {
        var marker = Path.Join(Resolve(directory), "local.json");
        if (!File.Exists(marker))
            return new(null, "No ready local deployment was found. Use local init for a new directory; preserve and investigate an interrupted setup.");
        var deployment = JsonSerializer.Deserialize(File.ReadAllText(marker), LocalJsonContext.Default.LocalDeployment);
        if (deployment is null || string.IsNullOrWhiteSpace(deployment.HostPath)
            || string.IsNullOrWhiteSpace(deployment.DocumentsPath) || string.IsNullOrWhiteSpace(deployment.Workspace))
            return new(null, "Local deployment configuration is invalid.");
        if (!NamePattern().IsMatch(deployment.Workspace) || deployment.Port is < 1024 or > 65535)
            return new(null, "Local deployment configuration is invalid.");
        if (LocalHostRunner.HttpPortError(deployment.Port) is { } portError)
            return new(null, portError);
        var configuration = ReadHostConfiguration(directory);
        if (configuration["CapabilityTokens"]?["RequireExistingStorage"]?.GetValue<bool>() != true
            || configuration["Weave"]?["Invocations"]?["RequireExistingStorage"]?.GetValue<bool>() != true)
            return new(null, "Retained storage must be required. Incomplete initialization cannot be served.");
        if (configuration["Weave"]?["Operator"]?["Enabled"]?.GetValue<bool>() != true
            || configuration["Weave"]?["Invocations"]?["Http"]?["DecisionsEnabled"]?.GetValue<bool>() != true
            || configuration["Weave"]?["Invocations"]?["ApprovalRequiredGrants"] is not JsonArray grants
            || !grants.Any(grant => grant?.GetValue<string>() == "tool:files:invoke:write_file"))
            return new(null, "This local configuration must retain protected management and required human approval for writes.");
        return new(deployment, null);
    }

    public string OperatorKey(string directory) => ReadHostConfiguration(directory)["Weave"]?["Operator"]?["Key"]?.GetValue<string>()
        ?? throw new JsonException("Missing operator credential.");

    public string SigningKey(string directory) => ReadHostConfiguration(directory)["CapabilityTokens"]?["SigningKey"]?.GetValue<string>()
        ?? throw new JsonException("Missing signing credential.");

    internal static string? SeparateDirectoriesError(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        foreach (var path in new[] { first, second })
        {
            for (var ancestor = new DirectoryInfo(path); ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    return "Choose local paths without symbolic links or directory redirection in their components.";
            }
        }
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var firstPrefix = Path.EndsInDirectorySeparator(first) ? first : first + Path.DirectorySeparatorChar;
        var secondPrefix = Path.EndsInDirectorySeparator(second) ? second : second + Path.DirectorySeparatorChar;
        if (first.Equals(second, comparison) || first.StartsWith(secondPrefix, comparison)
            || second.StartsWith(firstPrefix, comparison))
            return "Keep documents, Agent working files, private configuration and trusted executable bundles in separate directories, outside each other's roots.";
        return null;
    }

    private static JsonObject ReadHostConfiguration(string directory) =>
        JsonNode.Parse(File.ReadAllText(HostConfigurationPath(Resolve(directory)))) as JsonObject
        ?? throw new JsonException("Invalid private Host configuration.");

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
