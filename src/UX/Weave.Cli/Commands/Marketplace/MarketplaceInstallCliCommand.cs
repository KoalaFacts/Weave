using System.Globalization;
using Spectre.Console;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class MarketplaceInstallCliCommand : ICliCommand<MarketplaceInstallOptions>
{
    public string Name => "install";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Install a marketplace item — capability-gated; resolves the linked template and scaffolds a workspace.";

    public async Task<int> ExecuteAsync(MarketplaceInstallOptions options, CancellationToken ct)
    {
        using var client = new MarketplaceApiClient();
        if (!await client.IsReachableAsync(ct))
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave serve'.");
            return 1;
        }

        var itemId = await MarketplaceItemPrompt.SelectItemIdAsync(
            client, options.ItemId, "Which marketplace item would you like to install?", ct);
        if (itemId is null)
        {
            CliTheme.WriteWarning("No marketplace items found.");
            return 0;
        }

        ApiMarketplaceInstallResponse? result;
        try
        {
            result = await client.InstallAsync(itemId, ct);
        }
        catch (InvalidOperationException ex)
        {
            CliTheme.WriteError($"Install rejected: {ex.Message}");
            return 1;
        }

        if (result is null)
        {
            CliTheme.WriteError($"Item '{itemId}' not found.");
            return 1;
        }

        CliTheme.WriteSuccess($"Installed: {result.Item.Name}");
        CliTheme.WriteKeyValue("Item ID", result.Item.ItemId);
        CliTheme.WriteKeyValue("Install count", result.Item.InstallCount.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Resolved template", $"{result.Template.Name} ({result.Template.TemplateId})");
        CliTheme.WriteKeyValue("Template version", result.Template.Version);
        CliTheme.WriteKeyValue("Template instantiations", result.Template.InstantiationCount.ToString(CultureInfo.InvariantCulture));

        if (result.Template.Tags.Count > 0)
            CliTheme.WriteKeyValue("Tags", string.Join(", ", result.Template.Tags));

        var workspaceName = AnsiConsole.Prompt(
            new TextPrompt<string>("Workspace name:")
                .Styled()
                .DefaultValue(result.Template.Name));

        var basePath = Path.GetFullPath(workspaceName);
        if (Directory.Exists(basePath) && Directory.EnumerateFileSystemEntries(basePath).Any())
        {
            CliTheme.WriteError($"Cannot scaffold: directory '{basePath}' already exists and is not empty.");
            return 1;
        }

        await ScaffoldWorkspaceAsync(result.Template, workspaceName, basePath, ct);

        CliTheme.WriteSuccess($"Workspace \"{workspaceName}\" scaffolded at {basePath}.");
        CliTheme.WriteMuted($"  Run `weave workspace up {workspaceName}` to start.");
        return 0;
    }

    private static async Task ScaffoldWorkspaceAsync(
        ApiMarketplaceInstallTemplate template,
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

        WorkspaceRegistry.Register(workspaceName, basePath);

        await WorkspaceManifestFile.WriteAsync(
            Path.Join(basePath, "workspace.json"), manifest, ct);

        var promptContent = $"# {template.Name}\n\n{template.Description}\n";
        await File.WriteAllTextAsync(
            Path.Join(basePath, "prompts", $"{WorkspaceManifestFromTemplate.DefaultAgentName}.md"),
            promptContent,
            ct);
    }
}
