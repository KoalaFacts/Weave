using Spectre.Console;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class DataImportFileSelector
{
    public string SelectFilePath(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
            return filePath;

        var exportFiles = FindExportFiles();
        return exportFiles.Count > 0
            ? AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Which export file would you like to import?")
                    .Styled()
                    .AddChoices(exportFiles))
            : AnsiConsole.Prompt(new TextPrompt<string>("Path to export file:").Styled());
    }

    public bool FileExists(string filePath) => File.Exists(filePath);

    private static List<string> FindExportFiles() =>
        [.. Directory.GetFiles(".", "*-export.json")
            .Concat(Directory.GetFiles(".", "*.export.json"))
            .Concat(Directory.GetFiles(".", "*-backup.json"))
            .Distinct()];
}
