using System.Globalization;
using Spectre.Console;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.SystemInfo;
using Weave.Shared.Ids;
using Weave.Shared.Strings;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Templates;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceInstallCliCommand(
    IWorkspaceRegistry registry,
    GetSystemInfoAction systemInfoAction,
    BrowseMarketplaceItemsAction browseAction,
    InstallMarketplaceItemAction installAction) : ICliCommand<MarketplaceInstallOptions>
{
    public string Name => "install";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Install a marketplace item — capability-gated; resolves the linked template and scaffolds a workspace.";

    public async Task<int> ExecuteAsync(MarketplaceInstallOptions options, CancellationToken ct)
    {
        if (options.NoScaffold && !string.IsNullOrWhiteSpace(options.WorkspaceName))
            CliTheme.WriteWarning("--workspace-name is ignored when --no-scaffold is set.");

        var systemInfo = await systemInfoAction.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!systemInfo.IsSuccess || !systemInfo.Value.Reachable)
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var itemId = await MarketplaceItemPrompt.SelectItemIdAsync(
            browseAction, options.ItemId, "Which marketplace item would you like to install?", ct);
        if (itemId is null)
        {
            CliTheme.WriteWarning("No marketplace items found.");
            return 0;
        }

        var result = await installAction.ExecuteAsync(new InstallMarketplaceItemInput(itemId), ct);
        if (!result.IsSuccess)
        {
            switch (result.Failure.Reason)
            {
                case ActionFailureReason.Cancelled:
                    return 130;
                case ActionFailureReason.Conflict:
                    CliTheme.WriteError($"Install rejected: {result.Failure.Message}");
                    return 1;
                default:
                    CliTheme.WriteError(result.Failure.Message);
                    return 1;
            }
        }

        var item = result.Value.Item;
        var template = result.Value.Template;
        CliTheme.WriteSuccess($"Installed: {item.Name}");
        CliTheme.WriteKeyValue("Item ID", item.ItemId);
        CliTheme.WriteKeyValue("Install count", item.InstallCount.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Resolved template", $"{template.Name} ({template.TemplateId})");
        CliTheme.WriteKeyValue("Template version", template.Version);
        CliTheme.WriteKeyValue("Template instantiations", template.InstantiationCount.ToString(CultureInfo.InvariantCulture));

        if (template.Tags.Count > 0)
            CliTheme.WriteKeyValue("Tags", string.Join(", ", template.Tags));

        if (options.NoScaffold)
            return 0;

        var workspaceName = string.IsNullOrWhiteSpace(options.WorkspaceName)
            ? AnsiConsole.Prompt(
                new TextPrompt<string>("Workspace name:")
                    .Styled()
                    .DefaultValue(template.Name))
            : options.WorkspaceName;

        if (!IsSafeWorkspaceName(workspaceName))
        {
            CliTheme.WriteError($"Invalid workspace name '{SanitizeForEcho(workspaceName)}': must not contain path separators, '..', or reserved device names.");
            return 1;
        }

        var basePath = Path.GetFullPath(workspaceName);
        if (Directory.Exists(basePath) && Directory.EnumerateFileSystemEntries(basePath).Any())
        {
            CliTheme.WriteError($"Cannot scaffold: directory '{basePath}' already exists and is not empty.");
            return 1;
        }

        await ScaffoldWorkspaceAsync(template, workspaceName, basePath, ct);

        CliTheme.WriteSuccess($"Workspace \"{workspaceName}\" scaffolded at {basePath}.");
        CliTheme.WriteMuted($"  Run `weave workspace up {workspaceName}` to start.");
        return 0;
    }

    private async Task ScaffoldWorkspaceAsync(
        InstalledMarketplaceTemplate template,
        string workspaceName,
        string basePath,
        CancellationToken ct)
    {
        var capabilityTemplate = new CapabilityTemplate
        {
            TemplateId = TemplateId.From(template.TemplateId),
            Name = template.Name,
            Description = template.Description,
            Version = template.Version,
            Author = template.Author,
            Status = TemplateStatus.Published,
            AgentDefinition = template.AgentDefinition,
            RequiredTools = new Dictionary<string, ToolDefinition>(template.RequiredTools),
            Tags = [.. template.Tags],
            InstantiationCount = template.InstantiationCount
        };

        var manifest = WorkspaceManifestFromTemplate.Create(
            capabilityTemplate, workspaceName, IsolationLevel.Full);

        Directory.CreateDirectory(basePath);
        Directory.CreateDirectory(Path.Join(basePath, "prompts"));
        Directory.CreateDirectory(Path.Join(basePath, "data"));
        Directory.CreateDirectory(Path.Join(basePath, ".weave"));

        registry.Register(workspaceName, basePath);

        await WorkspaceManifestFile.WriteAsync(
            Path.Join(basePath, "workspace.json"), manifest, ct);

        var promptContent = $"# {template.Name}\n\n{template.Description}\n";
        await File.WriteAllTextAsync(
            Path.Join(basePath, "prompts", $"{WorkspaceManifestFromTemplate.DefaultAgentName}.md"),
            promptContent,
            ct);
    }

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    internal static bool IsSafeWorkspaceName(string workspaceName)
    {
        if (Path.IsPathRooted(workspaceName)
            || workspaceName.IndexOfAny(['/', '\\', ':']) >= 0
            || workspaceName == "."
            || workspaceName == "..")
        {
            return false;
        }

        // Windows reserves names like CON / NUL / COM1 even with extensions,
        // matching against the part before the FIRST dot (e.g. CON.tar.gz).
        var dotIndex = workspaceName.IndexOf('.');
        var stem = dotIndex < 0 ? workspaceName : workspaceName[..dotIndex];
        return !WindowsReservedNames.Contains(stem);
    }

    internal static string SanitizeForEcho(string value) =>
        ControlCharFilter.ReplaceControlChars(value);
}
