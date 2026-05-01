using Spectre.Console;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal static class WorkspaceNewSelectionPrompt
{
    public static WorkspaceNewSelection Select(string? preset)
    {
        string model;
        List<string> tools;
        string? selectedPresetName = preset;
        var isolation = IsolationLevel.Full;

        if (preset is not null)
        {
            if (!WorkspacePresets.All.TryGetValue(preset, out var presetDef))
                throw new ArgumentException($"Unknown preset '{preset}'. Use 'weave workspace presets' to see available options.");

            model = presetDef.Model;
            tools = [.. presetDef.Tools];
            return new WorkspaceNewSelection(model, tools, selectedPresetName, isolation);
        }

        var presetChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Choose a preset:")
                .Styled()
                .AddChoices([.. WorkspacePresets.All.Keys, "custom (configure everything yourself)"]));

        if (presetChoice != "custom (configure everything yourself)" &&
            WorkspacePresets.All.TryGetValue(presetChoice, out var selectedPreset))
        {
            selectedPresetName = presetChoice;
            model = selectedPreset.Model;
            tools = [.. selectedPreset.Tools];
            return new WorkspaceNewSelection(model, tools, selectedPresetName, isolation);
        }

        model = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a model for your assistant:")
                .Styled()
                .AddChoices("claude-sonnet-4-20250514", "gpt-4o", "custom..."));

        if (model == "custom...")
            model = AnsiConsole.Prompt(new TextPrompt<string>("Enter the model name:").Styled());

        tools = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Which tools should the assistant have access to?")
                .Styled()
                .NotRequired()
                .AddChoices("git", "file", "web", "document"));

        var isolationChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Workspace isolation level:")
                .Styled()
                .AddChoices("full (recommended)", "shared", "none"));

        isolation = isolationChoice switch
        {
            "shared" => IsolationLevel.Shared,
            "none" => IsolationLevel.None,
            _ => IsolationLevel.Full
        };

        return new WorkspaceNewSelection(model, tools, selectedPresetName, isolation);
    }
}
